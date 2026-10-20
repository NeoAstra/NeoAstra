// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoAstra.Rpc;

/// <summary>Binds an RPC host to the authenticated transport of one NeoAstra view.</summary>
public sealed class NeoRpcViewBinding : IAsyncDisposable
{
    private readonly NeoRpcHost _host;
    private readonly global::NeoAstra.NeoAstra _view;
    private readonly Func<NeoTransportSessionSnapshot, NeoRpcSession> _openSession;
    private readonly Func<string, bool> _closeTransportSession;
    private readonly NeoApplication? _application;
    private readonly object _gate = new();
    private readonly List<Task> _teardowns = [];
    private NeoRpcSession? _session;
    private bool _droppedFrameDiagnosed;
    private int _disposed;
    // The frames of the page, in the order the transport delivered them, read by one reader on the thread pool: the
    // UI thread only authenticates their envelope and queues them, and no JSON of a request is parsed into a document or
    // deserialized there.
    private readonly System.Threading.Channels.Channel<InboundFrame> _inbound =
        System.Threading.Channels.Channel.CreateUnbounded<InboundFrame>(new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
    private int _reading;

    private NeoRpcViewBinding(NeoRpcHost host, global::NeoAstra.NeoAstra view)
        : this(host, view, snapshot => Open(host, view, snapshot))
    {
    }

    internal NeoRpcViewBinding(NeoRpcHost host, global::NeoAstra.NeoAstra view, Func<NeoTransportSessionSnapshot, NeoRpcSession> openSession)
        : this(host, view, openSession, documentSessionId => view.CloseTransportSession(documentSessionId, "rpc_session_closed"))
    {
    }

    internal NeoRpcViewBinding(NeoRpcHost host, global::NeoAstra.NeoAstra view, Func<NeoTransportSessionSnapshot, NeoRpcSession> openSession, Func<string, bool> closeTransportSession)
    {
        _host = host;
        _view = view;
        _openSession = openSession;
        _closeTransportSession = closeTransportSession;
        _application = view.Environment?.Application;
        view.TransportApplicationMessageReceived += OnMessage;
        view.TransportSessionChanged += OnSessionChanged;
        if (_application is not null) _application.Stopping += OnApplicationStopping;
        if (view.TransportSession is { } session) QueueTransition(session);
    }

    /// <summary>Binds an RPC host to a bridge-enabled NeoAstra view.</summary>
    /// <param name="host">The immutable RPC host.</param>
    /// <param name="view">The view whose trusted sessions will own RPC state.</param>
    /// <returns>A binding that must be disposed before the host or view when detached early.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The view does not have a bridge-enabled transport.</exception>
    public static NeoRpcViewBinding Bind(NeoRpcHost host, global::NeoAstra.NeoAstra view)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(view);
        if (view.ViewLabel is null || !view.TransportEnabled) throw new InvalidOperationException("RPC requires a bridge-enabled view with an immutable view label.");
        return new(host, view);
    }

