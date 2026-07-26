// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NeoAstra.Interop;
using NeoAstra.Interop.Generated;

namespace NeoAstra;

/// <summary>Dispatches managed work to a NeoAstra application's UI thread.</summary>
public sealed unsafe class NeoDispatcher
{
    private static readonly CancellationToken ShutdownCancellationToken = new(canceled: true);
    private readonly NeoApplication _application;
    private readonly int _threadId;
    private readonly object _sync = new();
    private readonly Dictionary<nint, DispatchRegistration> _outstanding = [];
    private bool _shutdown;

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

    internal void MarkShutdown() => MarkShutdown(except: null);

    internal ValueTask InvokeShutdownAsync(Action action)
    {
        if (CheckAccess())
        {
            action();
            return ValueTask.CompletedTask;
        }

        var work = new InvokedWork(action, CancellationToken.None);
        Queue(work, _application.DangerousNativeHandle, allowShutdown: true);
        MarkShutdown(work);
        return new ValueTask(work.Task);
    }

    private void Queue(IDispatchWork work)
        => Queue(work, _application.NativeHandle, allowShutdown: false);

    private void Queue(IDispatchWork work, NativeMethods.neoastra_app_t handle, bool allowShutdown)
    {
        lock (_sync)
        {
            if (_shutdown && !allowShutdown)
            {
                throw new ObjectDisposedException(nameof(NeoApplication));
            }

            var registration = new DispatchRegistration(this, work);
            var root = GCHandle.Alloc(registration);
            var context = GCHandle.ToIntPtr(root);
            NativeMethods.neoastra_result_t result;
            try
            {
                result = NativeMethods.neoastra_app_dispatch(
                    handle,
                    (delegate* unmanaged[Cdecl]<void*, void>)&Dispatch,
                    (void*)context);
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

            // Native dispatch promises not to invoke the callback before returning. Holding
            // _sync here lets a callback race safely with registration and shutdown.
            _outstanding.Add(context, registration);
        }
    }

    private void MarkShutdown(IDispatchWork? except)
    {
        IDispatchWork[] pending;
        lock (_sync)
        {
            _shutdown = true;
            pending = _outstanding.Values
                .Select(static registration => registration.Work)
                .Where(work => !ReferenceEquals(work, except))
                .ToArray();
        }

        foreach (var work in pending)
        {
            work.Cancel();
        }
    }

    private void CompleteDispatch(nint context, DispatchRegistration registration)
    {
        lock (_sync)
        {
            _outstanding.Remove(context);
        }

        registration.Work.Execute();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Dispatch(void* context)
    {
        try
        {
            var contextValue = (nint)context;
            var root = GCHandle.FromIntPtr(contextValue);
            var registration = root.Target as DispatchRegistration;
            root.Free();
            registration?.Dispatcher.CompleteDispatch(contextValue, registration);
        }
        catch
        {
            // No managed exception may cross the unmanaged callback boundary.
        }
    }

    private interface IDispatchWork
    {
        void Execute();

        void Cancel();
    }

    private sealed class PostedWork(Action action) : IDispatchWork
    {
        private int _completed;

        public void Execute()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return;
            }

            try { action(); } catch { }
        }

        public void Cancel() => Interlocked.Exchange(ref _completed, 1);
    }

    private sealed class InvokedWork : IDispatchWork
    {
        private readonly Action _action;
        private readonly CancellationToken _cancellationToken;
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _completed;

        internal InvokedWork(Action action, CancellationToken cancellationToken)
        {
            _action = action;
            _cancellationToken = cancellationToken;
        }

        internal Task Task => _completion.Task;

        public void Execute()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return;
            }

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

        public void Cancel()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                _completion.TrySetCanceled(ShutdownCancellationToken);
            }
        }
    }

    private sealed class InvokedWork<T> : IDispatchWork
    {
        private readonly Func<T> _function;
        private readonly CancellationToken _cancellationToken;
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _completed;

        internal InvokedWork(Func<T> function, CancellationToken cancellationToken)
        {
            _function = function;
            _cancellationToken = cancellationToken;
        }

        internal Task<T> Task => _completion.Task;

        public void Execute()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return;
            }

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

        public void Cancel()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                _completion.TrySetCanceled(ShutdownCancellationToken);
            }
        }
    }

    private sealed class DispatchRegistration(NeoDispatcher dispatcher, IDispatchWork work)
    {
        internal NeoDispatcher Dispatcher { get; } = dispatcher;

        internal IDispatchWork Work { get; } = work;
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
