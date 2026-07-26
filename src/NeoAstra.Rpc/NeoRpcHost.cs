// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace NeoAstra.Rpc;

/// <summary>Sends one complete JSON protocol frame to a frontend connection.</summary>
/// <param name="json">The complete JSON object.</param>
/// <param name="cancellationToken">Cancels connection teardown, not a committed send.</param>
/// <returns>A task representing delivery acceptance.</returns>
public delegate ValueTask NeoRpcSendFrame(string json, CancellationToken cancellationToken);

/// <summary>Owns an immutable command registry and all active document RPC sessions.</summary>
public sealed class NeoRpcHost : IAsyncDisposable
{
    private readonly NeoRpcOptions _options;
    private readonly IReadOnlyDictionary<string, CommandDescriptor> _commands;
    private readonly IReadOnlyDictionary<string, EventDescriptor> _events;
    private readonly IReadOnlyList<INeoRpcServiceLifetimeOwner> _serviceLifetimes;
    private readonly ConcurrentDictionary<string, NeoRpcSession> _sessions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _lifecycleLock = new();
    private readonly object _admissionLock = new();
    private readonly Dictionary<string, int> _activeInvocationsByView = new(StringComparer.Ordinal);
    private int _activeInvocations;
    private int _disposed;

    internal NeoRpcHost(NeoRpcOptions options, IReadOnlyDictionary<string, CommandDescriptor> commands, IReadOnlyDictionary<string, EventDescriptor> events, IReadOnlyList<INeoRpcServiceLifetimeOwner> serviceLifetimes)
    {
        _options = options;
        _commands = new Dictionary<string, CommandDescriptor>(commands, StringComparer.Ordinal);
        _events = new Dictionary<string, EventDescriptor>(events, StringComparer.Ordinal);
        _serviceLifetimes = serviceLifetimes.ToArray();
    }

    /// <summary>Gets the number of active document sessions.</summary>
    public int ActiveSessionCount => _sessions.Count;

    /// <summary>Opens one trusted document session.</summary>
    /// <param name="identity">Trusted host identity. Its values are snapshotted before return.</param>
    /// <param name="send">The bounded transport send callback.</param>
    /// <returns>The newly owned session.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The identity is malformed or duplicates an active document session.</exception>
    /// <exception cref="ObjectDisposedException">The host is disposed.</exception>
    public NeoRpcSession OpenSession(NeoRpcSessionIdentity identity, NeoRpcSendFrame send)
        => OpenSessionCore(identity, send, null, null);

    internal NeoRpcSession OpenSessionCore(NeoRpcSessionIdentity identity, NeoRpcSendFrame send, global::NeoAstra.NeoAstra? view, NeoWindow? window)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(send);
        var snapshot = Snapshot(identity);
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var session = new NeoRpcSession(this, snapshot, send, view, window, _shutdown.Token);
            if (!_sessions.TryAdd(snapshot.DocumentSessionId, session))
            {
                session.DisposeWithoutCallback();
                throw new ArgumentException("The document-session ID is already active.", nameof(identity));
            }
            return session;
        }
    }

    /// <summary>Closes all sessions and releases all request, subscription, channel, and resource state.</summary>
    /// <returns>A task representing deterministic teardown.</returns>
    public async ValueTask DisposeAsync()
    {
        NeoRpcSession[] sessions;
        lock (_lifecycleLock)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            sessions = _sessions.Values.ToArray();
        }
        _shutdown.Cancel();
        foreach (var session in sessions) await session.DisposeAsync().ConfigureAwait(false);
        foreach (var lifetime in _serviceLifetimes) await lifetime.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }

    internal NeoRpcOptions Options => _options;
    internal bool TryGetCommand(string name, out CommandDescriptor? descriptor) => _commands.TryGetValue(name, out descriptor);
    internal bool TryGetEvent(string name, out EventDescriptor? descriptor) => _events.TryGetValue(name, out descriptor);

    internal bool TryEnterInvocation(string viewLabel)
    {
        lock (_admissionLock)
        {
            if (_activeInvocations >= _options.MaximumConcurrentInvocations) return false;
            _activeInvocationsByView.TryGetValue(viewLabel, out var viewActive);
            if (viewActive >= _options.MaximumConcurrentInvocations - 1) return false;
            _activeInvocations++;
            _activeInvocationsByView[viewLabel] = viewActive + 1;
            return true;
        }
    }

    internal void ExitInvocation(string viewLabel)
    {
        lock (_admissionLock)
        {
            _activeInvocations--;
            var viewActive = _activeInvocationsByView[viewLabel] - 1;
            if (viewActive == 0) _activeInvocationsByView.Remove(viewLabel); else _activeInvocationsByView[viewLabel] = viewActive;
        }
    }
    internal void Remove(NeoRpcSession session) => _sessions.TryRemove(new KeyValuePair<string, NeoRpcSession>(session.DocumentSessionId, session));

    internal async ValueTask CloseSessionServicesAsync(string documentSessionId)
    {
        foreach (var lifetime in _serviceLifetimes) await lifetime.CloseSessionAsync(documentSessionId).ConfigureAwait(false);
    }

    internal async ValueTask CloseViewServicesAsync(string viewLabel)
    {
        foreach (var lifetime in _serviceLifetimes) await lifetime.CloseViewAsync(viewLabel).ConfigureAwait(false);
    }

    internal async ValueTask<int> PublishAsync(EventDescriptor descriptor, byte[] json, Func<NeoRpcContext, bool> recipient, CancellationToken cancellationToken)
    {
        var accepted = 0;
        foreach (var session in _sessions.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (session.TryPublish(descriptor, json, recipient)) accepted++;
        }
        await Task.CompletedTask.ConfigureAwait(false);
        return accepted;
    }

    internal void Diagnose(NeoRpcDiagnosticLevel level, string code, string message, string? correlationId = null)
    {
        try { _options.DiagnosticSink?.Write(new(level, code, message, correlationId)); } catch { }
    }

    private static NeoRpcSessionIdentity Snapshot(NeoRpcSessionIdentity source)
    {
        ArgumentNullException.ThrowIfNull(source.Features);
        if (source.ProtocolMinor < 0) throw new ArgumentOutOfRangeException(nameof(source.ProtocolMinor));
        if (source.DocumentId is { Length: > 256 }) throw new ArgumentException("The document ID is too long.", nameof(source.DocumentId));
        if (source.ContractHash.Length > 256 || source.ContractHash.Any(static c => c > 0x7f || char.IsControl(c)))
            throw new ArgumentException("The contract hash must be bounded ASCII.", nameof(source.ContractHash));
        var features = source.Features.ToArray();
        if (features.Length > 64 || features.Any(static feature => !NeoRpcValidation.IsWireName(feature, 64)))
            throw new ArgumentException("Negotiated feature names are malformed.", nameof(source.Features));
        return new NeoRpcSessionIdentity(source.ViewLabel, source.DocumentSessionId)
        {
            SourceOrigin = source.SourceOrigin,
            IsMainFrame = source.IsMainFrame,
            WholeViewTrust = source.WholeViewTrust,
            DocumentId = source.DocumentId,
            ProtocolMinor = source.ProtocolMinor,
            Features = Array.AsReadOnly(features),
            ContractHash = source.ContractHash,
            Services = source.Services,
            Dispatcher = source.Dispatcher,
        };
    }
}

