// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using NeoAstra.Rpc;

namespace NeoAstra.Tests;

/// <summary>
/// How a session sends and admits frames: one ordered queue sent in batches, a rate charged when a frame arrived,
/// and denials that are forgotten over time.
/// </summary>
[TestClass]
public sealed class RpcSessionFlowTests
{
    private static NeoRpcCommandOptions Trusted => new() { MaximumConcurrency = 4096 };

    [TestMethod]
    public async Task FramesQueuedWhileABatchIsSent_LeaveTogetherInTheNextBatch_InOrder()
    {
        var batches = new ConcurrentQueue<string[]>();
        var firstSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = new NeoRpcBuilder(new NeoRpcOptions());
        builder.AddCommand<Request, Response>("flow.echo", (request, _, _) => ValueTask.FromResult(new Response(request.Id, "view")),
            RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response, Trusted);
        await using var host = builder.Build();
        await using var session = host.OpenSessionCore(new NeoRpcSessionIdentity("fixture", "batch-session"),
            (_, _) => throw new AssertFailedException("A view session sends in batches."),
            async frames =>
            {
                batches.Enqueue([.. frames]);
                if (batches.Count == 1) { firstSent.TrySetResult(); await releaseFirst.Task; }
            }, null, null);

        var first = session.ReceiveAsync(Invoke("first", "flow.echo", "{\"id\":\"first\"}")).AsTask();
        await firstSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var others = Enumerable.Range(0, 20).Select(index => session.ReceiveAsync(Invoke($"call-{index}", "flow.echo", $"{{\"id\":\"{index}\"}}")).AsTask()).ToArray();
        // The answers of the 20 calls wait behind the batch being sent: queued, not merely being computed, so that a slow
        // runner does not release the batch before they are there.
        await WaitUntilAsync(() => session.QueuedOutboundFrameCount >= 20);
        releaseFirst.TrySetResult();
        await Task.WhenAll(others.Append(first)).WaitAsync(TimeSpan.FromSeconds(5));

        var sent = batches.SelectMany(static batch => batch).Select(static frame => JsonDocument.Parse(frame).RootElement.GetProperty("id").GetString()).ToArray();
        Assert.HasCount(21, sent);
        Assert.AreEqual("first", sent[0]);
        CollectionAssert.AreEquivalent(Enumerable.Range(0, 20).Select(index => $"call-{index}").ToArray(), sent[1..]);
        Assert.IsLessThan(21, batches.Count, "Frames that queued together leave together.");
    }

    [TestMethod]
    public async Task ABatchTheTransportRefuses_FailsItsFrames_AndClosesTheSession()
    {
        var builder = new NeoRpcBuilder(new NeoRpcOptions());
        builder.AddCommand<Request, Response>("flow.echo", (request, _, _) => ValueTask.FromResult(new Response(request.Id, "view")),
            RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response, Trusted);
        await using var host = builder.Build();
        await using var session = host.OpenSessionCore(new NeoRpcSessionIdentity("fixture", "failing-session"),
            (_, _) => ValueTask.CompletedTask, _ => throw new InvalidOperationException("The view is gone."), null, null);
        try { await session.ReceiveAsync(Invoke("lost", "flow.echo", "{\"id\":\"lost\"}")).AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception) { /* The invocation fails with its frame. */ }
        Assert.IsTrue(session.Closed.IsCancellationRequested);
    }

    [TestMethod]
    public async Task FramesSentOneByOne_KeepTheirOrder_UnderConcurrentAnswers()
    {
        var frames = new ConcurrentQueue<string>();
        var builder = new NeoRpcBuilder(new NeoRpcOptions());
        builder.AddCommand<Request, Response>("flow.echo", async (request, _, _) => { await Task.Yield(); return new Response(request.Id, "view"); },
            RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response, Trusted);
        await using var host = builder.Build();
        await using var session = host.OpenSession(new NeoRpcSessionIdentity("fixture", "single-session"), async (json, _) => { await Task.Yield(); frames.Enqueue(json); });
        await Task.WhenAll(Enumerable.Range(0, 200).Select(index => session.ReceiveAsync(Invoke($"call-{index}", "flow.echo", $"{{\"id\":\"{index}\"}}")).AsTask()))
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.HasCount(200, frames);
        Assert.HasCount(200, frames.Select(static frame => JsonDocument.Parse(frame).RootElement.GetProperty("id").GetString()).Distinct().ToArray());
    }

