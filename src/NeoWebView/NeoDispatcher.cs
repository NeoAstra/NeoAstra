// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NeoWebView.Interop;
using NeoWebView.Interop.Generated;

namespace NeoWebView;

/// <summary>Dispatches managed work to a NeoWebView application's UI thread.</summary>
public sealed unsafe class NeoDispatcher
{
    private readonly NeoApplication _application;
    private readonly int _threadId;
    private volatile bool _shutdown;

    internal NeoDispatcher(NeoApplication application, int threadId)
    {
        _application = application;
        _threadId = threadId;
    }

    /// <summary>Gets whether the caller is the application UI thread.</summary>
    public bool CheckAccess() => Environment.CurrentManagedThreadId == _threadId;

    /// <summary>Queues work on the application UI thread.</summary>
    /// <param name="action">The work to execute.</param>
    /// <exception cref="ArgumentNullException"><paramref name="action"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">Application shutdown has started.</exception>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Queue(new PostedWork(action));
    }

    /// <summary>Asynchronously invokes work on the application UI thread.</summary>
    /// <param name="action">The work to execute.</param>
    /// <param name="cancellationToken">Cancels the wait before the work starts.</param>
    /// <returns>A task completed after the work runs.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="action"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">Application shutdown has started.</exception>
    public ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(cancellationToken);
        }

        var work = new InvokedWork(action, cancellationToken);
        Queue(work);
        return new ValueTask(work.Task);
    }

    /// <summary>Asynchronously invokes a function on the application UI thread.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="function">The function to execute.</param>
    /// <param name="cancellationToken">Cancels the wait before the function starts.</param>
    /// <returns>A task containing the function result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="function"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">Application shutdown has started.</exception>
    public ValueTask<T> InvokeAsync<T>(Func<T> function, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(function);
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<T>(cancellationToken);
        }

        var work = new InvokedWork<T>(function, cancellationToken);
        Queue(work);
        return new ValueTask<T>(work.Task);
    }

    internal void MarkShutdown() => _shutdown = true;

    private void Queue(IDispatchWork work)
    {
        if (_shutdown)
        {
            throw new ObjectDisposedException(nameof(NeoApplication));
        }

        var root = GCHandle.Alloc(work);
        NativeMethods.neo_webview_result_t result;
        try
        {
            result = NativeMethods.neo_webview_app_dispatch(
                _application.NativeHandle,
                (delegate* unmanaged[Cdecl]<void*, void>)&Dispatch,
                (void*)GCHandle.ToIntPtr(root));
        }
        catch
        {
            root.Free();
            throw;
        }

        if (NativeError.Code(result) != NeoErrorCode.Success)
        {
            root.Free();
            NativeError.ThrowIfFailed(result, default, "dispatch managed work");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Dispatch(void* context)
    {
        try
        {
            var root = GCHandle.FromIntPtr((nint)context);
            var work = root.Target as IDispatchWork;
            root.Free();
            work?.Execute();
        }
        catch
        {
            // No managed exception may cross the unmanaged callback boundary.
        }
    }

    private interface IDispatchWork
    {
        void Execute();
    }

    private sealed class PostedWork(Action action) : IDispatchWork
    {
        public void Execute()
        {
            try { action(); } catch { }
        }
    }

    private sealed class InvokedWork : IDispatchWork
    {
        private readonly Action _action;
        private readonly CancellationToken _cancellationToken;
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal InvokedWork(Action action, CancellationToken cancellationToken)
        {
            _action = action;
            _cancellationToken = cancellationToken;
        }

        internal Task Task => _completion.Task;

        public void Execute()
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(_cancellationToken);
                return;
            }

            try
            {
                _action();
                _completion.TrySetResult();
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
            }
        }
    }

    private sealed class InvokedWork<T> : IDispatchWork
    {
        private readonly Func<T> _function;
        private readonly CancellationToken _cancellationToken;
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal InvokedWork(Func<T> function, CancellationToken cancellationToken)
        {
            _function = function;
            _cancellationToken = cancellationToken;
        }

        internal Task<T> Task => _completion.Task;

        public void Execute()
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(_cancellationToken);
                return;
            }

            try
            {
                _completion.TrySetResult(_function());
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
            }
        }
    }
}

internal sealed class NeoDispatcherSynchronizationContext(NeoDispatcher dispatcher) : SynchronizationContext
{
    public override void Post(SendOrPostCallback callback, object? state)
    {
        ArgumentNullException.ThrowIfNull(callback);
        try
        {
            dispatcher.Post(() => callback(state));
        }
        catch (ObjectDisposedException)
        {
            // Continuations posted after shutdown are safely ignored.
        }
    }

    public override void Send(SendOrPostCallback callback, object? state)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (dispatcher.CheckAccess())
        {
            callback(state);
            return;
        }

        dispatcher.InvokeAsync(() => callback(state)).AsTask().GetAwaiter().GetResult();
    }

    public override SynchronizationContext CreateCopy() => new NeoDispatcherSynchronizationContext(dispatcher);
}