/// <summary>Owns all RPC state for one trusted document-session identity.</summary>
public sealed class NeoRpcSession : IAsyncDisposable
{
    private readonly NeoRpcHost _host;
    private readonly NeoRpcSessionIdentity _identity;
    private readonly NeoRpcSendFrame _send;
    private readonly WeakReference<global::NeoAstra.NeoAstra>? _view;
    private readonly WeakReference<NeoWindow>? _window;
    private readonly CancellationTokenSource _closed;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, InvocationState> _requests = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedRequestIds = new(StringComparer.Ordinal);
    private readonly object _requestIdsLock = new();
    private readonly ConcurrentDictionary<string, SubscriptionState> _subscriptions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ChannelState> _channels = new(StringComparer.Ordinal);
    private readonly NeoRpcResourceCollection _resources;
    private readonly TaskCompletionSource _invocationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeInvocations;
    private int _disposed;
    private long _nextChannelId;
    private long _unknownCancelCount;
    private int _subscriptionSlots;

    internal NeoRpcSession(NeoRpcHost host, NeoRpcSessionIdentity identity, NeoRpcSendFrame send, global::NeoAstra.NeoAstra? view, NeoWindow? window, CancellationToken shutdown)
    {
        _host = host;
        _identity = identity;
        _send = send;
        _view = view is null ? null : new(view);
        _window = window is null ? null : new(window);
        _closed = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        _resources = new NeoRpcResourceCollection(this, host.Options.MaximumResourcesPerSession);
    }

    /// <summary>Gets the opaque trusted document-session ID.</summary>
    public string DocumentSessionId => _identity.DocumentSessionId;
    /// <summary>Gets the immutable trusted view label.</summary>
    public string ViewLabel => _identity.ViewLabel;
    /// <summary>Gets the cancellation token triggered by session or host teardown.</summary>
    public CancellationToken Closed => _closed.Token;
    /// <summary>Gets the current active invocation count.</summary>
    public int ActiveInvocationCount => Volatile.Read(ref _activeInvocations);
    /// <summary>Gets the current active subscription count.</summary>
    public int ActiveSubscriptionCount => _subscriptions.Count;
    /// <summary>Gets the current active channel count.</summary>
    public int ActiveChannelCount => _channels.Count;
    /// <summary>Gets the current owned resource count.</summary>
    public int ActiveResourceCount => _resources.Count;

    /// <summary>Receives one bounded frontend RPC frame using the session's initial authenticated sender identity.</summary>
    /// <param name="json">A complete JSON protocol frame.</param>
    /// <param name="cancellationToken">Cancels processing before an invocation is accepted.</param>
    /// <returns>A task representing frame acceptance and, for invokes, terminal completion.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The session is closed.</exception>
    public ValueTask ReceiveAsync(string json, CancellationToken cancellationToken = default)
        => ReceiveAsync(json, _identity.SourceOrigin, _identity.IsMainFrame, cancellationToken);

