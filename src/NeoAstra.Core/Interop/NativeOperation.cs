// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.InteropServices;

namespace NeoAstra.Interop;

internal sealed class NativeOperation<T>
{
    private readonly object _sync = new();
    private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationToken _cancellationToken;
    private CancellationTokenRegistration _cancellationRegistration;
    private GCHandle _context;
    private SafeOperationHandle? _operation;
    private bool _nativeCompleted;
    private int _managedCompleted;

    internal NativeOperation(CancellationToken cancellationToken, object? owner = null)
    {
        _cancellationToken = cancellationToken;
        Owner = owner;
        _context = GCHandle.Alloc(this);
        if (cancellationToken.CanBeCanceled)
        {
            _cancellationRegistration = cancellationToken.Register(static state => ((NativeOperation<T>)state!).Cancel(), this);
        }
    }

    internal nint Context => GCHandle.ToIntPtr(_context);

    internal object? Owner { get; }

    internal ValueTask<T> ValueTask => new(_completion.Task);

    internal static unsafe NativeOperation<T>? FromContext(void* context)
    {
        try
        {
            return GCHandle.FromIntPtr((nint)context).Target as NativeOperation<T>;
        }
        catch
        {
            return null;
        }
    }

    internal void AttachOperation(nint operation)
    {
        if (operation == 0)
        {
            return;
        }

        var handle = new SafeOperationHandle(operation);
        var cancel = false;
        lock (_sync)
        {
            if (_nativeCompleted)
            {
                handle.Dispose();
                return;
            }

            _operation = handle;
            cancel = _cancellationToken.IsCancellationRequested;
        }

        if (cancel)
        {
            handle.Cancel();
        }
    }

    internal void Complete(T value) => FinishNative(() => _completion.TrySetResult(value));

    internal void Fail(Exception exception) => FinishNative(() => _completion.TrySetException(exception));

    internal void FailStart(Exception exception) => FinishNative(() => _completion.TrySetException(exception));

    private void Cancel()
    {
        SafeOperationHandle? operation;
        lock (_sync)
        {
            operation = _operation;
        }

        if (Interlocked.CompareExchange(ref _managedCompleted, 1, 0) == 0)
        {
            _completion.TrySetCanceled(_cancellationToken);
        }

        try
        {
            operation?.Cancel();
        }
        catch
        {
            // Cancellation is best effort. Native completion still owns context cleanup.
        }
    }

    private void FinishNative(Action complete)
    {
        SafeOperationHandle? operation;
        CancellationTokenRegistration registration;
        GCHandle context;
        lock (_sync)
        {
            if (_nativeCompleted)
            {
                return;
            }

            _nativeCompleted = true;
            operation = _operation;
            _operation = null;
            registration = _cancellationRegistration;
            _cancellationRegistration = default;
            context = _context;
            _context = default;
        }

        if (Interlocked.CompareExchange(ref _managedCompleted, 1, 0) == 0)
        {
            complete();
        }

        registration.Dispose();
        operation?.Dispose();
        if (context.IsAllocated)
        {
            context.Free();
        }
    }
}

internal static class NativeOperation
{
    internal static unsafe NativeOperation<T>? Get<T>(void* context) => NativeOperation<T>.FromContext(context);
}