    [TestMethod]
    public async Task TheRate_IsChargedWhenAFrameArrived_NotWhenItIsProcessed()
    {
        var builder = new NeoRpcBuilder(new NeoRpcOptions { RequestRatePerSecond = 1, RequestRateBurst = 1, AbuseClosureThreshold = 100 });
        builder.AddCommand<Request, Response>("flow.echo", (request, _, _) => ValueTask.FromResult(new Response(request.Id, "view")),
            RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response, Trusted);
        await using var host = builder.Build();
        var frames = new ConcurrentQueue<string>();
        await using var session = host.OpenSession(new NeoRpcSessionIdentity("fixture", "rate-session"), (json, _) => { frames.Enqueue(json); return ValueTask.CompletedTask; });
        var start = Stopwatch.GetTimestamp();
        var second = Stopwatch.Frequency;
        // Two frames that arrived two seconds apart and are processed one after the other: both are within the rate.
        await session.ReceiveAsync(Invoke("a", "flow.echo", "{\"id\":\"a\"}"), null, true, start, CancellationToken.None);
        await session.ReceiveAsync(Invoke("b", "flow.echo", "{\"id\":\"b\"}"), null, true, start + 2 * second, CancellationToken.None);
        // A third that arrived with the second is over it.
        await session.ReceiveAsync(Invoke("c", "flow.echo", "{\"id\":\"c\"}"), null, true, start + 2 * second, CancellationToken.None);
        var codes = frames.Select(static frame => JsonDocument.Parse(frame).RootElement)
            .ToDictionary(static root => root.GetProperty("id").GetString()!, static root => root.TryGetProperty("error", out var error) ? error.GetProperty("code").GetString() : null);
        Assert.IsNull(codes["a"]);
        Assert.IsNull(codes["b"]);
        Assert.AreEqual("too_many_requests", codes["c"]);
    }

    [TestMethod]
    public async Task ASecondWithDenialsCountsOnce_AndIsForgottenAfterAQuietSecond()
    {
        await using var host = new NeoRpcBuilder(RateLimited()).Build();
        await using var session = host.OpenSession(new NeoRpcSessionIdentity("fixture", "abuse-session"), (_, _) => ValueTask.CompletedTask);
        var start = Stopwatch.GetTimestamp();
        var second = Stopwatch.Frequency;
        for (var index = 0; index < 99; index++) session.CountAbuseDenial(start);
        Assert.AreEqual(1, session.CountAbuseDenial(start), "A burst, however large, counts one second.");
        for (var index = 1; index < 5; index++) session.CountAbuseDenial(start + index * second);
        Assert.AreEqual(5, session.CountAbuseDenial(start + 4 * second), "Five seconds in a row with denials add up.");
        Assert.AreEqual(5, session.CountAbuseDenial(start + 2 * second), "A late stamp counts with the last second.");
        Assert.AreEqual(3, session.CountAbuseDenial(start + 8 * second), "Three quiet seconds forget three seconds with denials.");
        Assert.AreEqual(1, session.CountAbuseDenial(start + 60 * second), "Denials scattered over a long session never add up.");
    }

