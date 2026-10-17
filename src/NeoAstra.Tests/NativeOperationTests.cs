// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.InteropServices;
using NeoAstra.Interop;

namespace NeoAstra.Tests;

[TestClass]
public sealed class NativeOperationTests
{
    [TestMethod]
    public async Task CancellationKeepsContextUntilNativeTerminalCompletion()
    {
        using var cancellation = new CancellationTokenSource();
        var operation = new NativeOperation<int>(cancellation.Token);
        var context = operation.Context;
        Assert.AreNotEqual(nint.Zero, context);

        cancellation.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(() => operation.ValueTask.AsTask());
        Assert.AreEqual(context, operation.Context);
        Assert.AreSame(operation, GCHandle.FromIntPtr(context).Target);

        // Native shutdown/rejection cleanup, not token cancellation, owns the root.
        operation.Fail(new OperationCanceledException("Native operation canceled during shutdown."));
        Assert.AreEqual(nint.Zero, operation.Context);
        operation.Complete(42);
        operation.Fail(new InvalidOperationException("Duplicate terminal delivery."));
        Assert.AreEqual(nint.Zero, operation.Context);
        await Assert.ThrowsAsync<TaskCanceledException>(() => operation.ValueTask.AsTask());
    }

    [TestMethod]
    public async Task NativeCompletionAndFailedStartReleaseContextExactlyOnce()
    {
        var completed = new NativeOperation<int>(CancellationToken.None);
        completed.Complete(42);
        completed.Complete(7);
        Assert.AreEqual(42, await completed.ValueTask);
        Assert.AreEqual(nint.Zero, completed.Context);

        var failed = new NativeOperation<int>(CancellationToken.None);
        failed.FailStart(new InvalidOperationException("Start rejected."));
        failed.FailStart(new InvalidOperationException("Duplicate failed start."));
        Assert.AreEqual(nint.Zero, failed.Context);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => failed.ValueTask.AsTask());
        Assert.AreEqual("Start rejected.", exception.Message);
    }
}
