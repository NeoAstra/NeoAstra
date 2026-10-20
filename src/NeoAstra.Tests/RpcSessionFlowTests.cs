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
                batches.Enqueue([.. frames.Select(static frame => frame.ToString())]);
                if (batches.Count == 1) { firstSent.TrySetResult(); await releaseFirst.Task; }
                return null;
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
    public async Task TheEventsOfOneSubscription_LeaveTogether_InSequenceOrder()
    {
        var gate = new BatchGate(static frames => frames.Any(static frame => Kind(frame) == "result"));
        var builder = new NeoRpcBuilder(new NeoRpcOptions());
        var changed = builder.AddEvent("flow.changed", RpcTestJsonContext.Default.Response);
        await using var host = builder.Build();
        await using var session = host.OpenSessionCore(new NeoRpcSessionIdentity("fixture", "event-batch-session"),
            (_, _) => throw new AssertFailedException("A view session sends in batches."), gate.SendAsync, null, null);
        using var releaseBeforeTeardown = gate;
        await session.ReceiveAsync("{\"neoastra\":1,\"kind\":\"subscribe\",\"id\":\"sub\",\"event\":\"flow.changed\"}");
        // An answer held in its batch: the events published meanwhile wait behind it.
        var holding = session.ReceiveAsync(Invoke("hold", "missing.command", "{}")).AsTask();
        await gate.Held.WaitAsync(TimeSpan.FromSeconds(5));
        for (var index = 0; index < 20; index++) Assert.AreEqual(1, await changed.PublishAsync(new Response(index.ToString(), "view")));
        var batchesBefore = gate.Batches;
        gate.Release();
        await holding.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => gate.Frames.Count(static frame => Kind(frame) == "event") == 20);

        var events = gate.Frames.Where(static frame => Kind(frame) == "event").Select(static frame => JsonDocument.Parse(frame).RootElement).ToArray();
        CollectionAssert.AreEqual(Enumerable.Range(1, 20).Select(static value => (long)value).ToArray(), events.Select(static root => root.GetProperty("sequence").GetInt64()).ToArray());
        CollectionAssert.AreEqual(Enumerable.Range(0, 20).Select(static value => value.ToString()).ToArray(), events.Select(static root => root.GetProperty("value").GetProperty("title").GetString()).ToArray());
        // The pump takes what was published while it waited, together: one round trip each would take 20 batches.
        Assert.IsLessThan(10, gate.Batches - batchesBefore, "The events of a subscription leave together, not one round trip each.");
    }

    [TestMethod]
    public async Task TheItemsOfAChannel_LeaveTogether_UpToItsUnacknowledgedWindow()
    {
        var gate = new BatchGate(static frames => frames.Any(static frame => Kind(frame) == "channel_item"));
        var builder = new NeoRpcBuilder(new NeoRpcOptions { MaximumUnacknowledgedChannelItems = 16 });
        builder.AddChannelCommand<Request, Response>("flow.stream", (_, _, _) => ValueTask.FromResult(new NeoRpcChannel<Response>(Items(40), RpcTestJsonContext.Default.Response)),
            RpcTestJsonContext.Default.Request);
        await using var host = builder.Build();
        await using var session = host.OpenSessionCore(new NeoRpcSessionIdentity("fixture", "channel-batch-session"),
            (_, _) => throw new AssertFailedException("A view session sends in batches."), gate.SendAsync, null, null);
        using var releaseBeforeTeardown = gate;
        await session.ReceiveAsync(Invoke("stream", "flow.stream", "{\"id\":\"x\"}"));
        await gate.Held.WaitAsync(TimeSpan.FromSeconds(5));
        // The first items are held in their batch; the rest of the 16 credits of the window queue without waiting for them.
        await WaitUntilAsync(() => ItemCount(gate) + session.QueuedOutboundFrameCount == 16);
        await Task.Delay(50);
        Assert.AreEqual(16, ItemCount(gate) + session.QueuedOutboundFrameCount, "No item beyond the unacknowledged window is queued.");
        gate.Release();
        await WaitUntilAsync(() => gate.Frames.Count(static frame => Kind(frame) == "channel_item") == 16);
        var channel = JsonDocument.Parse(gate.Frames.First(static frame => Kind(frame) == "result")).RootElement.GetProperty("value").GetProperty("channel").GetString();
        await session.ReceiveAsync($"{{\"neoastra\":1,\"kind\":\"channel_ack\",\"channel\":\"{channel}\",\"sequence\":16}}");
        await WaitUntilAsync(() => gate.Frames.Count(static frame => Kind(frame) == "channel_item") == 32);
        await session.ReceiveAsync($"{{\"neoastra\":1,\"kind\":\"channel_ack\",\"channel\":\"{channel}\",\"sequence\":32}}");
        await WaitUntilAsync(() => gate.Frames.Any(static frame => Kind(frame) == "channel_complete"));

        var items = gate.Frames.Where(static frame => Kind(frame) == "channel_item").Select(static frame => JsonDocument.Parse(frame).RootElement).ToArray();
        CollectionAssert.AreEqual(Enumerable.Range(1, 40).Select(static value => (long)value).ToArray(), items.Select(static root => root.GetProperty("sequence").GetInt64()).ToArray());
        Assert.AreEqual("channel_complete", Kind(gate.Frames.Last()), "The terminal frame follows every item.");
        Assert.IsLessThan(20, gate.Batches, "Items leave in batches, not one round trip each.");

        static async IAsyncEnumerable<Response> Items(int count)
        {
            for (var index = 0; index < count; index++) yield return new Response(index.ToString(), "view");
            await Task.CompletedTask;
        }
    }

    [TestMethod]
    public async Task TheQueuedEventsOfAClosedSubscription_AreWithdrawn()
    {
        var gate = new BatchGate(static frames => frames.Any(static frame => Kind(frame) == "result"));
        var builder = new NeoRpcBuilder(new NeoRpcOptions());
        var changed = builder.AddEvent("flow.changed", RpcTestJsonContext.Default.Response);
        await using var host = builder.Build();
        await using var session = host.OpenSessionCore(new NeoRpcSessionIdentity("fixture", "withdraw-session"),
            (_, _) => throw new AssertFailedException("A view session sends in batches."), gate.SendAsync, null, null);
        using var releaseBeforeTeardown = gate;
        await session.ReceiveAsync("{\"neoastra\":1,\"kind\":\"subscribe\",\"id\":\"sub\",\"event\":\"flow.changed\"}");
        var holding = session.ReceiveAsync(Invoke("hold", "missing.command", "{}")).AsTask();
        await gate.Held.WaitAsync(TimeSpan.FromSeconds(5));
        for (var index = 0; index < 10; index++) await changed.PublishAsync(new Response("late", "view"));
        // The pump queued what it took behind the held answer.
        await WaitUntilAsync(() => session.QueuedOutboundFrameCount >= 1);

        await session.ReceiveAsync("{\"neoastra\":1,\"kind\":\"unsubscribe\",\"id\":\"sub\"}").AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release();
        await holding.WaitAsync(TimeSpan.FromSeconds(5));
        await session.ReceiveAsync(Invoke("after", "missing.command", "{}"));
        await WaitUntilAsync(() => gate.Frames.Count(static frame => Kind(frame) == "result") == 2);
        Assert.AreEqual(0, gate.Frames.Count(static frame => Kind(frame) == "event"), "The events queued for a closed subscription are not sent.");
        Assert.AreEqual(0, session.ActiveSubscriptionCount);
        Assert.AreEqual(0, session.QueuedOutboundFrameCount);
    }

    [TestMethod]
    public async Task TheQueuedItemsOfAClosedChannel_AreWithdrawn_BeforeItsTerminalFrame()
    {
        var gate = new BatchGate(static frames => frames.Any(static frame => Kind(frame) == "channel_item"));
        var builder = new NeoRpcBuilder(new NeoRpcOptions { MaximumUnacknowledgedChannelItems = 16 });
        builder.AddChannelCommand<Request, Response>("flow.stream", (_, _, _) => ValueTask.FromResult(new NeoRpcChannel<Response>(Endless(gate.Held), RpcTestJsonContext.Default.Response)),
            RpcTestJsonContext.Default.Request);
        await using var host = builder.Build();
        await using var session = host.OpenSessionCore(new NeoRpcSessionIdentity("fixture", "channel-withdraw-session"),
            (_, _) => throw new AssertFailedException("A view session sends in batches."), gate.SendAsync, null, null);
        using var releaseBeforeTeardown = gate;
        await session.ReceiveAsync(Invoke("stream", "flow.stream", "{\"id\":\"x\"}"));
        await gate.Held.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => ItemCount(gate) + session.QueuedOutboundFrameCount == 16);
        var held = ItemCount(gate);
        var channel = JsonDocument.Parse(gate.Frames.First(static frame => Kind(frame) == "result")).RootElement.GetProperty("value").GetProperty("channel").GetString();
        await session.ReceiveAsync($"{{\"neoastra\":1,\"kind\":\"channel_close\",\"channel\":\"{channel}\"}}");
        gate.Release();
        await WaitUntilAsync(() => session.ActiveChannelCount == 0 && gate.Frames.Any(static frame => Kind(frame) is "channel_complete" or "channel_error"));
        Assert.AreEqual(held, ItemCount(gate), "Only the items already being sent leave; those queued after them are withdrawn.");
        Assert.IsLessThan(16, held);
        Assert.AreEqual(0, session.QueuedOutboundFrameCount);

        // The items after the first come while it is held in its batch: they queue behind it, whatever the speed of the writer.
        static async IAsyncEnumerable<Response> Endless(Task held, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new Response("0", "view");
            await held.WaitAsync(cancellationToken).ConfigureAwait(false);
            for (var index = 1; !cancellationToken.IsCancellationRequested; index++) yield return new Response(index.ToString(), "view");
        }
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
    public async Task TheContextOfACall_CarriesTheSessionAndTheSenderOfItsFrame()
    {
        var contexts = new ConcurrentQueue<NeoRpcContext>();
        var builder = new NeoRpcBuilder(new NeoRpcOptions { ContractHash = "contract" });
        builder.AddCommand<Request, Response>("flow.context", (request, context, _) => { contexts.Enqueue(context); return ValueTask.FromResult(new Response(request.Id, "view")); },
            RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response);
        await using var host = builder.Build();
        var frames = new ConcurrentQueue<string>();
        var identity = new NeoRpcSessionIdentity("context-view", "context-session")
        {
            SourceOrigin = new Uri("https://initial.example"), IsMainFrame = true, WholeViewTrust = true, DocumentId = "document",
            ProtocolMinor = 3, Features = ["rpc"], ContractHash = "contract",
        };
        await using var session = host.OpenSession(identity, (json, _) => { frames.Enqueue(json); return ValueTask.CompletedTask; });
        const string invoke = "{\"neoastra\":1,\"kind\":\"invoke\",\"id\":\"ID\",\"command\":\"flow.context\",\"contract\":\"contract\",\"args\":{\"id\":\"x\"}}";
        await session.ReceiveAsync(invoke.Replace("ID", "first"));
        await session.ReceiveAsync(invoke.Replace("ID", "second"), new Uri("https://frame.example"), false);
        await session.ReceiveAsync(invoke.Replace("ID", "bad\u007fid"));

        var values = contexts.ToArray();
        Assert.HasCount(2, values);
        foreach (var context in values)
        {
            Assert.AreEqual("context-view", context.ViewLabel);
            Assert.AreEqual("context-session", context.DocumentSessionId);
            Assert.AreEqual("document", context.DocumentId);
            Assert.IsTrue(context.WholeViewTrust);
            Assert.AreEqual(3, context.ProtocolMinor);
            CollectionAssert.AreEqual(new[] { "rpc" }, context.Features.ToArray());
            Assert.AreEqual("contract", context.ContractHash);
            Assert.IsFalse(context.TryGetView(out _));
            Assert.IsFalse(context.TryGetWindow(out _));
            StringAssert.Matches(context.CorrelationId, new System.Text.RegularExpressions.Regex("^[0-9a-f]{32}$"));
        }
        Assert.AreEqual(new Uri("https://initial.example"), values[0].SourceOrigin);
        Assert.IsTrue(values[0].IsMainFrame);
        Assert.AreEqual(new Uri("https://frame.example"), values[1].SourceOrigin, "The authenticated sender of a frame wins over the initial identity.");
        Assert.IsFalse(values[1].IsMainFrame);
        Assert.AreNotEqual(values[0].CorrelationId, values[1].CorrelationId);
        Assert.AreEqual(NeoRpcErrorCodes.InvalidRequest, ErrorCode(frames.Last()), "An ID outside printable ASCII is refused.");
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

    [TestMethod]
    public async Task PublishingWhileSubscriptionsAndSessionsClose_NeverThrows()
    {
        // Small queues: publications overflow and drop while subscriptions and sessions close under them.
        var builder = new NeoRpcBuilder(new NeoRpcOptions { MaximumQueuedEventsPerSubscription = 2 });
        var dropping = builder.AddEvent("flow.dropping", RpcTestJsonContext.Default.Response);
        var failing = builder.AddEvent("flow.failing", RpcTestJsonContext.Default.Response, new() { OverflowBehavior = NeoRpcOverflowBehavior.Fail });
        await using var host = builder.Build();
        var failures = new ConcurrentQueue<Exception>();
        using var stop = new CancellationTokenSource();
        var publishers = Enumerable.Range(0, Math.Max(2, Environment.ProcessorCount / 2)).Select(_ => Task.Run(async () =>
        {
            var value = new Response("x", "view");
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await dropping.PublishAsync(value);
                    await dropping.PublishAsync(value, static context => context.IsMainFrame);
                    await failing.PublishAsync(value);
                }
                catch (Exception exception) { failures.Enqueue(exception); }
            }
        })).ToArray();

        var deadline = Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency;
        for (var round = 0; Stopwatch.GetTimestamp() < deadline || round < 50; round++)
        {
            var session = host.OpenSession(new NeoRpcSessionIdentity("fixture", $"race-{round}"), async (_, cancellationToken) => await Task.Yield());
            try
            {
                for (var index = 0; index < 8; index++)
                    await session.ReceiveAsync($"{{\"neoastra\":1,\"kind\":\"subscribe\",\"id\":\"sub-{index}\",\"event\":\"{(index % 2 == 0 ? "flow.dropping" : "flow.failing")}\"}}");
                var unsubscribing = Enumerable.Range(0, 4).Select(index => Task.Run(() => session.ReceiveAsync($"{{\"neoastra\":1,\"kind\":\"unsubscribe\",\"id\":\"sub-{index}\"}}").AsTask())).ToArray();
                await session.DisposeAsync();
                try { await Task.WhenAll(unsubscribing); }
                catch (ObjectDisposedException) { /* An unsubscribe that arrives after the session closed is refused as such. */ }
                _ = session.Closed.IsCancellationRequested;
            }
            catch (Exception exception) { failures.Enqueue(exception); }
        }
        stop.Cancel();
        await Task.WhenAll(publishers);
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures.Take(5)));
        Assert.AreEqual(0, host.ActiveSessionCount);
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

    private static int ItemCount(BatchGate gate) => gate.Frames.Count(static frame => Kind(frame) == "channel_item");

    private static string? Kind(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("kind").GetString();
    }

    // Records the batches a session sends, and holds the first one that matches until released.
    // Disposing it releases a held batch: a failed assertion must not leave the writer, and so teardown, blocked.
    private sealed class BatchGate(Func<IReadOnlyList<string>, bool> hold) : IDisposable
    {
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<string> _frames = new();
        private int _batches;
        private int _holding;

        internal Task Held => _held.Task;
        internal int Batches => Volatile.Read(ref _batches);
        internal IReadOnlyCollection<string> Frames => _frames;
        internal void Release() => _release.TrySetResult();
        public void Dispose() => Release();

        internal async ValueTask<NeoRpcFrameRefusal[]?> SendAsync(IReadOnlyList<NeoTransportFrame> batch)
        {
            // The frames are the session's: they are read while the batch is sent.
            var frames = batch.Select(static frame => frame.ToString()).ToArray();
            Interlocked.Increment(ref _batches);
            foreach (var frame in frames) _frames.Enqueue(frame);
            if (hold(frames) && Interlocked.Exchange(ref _holding, 1) == 0)
            {
                _held.TrySetResult();
                await _release.Task;
            }
            // A turn of a UI thread is not free: frames queued while a batch is posted leave in the next one.
            await Task.Yield();
            return null;
        }
    }

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