    [TestMethod]
    public void TheDefaults_AreThoseOfATrustedDesktopApp()
    {
        var options = new NeoRpcOptions();
        Assert.AreSame(NeoSecurityProfile.ProductionLocalApp, options.SecurityProfile);
        Assert.AreEqual(0, options.RequestRatePerSecond, "No request rate is enforced.");
        Assert.AreEqual(8192, options.MaximumConcurrentInvocations);
        Assert.AreEqual(1024, options.MaximumConcurrentInvocationsPerSession);
        Assert.AreEqual(1024, options.DefaultCommandMaximumConcurrency);
        Assert.AreEqual(1024, new NeoPermissionDeclaration("documents:open", 1, ["documents.open"], NeoPermissionRisk.Low, NeoScopeFamily.None).MaximumConcurrency,
            "A command without its own limit fits the default declaration of its permission.");
        Assert.AreEqual(1024, options.MaximumSubscriptionsPerSession);
        Assert.AreEqual(256, options.MaximumChannelsPerSession);
        Assert.AreEqual(64, options.MaximumUnacknowledgedChannelItems);
        Assert.AreEqual(1024, options.MaximumQueuedEventsPerSubscription);
        Assert.AreEqual(1024, options.MaximumResourcesPerSession);
        Assert.AreEqual(4096, options.MaximumResourcesPerView);
        Assert.AreEqual(65_536, options.MaximumResources);
        _ = new NeoRpcBuilder(options);
        _ = new NeoRpcBuilder(new NeoRpcOptions { RequestRatePerSecond = 0, RequestRateBurst = 1 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new NeoRpcBuilder(new NeoRpcOptions { RequestRatePerSecond = -1 }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new NeoRpcBuilder(new NeoRpcOptions { RequestRatePerSecond = 300, RequestRateBurst = 200 }));
    }

    [TestMethod]
    public async Task TheDefaults_AdmitEveryInvokeOfABurstOf5000()
    {
        var builder = new NeoRpcBuilder(new NeoRpcOptions());
        builder.AddCommand<Request, Response>("flow.echo", (request, _, _) => ValueTask.FromResult(new Response(request.Id, "view")),
            RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response);
        await using var host = builder.Build();
        var frames = new ConcurrentQueue<string>();
        await using var session = host.OpenSession(new NeoRpcSessionIdentity("fixture", "burst-session"), (json, _) => { frames.Enqueue(json); return ValueTask.CompletedTask; });
        var start = Stopwatch.GetTimestamp();
        for (var index = 0; index < 5000; index++)
            await session.ReceiveAsync(Invoke($"burst-{index}", "flow.echo", "{\"id\":\"x\"}"), null, true, start, CancellationToken.None);
        Assert.HasCount(5000, frames);
        Assert.IsTrue(frames.All(static frame => ErrorCode(frame) is null), "No frame of the burst is refused.");
        Assert.AreEqual(1, host.ActiveSessionCount);
    }

    [TestMethod]
    public async Task TheDefaults_Admit1000ConcurrentCallsOfOneCommandInOneSession()
    {
        const int calls = 1000;
        var entered = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = new NeoRpcBuilder(new NeoRpcOptions());
        builder.AddCommand<Request, Response>("flow.wait", async (request, _, _) =>
        {
            Interlocked.Increment(ref entered);
            await release.Task;
            return new Response(request.Id, "view");
        }, RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response);
        await using var host = builder.Build();
        var frames = new ConcurrentQueue<string>();
        await using var session = host.OpenSession(new NeoRpcSessionIdentity("fixture", "concurrent-session"), (json, _) => { frames.Enqueue(json); return ValueTask.CompletedTask; });
        var invocations = Enumerable.Range(0, calls).Select(index => session.ReceiveAsync(Invoke($"call-{index}", "flow.wait", "{\"id\":\"x\"}")).AsTask()).ToArray();
        try
        {
            await WaitUntilAsync(() => Volatile.Read(ref entered) == calls || !frames.IsEmpty);
            Assert.IsEmpty(frames, "No call is refused.");
            Assert.AreEqual(calls, Volatile.Read(ref entered));
            Assert.AreEqual(calls, session.ActiveInvocationCount);
        }
        finally
        {
            release.TrySetResult();
        }
        await Task.WhenAll(invocations).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.HasCount(calls, frames);
        Assert.IsTrue(frames.All(static frame => ErrorCode(frame) is null));
    }

    // A rate an application configures as a safety rail: 100 frames a second, a burst of 200, closure after 20 seconds.
    private static NeoRpcOptions RateLimited(INeoRpcDiagnosticSink? diagnostics = null)
        => new() { RequestRatePerSecond = 100, RequestRateBurst = 200, AbuseClosureThreshold = 20, DiagnosticSink = diagnostics };

    [TestMethod]
    public async Task ABurstOverAConfiguredRate_IsRefusedInPart_AndTheSessionKeepsServing()
    {
        // A window that restores 30 tabs, each calling and subscribing a few things, sends some 250 frames in one tick.
        var diagnostics = new ConcurrentQueue<NeoRpcDiagnostic>();
        var builder = new NeoRpcBuilder(RateLimited(new DiagnosticSink(diagnostics)));
        builder.AddCommand<Request, Response>("flow.echo", (request, _, _) => ValueTask.FromResult(new Response(request.Id, "view")),
            RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response, Trusted);
        await using var host = builder.Build();
        var frames = new ConcurrentQueue<string>();
        await using var session = host.OpenSession(new NeoRpcSessionIdentity("fixture", "burst-session"), (json, _) => { frames.Enqueue(json); return ValueTask.CompletedTask; });
        var start = Stopwatch.GetTimestamp();
        for (var index = 0; index < 250; index++)
            await session.ReceiveAsync(Invoke($"burst-{index}", "flow.echo", "{\"id\":\"x\"}"), null, true, start, CancellationToken.None);

        Assert.AreEqual(50, frames.Count(static frame => ErrorCode(frame) == NeoRpcErrorCodes.TooManyRequests), "The burst is 200 frames.");
        Assert.IsFalse(session.Closed.IsCancellationRequested, "One burst must never close the session.");
        Assert.AreEqual(1, host.ActiveSessionCount);
        Assert.AreEqual(1, diagnostics.Count(static value => value.Code == NeoCapabilityDecisionCodes.LimitExceeded), "A second with denials is diagnosed once.");

        await session.ReceiveAsync(Invoke("later", "flow.echo", "{\"id\":\"x\"}"), null, true, start + Stopwatch.Frequency, CancellationToken.None);
        Assert.IsNull(ErrorCode(frames.Last()), "A call one second later is served.");
    }

    [TestMethod]
    public async Task SustainedFloodingForTheThresholdOfSeconds_ClosesTheSession()
    {
        var builder = new NeoRpcBuilder(RateLimited());
        builder.AddCommand<Request, Response>("flow.echo", (request, _, _) => ValueTask.FromResult(new Response(request.Id, "view")),
            RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response, Trusted);
        await using var host = builder.Build();
        await using var session = host.OpenSession(new NeoRpcSessionIdentity("fixture", "flood-session"), (_, _) => ValueTask.CompletedTask);
        var start = Stopwatch.GetTimestamp();
        var threshold = RateLimited().AbuseClosureThreshold;
        for (var second = 0; second < threshold; second++)
        {
            Assert.AreEqual(1, host.ActiveSessionCount, $"The session closed after {second} seconds of flooding.");
            // More than the burst in the first second, and more than the rate in every one after.
            for (var index = 0; index < 250; index++)
            {
                try { await session.ReceiveAsync(Invoke($"flood-{second}-{index}", "flow.echo", "{\"id\":\"x\"}"), null, true, start + second * Stopwatch.Frequency, CancellationToken.None); }
                catch (ObjectDisposedException) { break; }
            }
        }
        Assert.AreEqual(0, host.ActiveSessionCount, "A page that keeps flooding is closed.");
    }

    private static string Invoke(string id, string command, string args) => $"{{\"neoastra\":1,\"kind\":\"invoke\",\"id\":\"{id}\",\"command\":\"{command}\",\"args\":{args}}}";

    private static string? ErrorCode(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("error", out var error) ? error.GetProperty("code").GetString() : null;
    }

    private sealed class DiagnosticSink(ConcurrentQueue<NeoRpcDiagnostic> values) : INeoRpcDiagnosticSink
    {
        public void Write(NeoRpcDiagnostic diagnostic) => values.Enqueue(diagnostic);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (var index = 0; index < 400 && !predicate(); index++) await Task.Delay(5);
        Assert.IsTrue(predicate());
    }
}