    /// <summary>Receives one bounded frontend RPC frame with backend-authenticated sender metadata.</summary>
    /// <param name="json">A complete JSON protocol frame.</param>
    /// <param name="sourceOrigin">The authenticated source origin, or <see langword="null"/> when unavailable.</param>
    /// <param name="isMainFrame">Whether the backend authenticated the main frame as sender.</param>
    /// <param name="cancellationToken">Cancels processing before an invocation is accepted.</param>
    /// <returns>A task representing frame acceptance and, for invokes, terminal completion.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The session is closed.</exception>
    public async ValueTask ReceiveAsync(string json, Uri? sourceOrigin, bool isMainFrame, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(json);
        cancellationToken.ThrowIfCancellationRequested();
        if (Encoding.UTF8.GetByteCount(json) > _host.Options.MaximumFrameBytes)
        {
            _host.Diagnose(NeoRpcDiagnosticLevel.Warning, NeoRpcErrorCodes.PayloadTooLarge, "An RPC frame exceeded the configured byte limit.");
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = _host.Options.MaximumJsonDepth,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        }
        catch (JsonException)
        {
            _host.Diagnose(NeoRpcDiagnosticLevel.Warning, NeoRpcErrorCodes.InvalidRequest, "An RPC frame contained invalid JSON.");
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !IsProtocolFrame(root) || !TryString(root, "kind", out var kind))
            {
                _host.Diagnose(NeoRpcDiagnosticLevel.Warning, NeoRpcErrorCodes.InvalidRequest, "An RPC frame has an invalid discriminator or kind.");
                return;
            }
            switch (kind)
            {
                case "invoke":
                    await ReceiveInvokeAsync(root, sourceOrigin, isMainFrame, cancellationToken).ConfigureAwait(false);
                    break;
                case "cancel":
                    ReceiveCancel(root);
                    break;
                case "subscribe":
                    await ReceiveSubscribeAsync(root, sourceOrigin, isMainFrame, cancellationToken).ConfigureAwait(false);
                    break;
                case "unsubscribe":
                    await ReceiveUnsubscribeAsync(root).ConfigureAwait(false);
                    break;
                case "channel_ack":
                    ReceiveChannelAck(root);
                    break;
                case "channel_close":
                    await ReceiveChannelCloseAsync(root).ConfigureAwait(false);
                    break;
                case "resource_close":
                    await ReceiveResourceCloseAsync(root).ConfigureAwait(false);
                    break;
                case "result" or "subscribed" or "event" or "channel_item" or "channel_complete" or "channel_error":
                    _host.Diagnose(NeoRpcDiagnosticLevel.Warning, NeoRpcErrorCodes.InvalidRequest, "A frontend sent a host-owned RPC frame kind.");
                    break;
                default:
                    _host.Diagnose(NeoRpcDiagnosticLevel.Debug, NeoRpcErrorCodes.InvalidRequest, "An unknown RPC frame kind was ignored.");
                    break;
            }
        }
    }

    /// <summary>Closes the session and deterministically cancels and disposes all owned state.</summary>
    /// <returns>A task representing teardown.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _closed.Cancel();
        foreach (var request in _requests.Values) request.Cancel();
        if (Volatile.Read(ref _activeInvocations) == 0) _invocationsDrained.TrySetResult();
        try { await _invocationsDrained.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { _host.Diagnose(NeoRpcDiagnosticLevel.Warning, "teardown_timeout", "RPC invocation teardown exceeded five seconds."); }
        foreach (var subscription in _subscriptions.Values) subscription.Close();
        foreach (var channel in _channels.Values) channel.Close();
        await _resources.DisposeAsync().ConfigureAwait(false);
        await _host.CloseSessionServicesAsync(DocumentSessionId).ConfigureAwait(false);
        var pumps = _subscriptions.Values.Select(static value => value.Completion)
            .Concat(_channels.Values.Select(static value => value.Completion)).ToArray();
        if (pumps.Length != 0)
        {
            try { await Task.WhenAll(pumps).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException) { }
        }
        _subscriptions.Clear();
        Volatile.Write(ref _subscriptionSlots, 0);
        _channels.Clear();
        _requests.Clear();
        _host.Remove(this);
        _sendLock.Dispose();
        _closed.Dispose();
    }

    internal void DisposeWithoutCallback()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _closed.Cancel();
            _closed.Dispose();
            _sendLock.Dispose();
        }
    }

    internal bool TryPublish(EventDescriptor descriptor, byte[] json, Func<NeoRpcContext, bool> recipient)
    {
        var accepted = false;
        foreach (var subscription in _subscriptions.Values)
        {
            if (!ReferenceEquals(subscription.Descriptor, descriptor)) continue;
            if (!recipient(CreateContext(NewCorrelationId(), _closed.Token, subscription.SourceOrigin, subscription.IsMainFrame))) continue;
            accepted |= subscription.Enqueue(json);
        }
        return accepted;
    }

    internal ValueTask StartChannelAsync<T>(string channelId, NeoRpcChannel<T> channel, CancellationToken invocationToken)
    {
        var state = new ChannelState<T>(this, channelId, channel, invocationToken);
        if (!_channels.TryAdd(channelId, state)) throw new InvalidOperationException("A generated channel ID collided.");
        state.Start();
        return ValueTask.CompletedTask;
    }

    internal async ValueTask SendEventAsync(string subscriptionId, long sequence, byte[] json, CancellationToken cancellationToken)
        => await SendRawAsync(BuildFrame(writer =>
        {
            writer.WriteStartObject();
            WriteHeader(writer, "event");
            writer.WriteString("subscription", subscriptionId);
            writer.WriteNumber("sequence", sequence);
            writer.WritePropertyName("value"); writer.WriteRawValue(json, skipInputValidation: true);
            writer.WriteEndObject();
        }), cancellationToken).ConfigureAwait(false);

    internal async ValueTask SendChannelItemAsync(string channelId, long sequence, byte[] json, CancellationToken cancellationToken)
        => await SendRawAsync(BuildFrame(writer =>
        {
            writer.WriteStartObject(); WriteHeader(writer, "channel_item"); writer.WriteString("channel", channelId);
            writer.WriteNumber("sequence", sequence); writer.WritePropertyName("value"); writer.WriteRawValue(json, true); writer.WriteEndObject();
        }), cancellationToken).ConfigureAwait(false);

    internal ValueTask SendChannelTerminalAsync(string channelId, string kind, NeoRpcError? error, CancellationToken cancellationToken)
        => SendRawAsync(BuildFrame(writer =>
        {
            writer.WriteStartObject(); WriteHeader(writer, kind); writer.WriteString("channel", channelId);
            if (error is { } value) { writer.WritePropertyName("error"); WriteError(writer, value); }
            writer.WriteEndObject();
        }), cancellationToken);

    internal void RemoveChannel(string id, ChannelState state) => _channels.TryRemove(new KeyValuePair<string, ChannelState>(id, state));
    private void RemoveSubscription(string id, SubscriptionState state)
    {
        if (_subscriptions.TryRemove(new KeyValuePair<string, SubscriptionState>(id, state))) Interlocked.Decrement(ref _subscriptionSlots);
    }

    private async ValueTask ReceiveInvokeAsync(JsonElement root, Uri? sourceOrigin, bool isMainFrame, CancellationToken receiveCancellation)
    {
        if (!TryValidId(root, "id", out var id) || !TryString(root, "command", out var command) ||
            !NeoRpcValidation.IsWireName(command, _host.Options.MaximumWireNameLength) ||
            !root.TryGetProperty("args", out var args))
        {
            if (!string.IsNullOrEmpty(id)) await SendErrorResultAsync(id, FrameworkError(NeoRpcErrorCodes.InvalidRequest, "The invocation frame is invalid.", null)).ConfigureAwait(false);
            return;
        }
        if (!RememberRequestId(id))
        {
            await SendErrorResultAsync(id, FrameworkError(NeoRpcErrorCodes.DuplicateRequest, "The request ID was already used.", null)).ConfigureAwait(false);
            return;
        }
        if (!ContractMatches(root))
        {
            await SendErrorResultAsync(id, FrameworkError(NeoRpcErrorCodes.ProtocolMismatch, "The generated frontend contract does not match the host.", null)).ConfigureAwait(false);
            return;
        }
        if (!_host.TryGetCommand(command, out var descriptor) || descriptor is null)
        {
            await SendErrorResultAsync(id, FrameworkError(NeoRpcErrorCodes.CommandNotFound, "The requested command is not registered.", null)).ConfigureAwait(false);
            return;
        }
        var enteredSession = TryEnterInvocation();
        var enteredHost = enteredSession && _host.TryEnterInvocation(_identity.ViewLabel);
        if (!enteredHost)
        {
            if (enteredSession) Interlocked.Decrement(ref _activeInvocations);
            await SendErrorResultAsync(id, FrameworkError(NeoRpcErrorCodes.TooManyRequests, "The RPC concurrency limit is exhausted.", null, retryable: true)).ConfigureAwait(false);
            return;
        }

        var correlationId = NewCorrelationId();
        var timeout = descriptor.Options.Timeout ?? _host.Options.InvocationTimeout;
        var state = new InvocationState(
            _closed.Token,
            receiveCancellation,
            timeout,
            timedOut => SendTerminalErrorResultAsync(id, timedOut
                ? FrameworkError(NeoRpcErrorCodes.Timeout, "The command timed out.", correlationId, true)
                : FrameworkError(NeoRpcErrorCodes.OperationCanceled, "The operation was canceled.", correlationId)));
        if (!_requests.TryAdd(id, state))
        {
            state.Dispose();
            ExitInvocation();
            await SendErrorResultAsync(id, FrameworkError(NeoRpcErrorCodes.DuplicateRequest, "The request ID is already active.", correlationId)).ConfigureAwait(false);
            return;
        }
        var context = CreateContext(correlationId, state.Token, sourceOrigin, isMainFrame);
        try
        {
            var authorizationError = await AuthorizeAsync(context, command, descriptor.Options.Permission, isSubscription: false, state.Token).ConfigureAwait(false);
            if (authorizationError is { } denied)
            {
                await state.TryCommitAsync(() => SendTerminalErrorResultAsync(id, denied)).ConfigureAwait(false);
                return;
            }

            object request;
            try { request = descriptor.DeserializeRequest(args); }
            catch (Exception exception) when (exception is JsonException or NotSupportedException or FormatException or InvalidOperationException)
            {
                _host.Diagnose(NeoRpcDiagnosticLevel.Debug, NeoRpcErrorCodes.InvalidRequest, "RPC request deserialization failed against declared metadata.", correlationId);
                await state.TryCommitAsync(() => SendTerminalErrorResultAsync(id, FrameworkError(NeoRpcErrorCodes.InvalidRequest, "The request does not match the declared JSON contract.", correlationId))).ConfigureAwait(false);
                return;
            }

            object? applicationResult;
            try
            {
                if (descriptor.Options.Dispatch == NeoRpcDispatchMode.UiThread)
                {
                    var dispatcher = context.Dispatcher ?? throw new NeoRpcException(NeoRpcErrorCodes.InternalError, "A UI dispatcher is unavailable.");
                    applicationResult = await dispatcher.InvokeAsync(
                        async () => await descriptor.InvokeHandlerAsync(request, context, state.Token).ConfigureAwait(false), state.Token).ConfigureAwait(false);
                }
                else
                {
                    applicationResult = await descriptor.InvokeHandlerAsync(request, context, state.Token).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                var error = MapException(exception, context, state);
                await state.TryCommitAsync(() => SendTerminalErrorResultAsync(id, error)).ConfigureAwait(false);
                return;
            }

            CommandResult result;
            try { result = descriptor.SerializeResult(applicationResult); }
            catch (Exception)
            {
                _host.Diagnose(NeoRpcDiagnosticLevel.Error, NeoRpcErrorCodes.SerializationFailed, "RPC response serialization failed safely.", correlationId);
                await state.TryCommitAsync(() => SendTerminalErrorResultAsync(id, FrameworkError(NeoRpcErrorCodes.SerializationFailed, "The command response could not be serialized.", correlationId))).ConfigureAwait(false);
                return;
            }

            if (result is IChannelCommandResult channelResult)
            {
                if (_channels.Count >= _host.Options.MaximumChannelsPerSession)
                {
                    await state.TryCommitAsync(() => SendTerminalErrorResultAsync(id, FrameworkError(NeoRpcErrorCodes.TooManyRequests, "The channel limit is exhausted.", correlationId, true))).ConfigureAwait(false);
                    return;
                }
                var channelId = $"ch-{Interlocked.Increment(ref _nextChannelId):x}";
                var committed = await state.TryCommitAsync(() => SendChannelResultAsync(id, channelId)).ConfigureAwait(false);
                if (committed) await channelResult.StartAsync(this, channelId, _closed.Token).ConfigureAwait(false);
            }
            else
            {
                await state.TryCommitAsync(() => SendSuccessResultAsync(id, result)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (state.Token.IsCancellationRequested)
        {
            await state.TerminalCompletion.ConfigureAwait(false);
        }
        finally
        {
            try { await state.TerminalCompletion.ConfigureAwait(false); }
            finally
            {
                _requests.TryRemove(new KeyValuePair<string, InvocationState>(id, state));
                state.Dispose();
                ExitInvocation();
            }
        }
    }

    private void ReceiveCancel(JsonElement root)
    {
        if (!TryValidId(root, "id", out var id)) return;
        if (_requests.TryGetValue(id, out var state)) state.Cancel();
        else if (Interlocked.Increment(ref _unknownCancelCount) <= 100)
            _host.Diagnose(NeoRpcDiagnosticLevel.Debug, "late_cancel", "A cancel for an unknown or completed request was ignored.");
    }

    private async ValueTask ReceiveSubscribeAsync(JsonElement root, Uri? sourceOrigin, bool isMainFrame, CancellationToken cancellationToken)
    {
        if (!TryValidId(root, "id", out var id) || !TryString(root, "event", out var eventName) ||
            !_host.TryGetEvent(eventName, out var descriptor) || descriptor is null)
        {
            if (!string.IsNullOrEmpty(id)) await SendSubscriptionErrorAsync(id, FrameworkError(NeoRpcErrorCodes.InvalidRequest, "The subscription frame or event is invalid.", null)).ConfigureAwait(false);
            return;
        }
        if (!ContractMatches(root))
        {
            await SendSubscriptionErrorAsync(id, FrameworkError(NeoRpcErrorCodes.ProtocolMismatch, "The generated frontend contract does not match the host.", null)).ConfigureAwait(false);
            return;
        }
        if (Interlocked.Increment(ref _subscriptionSlots) > _host.Options.MaximumSubscriptionsPerSession)
        {
            Interlocked.Decrement(ref _subscriptionSlots);
            await SendSubscriptionErrorAsync(id, FrameworkError(NeoRpcErrorCodes.TooManyRequests, "The subscription limit is exhausted.", null, true)).ConfigureAwait(false);
            return;
        }
        if (!RememberRequestId(id))
        {
            Interlocked.Decrement(ref _subscriptionSlots);
            await SendSubscriptionErrorAsync(id, FrameworkError(NeoRpcErrorCodes.DuplicateRequest, "The subscription ID was already used.", null)).ConfigureAwait(false);
            return;
        }
        var subscription = new SubscriptionState(this, id, descriptor, sourceOrigin, isMainFrame);
        if (!_subscriptions.TryAdd(id, subscription))
        {
            Interlocked.Decrement(ref _subscriptionSlots);
            subscription.Close();
            subscription.DisposePending();
            await SendSubscriptionErrorAsync(id, FrameworkError(NeoRpcErrorCodes.DuplicateRequest, "The subscription ID is already active.", null)).ConfigureAwait(false);
            return;
        }
        try
        {
            var context = CreateContext(NewCorrelationId(), subscription.Token, sourceOrigin, isMainFrame);
            NeoRpcError? error;
            try { error = await AuthorizeAsync(context, eventName, descriptor.Options.Permission, true, subscription.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (subscription.Token.IsCancellationRequested) { return; }
            if (error is { } denied)
            {
                if (subscription.TryClosePending()) await SendSubscriptionErrorAsync(id, denied).ConfigureAwait(false);
                return;
            }
            if (!subscription.TryActivate()) return;
            await SendRawAsync(BuildFrame(writer => { writer.WriteStartObject(); WriteHeader(writer, "subscribed"); writer.WriteString("id", id); writer.WriteEndObject(); }), _closed.Token).ConfigureAwait(false);
            subscription.Start();
        }
        catch
        {
            _subscriptions.TryRemove(new KeyValuePair<string, SubscriptionState>(id, subscription));
            subscription.Close();
            throw;
        }
        finally
        {
            if (!subscription.IsActive)
            {
                RemoveSubscription(id, subscription);
                subscription.DisposePending();
            }
        }
    }

    private async ValueTask ReceiveUnsubscribeAsync(JsonElement root)
    {
        if (!TryValidId(root, "id", out var id)) return;
        if (_subscriptions.TryRemove(id, out var state))
        {
            Interlocked.Decrement(ref _subscriptionSlots);
            state.Close();
            await state.Completion.ConfigureAwait(false);
        }
    }

    private void ReceiveChannelAck(JsonElement root)
    {
        if (!TryValidId(root, "channel", out var id) || !root.TryGetProperty("sequence", out var sequence) || !sequence.TryGetInt64(out var value) || value < 1) return;
        if (_channels.TryGetValue(id, out var state)) state.Acknowledge(value);
    }

    private async ValueTask ReceiveChannelCloseAsync(JsonElement root)
    {
        if (!TryValidId(root, "channel", out var id)) return;
        if (_channels.TryRemove(id, out var state))
        {
            state.Close();
            await state.Completion.ConfigureAwait(false);
        }
    }

    private async ValueTask ReceiveResourceCloseAsync(JsonElement root)
    {
        if (TryValidId(root, "resource", out var id)) await _resources.CloseAsync(id).ConfigureAwait(false);
    }

    private async ValueTask<NeoRpcError?> AuthorizeAsync(NeoRpcContext context, string operation, string? permission, bool isSubscription, CancellationToken cancellationToken)
    {
        var service = _host.Options.AuthorizationService;
        if (service is null) return null;
        NeoRpcAuthorizationResult decision;
        try { decision = await service.AuthorizeAsync(new(context, operation, permission, isSubscription), cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            _host.Diagnose(NeoRpcDiagnosticLevel.Error, NeoRpcErrorCodes.InternalError, "RPC authorization failed safely.", context.CorrelationId);
            return MapException(exception, context, null);
        }
        if (decision.IsAllowed) return null;
        var code = decision.ErrorCode is NeoRpcErrorCodes.ScopeDenied ? NeoRpcErrorCodes.ScopeDenied : NeoRpcErrorCodes.PermissionDenied;
        return FrameworkError(code, code == NeoRpcErrorCodes.ScopeDenied ? "The command is outside the allowed scope." : "Permission was denied.", context.CorrelationId);
    }

    private NeoRpcError MapException(Exception exception, NeoRpcContext context, InvocationState? state)
    {
        if (exception is NeoRpcException explicitError)
            return new NeoRpcError(explicitError.Code, Bound(explicitError.Message), context.CorrelationId) { Retryable = explicitError.Retryable };
        foreach (var mapper in _host.Options.ErrorMappers)
        {
            try
            {
                if (mapper.TryMap(exception, context, out var mapped) && NeoRpcValidation.IsErrorCode(mapped.Code))
                    return new NeoRpcError(mapped.Code, mapped.Message, mapped.CorrelationId ?? context.CorrelationId) { Retryable = mapped.Retryable };
            }
            catch { }
        }
        if (exception is OperationCanceledException || state?.Token.IsCancellationRequested == true)
        {
            return state?.TimedOut == true
                ? FrameworkError(NeoRpcErrorCodes.Timeout, "The command timed out.", context.CorrelationId, true)
                : FrameworkError(NeoRpcErrorCodes.OperationCanceled, "The operation was canceled.", context.CorrelationId);
        }
        _host.Diagnose(NeoRpcDiagnosticLevel.Error, NeoRpcErrorCodes.InternalError, "An application RPC command failed. Full exception details remain host-side.", context.CorrelationId);
        var message = _host.Options.IncludeDevelopmentErrorDetails ? Bound(exception.Message) : "The command failed internally.";
        return FrameworkError(NeoRpcErrorCodes.InternalError, message, context.CorrelationId);
    }

    private NeoRpcContext CreateContext(string correlationId, CancellationToken cancellationToken, Uri? sourceOrigin, bool isMainFrame)
    {
        var identity = new NeoRpcSessionIdentity(_identity.ViewLabel, _identity.DocumentSessionId)
        {
            SourceOrigin = sourceOrigin,
            IsMainFrame = isMainFrame,
            WholeViewTrust = _identity.WholeViewTrust,
            DocumentId = _identity.DocumentId,
            ProtocolMinor = _identity.ProtocolMinor,
            Features = _identity.Features,
            ContractHash = _identity.ContractHash,
            Services = _identity.Services,
            Dispatcher = _identity.Dispatcher,
        };
        global::NeoAstra.NeoAstra? view = null;
        NeoWindow? window = null;
        _view?.TryGetTarget(out view);
        _window?.TryGetTarget(out window);
        return new(identity, correlationId, cancellationToken, _resources, view, window);
    }

    private bool RememberRequestId(string id)
    {
        lock (_requestIdsLock)
        {
            if (_usedRequestIds.Count >= _host.Options.MaximumRetainedRequestIds) return false;
            return _usedRequestIds.Add(id);
        }
    }

    private bool TryEnterInvocation()
    {
        while (true)
        {
            var current = Volatile.Read(ref _activeInvocations);
            if (current >= _host.Options.MaximumConcurrentInvocationsPerSession) return false;
            if (Interlocked.CompareExchange(ref _activeInvocations, current + 1, current) == current) return true;
        }
    }

    private void ExitInvocation()
    {
        if (Interlocked.Decrement(ref _activeInvocations) == 0 && Volatile.Read(ref _disposed) != 0) _invocationsDrained.TrySetResult();
        _host.ExitInvocation(_identity.ViewLabel);
    }

    private ValueTask SendSuccessResultAsync(string id, CommandResult result)
        => SendRawAsync(BuildFrame(writer =>
        {
            writer.WriteStartObject(); WriteHeader(writer, "result"); writer.WriteString("id", id); writer.WriteBoolean("ok", true); writer.WritePropertyName("value");
            if (result is ValueCommandResult value) writer.WriteRawValue(value.Json, true); else writer.WriteNullValue();
            writer.WriteEndObject();
        }), CancellationToken.None, allowClosing: true);

    private ValueTask SendChannelResultAsync(string id, string channelId)
        => SendRawAsync(BuildFrame(writer =>
        {
            writer.WriteStartObject(); WriteHeader(writer, "result"); writer.WriteString("id", id); writer.WriteBoolean("ok", true);
            writer.WritePropertyName("value"); writer.WriteStartObject(); writer.WriteString("channel", channelId); writer.WriteEndObject(); writer.WriteEndObject();
        }), CancellationToken.None, allowClosing: true);

    private ValueTask SendErrorResultAsync(string id, NeoRpcError error)
        => SendRawAsync(BuildFrame(writer =>
        {
            writer.WriteStartObject(); WriteHeader(writer, "result"); writer.WriteString("id", id); writer.WriteBoolean("ok", false);
            writer.WritePropertyName("error"); WriteError(writer, error); writer.WriteEndObject();
        }), _closed.Token);

    private ValueTask SendTerminalErrorResultAsync(string id, NeoRpcError error)
        => SendRawAsync(BuildFrame(writer =>
        {
            writer.WriteStartObject(); WriteHeader(writer, "result"); writer.WriteString("id", id); writer.WriteBoolean("ok", false);
            writer.WritePropertyName("error"); WriteError(writer, error); writer.WriteEndObject();
        }), CancellationToken.None, allowClosing: true);

    private ValueTask SendSubscriptionErrorAsync(string id, NeoRpcError error)
        => SendRawAsync(BuildFrame(writer =>
        {
            writer.WriteStartObject(); WriteHeader(writer, "subscribed"); writer.WriteString("id", id); writer.WritePropertyName("error"); WriteError(writer, error); writer.WriteEndObject();
        }), _closed.Token);

    private async ValueTask SendRawAsync(string json, CancellationToken cancellationToken, bool allowClosing = false)
    {
        if (!allowClosing && Volatile.Read(ref _disposed) != 0) throw new OperationCanceledException("The RPC session is closed.", _closed.Token);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await _send(json, cancellationToken).ConfigureAwait(false); }
        catch
        {
            _closed.Cancel();
            throw;
        }
        finally { _sendLock.Release(); }
    }

    private bool TryValidId(JsonElement root, string property, out string value)
    {
        value = string.Empty;
        return TryString(root, property, out value) && value.Length <= _host.Options.MaximumIdLength &&
               value.All(static character => character is >= (char)0x21 and <= (char)0x7e);
    }

    private static bool IsProtocolFrame(JsonElement root)
        => root.TryGetProperty("neoastra", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var discriminator) && discriminator == 1;

    private bool ContractMatches(JsonElement root)
    {
        if (_host.Options.ContractHash.Length == 0) return true;
        return TryString(root, "contract", out var hash) && string.Equals(hash, _host.Options.ContractHash, StringComparison.Ordinal);
    }

    private static bool TryString(JsonElement root, string property, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString()!;
        return value.Length != 0;
    }

    private static void WriteHeader(Utf8JsonWriter writer, string kind) { writer.WriteNumber("neoastra", 1); writer.WriteString("kind", kind); }

    private static void WriteError(Utf8JsonWriter writer, NeoRpcError error)
    {
        writer.WriteStartObject(); writer.WriteString("code", error.Code); writer.WriteString("message", Bound(error.Message));
        if (error.CorrelationId is not null) writer.WriteString("correlationId", error.CorrelationId);
        writer.WriteBoolean("retryable", error.Retryable); writer.WriteEndObject();
    }

    private static string BuildFrame(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) write(writer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static NeoRpcError FrameworkError(string code, string message, string? correlationId, bool retryable = false)
        => new(code, message, correlationId) { Retryable = retryable };

    private static string Bound(string message)
    {
        var sanitized = new string(message.Where(static character => character is not ('\r' or '\n') && !char.IsControl(character)).Take(512).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "The command failed." : sanitized;
    }

    private static string NewCorrelationId() => Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();

    private sealed class InvocationState : IDisposable
    {
        private const int Pending = 0;
        private const int ResultCommitted = 1;
        private const int Canceled = 2;
        private const int TimedOutState = 3;
        private readonly CancellationTokenSource _linked;
        private readonly CancellationTokenSource _timeout = new();
        private readonly CancellationTokenRegistration _sessionRegistration;
        private readonly CancellationTokenRegistration _receiveRegistration;
        private readonly CancellationTokenRegistration _timeoutRegistration;
        private readonly Func<bool, ValueTask> _sendCancellation;
        private readonly TaskCompletionSource _terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _state;

        internal InvocationState(CancellationToken session, CancellationToken receive, TimeSpan timeout, Func<bool, ValueTask> sendCancellation)
        {
            _sendCancellation = sendCancellation;
            _linked = new CancellationTokenSource();
            _sessionRegistration = session.Register(static state => ((InvocationState)state!).CancelFromSource(timedOut: false), this);
            _receiveRegistration = receive.Register(static state => ((InvocationState)state!).CancelFromSource(timedOut: false), this);
            _timeoutRegistration = _timeout.Token.Register(static state => ((InvocationState)state!).CancelFromSource(timedOut: true), this);
            _timeout.CancelAfter(timeout);
        }

        internal CancellationToken Token => _linked.Token;
        internal bool TimedOut => Volatile.Read(ref _state) == TimedOutState;
        internal Task TerminalCompletion => _terminal.Task;
        internal void Cancel()
        {
            TryCancel(timedOut: false);
            try { _linked.Cancel(); } catch (ObjectDisposedException) { }
        }
        internal async ValueTask<bool> TryCommitAsync(Func<ValueTask> send)
        {
            if (Interlocked.CompareExchange(ref _state, ResultCommitted, Pending) != Pending) return false;
            try { await send().ConfigureAwait(false); _terminal.TrySetResult(); return true; }
            catch (Exception exception) { _terminal.TrySetException(exception); throw; }
        }
        public void Dispose()
        {
            _sessionRegistration.Dispose(); _receiveRegistration.Dispose(); _timeoutRegistration.Dispose();
            _linked.Dispose(); _timeout.Dispose();
        }

        private void TryCancel(bool timedOut)
        {
            var terminal = timedOut ? TimedOutState : Canceled;
            if (Interlocked.CompareExchange(ref _state, terminal, Pending) != Pending) return;
            _ = CompleteCancellationAsync(timedOut);
        }

        private void CancelFromSource(bool timedOut)
        {
            TryCancel(timedOut);
            try { _linked.Cancel(); } catch (ObjectDisposedException) { }
        }

        private async Task CompleteCancellationAsync(bool timedOut)
        {
            try { await _sendCancellation(timedOut).ConfigureAwait(false); _terminal.TrySetResult(); }
            catch (Exception exception) { _terminal.TrySetException(exception); }
        }
    }

    private sealed class SubscriptionState
    {
        private const int Pending = 0;
        private const int Active = 1;
        private const int Closed = 2;
        private readonly NeoRpcSession _session;
        private readonly string _id;
        private readonly object _lock = new();
        private readonly Queue<byte[]> _queue = [];
        private readonly SemaphoreSlim _ready;
        private readonly CancellationTokenSource _closed;
        private int _bytes;
        private int _started;
        private long _sequence;
        private int _state;

        internal SubscriptionState(NeoRpcSession session, string id, EventDescriptor descriptor, Uri? sourceOrigin, bool isMainFrame)
        {
            _session = session; _id = id; Descriptor = descriptor;
            SourceOrigin = sourceOrigin; IsMainFrame = isMainFrame;
            _closed = CancellationTokenSource.CreateLinkedTokenSource(session._closed.Token);
            _ready = new SemaphoreSlim(0, session._host.Options.MaximumQueuedEventsPerSubscription);
        }
        internal EventDescriptor Descriptor { get; }
        internal Uri? SourceOrigin { get; }
        internal bool IsMainFrame { get; }
        internal CancellationToken Token => _closed.Token;
        internal bool IsActive => Volatile.Read(ref _state) == Active;
        internal Task Completion { get; private set; } = Task.CompletedTask;
        internal bool TryActivate() => !_closed.IsCancellationRequested && Interlocked.CompareExchange(ref _state, Active, Pending) == Pending;
        internal bool TryClosePending()
        {
            if (Interlocked.CompareExchange(ref _state, Closed, Pending) != Pending) return false;
            if (!_closed.IsCancellationRequested) _closed.Cancel();
            return true;
        }
        internal void Start() { if (Volatile.Read(ref _state) == Active && Interlocked.Exchange(ref _started, 1) == 0) Completion = PumpAsync(); }
        internal bool Enqueue(byte[] value)
        {
            lock (_lock)
            {
                if (_closed.IsCancellationRequested || value.Length > _session._host.Options.MaximumQueuedEventBytesPerSubscription) return false;
                var countFull = _queue.Count >= _session._host.Options.MaximumQueuedEventsPerSubscription;
                var bytesFull = _bytes + value.Length > _session._host.Options.MaximumQueuedEventBytesPerSubscription;
                if (countFull || bytesFull)
                {
                    switch (Descriptor.Options.OverflowBehavior)
                    {
                        case NeoRpcOverflowBehavior.DropNewest: return false;
                        case NeoRpcOverflowBehavior.Fail: _closed.Cancel(); return false;
                        case NeoRpcOverflowBehavior.Coalesce:
                            var removed = _queue.Count;
                            _queue.Clear(); _bytes = 0;
                            while (removed-- > 0) _ready.Wait(0);
                            break;
                        case NeoRpcOverflowBehavior.DropOldest:
                            while (_queue.Count != 0 && (_queue.Count >= _session._host.Options.MaximumQueuedEventsPerSubscription || _bytes + value.Length > _session._host.Options.MaximumQueuedEventBytesPerSubscription))
                            {
                                _bytes -= _queue.Dequeue().Length;
                                _ready.Wait(0);
                            }
                            break;
                    }
                }
                _queue.Enqueue(value); _bytes += value.Length;
                try { _ready.Release(); } catch (SemaphoreFullException) { }
                return true;
            }
        }
        internal void Close() { Interlocked.Exchange(ref _state, Closed); if (!_closed.IsCancellationRequested) _closed.Cancel(); }
        internal void DisposePending()
        {
            if (Volatile.Read(ref _started) == 0) { _ready.Dispose(); _closed.Dispose(); }
        }
        private async Task PumpAsync()
        {
            try
            {
                while (true)
                {
                    await _ready.WaitAsync(_closed.Token).ConfigureAwait(false);
                    byte[]? item = null;
                    lock (_lock) { if (_queue.Count != 0) { item = _queue.Dequeue(); _bytes -= item.Length; } }
                    if (item is not null) await _session.SendEventAsync(_id, Interlocked.Increment(ref _sequence), item, _closed.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
            catch (Exception) { Close(); }
            finally { _session.RemoveSubscription(_id, this); _ready.Dispose(); _closed.Dispose(); }
        }
    }

    internal abstract class ChannelState
    {
        internal abstract Task Completion { get; }
        internal abstract void Acknowledge(long sequence);
        internal abstract void Close();
    }

    private sealed class ChannelState<T> : ChannelState
    {
        private readonly NeoRpcSession _session;
        private readonly string _id;
        private readonly NeoRpcChannel<T> _channel;
        private readonly CancellationTokenSource _closed;
        private readonly SemaphoreSlim _credits;
        private long _sent;
        private long _acknowledged;
        internal ChannelState(NeoRpcSession session, string id, NeoRpcChannel<T> channel, CancellationToken token)
        {
            _session = session; _id = id; _channel = channel;
            _closed = CancellationTokenSource.CreateLinkedTokenSource(session._closed.Token, token);
            _credits = new SemaphoreSlim(session._host.Options.MaximumUnacknowledgedChannelItems, session._host.Options.MaximumUnacknowledgedChannelItems);
        }
        private Task _completion = Task.CompletedTask;
        internal override Task Completion => _completion;
        internal void Start() => _completion = PumpAsync();
        internal override void Acknowledge(long sequence)
        {
            while (true)
            {
                var current = Volatile.Read(ref _acknowledged);
                var target = Math.Min(sequence, Volatile.Read(ref _sent));
                if (target <= current) return;
                if (Interlocked.CompareExchange(ref _acknowledged, target, current) == current)
                {
                    _credits.Release(checked((int)(target - current)));
                    return;
                }
            }
        }
        internal override void Close() { if (!_closed.IsCancellationRequested) _closed.Cancel(); }
        private async Task PumpAsync()
        {
            NeoRpcError? error = null;
            try
            {
                await foreach (var item in _channel.Items.WithCancellation(_closed.Token).ConfigureAwait(false))
                {
                    await _credits.WaitAsync(_closed.Token).ConfigureAwait(false);
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(item, _channel.ItemTypeInfo);
                    var sequence = Interlocked.Increment(ref _sent);
                    await _session.SendChannelItemAsync(_id, sequence, bytes, _closed.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
            catch (Exception)
            {
                error = FrameworkError(NeoRpcErrorCodes.InternalError, "The channel failed.", NewCorrelationId());
                _session._host.Diagnose(NeoRpcDiagnosticLevel.Error, NeoRpcErrorCodes.InternalError, "A channel source failed safely.", error.Value.CorrelationId);
            }
            finally
            {
                try { if (!_session._closed.IsCancellationRequested) await _session.SendChannelTerminalAsync(_id, error is null ? "channel_complete" : "channel_error", error, _session._closed.Token).ConfigureAwait(false); } catch { }
                _session.RemoveChannel(_id, this); _credits.Dispose(); _closed.Dispose();
            }
        }
    }
}
