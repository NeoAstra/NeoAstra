// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoAstra.Rpc;

/// <summary>Binds an RPC host to the authenticated Step 1 transport of one NeoAstra view.</summary>
public sealed class NeoRpcViewBinding : IAsyncDisposable
{
    private readonly NeoRpcHost _host;
    private readonly global::NeoAstra.NeoAstra _view;
    private readonly Func<NeoTransportSessionSnapshot, NeoRpcSession> _openSession;
    private readonly object _gate = new();
    private readonly List<Task> _teardowns = [];
    private NeoRpcSession? _session;
    private int _disposed;

    private NeoRpcViewBinding(NeoRpcHost host, global::NeoAstra.NeoAstra view)
        : this(host, view, snapshot => Open(host, view, snapshot))
    {
    }

    internal NeoRpcViewBinding(NeoRpcHost host, global::NeoAstra.NeoAstra view, Func<NeoTransportSessionSnapshot, NeoRpcSession> openSession)
    {
        _host = host;
        _view = view;
        _openSession = openSession;
        view.TransportApplicationMessageReceived += OnMessage;
        view.TransportSessionChanged += OnSessionChanged;
        if (view.TransportSession is { } session) QueueTransition(session);
    }

    /// <summary>Binds an RPC host to a bridge-enabled NeoAstra view.</summary>
    /// <param name="host">The immutable RPC host.</param>
    /// <param name="view">The view whose trusted Step 1 sessions will own RPC state.</param>
    /// <returns>A binding that must be disposed before the host or view when detached early.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The view does not have a bridge-enabled transport.</exception>
    public static NeoRpcViewBinding Bind(NeoRpcHost host, global::NeoAstra.NeoAstra view)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(view);
        if (view.ViewLabel is null) throw new InvalidOperationException("RPC requires a bridge-enabled view with an immutable view label.");
        return new(host, view);
    }

    /// <summary>Detaches the binding and closes the current document session.</summary>
    /// <returns>A task representing deterministic session teardown.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _view.TransportApplicationMessageReceived -= OnMessage;
        _view.TransportSessionChanged -= OnSessionChanged;
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
        await _host.CloseViewServicesAsync(_view.ViewLabel!).ConfigureAwait(false);
    }

    private void OnSessionChanged(NeoTransportSessionSnapshot? snapshot)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        QueueTransition(snapshot);
    }

    private void QueueTransition(NeoTransportSessionSnapshot? snapshot)
    {
        NeoRpcSession? previous;
        TaskCompletionSource? teardownCompletion = null;
        Exception? openFailure = null;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            NeoRpcSession? next = null;
            if (snapshot is not null)
            {
                try { next = _openSession(snapshot.Value); }
                catch (Exception exception) { openFailure = exception; }
            }
            previous = _session;
            _session = next;
            if (previous is not null)
            {
                _teardowns.RemoveAll(static task => task.IsCompleted);
                teardownCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _teardowns.Add(teardownCompletion.Task);
            }
        }
        if (previous is not null) _ = CompleteTeardownAsync(previous, teardownCompletion!);
        if (openFailure is not null)
            _host.Diagnose(NeoRpcDiagnosticLevel.Error, NeoRpcErrorCodes.ConnectionClosed, "The platform RPC binding could not open its document session.");
    }

    private static NeoRpcSession Open(NeoRpcHost host, global::NeoAstra.NeoAstra view, NeoTransportSessionSnapshot snapshot)
    {
        var identity = new NeoRpcSessionIdentity(view.ViewLabel!, snapshot.DocumentSessionId)
        {
            WholeViewTrust = snapshot.WholeViewTrust,
            ProtocolMinor = snapshot.ProtocolMinor,
            Features = snapshot.Features,
            ContractHash = host.Options.ContractHash,
            Dispatcher = new ViewDispatcher(view.Environment.Application.Dispatcher),
        };
        return host.OpenSessionCore(identity,
            async (json, cancellationToken) => await view.PostMessageAsync(json, cancellationToken).ConfigureAwait(false),
            view,
            view.OwnedWindow);
    }

    private void OnMessage(NeoTransportApplicationMessage message)
    {
        NeoRpcSession? session;
        lock (_gate) session = _session;
        if (session is null || !string.Equals(session.DocumentSessionId, message.Session.DocumentSessionId, StringComparison.Ordinal)) return;
        _ = ReceiveContainedAsync(session, message);
    }

    private async Task ReceiveContainedAsync(NeoRpcSession session, NeoTransportApplicationMessage message)
    {
        try { await session.ReceiveAsync(message.Json, message.SourceOrigin, message.IsMainFrame, session.Closed).ConfigureAwait(false); }
        catch (OperationCanceledException) when (session.Closed.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (Exception exception)
        {
            _host.Diagnose(NeoRpcDiagnosticLevel.Error, NeoRpcErrorCodes.ConnectionClosed, "The platform RPC binding failed and closed its document session.");
            try { await session.DisposeAsync().ConfigureAwait(false); } catch { }
            _ = exception;
        }
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
