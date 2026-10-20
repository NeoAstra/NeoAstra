// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace NeoAstra.Rpc;

/// <summary>Sends one complete JSON protocol frame to a frontend connection.</summary>
/// <param name="json">The complete JSON object.</param>
/// <param name="cancellationToken">Canceled when the session closes or when the channel or subscription that owns the frame closes; never for a committed result.</param>
/// <returns>A task representing delivery acceptance.</returns>
/// <remarks>
/// Throwing <see cref="OperationCanceledException"/> after <paramref name="cancellationToken"/> is canceled withdraws only that frame,
/// so the connection must still be able to carry later frames. Throwing <see cref="NeoRpcException"/> with the code
/// <see cref="NeoRpcErrorCodes.PayloadTooLarge"/> refuses only that frame, as too large for the connection: the call, subscription,
/// or channel it belongs to fails, and the session keeps serving. Any other failure is a transport failure and closes the session.
/// </remarks>
public delegate ValueTask NeoRpcSendFrame(string json, CancellationToken cancellationToken);

// Sends frames a session queued, in order, in one call: a view posts them in one turn of its UI thread.
internal enum NeoRpcFrameRefusal : byte { None = 0, TooLarge = 1, Refused = 2 }
// Returns null when every frame was posted, or an array with exactly frames.Count entries: None = posted,
// TooLarge = that frame alone was not posted because its envelope exceeds the view's MaximumMessageSize,
// Refused = that frame alone was not posted for another reason and the connection still carries later frames.
// The frames after a refused one are still attempted, in order. Throwing = the transport failed: every frame
// of the batch fails and the session closes (as today).
internal delegate ValueTask<NeoRpcFrameRefusal[]?> NeoRpcSendFrames(IReadOnlyList<string> frames);

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
    private readonly object _resourceLock = new();
    private readonly Dictionary<string, int> _activeResourcesByView = new(StringComparer.Ordinal);
    private int _activeInvocations;
    private int _activeResources;
    private long _activeResourceBytes;
    private int _disposed;
    private Task? _disposeTask;

    internal NeoRpcHost(NeoRpcOptions options, IReadOnlyDictionary<string, CommandDescriptor> commands, IReadOnlyDictionary<string, EventDescriptor> events, IReadOnlyList<INeoRpcServiceLifetimeOwner> serviceLifetimes)
    {
        _options = options;
        _commands = new Dictionary<string, CommandDescriptor>(commands, StringComparer.Ordinal);
        _events = new Dictionary<string, EventDescriptor>(events, StringComparer.Ordinal);
        _serviceLifetimes = serviceLifetimes.ToArray();
    }

    /// <summary>Gets the number of active document sessions.</summary>
    public int ActiveSessionCount => _sessions.Count;

    /// <summary>Creates a redacted snapshot of resolved policy, bounded limits, and current aggregate usage.</summary>
    /// <returns>The immutable support snapshot.</returns>
    public NeoRpcDiagnosticSnapshot GetDiagnosticSnapshot()
    {
        var limits = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["frameBytes"] = _options.MaximumFrameBytes, ["resultBytes"] = _options.MaximumResultBytes, ["jsonDepth"] = _options.MaximumJsonDepth,
            ["applicationInvocations"] = _options.MaximumConcurrentInvocations, ["sessionInvocations"] = _options.MaximumConcurrentInvocationsPerSession,
            ["retainedRequestIdsPerSession"] = _options.MaximumRetainedRequestIds,
            ["completedRequestIdsPerSession"] = Math.Min(_options.MaximumCompletedRequestIds, _options.MaximumRetainedRequestIds),
            ["requestRatePerSecond"] = _options.RequestRatePerSecond, ["requestBurst"] = _options.RequestRateBurst,
            ["subscriptionsPerSession"] = _options.MaximumSubscriptionsPerSession, ["channelsPerSession"] = _options.MaximumChannelsPerSession,
            ["resourcesPerSession"] = _options.MaximumResourcesPerSession, ["resourcesPerView"] = _options.MaximumResourcesPerView,
            ["applicationResources"] = _options.MaximumResources, ["applicationResourceBytes"] = _options.MaximumResourceBytes,
        };
        lock (_resourceLock) return new(_options.SecurityProfile.Name, _options.CapabilityManifest?.Hash, _sessions.Count, Volatile.Read(ref _activeInvocations), _activeResources, _activeResourceBytes, limits, _options.CapabilityManifest?.GrantSummaries ?? Array.Empty<string>());
    }

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
        => OpenSessionCore(identity, send, null, view, window);

    // A session of a view refuses what the transport of that view cannot carry.
    internal NeoRpcSession OpenSessionCore(NeoRpcSessionIdentity identity, NeoRpcSendFrame send, NeoRpcSendFrames? sendBatch, global::NeoAstra.NeoAstra? view, NeoWindow? window)
        => OpenSessionCore(identity, send, sendBatch, view, window, view is null ? NeoRpcOutboundLimits.None : NeoRpcOutboundLimits.Of(view.TransportLimits.MaximumMessageSize, view.TransportLimits.MaximumJsonDepth));

    internal NeoRpcSession OpenSessionCore(NeoRpcSessionIdentity identity, NeoRpcSendFrame send, NeoRpcSendFrames? sendBatch, global::NeoAstra.NeoAstra? view, NeoWindow? window, NeoRpcOutboundLimits limits)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(send);
        var snapshot = Snapshot(identity);
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var session = new NeoRpcSession(this, snapshot, send, sendBatch, view, window, limits, _shutdown.Token);
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
    /// <exception cref="AggregateException">One or more application-owned cancellation or service teardown callbacks failed after all owned state was closed.</exception>
    public ValueTask DisposeAsync()
    {
        lock (_lifecycleLock)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            Volatile.Write(ref _disposed, 1);
            var sessions = _sessions.Values.ToArray();
            _disposeTask = Task.Run(() => DisposeCoreAsync(sessions));
            return new(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(NeoRpcSession[] sessions)
    {
        List<Exception>? failures = null;
        try { _shutdown.Cancel(); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
        foreach (var session in sessions)
        {
            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        foreach (var lifetime in _serviceLifetimes)
        {
            try { await lifetime.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        _shutdown.Dispose();
        if (failures is not null) throw new AggregateException("One or more RPC teardown operations failed after all owned state was closed.", failures);
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

    internal bool TryAddResource(string viewLabel, long bytes)
    {
        lock (_resourceLock)
        {
            _activeResourcesByView.TryGetValue(viewLabel, out var viewCount);
            if (_activeResources >= _options.MaximumResources || viewCount >= _options.MaximumResourcesPerView || bytes > _options.MaximumResourceBytes - _activeResourceBytes) return false;
            _activeResources++; _activeResourceBytes += bytes; _activeResourcesByView[viewLabel] = viewCount + 1; return true;
        }
    }

    internal void RemoveResource(string viewLabel, long bytes)
    {
        lock (_resourceLock)
        {
            _activeResources--; _activeResourceBytes -= bytes; var count = _activeResourcesByView[viewLabel] - 1;
            if (count == 0) _activeResourcesByView.Remove(viewLabel); else _activeResourcesByView[viewLabel] = count;
        }
    }

    internal async ValueTask CloseSessionServicesAsync(string documentSessionId)
    {
        foreach (var lifetime in _serviceLifetimes) await lifetime.CloseSessionAsync(documentSessionId).ConfigureAwait(false);
    }

    // The scope of per-view services: the view of a view binding, or the label of a view whose sessions have none.
    internal async ValueTask CloseViewServicesAsync(object viewScope)
    {
        foreach (var lifetime in _serviceLifetimes) await lifetime.CloseViewAsync(viewScope).ConfigureAwait(false);
    }

    // A null recipient sends to every subscription: no context is built for it.
    internal async ValueTask<int> PublishAsync(EventDescriptor descriptor, byte[] json, Func<NeoRpcContext, bool>? recipient, CancellationToken cancellationToken)
    {
        var accepted = 0;
        var depth = -1;
        foreach (var session in _sessions.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (session.TryPublish(descriptor, json, recipient, ref depth)) accepted++;
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
            Platform = source.Platform,
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
    private readonly NeoRpcSendFrames? _sendBatch;
    // Every outbound frame (answers, events, channel items) waits in one ordered queue, and one writer sends what has
    // queued: a view posts it in one turn of its UI thread instead of one dispatcher round trip per frame.
    // Two readers: the writer, and teardown withdrawing what is still queued while the writer may still be running.
    private readonly Channel<OutboundFrame> _outbound = Channel.CreateUnbounded<OutboundFrame>(new UnboundedChannelOptions { SingleReader = false });
    private int _queuedOutbound;
    private int _writing;
    private readonly WeakReference<global::NeoAstra.NeoAstra>? _view;
    private readonly WeakReference<NeoWindow>? _window;
    private readonly CancellationTokenSource _closed;
    // Read once: the token stays usable after the source is disposed at the end of teardown, while the Token property of
    // a disposed source throws, and a publisher or a late frame may still look at the session then.
    private readonly CancellationToken _closedToken;
    private readonly ConcurrentDictionary<string, InvocationState> _requests = new(StringComparer.Ordinal);
    // Actual IDs and request-state objects are owned only while processing is active.
    private readonly Dictionary<string, RequestIdState> _usedRequestIds = new(StringComparer.Ordinal);
    private readonly HashSet<RequestIdFingerprint> _completedRequestIds = new();
    private readonly Queue<RequestIdFingerprint> _completedRequestIdOrder = new();
    private readonly object _requestIdsLock = new();
    private readonly ConcurrentDictionary<string, SubscriptionState> _subscriptions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ChannelState> _channels = new(StringComparer.Ordinal);
    private readonly NeoRpcResourceCollection _resources;
    private readonly TaskCompletionSource _invocationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _disposeLock = new();
    private int _activeInvocations;
    private int _disposed;
    private Task? _disposeTask;
    private long _nextChannelId;
    private long _unknownCancelCount;
    private int _subscriptionSlots;
    private readonly NeoRpcOutboundLimits _limits;
    private readonly object _rateLock = new();
    private readonly long _openedTimestamp;
    private double _rateTokens;
    private long _lastRateTimestamp;
    private int _abuseSeconds;
    private long _lastAbuseSecond;

    internal NeoRpcSession(NeoRpcHost host, NeoRpcSessionIdentity identity, NeoRpcSendFrame send, global::NeoAstra.NeoAstra? view, NeoWindow? window, CancellationToken shutdown)
        : this(host, identity, send, null, view, window, NeoRpcOutboundLimits.None, shutdown)
    {
    }

    internal NeoRpcSession(NeoRpcHost host, NeoRpcSessionIdentity identity, NeoRpcSendFrame send, NeoRpcSendFrames? sendBatch, global::NeoAstra.NeoAstra? view, NeoWindow? window, NeoRpcOutboundLimits limits, CancellationToken shutdown)
    {
        _host = host;
        _identity = identity;
        _send = send;
        _sendBatch = sendBatch;
        _limits = limits;
        _view = view is null ? null : new(view);
        _window = window is null ? null : new(window);
        _closed = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        _closedToken = _closed.Token;
        _resources = new NeoRpcResourceCollection(this, host.Options.MaximumResourcesPerSession);
        _rateTokens = host.Options.RequestRateBurst;
        _openedTimestamp = _lastRateTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    /// <summary>Gets the opaque trusted document-session ID.</summary>
    public string DocumentSessionId => _identity.DocumentSessionId;
    /// <summary>Gets the immutable trusted view label.</summary>
    public string ViewLabel => _identity.ViewLabel;
    /// <summary>Gets the cancellation token triggered by session or host teardown.</summary>
    /// <remarks>It remains readable after the session is disposed.</remarks>
    public CancellationToken Closed => _closedToken;
    /// <summary>Gets the current active invocation count.</summary>
    public int ActiveInvocationCount => Volatile.Read(ref _activeInvocations);
    // The frames queued for the writer and not yet taken into a batch (the queue of one reader does not count them).
    internal int QueuedOutboundFrameCount => Volatile.Read(ref _queuedOutbound);
    /// <summary>Gets the current number of tracked invocation/subscription identities, including active IDs and bounded completed fingerprints.</summary>
    /// <remarks>Completed ID strings and request-state objects are released; only fixed-size SHA-256 fingerprints remain for replay checks.</remarks>
    public int RetainedRequestIdCount { get { lock (_requestIdsLock) return _usedRequestIds.Count + _completedRequestIds.Count; } }
    /// <summary>Gets the number of request IDs pinned by processing invocations or pending/active subscriptions.</summary>
    /// <remarks>Cancellation or timeout does not release an invocation ID until backend work and terminal delivery stop.</remarks>
    public int ActiveRequestIdCount { get { lock (_requestIdsLock) return _usedRequestIds.Count; } }
    /// <summary>Gets the capacity for new unique IDs, including slots recoverable by evicting completed history.</summary>
    public int RemainingRequestIdCapacity { get { lock (_requestIdsLock) return _host.Options.MaximumRetainedRequestIds - _usedRequestIds.Count; } }
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
    public ValueTask ReceiveAsync(string json, Uri? sourceOrigin, bool isMainFrame, CancellationToken cancellationToken = default)
        => ReceiveAsync(json, sourceOrigin, isMainFrame, System.Diagnostics.Stopwatch.GetTimestamp(), cancellationToken);

    // A frame is charged to the rate of the session when it arrived (a Stopwatch timestamp), not when it is processed:
    // a reader that falls behind does not turn a steady page into one over its rate.
    internal async ValueTask ReceiveAsync(string json, Uri? sourceOrigin, bool isMainFrame, long arrival, CancellationToken cancellationToken)
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
                    await ReceiveInvokeAsync(root, sourceOrigin, isMainFrame, arrival, cancellationToken).ConfigureAwait(false);
                    break;
                case "cancel":
                    ReceiveCancel(root);
                    break;
                case "subscribe":
                    await ReceiveSubscribeAsync(root, sourceOrigin, isMainFrame, arrival, cancellationToken).ConfigureAwait(false);
                    break;
                case "unsubscribe":
                    await ReceiveUnsubscribeAsync(root).ConfigureAwait(false);
                    break;
                case "channel_ack":
                    ReceiveChannelAck(root);
                    break;
                case "channel_close":
                    ReceiveChannelClose(root);
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
    /// <exception cref="AggregateException">One or more application-owned cancellation or service teardown callbacks failed after all session state was closed.</exception>
    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            Volatile.Write(ref _disposed, 1);
            _disposeTask = Task.Run(DisposeCoreAsync);
            return new(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        List<Exception>? failures = null;
        try { _closed.Cancel(); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
        foreach (var request in _requests.Values)
        {
            try { request.Cancel(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        foreach (var channel in _channels.Values)
        {
            try { channel.Close(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        if (Volatile.Read(ref _activeInvocations) == 0) _invocationsDrained.TrySetResult();
        try { await _invocationsDrained.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { _host.Diagnose(NeoRpcDiagnosticLevel.Warning, "teardown_timeout", "RPC invocation teardown exceeded five seconds."); }
        foreach (var subscription in _subscriptions.Values) subscription.Close();
        var channels = _channels.Values.Select(static value => value.Completion).ToArray();
        if (channels.Length != 0)
        {
            var drained = Task.WhenAll(channels);
            try
            {
                try { await drained.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    _host.Diagnose(NeoRpcDiagnosticLevel.Warning, "teardown_timeout", "RPC channel cleanup is still pending; services remain alive until enumeration stops.");
                    await drained.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { _host.Diagnose(NeoRpcDiagnosticLevel.Warning, "teardown_pump_failure", "An RPC pump failed while the session was closing."); }
        }
        try { await _resources.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
        try { await _host.CloseSessionServicesAsync(DocumentSessionId).ConfigureAwait(false); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
        var subscriptions = _subscriptions.Values.Select(static value => value.Completion).ToArray();
        if (subscriptions.Length != 0)
        {
            try { await Task.WhenAll(subscriptions).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException) { }
            catch (Exception) { _host.Diagnose(NeoRpcDiagnosticLevel.Warning, "teardown_pump_failure", "An RPC pump failed while the session was closing."); }
        }
        _subscriptions.Clear();
        Volatile.Write(ref _subscriptionSlots, 0);
        _channels.Clear();
        _requests.Clear();
        lock (_requestIdsLock)
        {
            _usedRequestIds.Clear();
            _completedRequestIds.Clear();
            _completedRequestIdOrder.Clear();
        }
        _host.Remove(this);
        CompleteOutbound();
        _closed.Dispose();
        if (failures is not null) throw new AggregateException("One or more RPC session teardown callbacks failed after all session state was closed.", failures);
    }

    internal void DisposeWithoutCallback()
    {
        lock (_disposeLock)
        {
            if (_disposeTask is not null) return;
            Volatile.Write(ref _disposed, 1);
            _closed.Cancel();
            _closed.Dispose();
            CompleteOutbound();
            _disposeTask = Task.CompletedTask;
        }
    }

    // No frame is queued once the session is closed; frames still waiting when it closed are failed as withdrawn.
    private void CompleteOutbound()
    {
        _outbound.Writer.TryComplete();
        while (_outbound.Reader.TryRead(out var frame))
        {
            Interlocked.Decrement(ref _queuedOutbound);
            if (frame.TryTake()) frame.Cancel(CancellationToken.None);
        }
    }

    // The depth of the event is measured once for every session that needs it.
    internal bool TryPublish(EventDescriptor descriptor, byte[] json, Func<NeoRpcContext, bool>? recipient, ref int measuredDepth)
    {
        var accepted = false;
        foreach (var subscription in _subscriptions.Values)
        {
            if (!ReferenceEquals(subscription.Descriptor, descriptor)) continue;
            if (recipient is not null && !recipient(CreateContext(NewCorrelationId(), _closedToken, subscription.SourceOrigin, subscription.IsMainFrame))) continue;
            // The page would drop it without a word: the subscription fails instead of missing it silently.
            if (_limits.IsTooDeep(json, ref measuredDepth))
            {
                subscription.Fail(new NeoRpcFrameRefusedException(NeoRpcErrorCodes.SerializationFailed, "The event is nested deeper than the transport of the view accepts."));
                continue;
            }
            accepted |= subscription.Enqueue(json);
        }
        return accepted;
    }

    internal ChannelState PrepareChannel<T>(string channelId, NeoRpcChannel<T> channel)
        => new ChannelState<T>(this, channelId, channel);

    private bool TryRegisterChannel(string channelId, ChannelState state)
    {
        lock (_disposeLock)
        {
            if (_disposed != 0 || _closed.IsCancellationRequested || _channels.Count >= _host.Options.MaximumChannelsPerSession) return false;
            if (!_channels.TryAdd(channelId, state)) throw new InvalidOperationException("A generated channel ID collided.");
            return true;
        }
    }

    // The last event of what a pump took from its queue is awaited, so that one subscription holds at most one batch of
    // events in the session queue and its own bounded queue applies its overflow policy; the others only queue.
    // A frame too large for the view is refused before it is queued (NeoRpcFrameRefusedException), and so is a channel item
    // nested deeper than its transport accepts; one the view refuses when it is posted is reported to its owner.
    private ValueTask SendEventAsync(string subscriptionId, long sequence, byte[] json, bool awaitDelivery, IStreamFrameOwner owner, CancellationToken cancellationToken)
    {
        var frame = BuildFrame(writer =>
        {
            writer.WriteStartObject();
            WriteHeader(writer, "event");
            writer.WriteString("subscription", subscriptionId);
            writer.WriteNumber("sequence", sequence);
            writer.WritePropertyName("value"); writer.WriteRawValue(json, skipInputValidation: true);
            writer.WriteEndObject();
        });
        if (_limits.IsTooLarge(frame)) throw new NeoRpcFrameRefusedException(NeoRpcErrorCodes.PayloadTooLarge, NeoRpcFrameRefusedException.TooLargeMessage);
        if (awaitDelivery) return SendRawAsync(frame, cancellationToken);
        QueueStreamFrame(frame, cancellationToken, owner);
        return ValueTask.CompletedTask;
    }

    // Channel items are only queued: the unacknowledged-item credits of the channel bound how many are in flight.
    private void QueueChannelItem(string channelId, long sequence, byte[] json, IStreamFrameOwner owner, CancellationToken cancellationToken)
    {
        if (_limits.IsTooDeep(json)) throw new NeoRpcFrameRefusedException(NeoRpcErrorCodes.SerializationFailed, "A channel item is nested deeper than the transport of the view accepts.");
        var frame = BuildFrame(writer =>
        {
            writer.WriteStartObject(); WriteHeader(writer, "channel_item"); writer.WriteString("channel", channelId);
            writer.WriteNumber("sequence", sequence); writer.WritePropertyName("value"); writer.WriteRawValue(json, true); writer.WriteEndObject();
        });
        if (_limits.IsTooLarge(frame)) throw new NeoRpcFrameRefusedException(NeoRpcErrorCodes.PayloadTooLarge, NeoRpcFrameRefusedException.TooLargeMessage);
        QueueStreamFrame(frame, cancellationToken, owner);
    }

    private void DiagnoseRefusedStream(NeoRpcFrameRefusedException refusal, string what)
        => _host.Diagnose(NeoRpcDiagnosticLevel.Warning, refusal.Code, $"An RPC {what} could not be delivered to its view and was closed: {refusal.Message}");

    internal ValueTask SendChannelTerminalAsync(string channelId, string kind, NeoRpcError? error, CancellationToken cancellationToken)
        => SendRawAsync(BuildFrame(writer =>
        {
            writer.WriteStartObject(); WriteHeader(writer, kind); writer.WriteString("channel", channelId);
            if (error is { } value) { writer.WritePropertyName("error"); WriteError(writer, value); }
            writer.WriteEndObject();
        }), cancellationToken);

    internal void RemoveChannel(string id, ChannelState state) => _channels.TryRemove(new KeyValuePair<string, ChannelState>(id, state));
    internal bool TryAddResource(long bytes, long sessionBytes) => bytes >= 0 && bytes <= _host.Options.MaximumResourceBytesPerSession - sessionBytes && _host.TryAddResource(ViewLabel, bytes);
    internal void RemoveResource(long bytes) => _host.RemoveResource(ViewLabel, bytes);
    private void RemoveSubscription(string id, SubscriptionState state)
    {
        if (_subscriptions.TryRemove(new KeyValuePair<string, SubscriptionState>(id, state))) Interlocked.Decrement(ref _subscriptionSlots);
    }

    private async ValueTask ReceiveInvokeAsync(JsonElement root, Uri? sourceOrigin, bool isMainFrame, long arrival, CancellationToken receiveCancellation)
    {
        if (!TryValidId(root, "id", out var id) || !TryString(root, "command", out var command) ||
            !NeoRpcValidation.IsWireName(command, _host.Options.MaximumWireNameLength) ||
            !root.TryGetProperty("args", out var args))
        {
            if (!string.IsNullOrEmpty(id)) await SendErrorResultAsync(id, FrameworkError(NeoRpcErrorCodes.InvalidRequest, "The invocation frame is invalid.", null)).ConfigureAwait(false);
            return;
        }
        if (!TryAdmitRate(arrival))
        {
            await SendErrorResultAsync(id, FrameworkError(NeoRpcErrorCodes.TooManyRequests, "The RPC request-rate limit is exhausted.", null, true)).ConfigureAwait(false);
            await HandleAbuseAsync(arrival).ConfigureAwait(false);
            return;
        }
        var requestId = RememberRequestId(id, out var admissionError);
        if (requestId is null)
        {
            await SendErrorResultAsync(id, admissionError).ConfigureAwait(false);
            return;
        }
        try { await InvokeCoreAsync(root, id, command, args, sourceOrigin, isMainFrame, receiveCancellation).ConfigureAwait(false); }
        finally { CompleteRequestId(requestId); }
    }

    private async ValueTask InvokeCoreAsync(JsonElement root, string id, string command, JsonElement args, Uri? sourceOrigin, bool isMainFrame, CancellationToken receiveCancellation)
    {
        if (!ContractMatches(root))
        {
            await SendErrorResultAsync(id, ContractMismatchError(root)).ConfigureAwait(false);
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
            await SendErrorResultAsync(id, FrameworkError(NeoRpcErrorCodes.TooManyRequests, "An RPC concurrency limit is exhausted.", null, retryable: true)).ConfigureAwait(false);
            return;
        }
        var enteredCommand = false;

        var correlationId = NewCorrelationId();
        var timeout = descriptor.Options.Timeout ?? _host.Options.InvocationTimeout;
        var state = new InvocationState(
            _closedToken,
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
        INeoRpcChannel? returnedChannel = null;
        ChannelState? preparedChannel = null;
        try
        {
            var authorization = await AuthorizeAsync(context, command, descriptor.Options.Permission, isSubscription: false, args, state.Token).ConfigureAwait(false);
            var authorizationError = authorization.Error;
            if (authorizationError is { } denied)
            {
                await state.TryCommitAsync(() => SendTerminalErrorResultAsync(id, denied)).ConfigureAwait(false);
                return;
            }
            context = authorization.Context;
            if (!descriptor.TryEnter())
            {
                await state.TryCommitAsync(() => SendTerminalErrorResultAsync(id, FrameworkError(NeoRpcErrorCodes.TooManyRequests, "The RPC command concurrency limit is exhausted.", correlationId, retryable: true))).ConfigureAwait(false);
                return;
            }
            enteredCommand = true;

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
                        () => InvokeHandlerAsync(request), state.Token).ConfigureAwait(false);
                }
                else
                {
                    // Frames may be received on the UI dispatcher (a view binding receives them there): leave it,
                    // and its synchronization context, before any application code runs, so synchronous work
                    // before the handler's first await cannot block the UI or the frames that follow.
                    applicationResult = await Task.Run(() => InvokeHandlerAsync(request).AsTask()).ConfigureAwait(false);
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
                var channelId = $"ch-{Interlocked.Increment(ref _nextChannelId):x}";
                preparedChannel = channelResult.Prepare(this, channelId);
                returnedChannel = null; // The prepared state owns cleanup even if it is never registered or started.
                if (!TryRegisterChannel(channelId, preparedChannel))
                {
                    await state.TryCommitAsync(() => SendTerminalErrorResultAsync(id, FrameworkError(NeoRpcErrorCodes.TooManyRequests, "The channel limit is exhausted.", correlationId, true))).ConfigureAwait(false);
                    return;
                }
                var committed = await state.TryCommitAsync(() => SendChannelResultAsync(id, channelId)).ConfigureAwait(false);
                if (committed)
                {
                    preparedChannel.Start();
                    preparedChannel = null; // The session tracks the running (or already closed) state through cleanup.
                }
            }
            else
            {
                await state.TryCommitAsync(() => SendSuccessResultAsync(id, result, correlationId)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (state.Token.IsCancellationRequested)
        {
            await state.TerminalCompletion.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Preparation/registration failures must also choose a terminal state before the cleanup guard awaits it.
            await state.TryCommitAsync(() => SendTerminalErrorResultAsync(id, MapException(exception, context, state))).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                try
                {
                    if (preparedChannel is not null)
                    {
                        try { preparedChannel.Close(); }
                        finally { await preparedChannel.Completion.ConfigureAwait(false); }
                    }
                    if (returnedChannel is not null) await returnedChannel.DisposeAsync().ConfigureAwait(false);
                }
                finally { await state.TerminalCompletion.ConfigureAwait(false); }
            }
            finally
            {
                _requests.TryRemove(new KeyValuePair<string, InvocationState>(id, state));
                state.Dispose();
                if (enteredCommand) descriptor.Exit();
                ExitInvocation();
            }
        }

        async ValueTask<object?> InvokeHandlerAsync(object request)
        {
            var value = await descriptor.InvokeHandlerAsync(request, context, state.Token).ConfigureAwait(false);
            returnedChannel = value as INeoRpcChannel;
            return value;
        }
    }

    private void ReceiveCancel(JsonElement root)
    {
        if (!TryValidId(root, "id", out var id)) return;
        if (_requests.TryGetValue(id, out var state)) state.Cancel();
        else if (Interlocked.Increment(ref _unknownCancelCount) <= 100)
            _host.Diagnose(NeoRpcDiagnosticLevel.Debug, "late_cancel", "A cancel for an unknown or completed request was ignored.");
    }

    private async ValueTask ReceiveSubscribeAsync(JsonElement root, Uri? sourceOrigin, bool isMainFrame, long arrival, CancellationToken cancellationToken)
    {
        if (!TryValidId(root, "id", out var id) || !TryString(root, "event", out var eventName) ||
            !_host.TryGetEvent(eventName, out var descriptor) || descriptor is null)
        {
            if (!string.IsNullOrEmpty(id)) await SendSubscriptionErrorAsync(id, FrameworkError(NeoRpcErrorCodes.InvalidRequest, "The subscription frame or event is invalid.", null)).ConfigureAwait(false);
            return;
        }
        if (!TryAdmitRate(arrival))
        {
            await SendSubscriptionErrorAsync(id, FrameworkError(NeoRpcErrorCodes.TooManyRequests, "The RPC request-rate limit is exhausted.", null, true)).ConfigureAwait(false);
            await HandleAbuseAsync(arrival).ConfigureAwait(false);
            return;
        }
        if (!ContractMatches(root))
        {
            await SendSubscriptionErrorAsync(id, ContractMismatchError(root)).ConfigureAwait(false);
            return;
        }
        if (Interlocked.Increment(ref _subscriptionSlots) > _host.Options.MaximumSubscriptionsPerSession)
        {
            Interlocked.Decrement(ref _subscriptionSlots);
            await SendSubscriptionErrorAsync(id, FrameworkError(NeoRpcErrorCodes.TooManyRequests, "The subscription limit is exhausted.", null, true)).ConfigureAwait(false);
            return;
        }
        var requestId = RememberRequestId(id, out var admissionError);
        if (requestId is null)
        {
            Interlocked.Decrement(ref _subscriptionSlots);
            await SendSubscriptionErrorAsync(id, admissionError).ConfigureAwait(false);
            return;
        }
        SubscriptionState subscription;
        try { subscription = new SubscriptionState(this, requestId, descriptor, sourceOrigin, isMainFrame); }
        catch
        {
            Interlocked.Decrement(ref _subscriptionSlots);
            CompleteRequestId(requestId);
            throw;
        }
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
            try { error = (await AuthorizeAsync(context, eventName, descriptor.Options.Permission, true, default, subscription.Token).ConfigureAwait(false)).Error; }
            catch (OperationCanceledException) when (subscription.Token.IsCancellationRequested) { return; }
            if (error is { } denied)
            {
                if (subscription.TryClosePending()) await SendSubscriptionErrorAsync(id, denied).ConfigureAwait(false);
                return;
            }
            if (!subscription.TryActivate()) return;
            await SendRawAsync(BuildFrame(writer => { writer.WriteStartObject(); WriteHeader(writer, "subscribed"); writer.WriteString("id", id); writer.WriteEndObject(); }), _closedToken).ConfigureAwait(false);
            subscription.Start();
        }
        catch
        {
            RemoveSubscription(id, subscription);
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

    private void ReceiveChannelClose(JsonElement root)
    {
        if (!TryValidId(root, "channel", out var id)) return;
        // This acknowledges cancellation, not completed cleanup: the transport can reenter while a pump is sending.
        // The session retains the state and awaits its stable completion during teardown instead.
        if (_channels.TryGetValue(id, out var state)) state.Close();
    }

    private async ValueTask ReceiveResourceCloseAsync(JsonElement root)
    {
        if (TryValidId(root, "resource", out var id)) await _resources.CloseAsync(id).ConfigureAwait(false);
    }

    private async ValueTask<(NeoRpcError? Error, NeoRpcContext Context)> AuthorizeAsync(NeoRpcContext context, string operation, string? permission, bool isSubscription, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (permission is null) return (null, context);
        var service = _host.Options.AuthorizationService;
        if (service is null)
        {
            _host.Diagnose(NeoRpcDiagnosticLevel.Warning, NeoCapabilityDecisionCodes.NoMatchingCapability, "No capability authorization service is configured for this explicitly restricted RPC operation.", context.CorrelationId);
            return (FrameworkError(NeoRpcErrorCodes.PermissionDenied, "Permission was denied.", context.CorrelationId), context);
        }
        NeoRpcAuthorizationResult decision;
        try { decision = await service.AuthorizeAsync(new(context, operation, permission, isSubscription, arguments), cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            _host.Diagnose(NeoRpcDiagnosticLevel.Error, NeoRpcErrorCodes.InternalError, "RPC authorization failed safely.", context.CorrelationId);
            return (FrameworkError(NeoRpcErrorCodes.InternalError, "RPC authorization failed internally.", context.CorrelationId), context);
        }
        if (decision.IsAllowed)
        {
            var attached = decision.Decision ?? new NeoRpcAuthorizationDecision(permission, decision.DecisionCode ?? NeoCapabilityDecisionCodes.Allowed, Array.Empty<NeoCapabilityScope>());
            return (null, context.WithAuthorization(attached));
        }
        _host.Diagnose(NeoRpcDiagnosticLevel.Warning, decision.DecisionCode ?? NeoCapabilityDecisionCodes.PermissionMissing, "RPC authorization denied the operation.", context.CorrelationId);
        var code = decision.ErrorCode is NeoRpcErrorCodes.ScopeDenied ? NeoRpcErrorCodes.ScopeDenied : NeoRpcErrorCodes.PermissionDenied;
        return (FrameworkError(code, code == NeoRpcErrorCodes.ScopeDenied ? "The command is outside the allowed scope." : "Permission was denied.", context.CorrelationId), context);
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
        // The session snapshot and its weak references are shared: a context neither copies nor validates them again.
        => new(_identity, sourceOrigin, isMainFrame, correlationId, cancellationToken, _resources, _view, _window);

    private RequestIdState? RememberRequestId(string id, out NeoRpcError error)
    {
        var fingerprint = RequestIdFingerprint.Create(id);
        lock (_requestIdsLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            // Check duplicates before eviction, including when the history is full.
            if (_usedRequestIds.ContainsKey(id) || _completedRequestIds.Contains(fingerprint))
            {
                error = FrameworkError(NeoRpcErrorCodes.DuplicateRequest, "The request ID is active or retained in completed history.", null);
                return null;
            }
            if (_usedRequestIds.Count + _completedRequestIds.Count >= _host.Options.MaximumRetainedRequestIds && _completedRequestIdOrder.TryDequeue(out var completed))
                _completedRequestIds.Remove(completed);
            if (_usedRequestIds.Count + _completedRequestIds.Count < _host.Options.MaximumRetainedRequestIds)
            {
                var requestId = new RequestIdState(id, fingerprint);
                _usedRequestIds.Add(id, requestId);
                error = default;
                return requestId;
            }
        }
        // Application-owned diagnostic callbacks must not run under the admission lock.
        _host.Diagnose(NeoRpcDiagnosticLevel.Warning, NeoRpcErrorCodes.RequestIdCapacityExhausted,
            $"RPC request-ID capacity is exhausted: retained={_host.Options.MaximumRetainedRequestIds}, remaining=0. All retained IDs are active.");
        error = FrameworkError(NeoRpcErrorCodes.RequestIdCapacityExhausted, "All retained request-ID slots are active. Wait for backend work or subscriptions to close.", null, retryable: true);
        return null;
    }

    private void CompleteRequestId(RequestIdState requestId)
    {
        lock (_requestIdsLock)
        {
            // The ownership check prevents late cleanup from retiring a subsequently reused ID.
            if (!_usedRequestIds.TryGetValue(requestId.Id, out var retained) || !ReferenceEquals(retained, requestId)) return;
            _usedRequestIds.Remove(requestId.Id);
            if (Volatile.Read(ref _disposed) != 0) return;
            if (_completedRequestIds.Add(requestId.Fingerprint)) _completedRequestIdOrder.Enqueue(requestId.Fingerprint);
            if (_completedRequestIds.Count > Math.Min(_host.Options.MaximumCompletedRequestIds, _host.Options.MaximumRetainedRequestIds))
                _completedRequestIds.Remove(_completedRequestIdOrder.Dequeue());
        }
    }

    private bool TryAdmitRate(long arrival)
    {
        // No rate is enforced unless the application configures one.
        if (_host.Options.RequestRatePerSecond == 0) return true;
        lock (_rateLock)
        {
            // Frames are processed in the order they arrived: a stamp older than the last one charged counts as the same moment.
            var now = Math.Max(arrival, _lastRateTimestamp);
            var elapsed = (now - _lastRateTimestamp) / (double)System.Diagnostics.Stopwatch.Frequency;
            _lastRateTimestamp = now;
            _rateTokens = Math.Min(_host.Options.RequestRateBurst, _rateTokens + elapsed * _host.Options.RequestRatePerSecond);
            if (_rateTokens < 1) return false;
            _rateTokens -= 1;
            return true;
        }
    }

    private async ValueTask HandleAbuseAsync(long arrival)
    {
        var (seconds, newSecond) = CountAbuseDenialCore(arrival);
        // One warning for each second with denials: a burst does not flood the diagnostic sink either.
        if (newSecond) _host.Diagnose(NeoRpcDiagnosticLevel.Warning, NeoCapabilityDecisionCodes.LimitExceeded, "A document session exceeded its bounded RPC request rate.");
        if (seconds >= _host.Options.AbuseClosureThreshold) await DisposeAsync().ConfigureAwait(false);
    }

    internal int CountAbuseDenial(long timestamp) => CountAbuseDenialCore(timestamp).Seconds;

    // The denials of one second (counted from the opening of the session, when the frames arrived) count once, and one
    // such second is forgotten for each second without a denial. A burst, however large, counts one second, so only a page
    // that keeps sending more than its rate for AbuseClosureThreshold seconds is closed; denials scattered over a long
    // session never add up.
    private (int Seconds, bool NewSecond) CountAbuseDenialCore(long timestamp)
    {
        lock (_rateLock)
        {
            var second = Math.Max(0, timestamp - _openedTimestamp) / System.Diagnostics.Stopwatch.Frequency;
            // A stamp older than the last second counted counts with it, as frames are charged in the order they arrived.
            if (_abuseSeconds != 0 && second <= _lastAbuseSecond) return (_abuseSeconds, false);
            var quiet = _abuseSeconds == 0 ? 0 : second - _lastAbuseSecond - 1;
            _abuseSeconds = (int)Math.Max(0, _abuseSeconds - quiet) + 1;
            _lastAbuseSecond = second;
            return (_abuseSeconds, true);
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

    // A result the page could not receive fails its call alone, with an answer: its caller learns why, and the session
    // keeps serving the others. The page drops a frame deeper than its transport accepts without answering anything.
    private async ValueTask SendSuccessResultAsync(string id, CommandResult result, string correlationId)
    {
        if (result is ValueCommandResult deep && _limits.IsTooDeep(deep.Json))
        {
            _host.Diagnose(NeoRpcDiagnosticLevel.Warning, NeoRpcErrorCodes.SerializationFailed, "An RPC result was nested deeper than the transport of its view accepts and was replaced by an error.", correlationId);
            await SendTerminalErrorResultAsync(id, FrameworkError(NeoRpcErrorCodes.SerializationFailed, "The command result is nested deeper than the transport of the view accepts.", correlationId)).ConfigureAwait(false);
            return;
        }
        var frame = BuildFrame(writer =>
        {
            writer.WriteStartObject(); WriteHeader(writer, "result"); writer.WriteString("id", id); writer.WriteBoolean("ok", true); writer.WritePropertyName("value");
            if (result is ValueCommandResult value) writer.WriteRawValue(value.Json, true); else writer.WriteNullValue();
            writer.WriteEndObject();
        });
        var maximum = _host.Options.MaximumResultBytes;
        if (maximum != int.MaxValue && Encoding.UTF8.GetByteCount(frame) > maximum)
        {
            _host.Diagnose(NeoRpcDiagnosticLevel.Warning, NeoRpcErrorCodes.PayloadTooLarge, "An RPC result exceeded the configured byte limit and was replaced by an error.", correlationId);
            await SendTerminalErrorResultAsync(id, FrameworkError(NeoRpcErrorCodes.PayloadTooLarge, "The command result exceeds the configured size limit.", correlationId)).ConfigureAwait(false);
            return;
        }
        try
        {
            if (_limits.IsTooLarge(frame)) throw new NeoRpcFrameRefusedException(NeoRpcErrorCodes.PayloadTooLarge, NeoRpcFrameRefusedException.TooLargeMessage);
            await SendRawAsync(frame, CancellationToken.None, allowClosing: true).ConfigureAwait(false);
        }
        catch (NeoRpcFrameRefusedException refusal)
        {
            // Refused before it was sent, or by the view when it was posted: the answer is the reason instead.
            _host.Diagnose(NeoRpcDiagnosticLevel.Warning, refusal.Code, "An RPC result could not be delivered to its view and was replaced by an error.", correlationId);
            await SendTerminalErrorResultAsync(id, FrameworkError(refusal.Code, refusal.Message, correlationId)).ConfigureAwait(false);
        }
    }

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
        }), _closedToken);

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
        }), _closedToken);

    private async ValueTask SendRawAsync(string json, CancellationToken cancellationToken, bool allowClosing = false)
    {
        if (!allowClosing && Volatile.Read(ref _disposed) != 0) throw new OperationCanceledException("The RPC session is closed.", _closedToken);
        cancellationToken.ThrowIfCancellationRequested();
        var frame = new OutboundFrame(json, cancellationToken, awaited: true);
        if (!TryQueue(frame)) throw new OperationCanceledException("The RPC session is closed.");
        // The caller withdraws a frame still waiting in the queue with its own token (a closed channel or
        // subscription): it is not sent, and the transport did not fail.
        using (cancellationToken.CanBeCanceled ? cancellationToken.Register(static state => ((OutboundFrame)state!).TryWithdraw(), frame) : default)
        {
            StartWriter();
            await frame.Done!.Task.ConfigureAwait(false);
        }
    }

    // Queues a frame of a stream (an event or a channel item) without waiting for it to be sent: the frames of a stream
    // leave in batches instead of one round trip each. Nobody waits for it, so it is withdrawn when its token is canceled
    // before the writer takes it, a failure of the transport closes the session, which ends its stream, and a refusal of
    // the frame alone is reported to its stream.
    private void QueueStreamFrame(string json, CancellationToken cancellationToken, IStreamFrameOwner owner)
    {
        if (Volatile.Read(ref _disposed) != 0 || cancellationToken.IsCancellationRequested) return;
        if (TryQueue(new OutboundFrame(json, cancellationToken, awaited: false, owner))) StartWriter();
    }

    private bool TryQueue(OutboundFrame frame)
    {
        Interlocked.Increment(ref _queuedOutbound);
        if (_outbound.Writer.TryWrite(frame)) return true;
        Interlocked.Decrement(ref _queuedOutbound);
        return false;
    }

    private void StartWriter()
    {
        if (Interlocked.CompareExchange(ref _writing, 1, 0) == 0) _ = Task.Run(WriteAsync);
    }

    // The one writer of the session: it takes every frame waiting and sends them in order, in one call when the
    // transport takes a batch. A frame withdrawn before it was taken is skipped. A frame the view refuses alone (too
    // large for its messages) fails its own call, subscription, or channel; a failure of the transport fails the frames
    // it held and closes the session, as a failed send did.
    private async Task WriteAsync()
    {
        var batch = new List<OutboundFrame>();
        while (true)
        {
            batch.Clear();
            while (batch.Count < MaximumOutboundBatch && _outbound.Reader.TryRead(out var next))
            {
                Interlocked.Decrement(ref _queuedOutbound);
                // A frame of a closed stream is withdrawn here: nobody registered to withdraw it earlier.
                if (next.Token.IsCancellationRequested) next.TryWithdraw();
                else if (next.TryTake()) batch.Add(next);
            }
            if (batch.Count == 0)
            {
                Volatile.Write(ref _writing, 0);
                // A frame queued after the last read and before the flag was cleared is written by this writer.
                if (!_outbound.Reader.TryPeek(out _) || Interlocked.CompareExchange(ref _writing, 1, 0) != 0) return;
                continue;
            }

            if (_sendBatch is { } sendBatch)
            {
                var frames = new string[batch.Count];
                for (var index = 0; index < frames.Length; index++) frames[index] = batch[index].Json;
                NeoRpcFrameRefusal[]? refusals;
                try
                {
                    refusals = await sendBatch(frames).ConfigureAwait(false);
                    if (refusals is not null && refusals.Length != frames.Length) throw new InvalidOperationException("The view reported refusals for a different number of frames.");
                }
                catch (Exception exception)
                {
                    CloseAfterSendFailure();
                    foreach (var frame in batch) frame.Fail(exception);
                    continue;
                }
                for (var index = 0; index < batch.Count; index++)
                {
                    if (refusals is null || refusals[index] == NeoRpcFrameRefusal.None) batch[index].Complete();
                    else batch[index].Refuse(new NeoRpcFrameRefusedException(refusals[index]));
                }

                continue;
            }

            foreach (var frame in batch)
            {
                try
                {
                    await _send(frame.Json, frame.Token).ConfigureAwait(false);
                    frame.Complete();
                }
                // The caller withdrew its own frame while it was sent (a closed channel or subscription): the
                // transport did not fail. Session teardown cancels every caller token too.
                catch (OperationCanceledException exception) when (frame.Token.IsCancellationRequested) { frame.Cancel(exception.CancellationToken); }
                // The connection refused this frame alone, as too large for it.
                catch (NeoRpcException exception) when (exception.Code == NeoRpcErrorCodes.PayloadTooLarge) { frame.Refuse(new NeoRpcFrameRefusedException(NeoRpcFrameRefusal.TooLarge)); }
                catch (Exception exception)
                {
                    CloseAfterSendFailure();
                    frame.Fail(exception);
                }
            }
        }
    }

    private void CloseAfterSendFailure()
    {
        try { _closed.Cancel(); }
        catch (ObjectDisposedException) { /* The session finished closing already. */ }
    }

    // The most frames one write takes: one turn of a UI thread stays bounded.
    private const int MaximumOutboundBatch = 256;

    // The subscription or channel of a stream frame, told when the view refused that frame alone.
    private interface IStreamFrameOwner
    {
        void OnFrameRefused(NeoRpcFrameRefusedException refusal);
    }

    // A frame that a caller awaits has a completion; a frame of a stream has none, and nothing observes its outcome but
    // the stream it belongs to, when the view refuses it.
    private sealed class OutboundFrame(string json, CancellationToken token, bool awaited, IStreamFrameOwner? owner = null)
    {
        private int _state; // 0 queued, 1 taken by the writer, 2 withdrawn by its caller
        public string Json { get; } = json;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource? Done { get; } = awaited ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        public bool TryTake() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;
        public void TryWithdraw()
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) == 0) Done?.TrySetCanceled(Token);
        }
        public void Complete() => Done?.TrySetResult();
        public void Cancel(CancellationToken cancellationToken) => Done?.TrySetCanceled(cancellationToken);
        public void Fail(Exception exception) => Done?.TrySetException(exception);
        public void Refuse(NeoRpcFrameRefusedException refusal)
        {
            if (Done is not null) Done.TrySetException(refusal);
            else owner?.OnFrameRefused(refusal);
        }
    }

    private bool TryValidId(JsonElement root, string property, out string value)
    {
        value = string.Empty;
        return TryString(root, property, out value) && value.Length <= _host.Options.MaximumIdLength &&
               !value.AsSpan().ContainsAnyExceptInRange((char)0x21, (char)0x7e);
    }

    private static bool IsProtocolFrame(JsonElement root)
        => root.TryGetProperty("neoastra", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var discriminator) && discriminator == 1;

    private bool ContractMatches(JsonElement root)
    {
        if (_host.Options.ContractHash.Length == 0) return true;
        return TryString(root, "contract", out var hash) && string.Equals(hash, _host.Options.ContractHash, StringComparison.Ordinal);
    }

    private NeoRpcError ContractMismatchError(JsonElement root)
    {
        var message = TryString(root, "contract", out _)
            ? "The generated frontend contract does not match the host. Regenerate the frontend RPC bindings and rebuild the frontend assets."
            : "The RPC request did not include a contract hash. Use the generated frontend RPC bindings instead of calling the low-level invoke API directly.";
        _host.Diagnose(NeoRpcDiagnosticLevel.Warning, NeoRpcErrorCodes.ProtocolMismatch, message);
        return FrameworkError(NeoRpcErrorCodes.ProtocolMismatch, message, null);
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

    // 32 lowercase hexadecimal characters, in one allocation.
    private static string NewCorrelationId() => Guid.NewGuid().ToString("N");

    private sealed class RequestIdState(string id, RequestIdFingerprint fingerprint)
    {
        internal string Id { get; } = id;
        internal RequestIdFingerprint Fingerprint { get; } = fingerprint;
    }

    private readonly record struct RequestIdFingerprint(ulong First, ulong Second, ulong Third, ulong Fourth)
    {
        internal static RequestIdFingerprint Create(string id)
        {
            // IDs have already been validated as printable ASCII of at most 256 characters.
            Span<byte> encoded = stackalloc byte[id.Length];
            Encoding.UTF8.GetBytes(id, encoded);
            Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(encoded, digest);
            return new(BinaryPrimitives.ReadUInt64LittleEndian(digest), BinaryPrimitives.ReadUInt64LittleEndian(digest[8..]),
                BinaryPrimitives.ReadUInt64LittleEndian(digest[16..]), BinaryPrimitives.ReadUInt64LittleEndian(digest[24..]));
        }
    }

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
            // A view binding receives with the token of the session itself: one registration is enough.
            if (!receive.Equals(session)) _receiveRegistration = receive.Register(static state => ((InvocationState)state!).CancelFromSource(timedOut: false), this);
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

    private sealed class SubscriptionState : IStreamFrameOwner
    {
        private const int Pending = 0;
        private const int Active = 1;
        private const int Closed = 2;
        private readonly NeoRpcSession _session;
        private readonly RequestIdState _requestId;
        private readonly string _id;
        // Guards the queue, the pump waiting for it, and disposal: a publisher may enqueue at the moment the subscription
        // or its session closes. Nothing that waits (a semaphore) is disposed under a publisher.
        private readonly object _lock = new();
        private readonly Queue<byte[]> _queue = [];
        private readonly CancellationTokenSource _closed;
        private readonly CancellationToken _token;
        private TaskCompletionSource? _waiting;
        private bool _disposed;
        private long _bytes;
        private int _started;
        private long _sequence;
        private int _state;

        internal SubscriptionState(NeoRpcSession session, RequestIdState requestId, EventDescriptor descriptor, Uri? sourceOrigin, bool isMainFrame)
        {
            _session = session; _requestId = requestId; _id = requestId.Id; Descriptor = descriptor;
            SourceOrigin = sourceOrigin; IsMainFrame = isMainFrame;
            _closed = CancellationTokenSource.CreateLinkedTokenSource(session._closedToken);
            _token = _closed.Token;
        }
        internal EventDescriptor Descriptor { get; }
        internal Uri? SourceOrigin { get; }
        internal bool IsMainFrame { get; }
        internal CancellationToken Token => _token;
        internal bool IsActive => Volatile.Read(ref _state) == Active;
        internal Task Completion { get; private set; } = Task.CompletedTask;
        internal bool TryActivate() => !_token.IsCancellationRequested && Interlocked.CompareExchange(ref _state, Active, Pending) == Pending;
        internal bool TryClosePending()
        {
            if (Interlocked.CompareExchange(ref _state, Closed, Pending) != Pending) return false;
            Cancel();
            return true;
        }
        internal void Start() { if (Volatile.Read(ref _state) == Active && Interlocked.Exchange(ref _started, 1) == 0) Completion = PumpAsync(); }
        internal bool Enqueue(byte[] value)
        {
            if (TryEnqueue(value, out var failed)) return true;
            if (failed) Cancel();
            return false;
        }

        private bool TryEnqueue(byte[] value, out bool failed)
        {
            failed = false;
            lock (_lock)
            {
                if (_token.IsCancellationRequested || value.Length > _session._host.Options.MaximumQueuedEventBytesPerSubscription) return false;
                var countFull = _queue.Count >= _session._host.Options.MaximumQueuedEventsPerSubscription;
                var bytesFull = _bytes + value.Length > _session._host.Options.MaximumQueuedEventBytesPerSubscription;
                if (countFull || bytesFull)
                {
                    switch (Descriptor.Options.OverflowBehavior)
                    {
                        case NeoRpcOverflowBehavior.DropNewest: return false;
                        case NeoRpcOverflowBehavior.Fail: failed = true; return false;
                        case NeoRpcOverflowBehavior.Coalesce:
                            _queue.Clear(); _bytes = 0;
                            break;
                        case NeoRpcOverflowBehavior.DropOldest:
                            while (_queue.Count != 0 && (_queue.Count >= _session._host.Options.MaximumQueuedEventsPerSubscription || _bytes + value.Length > _session._host.Options.MaximumQueuedEventBytesPerSubscription))
                                _bytes -= _queue.Dequeue().Length;
                            break;
                    }
                }
                _queue.Enqueue(value); _bytes += value.Length;
                WakeLocked();
                return true;
            }
        }
        internal void Close() { Interlocked.Exchange(ref _state, Closed); Cancel(); }

        // An event the view cannot receive fails the subscription: the page would otherwise miss it without a word.
        internal void Fail(NeoRpcFrameRefusedException refusal)
        {
            if (_token.IsCancellationRequested) return;
            _session.DiagnoseRefusedStream(refusal, "subscription");
            Close();
        }

        void IStreamFrameOwner.OnFrameRefused(NeoRpcFrameRefusedException refusal) => Fail(refusal);
        internal void DisposePending()
        {
            if (Volatile.Read(ref _started) == 0) { Dispose(); _session.CompleteRequestId(_requestId); }
        }

        // Never under the lock: application callbacks registered on the token (an authorization service) run here. The
        // source is disposed only once it was canceled, so a disposed source is canceled already.
        private void Cancel()
        {
            try { _closed.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private void WakeLocked()
        {
            _waiting?.TrySetResult();
            _waiting = null;
        }

        private void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _queue.Clear();
                _bytes = 0;
            }
            _closed.Dispose();
        }

        private async Task PumpAsync()
        {
            // Closing the subscription or its session wakes a pump that waits for an event.
            var wake = _token.UnsafeRegister(static state => { var subscription = (SubscriptionState)state!; lock (subscription._lock) subscription.WakeLocked(); }, this);
            var taken = new List<byte[]>();
            try
            {
                while (true)
                {
                    Task? waiting = null;
                    lock (_lock)
                    {
                        if (_token.IsCancellationRequested) return;
                        // Everything queued leaves together, in order; events published meanwhile wait in the bounded queue.
                        while (_queue.TryDequeue(out var item)) { _bytes -= item.Length; taken.Add(item); }
                        if (taken.Count == 0) waiting = (_waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                    }
                    if (waiting is not null)
                    {
                        await waiting.ConfigureAwait(false);
                        continue;
                    }
                    for (var index = 0; index < taken.Count; index++)
                        await _session.SendEventAsync(_id, ++_sequence, taken[index], awaitDelivery: index == taken.Count - 1, this, _token).ConfigureAwait(false);
                    taken.Clear();
                }
            }
            catch (NeoRpcFrameRefusedException refusal) { Fail(refusal); }
            catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
            catch (Exception) { Close(); }
            finally { wake.Dispose(); _session.RemoveSubscription(_id, this); Dispose(); _session.CompleteRequestId(_requestId); }
        }
    }

    internal abstract class ChannelState
    {
        internal abstract Task Completion { get; }
        internal abstract void Start();
        internal abstract void Acknowledge(long sequence);
        internal abstract void Close();
    }

    private sealed class ChannelState<T> : ChannelState, IStreamFrameOwner
    {
        private readonly NeoRpcSession _session;
        private readonly string _id;
        private readonly NeoRpcChannel<T> _channel;
        private readonly CancellationTokenSource _closed;
        private readonly SemaphoreSlim _credits;
        private readonly TaskCompletionSource<bool> _start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _completion;
        private long _sent;
        private long _acknowledged;
        private NeoRpcFrameRefusedException? _refusal;
        internal ChannelState(NeoRpcSession session, string id, NeoRpcChannel<T> channel)
        {
            _session = session; _id = id; _channel = channel;
            _closed = CancellationTokenSource.CreateLinkedTokenSource(session._closedToken);
            _credits = new SemaphoreSlim(session._host.Options.MaximumUnacknowledgedChannelItems, session._host.Options.MaximumUnacknowledgedChannelItems);
            _completion = PumpAsync();
        }
        internal override Task Completion => _completion;
        internal override void Start() => _start.TrySetResult(true);
        internal override void Acknowledge(long sequence)
        {
            while (true)
            {
                var current = Volatile.Read(ref _acknowledged);
                var target = Math.Min(sequence, Volatile.Read(ref _sent));
                if (target <= current) return;
                if (Interlocked.CompareExchange(ref _acknowledged, target, current) == current)
                {
                    try { _credits.Release(checked((int)(target - current))); }
                    catch (ObjectDisposedException) { }
                    return;
                }
            }
        }
        internal override void Close()
        {
            try { _closed.Cancel(); }
            catch (ObjectDisposedException) { }
            finally { _start.TrySetResult(false); }
        }

        // An item the view cannot receive fails the channel with the reason, sent as its terminal error.
        private void Refuse(NeoRpcFrameRefusedException refusal)
        {
            if (Interlocked.CompareExchange(ref _refusal, refusal, null) is null) _session.DiagnoseRefusedStream(refusal, "channel");
            Close();
        }

        void IStreamFrameOwner.OnFrameRefused(NeoRpcFrameRefusedException refusal) => Refuse(refusal);
        private async Task PumpAsync()
        {
            NeoRpcError? error = null;
            var started = false;
            try
            {
                started = await _start.Task.ConfigureAwait(false);
                if (!started) return;
                _closed.Token.ThrowIfCancellationRequested();
                await foreach (var item in _channel.Items.WithCancellation(_closed.Token).ConfigureAwait(false))
                {
                    await _credits.WaitAsync(_closed.Token).ConfigureAwait(false);
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(item, _channel.ItemTypeInfo);
                    var sequence = Interlocked.Increment(ref _sent);
                    _session.QueueChannelItem(_id, sequence, bytes, this, _closed.Token);
                }
            }
            catch (NeoRpcFrameRefusedException refusal) { Refuse(refusal); }
            catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
            catch (Exception)
            {
                error = FrameworkError(NeoRpcErrorCodes.InternalError, "The channel failed.", NewCorrelationId());
                _session._host.Diagnose(NeoRpcDiagnosticLevel.Error, NeoRpcErrorCodes.InternalError, "A channel source failed safely.", error.Value.CorrelationId);
            }
            finally
            {
                try
                {
                    // await foreach has already disposed its enumerator. Never release a service while it is still in use.
                    try { await _channel.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception)
                    {
                        error = FrameworkError(NeoRpcErrorCodes.InternalError, "The channel failed.", NewCorrelationId());
                        _session._host.Diagnose(NeoRpcDiagnosticLevel.Error, "channel_cleanup_failed", "A channel service lease failed to dispose safely.", error.Value.CorrelationId);
                    }
                    // Abandoning an unstarted result must not send: a reentrant close may be inside the result send itself.
                    if (error is null && Volatile.Read(ref _refusal) is { } refused) error = FrameworkError(refused.Code, refused.Message, NewCorrelationId());
                    try { if (started && !_session._closed.IsCancellationRequested) await _session.SendChannelTerminalAsync(_id, error is null ? "channel_complete" : "channel_error", error, _session._closedToken).ConfigureAwait(false); } catch { }
                }
                finally
                {
                    _credits.Dispose(); _closed.Dispose();
                    _session.RemoveChannel(_id, this);
                }
            }
        }
    }
}

// What the transport of a view carries: the largest frame, its envelope taken out, and the deepest value a frame may hold.
// Depth is counted as System.Text.Json and the client count it: objects and arrays, the root included. The value of a frame
// is nested in the frame object, so it may use one level less than the transport.
internal readonly record struct NeoRpcOutboundLimits(int MaximumFrameBytes, int MaximumValueDepth)
{
    // {"__neoastraTransport":1,"hostViewBinding":"<32 hex>","rendererDocumentId":"<id>","frame":<frame>} is 111 bytes and the
    // renderer document ID, of at least one character: a frame refused against this least envelope could never be delivered,
    // and one that fits it but not the envelope of its document is refused by the view when it is posted.
    internal const int MinimumEnvelopeBytes = 112;

    internal static NeoRpcOutboundLimits None { get; } = new(int.MaxValue, int.MaxValue);

    internal static NeoRpcOutboundLimits Of(uint maximumMessageSize, int maximumJsonDepth)
        => new((int)Math.Clamp((long)maximumMessageSize - MinimumEnvelopeBytes, 0, int.MaxValue), Math.Max(0, maximumJsonDepth - 1));

    internal bool IsTooLarge(string frame)
    {
        // A UTF-16 character is at most three UTF-8 bytes (a surrogate pair is four for two): most frames need no count.
        if (frame.Length <= MaximumFrameBytes / 3) return false;
        return frame.Length > MaximumFrameBytes || Encoding.UTF8.GetByteCount(frame) > MaximumFrameBytes;
    }

    internal bool IsTooDeep(ReadOnlySpan<byte> value)
    {
        // Nesting deeper than the limit takes two bytes for each level: a shorter value cannot.
        if (value.Length <= 2 * (long)MaximumValueDepth + 1) return false;
        var reader = new Utf8JsonReader(value, new JsonReaderOptions { MaxDepth = MaximumJsonReadDepth });
        try
        {
            while (reader.Read())
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray && reader.CurrentDepth >= MaximumValueDepth) return true;
        }
        catch (JsonException) { return true; }
        return false;
    }

    // Measured once for an event that several sessions, with limits of their own, may receive.
    internal bool IsTooDeep(byte[] value, ref int measuredDepth)
    {
        if (value.Length <= 2 * (long)MaximumValueDepth + 1) return false;
        if (measuredDepth < 0) measuredDepth = MeasureDepth(value);
        return measuredDepth > MaximumValueDepth;
    }

    internal static int MeasureDepth(ReadOnlySpan<byte> value)
    {
        var reader = new Utf8JsonReader(value, new JsonReaderOptions { MaxDepth = MaximumJsonReadDepth });
        var depth = 0;
        try
        {
            while (reader.Read())
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray && reader.CurrentDepth >= depth) depth = reader.CurrentDepth + 1;
        }
        catch (JsonException) { return int.MaxValue; }
        return depth;
    }

    // Deeper than any transport accepts (128), and what the host serialized itself is valid JSON.
    private const int MaximumJsonReadDepth = 256;
}

// A frame that the host refused before sending it, or that the view refused alone: the call, subscription, or channel it
// belongs to fails with a safe code and message, and the session keeps serving.
internal sealed class NeoRpcFrameRefusedException(string code, string message) : Exception(message)
{
    internal NeoRpcFrameRefusedException(NeoRpcFrameRefusal refusal)
        : this(refusal == NeoRpcFrameRefusal.TooLarge ? NeoRpcErrorCodes.PayloadTooLarge : NeoRpcErrorCodes.InternalError,
            refusal == NeoRpcFrameRefusal.TooLarge ? TooLargeMessage : "The view refused the frame.")
    {
    }

    internal const string TooLargeMessage = "The frame exceeds the maximum message size of the view.";
    internal string Code { get; } = code;
}
