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
        // The answers of the 20 calls wait behind the batch being sent.
        await WaitUntilAsync(() => session.ActiveInvocationCount >= 20);
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
    public async Task DenialsAreForgottenOnePerQuietSecond()
    {
        await using var host = new NeoRpcBuilder(new NeoRpcOptions()).Build();
        await using var session = host.OpenSession(new NeoRpcSessionIdentity("fixture", "abuse-session"), (_, _) => ValueTask.CompletedTask);
        var start = Stopwatch.GetTimestamp();
        var second = Stopwatch.Frequency;
        for (var index = 0; index < 9; index++) session.CountAbuseDenial(start);
        Assert.AreEqual(10, session.CountAbuseDenial(start), 1e-9, "A flood adds up.");
        Assert.AreEqual(6, session.CountAbuseDenial(start + 5 * second), 1e-9, "Five quiet seconds forget five denials.");
        Assert.AreEqual(1, session.CountAbuseDenial(start + 60 * second), 1e-9, "Denials scattered over a long session never add up.");
    }

    private static string Invoke(string id, string command, string args) => $"{{\"neoastra\":1,\"kind\":\"invoke\",\"id\":\"{id}\",\"command\":\"{command}\",\"args\":{args}}}";

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (var index = 0; index < 400 && !predicate(); index++) await Task.Delay(5);
        Assert.IsTrue(predicate());
    }
}