    /// <summary>Detaches the binding and closes the current document session.</summary>
    /// <returns>A task representing deterministic session teardown.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _view.TransportApplicationMessageReceived -= OnMessage;
        _view.TransportSessionChanged -= OnSessionChanged;
        if (_application is not null) _application.Stopping -= OnApplicationStopping;
        NeoRpcSession? session;
        Task[] teardowns;
        lock (_gate)
        {
            session = _session;
            _session = null;
            teardowns = _teardowns.ToArray();
            _teardowns.Clear();
        }
        var currentTeardown = session is null ? Task.CompletedTask : DisposeContainedAsync(session);
        await Task.WhenAll(teardowns.Append(currentTeardown)).ConfigureAwait(false);
        await _host.CloseViewServicesAsync(_view.RpcServiceScope).ConfigureAwait(false);
    }

    private void OnSessionChanged(NeoTransportSessionSnapshot? snapshot)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        QueueTransition(snapshot);
    }

    private void OnApplicationStopping(object? sender, EventArgs args)
    {
        // Revoke document authority before native views are destroyed. QueueTransition is
        // idempotent and closes calls, subscriptions, channels, resources, and scoped services.
        QueueTransition(null);
    }

    private void QueueTransition(NeoTransportSessionSnapshot? snapshot)
    {
        NeoRpcSession? previous;
        NeoRpcSession? next = null;
        TaskCompletionSource? teardownCompletion = null;
        Exception? openFailure = null;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (snapshot is not null)
            {
                try { next = _openSession(snapshot.Value); }
                catch (Exception exception) { openFailure = exception; }
            }
            previous = _session;
            _session = next;
            _droppedFrameDiagnosed = false;
            if (previous is not null)
            {
                _teardowns.RemoveAll(static task => task.IsCompleted);
                teardownCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _teardowns.Add(teardownCompletion.Task);
            }
        }
        if (previous is not null) _ = CompleteTeardownAsync(previous, teardownCompletion!);
        if (next is not null) WatchSession(next);
        if (openFailure is not null)
        {
            _host.Diagnose(NeoRpcDiagnosticLevel.Error, NeoRpcErrorCodes.ConnectionClosed, $"The platform RPC binding could not open its document session ({FailureName(openFailure)}).");
            // The transport answers the hello of that document: without a revocation, its calls would wait for answers
            // nobody sends. The transport is announcing the session now, so the revocation runs after that.
            PostRevocation(snapshot!.Value.DocumentSessionId);
        }
    }

    private static NeoRpcSession Open(NeoRpcHost host, global::NeoAstra.NeoAstra view, NeoTransportSessionSnapshot snapshot)
    {
        var identity = new NeoRpcSessionIdentity(view.ViewLabel!, snapshot.DocumentSessionId)
        {
            Platform = CurrentPlatform(),
            WholeViewTrust = snapshot.WholeViewTrust,
            ProtocolMinor = snapshot.ProtocolMinor,
            Features = snapshot.Features,
            ContractHash = host.Options.ContractHash,
            Dispatcher = new ViewDispatcher(view.Environment.Application.Dispatcher),
        };
        var documentSessionId = snapshot.DocumentSessionId;
        return host.OpenSessionCore(identity,
            (json, cancellationToken) => SendAsync(view, documentSessionId, json, cancellationToken),
            frames => SendBatchAsync(view, documentSessionId, frames),
            view,
            view.Window);
    }

    // The frames a session queued, posted in order in one turn of the UI thread: answers do not wait one UI round
    // trip each, and the UI thread only writes their envelope and posts them. They go only to the document of the
    // session that wrote them, while that session is the active one of the view.
    // The UI thread reads the frames until the dispatched call completes; a canceled call never runs afterwards.
    private static async ValueTask<NeoRpcFrameRefusal[]?> SendBatchAsync(global::NeoAstra.NeoAstra view, string documentSessionId, IReadOnlyList<NeoTransportFrame> frames)
        => ToRefusals(await view.Environment.Application.Dispatcher
            .InvokeAsync(() => view.PostTrustedMessages(documentSessionId, frames)).ConfigureAwait(false), frames.Count);

    private static async ValueTask SendAsync(global::NeoAstra.NeoAstra view, string documentSessionId, string json, CancellationToken cancellationToken)
    {
        var frame = NeoTransportFrame.FromString(json);
        try
        {
            var refusals = ToRefusals(await view.Environment.Application.Dispatcher
                .InvokeAsync(() => view.PostTrustedMessages(documentSessionId, [frame]), cancellationToken).ConfigureAwait(false), 1);
            if (refusals is not null) throw new NeoRpcException(NeoRpcErrorCodes.PayloadTooLarge, "The frame exceeds the maximum message size of the view.");
        }
        finally
        {
            frame.Release();
        }
    }

    // What the view did with a batch, as NeoRpcSendFrames reports it: null when every frame was posted, otherwise one entry
    // for each frame. A closed session is a transport that failed for these frames: none of them has a document to go to.
    internal static NeoRpcFrameRefusal[]? ToRefusals(NeoTrustedPostResult result, int frameCount)
    {
        if (result.SessionClosed) throw new OperationCanceledException("The document session of these frames is closed.");
        if (result.RejectedFrames is not { Count: > 0 } rejected) return null;
        var refusals = new NeoRpcFrameRefusal[frameCount];
        foreach (var index in rejected) refusals[index] = NeoRpcFrameRefusal.TooLarge;
        return refusals;
    }

    private static NeoCapabilityPlatform CurrentPlatform()
    {
        if (OperatingSystem.IsWindows()) return NeoCapabilityPlatform.Windows;
        if (OperatingSystem.IsMacOS()) return NeoCapabilityPlatform.MacOS;
        if (OperatingSystem.IsLinux()) return NeoCapabilityPlatform.Linux;
        throw new PlatformNotSupportedException("NeoAstra RPC capabilities require a supported Windows, macOS, or Linux backend.");
    }

    private void OnMessage(NeoTransportApplicationMessage message)
    {
        NeoRpcSession? session;
        lock (_gate) session = _session;
        // The binding owns the frame of the message: the session it goes to releases it, or the binding does.
        if (session is null || !string.Equals(session.DocumentSessionId, message.Session.DocumentSessionId, StringComparison.Ordinal))
        {
            message.Frame.Release();
            return;
        }
        // Transport order is kept by one reader, and the native WebView callback is left at once: no application
        // handler can enter its own native modal loop from it. The frame is stamped now, for the rate of the session.
        if (!_inbound.Writer.TryWrite(new(session, message, System.Diagnostics.Stopwatch.GetTimestamp())))
        {
            message.Frame.Release();
            return;
        }
        if (Interlocked.CompareExchange(ref _reading, 1, 0) == 0) _ = Task.Run(ReadInbound);
    }

    // Starts each frame in order, as the UI thread did: a frame runs until its first wait before the next one starts.
    private void ReadInbound()
    {
        while (true)
        {
            while (_inbound.Reader.TryRead(out var frame)) _ = ReceiveContainedAsync(frame.Session, frame.Message, frame.Arrival);
            Volatile.Write(ref _reading, 0);
            // A frame queued after the last read and before the flag was cleared is read by this reader.
            if (!_inbound.Reader.TryPeek(out _) || Interlocked.CompareExchange(ref _reading, 1, 0) != 0) return;
        }
    }

    private readonly record struct InboundFrame(NeoRpcSession Session, NeoTransportApplicationMessage Message, long Arrival);

    private async Task ReceiveContainedAsync(NeoRpcSession session, NeoTransportApplicationMessage message, long arrival)
    {
        try { await session.ReceiveAsync(message.Frame, message.SourceOrigin, message.IsMainFrame, arrival, session.Closed).ConfigureAwait(false); }
        catch (OperationCanceledException) when (session.Closed.IsCancellationRequested) { DiagnoseDroppedFrame(session); }
        catch (ObjectDisposedException) { DiagnoseDroppedFrame(session); }
        catch (Exception exception)
        {
            _host.Diagnose(NeoRpcDiagnosticLevel.Error, NeoRpcErrorCodes.ConnectionClosed, $"The platform RPC binding failed and closed its document session ({FailureName(exception)}).");
            try { await session.DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }

    // Diagnostics stay bounded and free of user data: the exception type identifies a contained failure,
    // while its message may quote arguments, paths, or payload contents.
    private static string FailureName(Exception exception)
    {
        var name = exception.GetType().FullName ?? exception.GetType().Name;
        return name.Length <= 128 ? name : name[..128];
    }

    private void WatchSession(NeoRpcSession session)
    {
        // A failed send, abuse closure, or host disposal closes a session that is still this view's current
        // one. Its document cannot observe that, so the transport session it is bound to is revoked too.
        try { session.Closed.Register(() => OnSessionClosed(session)); }
        catch (ObjectDisposedException) { OnSessionClosed(session); }
    }

    private void OnSessionClosed(NeoRpcSession session)
    {
        // A session replaced by a navigation or detached by disposal was closed by the transport itself.
        lock (_gate) if (!ReferenceEquals(_session, session)) return;
        PostRevocation(session.DocumentSessionId);
    }

    private void PostRevocation(string documentSessionId)
    {
        if (_application is null)
        {
            _ = Task.Run(() => RevokeTransportSession(documentSessionId));
            return;
        }
        try
        {
            // The transport is owned by the UI thread, and this runs on whichever thread closed the session.
            _application.Dispatcher.Post(() => RevokeTransportSession(documentSessionId));
        }
        catch
        {
            // Application shutdown closes the transport itself. Nothing may escape either way: this is a
            // cancellation callback, and a failure here would replace the error that closed the session.
        }
    }

    private void RevokeTransportSession(string documentSessionId)
    {
        bool revoked;
        try { revoked = _closeTransportSession(documentSessionId); }
        catch { revoked = false; }
        // The transport no longer has that session when a navigation or view disposal already replaced it.
        if (revoked)
            _host.Diagnose(NeoRpcDiagnosticLevel.Warning, NeoRpcErrorCodes.ConnectionClosed, "The platform RPC binding closed the transport connection of a closed document session; its document must reload to reconnect.");
    }

    private void DiagnoseDroppedFrame(NeoRpcSession session)
    {
        lock (_gate)
        {
            // Frames racing a replaced or detached session are expected. A closed session that is still
            // current answers nothing until the document reloads, so report that state once.
            if (!ReferenceEquals(_session, session) || _droppedFrameDiagnosed) return;
            _droppedFrameDiagnosed = true;
        }
        _host.Diagnose(NeoRpcDiagnosticLevel.Warning, NeoRpcErrorCodes.ConnectionClosed, "The platform RPC binding is dropping frames for a closed document session until its document reloads.");
    }

    private static async Task DisposeContainedAsync(NeoRpcSession session)
    {
        try { await session.DisposeAsync().ConfigureAwait(false); } catch { }
    }

    private static async Task CompleteTeardownAsync(NeoRpcSession session, TaskCompletionSource completion)
    {
        await DisposeContainedAsync(session).ConfigureAwait(false);
        completion.TrySetResult();
    }

    private sealed class ViewDispatcher(NeoDispatcher dispatcher) : INeoRpcDispatcher
    {
        public async ValueTask<object?> InvokeAsync(Func<ValueTask<object?>> callback, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(callback);
            var task = await dispatcher.InvokeAsync(() => callback().AsTask(), cancellationToken).ConfigureAwait(false);
            return await task.ConfigureAwait(false);
        }
    }
}
