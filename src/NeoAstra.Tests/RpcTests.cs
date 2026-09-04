// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using NeoAstra.Rpc;

namespace NeoAstra.Tests;

[TestClass]
public sealed class RpcTests
{
    [TestMethod]
    public async Task InvokeSupportsValueVoidUnknownMalformedAndDuplicate()
    {
        var invoked = 0;
        var (host, session, frames) = Create(builder =>
        {
            builder.AddCommand<Request, Response>("documents.open", (request, context, _) =>
            {
                Interlocked.Increment(ref invoked);
                return ValueTask.FromResult(new Response(request.Id.ToUpperInvariant(), context.ViewLabel));
            }, RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response, CommandPolicy);
            builder.AddCommand<Request>("documents.touch", (_, _, _) => ValueTask.CompletedTask, RpcTestJsonContext.Default.Request, CommandPolicy);
        });
        await using (host) await using (session)
        {
            await session.ReceiveAsync(Invoke("one", "documents.open", "{\"id\":\"readme\"}"));
            await session.ReceiveAsync(Invoke("two", "documents.touch", "{\"id\":\"readme\"}"));
            await session.ReceiveAsync(Invoke("three", "missing", "{}"));
            await session.ReceiveAsync(Invoke("one", "documents.open", "{\"id\":\"again\"}"));
            await session.ReceiveAsync("not json");
            Assert.AreEqual(1, invoked);
            var results = frames.ToArray();
            Assert.AreEqual("README", Parse(results[0]).GetProperty("value").GetProperty("title").GetString());
            Assert.AreEqual(JsonValueKind.Null, Parse(results[1]).GetProperty("value").ValueKind);
            Assert.AreEqual("command_not_found", ErrorCode(results[2]));
            Assert.AreEqual("duplicate_request", ErrorCode(results[3]));
        }
    }

    [TestMethod]
    public async Task CancelTimeoutConcurrencyAndTeardownAreBounded()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new NeoRpcOptions { MaximumConcurrentInvocations = 2, MaximumConcurrentInvocationsPerSession = 1, InvocationTimeout = TimeSpan.FromMilliseconds(40) };
        var (host, session, frames) = Create(builder => builder.AddCommand<Request, Response>("slow.wait", async (request, _, token) =>
        {
            if (request.Id == "a") firstEntered.TrySetResult();
            if (request.Id == "c") cancellationEntered.TrySetResult();
            await gate.Task.WaitAsync(token);
            return new Response(request.Id, "done");
        }, RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response, CommandPolicy), options);
        await using (host) await using (session)
        {
            var first = session.ReceiveAsync(Invoke("slow-1", "slow.wait", "{\"id\":\"a\"}")).AsTask();
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await session.ReceiveAsync(Invoke("slow-2", "slow.wait", "{\"id\":\"b\"}"));
            await first;
            Assert.IsTrue(frames.Any(frame => ErrorCode(frame) == "too_many_requests"));
            Assert.IsTrue(frames.Any(frame => ErrorCode(frame) == "timeout"));

            var canceled = session.ReceiveAsync(Invoke("slow-3", "slow.wait", "{\"id\":\"c\"}")).AsTask();
            await cancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await session.ReceiveAsync("{\"neoastra\":1,\"kind\":\"cancel\",\"id\":\"slow-3\"}");
            await canceled;
            Assert.IsTrue(frames.Any(frame => ErrorCode(frame) == "operation_canceled"));
        }
        Assert.AreEqual(0, host.ActiveSessionCount);
    }

    [TestMethod]
    public async Task InvocationTerminalWinnerIsAtomicWhenHandlersIgnoreCancellation()
    {
        var cancelEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resultEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resultRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeoutEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeoutRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, session, frames) = Create(builder =>
        {
            builder.AddCommand<Request, Response>("race.cancel", async (request, _, _) => { cancelEntered.TrySetResult(); await cancelRelease.Task; return new(request.Id, "late"); }, RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response, CommandPolicy);
            builder.AddCommand<Request, Response>("race.result", async (request, _, _) => { resultEntered.TrySetResult(); await resultRelease.Task; return new(request.Id, "winner"); }, RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response, CommandPolicy);
            builder.AddCommand<Request, Response>("race.timeout", async (request, _, _) => { timeoutEntered.TrySetResult(); await timeoutRelease.Task; return new(request.Id, "late"); }, RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response, new() { Permission = "test:invoke", Timeout = TimeSpan.FromMilliseconds(30) });
            builder.AddCommand<Request, Response>("race.close", async (request, _, _) => { closeEntered.TrySetResult(); await closeRelease.Task; return new(request.Id, "late"); }, RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response, CommandPolicy);
        });
        await using (host) await using (session)
        {
            var canceled = session.ReceiveAsync(Invoke("cancel-race", "race.cancel", "{\"id\":\"a\"}")).AsTask();
            await cancelEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await session.ReceiveAsync("{\"neoastra\":1,\"kind\":\"cancel\",\"id\":\"cancel-race\"}");
            await WaitUntilAsync(() => frames.Any(frame => Parse(frame).GetProperty("id").GetString() == "cancel-race"));
            cancelRelease.TrySetResult();
            await canceled;
            AssertSingleTerminal(frames, "cancel-race", "operation_canceled");

            var committed = session.ReceiveAsync(Invoke("result-race", "race.result", "{\"id\":\"b\"}")).AsTask();
            await resultEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            resultRelease.TrySetResult();
            await committed;
            await session.ReceiveAsync("{\"neoastra\":1,\"kind\":\"cancel\",\"id\":\"result-race\"}");
            AssertSingleTerminal(frames, "result-race", null);

            var timedOut = session.ReceiveAsync(Invoke("timeout-race", "race.timeout", "{\"id\":\"c\"}")).AsTask();
            await timeoutEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => frames.Any(frame => Parse(frame).TryGetProperty("id", out var id) && id.GetString() == "timeout-race"));
            timeoutRelease.TrySetResult();
            await timedOut;
            AssertSingleTerminal(frames, "timeout-race", "timeout");

            var closingInvocation = session.ReceiveAsync(Invoke("close-race", "race.close", "{\"id\":\"d\"}")).AsTask();
            await closeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var closing = session.DisposeAsync().AsTask();
            await WaitUntilAsync(() => frames.Any(frame => Parse(frame).TryGetProperty("id", out var id) && id.GetString() == "close-race"));
            closeRelease.TrySetResult();
            await Task.WhenAll(closingInvocation, closing);
            AssertSingleTerminal(frames, "close-race", "operation_canceled");
        }
    }

    [TestMethod]
    public async Task ResultCommitWinsCancellationThatArrivesWhileItsFrameIsSending()
    {
        var sendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = new ConcurrentQueue<string>();
        var builder = new NeoRpcBuilder(TestOptions());
        builder.AddCommand<Request, Response>("race.commit", (request, _, _) => ValueTask.FromResult(new Response(request.Id, "winner")), RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response, CommandPolicy);
        await using var host = builder.Build();
        await using var session = host.OpenSession(new NeoRpcSessionIdentity("fixture", "commit-session"), async (json, _) =>
        {
            frames.Enqueue(json);
            sendStarted.TrySetResult();
            await releaseSend.Task;
        });
        var invocation = session.ReceiveAsync(Invoke("commit-race", "race.commit", "{\"id\":\"x\"}")).AsTask();
        await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await session.ReceiveAsync("{\"neoastra\":1,\"kind\":\"cancel\",\"id\":\"commit-race\"}");
        releaseSend.TrySetResult();
        await invocation;
        AssertSingleTerminal(frames, "commit-race", null);
    }

    [TestMethod]
    public async Task PerInvocationServiceRemainsAliveUntilLazyChannelCleanup()
    {
        var disposed = 0;
        var enumeratorDisposed = false;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activator = new NeoRpcServiceActivator<ConcurrentService>(_ => new ConcurrentService(1, () =>
        {
            Assert.IsTrue(enumeratorDisposed, "The service must outlive its enumerator.");
            Interlocked.Increment(ref disposed);
        }), NeoRpcServiceLifetime.PerInvocation);
        var (host, session, frames) = Create(builder =>
        {
            builder.AddServiceActivator(activator);
            builder.AddChannelCommand<Request, Response>("service.stream", (_, context, _) =>
                activator.InvokeAsync(context, service => ValueTask.FromResult(new NeoRpcChannel<Response>(Items(service), RpcTestJsonContext.Default.Response))), RpcTestJsonContext.Default.Request);
        });
        await using (host) await using (session)
        {
            try
            {
                await session.ReceiveAsync(Invoke("lazy", "service.stream", "{\"id\":\"x\"}"));
                Assert.IsTrue(Parse(frames.First()).GetProperty("ok").GetBoolean(), "Returning a lazy channel must not dispose its service.");
                Assert.AreEqual(0, disposed);
            }
            finally { release.TrySetResult(); }
            await WaitUntilAsync(() => frames.Any(frame => Kind(frame) == "channel_complete"));
            Assert.AreEqual(1, disposed);
        }

        async IAsyncEnumerable<Response> Items(ConcurrentService service, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                await release.Task.WaitAsync(cancellationToken);
                Assert.AreEqual(0, disposed);
                yield return new Response(service.Id.ToString(), "alive");
            }
            finally { enumeratorDisposed = true; }
        }
    }

    [TestMethod]
    public async Task ChannelServiceScopesDrainEnumeratorsBeforeDisposingServices()
    {
        foreach (var lifetime in Enum.GetValues<NeoRpcServiceLifetime>())
        {
            var service = new ChannelService { HoldCleanup = true };
            var activator = new NeoRpcServiceActivator<ChannelService>(_ => service, lifetime);
            var (host, session, _) = Create(builder => RegisterChannelService(builder, activator));
            await using (host) await using (session)
            {
                try
                {
                    await session.ReceiveAsync(Invoke("scope-stream", "service.stream", "{\"id\":\"x\"}"));
                    await service.EnumerationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    var closing = session.DisposeAsync().AsTask();
                    await service.CleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    Assert.IsFalse(closing.IsCompleted, lifetime.ToString());
                    Assert.AreEqual(1, session.ActiveChannelCount, "A closing channel must remain tracked through enumerator cleanup.");
                    Assert.AreEqual(0, service.DisposeCount, lifetime.ToString());
                    service.ReleaseCleanup.TrySetResult();
                    await closing.WaitAsync(TimeSpan.FromSeconds(2));
                    Assert.AreEqual(1, service.EnumeratorDisposeCount);
                    Assert.AreEqual(lifetime is NeoRpcServiceLifetime.PerInvocation or NeoRpcServiceLifetime.PerDocumentSession ? 1 : 0, service.DisposeCount);
                    if (lifetime == NeoRpcServiceLifetime.PerView) await host.CloseViewServicesAsync("fixture");
                    await host.DisposeAsync();
                    Assert.AreEqual(1, service.DisposeCount);
                }
                finally { service.ReleaseCleanup.TrySetResult(); service.ReleaseItems.TrySetResult(); }
            }
        }
    }

    [TestMethod]
    public async Task UnstartedChannelResultsReleaseServicesOnEveryAbandonmentPath()
    {
        foreach (var mode in new[] { "cancel", "timeout", "receive-cancel", "send-failure", "reentrant-close", "null", "throw", "session-close" })
        {
            var service = new ChannelService();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var activator = new NeoRpcServiceActivator<ChannelService>(_ => service, NeoRpcServiceLifetime.PerInvocation);
            var builder = new NeoRpcBuilder();
            builder.AddServiceActivator(activator);
            builder.AddChannelCommand<Request, Response>("service.stream", (_, context, _) => activator.InvokeAsync<NeoRpcChannel<Response>>(context, async instance =>
            {
                entered.TrySetResult();
                await release.Task; // Deliberately return a late result even if invocation cancellation already won.
                return mode switch { "null" => null!, "throw" => throw new InvalidOperationException("handler failed"), _ => instance.CreateChannel() };
            }), RpcTestJsonContext.Default.Request, new() { Timeout = mode == "timeout" ? TimeSpan.FromMilliseconds(30) : TimeSpan.FromSeconds(5) });
            await using var host = builder.Build();
            var frames = new ConcurrentQueue<string>();
            NeoRpcSession? session = null;
            session = host.OpenSession(new NeoRpcSessionIdentity("fixture", "abandon-" + mode), async (json, _) =>
            {
                frames.Enqueue(json);
                if (Kind(json) == "result" && Parse(json).GetProperty("ok").GetBoolean())
                {
                    if (mode == "send-failure") throw new InvalidOperationException("send failed");
                    if (mode == "reentrant-close")
                    {
                        var channelId = Parse(json).GetProperty("value").GetProperty("channel").GetString();
                        await session!.ReceiveAsync(ChannelClose(channelId!));
                        await session.ReceiveAsync(ChannelClose(channelId!));
                    }
                }
            });
            await using (session)
            using (var receiveCancellation = new CancellationTokenSource())
            {
                var invocation = session.ReceiveAsync(Invoke("abandoned", "service.stream", "{\"id\":\"x\"}"), receiveCancellation.Token).AsTask();
                Task? closing = null;
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    if (mode == "cancel") await session.ReceiveAsync("{\"neoastra\":1,\"kind\":\"cancel\",\"id\":\"abandoned\"}");
                    if (mode == "receive-cancel") receiveCancellation.Cancel();
                    if (mode == "session-close") closing = session.DisposeAsync().AsTask();
                    if (mode is "cancel" or "timeout" or "receive-cancel" or "session-close") await WaitUntilAsync(() => !frames.IsEmpty);
                    release.TrySetResult();
                    if (mode == "send-failure") await Assert.ThrowsAsync<InvalidOperationException>(() => invocation.WaitAsync(TimeSpan.FromSeconds(2)));
                    else await invocation.WaitAsync(TimeSpan.FromSeconds(2));
                    if (closing is not null) await closing.WaitAsync(TimeSpan.FromSeconds(2));
                    await WaitUntilAsync(() => service.DisposeCount == 1 && session.ActiveChannelCount == 0);
                    Assert.AreEqual(0, service.EnumerationCount, mode);
                    Assert.AreEqual(1, service.DisposeCount, mode);
                    Assert.AreEqual(0, session.ActiveChannelCount, mode);
                    Assert.AreEqual(0, session.ActiveInvocationCount, mode);
                    Assert.AreEqual(1, frames.Count(frame => Kind(frame) == "result"), mode);
                }
                finally { release.TrySetResult(); service.ReleaseItems.TrySetResult(); }
            }
        }
    }

    [TestMethod]
    public async Task ConcurrentChannelAdmissionReservesCapacityBeforeSendingResults()
    {
        var services = new ConcurrentBag<ChannelService>();
        var firstSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activator = new NeoRpcServiceActivator<ChannelService>(_ => { var service = new ChannelService(); services.Add(service); return service; }, NeoRpcServiceLifetime.PerInvocation);
        var builder = new NeoRpcBuilder(new NeoRpcOptions { MaximumChannelsPerSession = 1 });
        RegisterChannelService(builder, activator);
        await using var host = builder.Build();
        var frames = new ConcurrentQueue<string>();
        await using var session = host.OpenSession(new NeoRpcSessionIdentity("fixture", "capacity"), async (json, _) =>
        {
            frames.Enqueue(json);
            if (Kind(json) == "result" && Parse(json).GetProperty("ok").GetBoolean())
            {
                firstSend.TrySetResult();
                await releaseSend.Task;
            }
        });
        var invocations = Enumerable.Range(0, 8).Select(index => session.ReceiveAsync(Invoke("capacity-" + index, "service.stream", "{\"id\":\"x\"}")).AsTask()).ToArray();
        try
        {
            await firstSend.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => services.Count == 8);
            Assert.AreEqual(1, session.ActiveChannelCount);
            Assert.IsTrue(services.All(service => service.EnumerationCount == 0));
            releaseSend.TrySetResult();
            await Task.WhenAll(invocations).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(7, services.Sum(service => service.DisposeCount));
            Assert.AreEqual(7, frames.Count(frame => ErrorCode(frame) == NeoRpcErrorCodes.TooManyRequests));
            var channel = frames.Select(Parse).Single(root => root.GetProperty("ok").GetBoolean()).GetProperty("value").GetProperty("channel").GetString();
            await session.ReceiveAsync(ChannelClose(channel!)).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => session.ActiveChannelCount == 0);
            Assert.AreEqual(8, services.Sum(service => service.DisposeCount));
            Assert.AreEqual(0, session.ActiveChannelCount);
        }
        finally
        {
            releaseSend.TrySetResult();
            foreach (var service in services) service.ReleaseItems.TrySetResult();
        }
    }

    [TestMethod]
    public async Task ChannelTerminationAlwaysReleasesItsLeaseAndTracksCleanup()
    {
        foreach (var mode in new[] { "complete", "source-failure", "enumerator-failure", "item-serialization-failure", "item-send-failure", "dispose-failure", "credit-close" })
        {
            var service = new ChannelService
            {
                ThrowOnMove = mode == "source-failure", ThrowOnDispose = mode == "dispose-failure",
                ThrowOnGetEnumerator = mode == "enumerator-failure", ThrowOnSerialize = mode == "item-serialization-failure",
                ItemCount = mode == "credit-close" ? 3 : 1
            };
            service.ReleaseItems.TrySetResult();
            var diagnostics = new RpcDiagnosticSink();
            var builder = new NeoRpcBuilder(new NeoRpcOptions { MaximumUnacknowledgedChannelItems = 1, DiagnosticSink = diagnostics });
            RegisterChannelService(builder, new NeoRpcServiceActivator<ChannelService>(_ => service, NeoRpcServiceLifetime.PerInvocation));
            await using var host = builder.Build();
            var frames = new ConcurrentQueue<string>();
            await using var session = host.OpenSession(new NeoRpcSessionIdentity("fixture", "terminal-" + mode), (json, _) =>
            {
                frames.Enqueue(json);
                if (mode == "item-send-failure" && Kind(json) == "channel_item") throw new InvalidOperationException("send failed");
                return ValueTask.CompletedTask;
            });
            await session.ReceiveAsync(Invoke("terminal", "service.stream", "{\"id\":\"x\"}"));
            if (mode == "credit-close")
            {
                await WaitUntilAsync(() => service.YieldCount == 2);
                Assert.AreEqual(0, service.DisposeCount);
                var channel = Parse(frames.First()).GetProperty("value").GetProperty("channel").GetString();
                await Task.WhenAll(session.ReceiveAsync(ChannelClose(channel!)).AsTask(), session.ReceiveAsync(ChannelClose(channel!)).AsTask()).WaitAsync(TimeSpan.FromSeconds(2));
            }
            await WaitUntilAsync(() => session.ActiveChannelCount == 0);
            Assert.AreEqual(mode == "enumerator-failure" ? 0 : 1, service.EnumeratorDisposeCount, mode);
            Assert.AreEqual(1, service.DisposeCount, mode);
            if (mode == "dispose-failure")
            {
                Assert.IsTrue(diagnostics.Values.Any(value => value.Code == "channel_cleanup_failed"));
                Assert.IsTrue(frames.Any(frame => Kind(frame) == "channel_error"));
            }
        }
    }

    [TestMethod]
    public async Task ReentrantChannelCloseFromPumpSendsDoesNotAwaitItsOwnPump()
    {
        foreach (var sendKind in new[] { "channel_item", "channel_complete" })
        {
            var service = new ChannelService();
            service.ReleaseItems.TrySetResult();
            var builder = new NeoRpcBuilder();
            RegisterChannelService(builder, new NeoRpcServiceActivator<ChannelService>(_ => service, NeoRpcServiceLifetime.PerInvocation));
            await using var host = builder.Build();
            var closeAccepted = false;
            var closeTimedOut = false;
            NeoRpcSession? session = null;
            session = host.OpenSession(new NeoRpcSessionIdentity("fixture", "reentrant-" + sendKind), async (json, _) =>
            {
                if (Kind(json) != sendKind) return;
                var channel = Parse(json).GetProperty("channel").GetString();
                try
                {
                    await session!.ReceiveAsync(ChannelClose(channel!)).AsTask().WaitAsync(TimeSpan.FromMilliseconds(500));
                    closeAccepted = true;
                }
                catch (TimeoutException)
                {
                    // Break the transport cycle so a failed regression does not leave test teardown hanging.
                    closeTimedOut = true;
                    throw;
                }
            });
            await using (session)
            {
                await session.ReceiveAsync(Invoke("reentrant", "service.stream", "{\"id\":\"x\"}"));
                await WaitUntilAsync(() => session.ActiveChannelCount == 0);
                Assert.IsFalse(closeTimedOut, "A reentrant close deadlocked the " + sendKind + " send.");
                Assert.IsTrue(closeAccepted, sendKind);
                Assert.AreEqual(1, service.DisposeCount);
                Assert.AreEqual(1, service.EnumeratorDisposeCount);
            }
        }
    }

    [TestMethod]
    public async Task ChannelOwnershipIsCapturedBeforeResultConversion()
    {
        var service = new ChannelService();
        var activator = new NeoRpcServiceActivator<ChannelService>(_ => service, NeoRpcServiceLifetime.PerInvocation);
        var (host, session, frames) = Create(builder =>
        {
            builder.AddServiceActivator(activator);
            // Deliberately use a value descriptor whose conversion fails after activation has returned the channel.
            builder.AddCommand<Request, NeoRpcChannel<Response>>("service.convert", (_, context, _) =>
                activator.InvokeAsync(context, instance => ValueTask.FromResult(instance.CreateChannel())), RpcTestJsonContext.Default.Request,
                JsonMetadataServices.CreateValueInfo<NeoRpcChannel<Response>>(new JsonSerializerOptions(), new FailingConverter<NeoRpcChannel<Response>>()));
        });
        await using (host) await using (session)
        {
            await session.ReceiveAsync(Invoke("convert", "service.convert", "{\"id\":\"x\"}"));
            Assert.AreEqual(NeoRpcErrorCodes.SerializationFailed, ErrorCode(frames.Single()));
            Assert.AreEqual(0, service.EnumerationCount);
            Assert.AreEqual(1, service.DisposeCount);
            Assert.AreEqual(0, session.ActiveInvocationCount);
        }
    }

    [TestMethod]
    public async Task ChannelCloseTracksAndAwaitsAsynchronousServiceDisposal()
    {
        var service = new BlockingAsyncDisposable();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activator = new NeoRpcServiceActivator<BlockingAsyncDisposable>(_ => service, NeoRpcServiceLifetime.PerInvocation);
        var (host, session, frames) = Create(builder =>
        {
            builder.AddServiceActivator(activator);
            builder.AddChannelCommand<Request, Response>("service.stream", (_, context, _) => activator.InvokeAsync(context, _ =>
                ValueTask.FromResult(new NeoRpcChannel<Response>(Items(), RpcTestJsonContext.Default.Response))), RpcTestJsonContext.Default.Request);
        });
        await using (host) await using (session)
        {
            try
            {
                await session.ReceiveAsync(Invoke("async-dispose", "service.stream", "{\"id\":\"x\"}"));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                var channel = Parse(frames.First()).GetProperty("value").GetProperty("channel").GetString();
                var firstClose = session.ReceiveAsync(ChannelClose(channel!)).AsTask();
                await service.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                var secondClose = session.ReceiveAsync(ChannelClose(channel!)).AsTask();
                var sessionClose = session.DisposeAsync().AsTask();
                var hostClose = host.DisposeAsync().AsTask();
                Assert.IsTrue(firstClose.IsCompletedSuccessfully, "A close control frame acknowledges cancellation without waiting for service cleanup.");
                Assert.IsTrue(secondClose.IsCompletedSuccessfully);
                Assert.IsFalse(sessionClose.IsCompleted);
                Assert.IsFalse(hostClose.IsCompleted);
                Assert.AreEqual(1, session.ActiveChannelCount);
                Assert.AreEqual(1, service.DisposeCount);
                service.ReleaseDispose.TrySetResult();
                await Task.WhenAll(firstClose, secondClose, sessionClose, hostClose).WaitAsync(TimeSpan.FromSeconds(2));
                Assert.AreEqual(0, session.ActiveChannelCount);
                Assert.AreEqual(1, service.DisposeCount);
            }
            finally { service.ReleaseDispose.TrySetResult(); }
        }

        async IAsyncEnumerable<Response> Items([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    [TestMethod]
    public async Task NoncooperativeChannelKeepsItsServiceAliveAfterTeardownWarning()
    {
        var service = new ChannelService { IgnoreCancellation = true };
        var diagnostics = new RpcDiagnosticSink();
        var (host, session, _) = Create(builder => RegisterChannelService(builder,
            new NeoRpcServiceActivator<ChannelService>(_ => service, NeoRpcServiceLifetime.PerInvocation)), new NeoRpcOptions { DiagnosticSink = diagnostics });
        await using (host) await using (session)
        {
            try
            {
                await session.ReceiveAsync(Invoke("uncooperative", "service.stream", "{\"id\":\"x\"}"));
                await service.EnumerationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                var closing = session.DisposeAsync().AsTask();
                await diagnostics.TeardownWarning.Task.WaitAsync(TimeSpan.FromSeconds(8));
                Assert.IsFalse(closing.IsCompleted);
                Assert.AreEqual(0, service.DisposeCount);
                Assert.AreEqual(1, session.ActiveChannelCount);
                service.ReleaseItems.TrySetResult();
                await closing.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.AreEqual(1, service.DisposeCount);
            }
            finally { service.ReleaseItems.TrySetResult(); }
        }
    }

    [TestMethod]
    public async Task DirectUnenumeratedChannelCopiesReleaseTheirOwnLeasesExactlyOnce()
    {
        var services = new ConcurrentBag<BlockingAsyncDisposable>();
        var activator = new NeoRpcServiceActivator<BlockingAsyncDisposable>(_ => { var service = new BlockingAsyncDisposable(); services.Add(service); return service; }, NeoRpcServiceLifetime.PerInvocation);
        NeoRpcChannel<Response>? first = null;
        NeoRpcChannel<Response>? second = null;
        var source = new NeoRpcChannel<Response>(Items(), RpcTestJsonContext.Default.Response);
        var (host, session, _) = Create(builder =>
        {
            builder.AddServiceActivator(activator);
            builder.AddCommand<Request>("service.capture", async (_, context, _) =>
            {
                first = await activator.InvokeAsync(context, _ => ValueTask.FromResult(source));
                second = await activator.InvokeAsync(context, _ => ValueTask.FromResult(source));
            }, RpcTestJsonContext.Default.Request);
        });
        await using (host) await using (session)
        {
            try
            {
                await session.ReceiveAsync(Invoke("copies", "service.capture", "{\"id\":\"x\"}"));
                Assert.AreNotSame(source, first);
                Assert.AreNotSame(first, second);
                var firstDispose = first!.DisposeAsync().AsTask();
                Assert.AreSame(firstDispose, first.DisposeAsync().AsTask());
                Assert.IsFalse(firstDispose.IsCompleted);
                Assert.AreEqual(1, services.Sum(service => service.DisposeCount));
                foreach (var service in services) service.ReleaseDispose.TrySetResult();
                await firstDispose.WaitAsync(TimeSpan.FromSeconds(2));
                await second!.DisposeAsync();
                Assert.AreEqual(2, services.Sum(service => service.DisposeCount));
            }
            finally
            {
                foreach (var service in services) service.ReleaseDispose.TrySetResult();
                if (first is not null) await first.DisposeAsync();
                if (second is not null) await second.DisposeAsync();
            }
        }

        static async IAsyncEnumerable<Response> Items()
        {
            await Task.CompletedTask;
            Assert.Fail("An abandoned channel must never be enumerated.");
            yield break;
        }
    }

    private static void RegisterChannelService(NeoRpcBuilder builder, NeoRpcServiceActivator<ChannelService> activator)
    {
        builder.AddServiceActivator(activator);
        builder.AddChannelCommand<Request, Response>("service.stream", (_, context, _) =>
            activator.InvokeAsync(context, service => ValueTask.FromResult(service.CreateChannel())), RpcTestJsonContext.Default.Request);
    }

    private static string ChannelClose(string id) => $"{{\"neoastra\":1,\"kind\":\"channel_close\",\"channel\":\"{id}\"}}";

    [TestMethod]
    public async Task ScopedServiceActivationIsExactlyOnceAndDisposesAtItsBoundary()
    {
        foreach (var lifetime in new[] { NeoRpcServiceLifetime.PerDocumentSession, NeoRpcServiceLifetime.PerView })
        {
            var created = 0;
            var disposed = 0;
            using var factoryEntered = new ManualResetEventSlim();
            using var releaseFactory = new ManualResetEventSlim();
            var activator = new NeoRpcServiceActivator<ConcurrentService>(_ =>
            {
                Interlocked.Increment(ref created);
                factoryEntered.Set();
                releaseFactory.Wait(TimeSpan.FromSeconds(2));
                return new ConcurrentService(Interlocked.Increment(ref ConcurrentService.NextId), () => Interlocked.Increment(ref disposed));
            }, lifetime);
            var (host, session, frames) = Create(builder =>
            {
                builder.AddServiceActivator(activator);
                builder.AddCommand<Request, ServiceResponse>("service.resolve", (request, context, _) => activator.InvokeAsync(context, service => ValueTask.FromResult(new ServiceResponse(service.Id))), RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.ServiceResponse, CommandPolicy);
            }, new NeoRpcOptions { MaximumConcurrentInvocations = 17, MaximumConcurrentInvocationsPerSession = 16 });
            await using (host) await using (session)
            {
                var invocations = Enumerable.Range(0, 12).Select(index => Task.Run(async () => await session.ReceiveAsync(Invoke($"scope-{index}", "service.resolve", $"{{\"id\":\"{index}\"}}")))).ToArray();
                Assert.IsTrue(factoryEntered.Wait(TimeSpan.FromSeconds(2)));
                await Task.Delay(30);
                releaseFactory.Set();
                await Task.WhenAll(invocations);
                Assert.AreEqual(1, created, lifetime.ToString());
                var ids = frames.Select(Parse).Where(root => root.GetProperty("ok").GetBoolean()).Select(root => root.GetProperty("value").GetProperty("id").GetInt32()).Distinct().ToArray();
                Assert.AreEqual(1, ids.Length, lifetime.ToString());
                if (lifetime == NeoRpcServiceLifetime.PerDocumentSession)
                {
                    await session.DisposeAsync();
                    Assert.AreEqual(1, disposed);
                }
                else
                {
                    await session.DisposeAsync();
                    Assert.AreEqual(0, disposed);
                    await host.CloseViewServicesAsync("fixture");
                    Assert.AreEqual(1, disposed);
                }
            }
            Assert.AreEqual(1, disposed, lifetime.ToString());
        }
    }

    [TestMethod]
    public async Task ScopedServiceFactoryFailureDoesNotPublishOrCacheAPartialInstance()
    {
        var attempts = 0;
        var disposed = 0;
        var activator = new NeoRpcServiceActivator<ConcurrentService>(_ =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new InvalidOperationException("factory failed");
            return new ConcurrentService(1, () => Interlocked.Increment(ref disposed));
        }, NeoRpcServiceLifetime.PerDocumentSession);
        var (host, session, frames) = Create(builder =>
        {
            builder.AddServiceActivator(activator);
            builder.AddCommand<Request, ServiceResponse>("service.retry", (_, context, _) => activator.InvokeAsync(context, service => ValueTask.FromResult(new ServiceResponse(service.Id))), RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.ServiceResponse, CommandPolicy);
        });
        await using (host) await using (session)
        {
            await session.ReceiveAsync(Invoke("factory-1", "service.retry", "{\"id\":\"a\"}"));
            await session.ReceiveAsync(Invoke("factory-2", "service.retry", "{\"id\":\"b\"}"));
            Assert.AreEqual("internal_error", ErrorCode(frames.ElementAt(0)));
            Assert.IsTrue(Parse(frames.ElementAt(1)).GetProperty("ok").GetBoolean());
            Assert.AreEqual(2, attempts);
        }
        Assert.AreEqual(1, disposed);
    }

    [TestMethod]
    public void Int64EnumsAndRpcErrorsUseTheirDeclaredWireContracts()
    {
        var dto = new WirePolicyDto { Signed = long.MinValue, Unsigned = ulong.MaxValue, Optional = long.MaxValue, Kind = WireContractKind.Second };
        var json = JsonSerializer.Serialize(dto, RpcTestJsonContext.Default.WirePolicyDto);
        StringAssert.Contains(json, "\"signed\":\"-9223372036854775808\"");
        StringAssert.Contains(json, "\"unsigned\":\"18446744073709551615\"");
        StringAssert.Contains(json, "\"optional\":\"9223372036854775807\"");
        StringAssert.Contains(json, "\"kind\":\"Second\"");
        var roundTrip = JsonSerializer.Deserialize(json, RpcTestJsonContext.Default.WirePolicyDto)!;
        Assert.AreEqual(long.MinValue, roundTrip.Signed);
        Assert.AreEqual(ulong.MaxValue, roundTrip.Unsigned);
        Assert.AreEqual(long.MaxValue, roundTrip.Optional);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"signed\":1,\"unsigned\":\"0\",\"kind\":\"First\"}", RpcTestJsonContext.Default.WirePolicyDto));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"signed\":\"+1\",\"unsigned\":\"0\",\"kind\":\"First\"}", RpcTestJsonContext.Default.WirePolicyDto));

        Assert.Throws<ArgumentException>(() => new NeoRpcError("Bad-Code", "Safe.", null));
        Assert.Throws<ArgumentException>(() => new NeoRpcError("safe_code", "line\nbreak", null));
        Assert.Throws<ArgumentException>(() => new NeoRpcError("safe_code", "Safe.", "bad\ncorrelation"));
        Assert.Throws<ArgumentException>(() => new NeoRpcException("bad.code", "Safe."));
        _ = new NeoRpcError("documents:not_found", "Safe.", "correlation-1");
    }

    [TestMethod]
    public async Task HostOpenCannotEscapeAConcurrentDisposeSnapshot()
    {
        var features = new BlockingFeatureList();
        var host = new NeoRpcBuilder(TestOptions()).Build();
        var identity = new NeoRpcSessionIdentity("view", "late-session") { Features = features };
        var opening = Task.Run(() => host.OpenSession(identity, (_, _) => ValueTask.CompletedTask));
        await features.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await host.DisposeAsync();
        features.Release.TrySetResult();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await opening);
        Assert.AreEqual(0, host.ActiveSessionCount);
    }

    [TestMethod]
    public async Task HostCancellationCallbacksRunOutsideTheLifecycleLockAndCanReenter()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lockHeld = true;
        Exception? reentryException = null;
        NeoRpcHost? host = null;
        var builder = new NeoRpcBuilder(TestOptions());
        builder.AddCommand<Request, Response>("lifecycle.wait", async (request, _, cancellationToken) =>
        {
            using var registration = cancellationToken.Register(() =>
            {
                var lifecycleLock = typeof(NeoRpcHost).GetField("_lifecycleLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host!)!;
                lockHeld = Monitor.IsEntered(lifecycleLock);
                try { host!.OpenSession(new NeoRpcSessionIdentity("reentrant-view", "reentrant-session"), (_, _) => ValueTask.CompletedTask); }
                catch (Exception exception) { reentryException = exception; }
                callback.TrySetResult();
            });
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new Response(request.Id, "unreachable");
        }, RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response, CommandPolicy);
        host = builder.Build();
        var session = host.OpenSession(new NeoRpcSessionIdentity("view", "document"), (_, _) => ValueTask.CompletedTask);
        var invocation = session.ReceiveAsync(Invoke("lifecycle-call", "lifecycle.wait", "{\"id\":\"x\"}")).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var disposing = host.DisposeAsync().AsTask();
        await callback.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.WhenAll(invocation, disposing).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(lockHeld);
        Assert.IsInstanceOfType<ObjectDisposedException>(reentryException);
    }

    [TestMethod]
    public async Task ViewBindingInstallsNewSessionBeforeBlockedOldTeardownCompletes()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = new NeoRpcBuilder(TestOptions());
        builder.AddCommand<Request, Response>("binding.wait", async (request, _, _) => { entered.TrySetResult(); await release.Task; return new Response(request.Id, "late"); }, RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response, CommandPolicy);
        builder.AddCommand<Request, Response>("binding.echo", (request, context, _) => ValueTask.FromResult(new Response(request.Id, context.DocumentSessionId)), RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response, CommandPolicy);
        await using var host = builder.Build();
        var frames = new ConcurrentDictionary<string, ConcurrentQueue<string>>(StringComparer.Ordinal);
        var view = (global::NeoAstra.NeoAstra)RuntimeHelpers.GetUninitializedObject(typeof(global::NeoAstra.NeoAstra));
        SetField(view, "_viewLabel", "binding-view");
        var binding = new NeoRpcViewBinding(host, view, snapshot => snapshot.DocumentSessionId == "broken-open"
            ? throw new InvalidOperationException("contained open failure")
            : host.OpenSession(
                new NeoRpcSessionIdentity("binding-view", snapshot.DocumentSessionId),
                (json, _) => { frames.GetOrAdd(snapshot.DocumentSessionId, static _ => new()).Enqueue(json); return ValueTask.CompletedTask; }));
        var queue = typeof(NeoRpcViewBinding).GetMethod("QueueTransition", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var message = typeof(NeoRpcViewBinding).GetMethod("OnMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var oldSnapshot = new NeoTransportSessionSnapshot("old-document", 0, Array.Empty<string>(), true);
        queue.Invoke(binding, [oldSnapshot]);
        message.Invoke(binding, [new NeoTransportApplicationMessage(Invoke("old-call", "binding.wait", "{\"id\":\"old\"}"), oldSnapshot, null, true)]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var newSnapshot = new NeoTransportSessionSnapshot("new-document", 0, Array.Empty<string>(), true);
        queue.Invoke(binding, [newSnapshot]);
        message.Invoke(binding, [new NeoTransportApplicationMessage(Invoke("new-call", "binding.echo", "{\"id\":\"new\"}"), newSnapshot, null, true)]);
        await WaitUntilAsync(() => frames.TryGetValue("new-document", out var newFrames) && newFrames.Count != 0);
        var response = Parse(frames["new-document"].Single());
        Assert.IsTrue(response.GetProperty("ok").GetBoolean());
        Assert.AreEqual("new-document", response.GetProperty("value").GetProperty("viewLabel").GetString());
        queue.Invoke(binding, [new NeoTransportSessionSnapshot("broken-open", 0, Array.Empty<string>(), true)]);

        var disposing = binding.DisposeAsync().AsTask();
        await Task.Delay(20);
        Assert.IsFalse(disposing.IsCompleted);
        release.TrySetResult();
        await disposing.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(0, host.ActiveSessionCount);

        static void SetField(object target, string name, object? value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    }

    [TestMethod]
    public async Task ViewBindingReturnsBeforeDispatchingUiThreadCommand()
    {
        using var dispatcher = new TestRpcDispatcher();
        var callbackReturned = 0;
        var invocations = 0;
        var builder = new NeoRpcBuilder(TestOptions());
        builder.AddCommand<Request, Response>("binding.ui", async (request, context, _) =>
        {
            Assert.AreEqual(1, Volatile.Read(ref callbackReturned));
            Assert.AreEqual(dispatcher.ThreadId, Environment.CurrentManagedThreadId);
            await Task.Yield();
            Assert.AreEqual(dispatcher.ThreadId, Environment.CurrentManagedThreadId);
            Interlocked.Increment(ref invocations);
            return new Response(request.Id, context.ViewLabel);
        }, RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response,
            new NeoRpcCommandOptions { Permission = "test:invoke", Dispatch = NeoRpcDispatchMode.UiThread });
        await using var host = builder.Build();
        var frames = new ConcurrentQueue<string>();
        var view = (global::NeoAstra.NeoAstra)RuntimeHelpers.GetUninitializedObject(typeof(global::NeoAstra.NeoAstra));
        SetField(view, "_viewLabel", "binding-view");
        await using var binding = new NeoRpcViewBinding(host, view, snapshot => host.OpenSession(
            new NeoRpcSessionIdentity("binding-view", snapshot.DocumentSessionId) { Dispatcher = dispatcher },
            (json, _) => { frames.Enqueue(json); return ValueTask.CompletedTask; }));
        var queue = typeof(NeoRpcViewBinding).GetMethod("QueueTransition", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var receive = typeof(NeoRpcViewBinding).GetMethod("OnMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var snapshot = new NeoTransportSessionSnapshot("ui-document", 0, Array.Empty<string>(), true);
        queue.Invoke(binding, [snapshot]);

        dispatcher.Invoke(() =>
        {
            receive.Invoke(binding, [new NeoTransportApplicationMessage(Invoke("ui-call", "binding.ui", "{\"id\":\"ui\"}"), snapshot, null, true)]);
            Volatile.Write(ref callbackReturned, 1);
        });

        await WaitUntilAsync(() => frames.Count != 0);
        var response = Parse(frames.Single());
        Assert.IsTrue(response.GetProperty("ok").GetBoolean());
        Assert.AreEqual(1, Volatile.Read(ref invocations));

        static void SetField(object target, string name, object? value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    }

    [TestMethod]
    public async Task AbuseClosureAndConcurrentDisposalAwaitOneCompleteTeardown()
    {
        var resource = new BlockingAsyncDisposable();
        var service = new BlockingAsyncDisposable();
        var activator = new NeoRpcServiceActivator<BlockingAsyncDisposable>(_ => service, NeoRpcServiceLifetime.PerDocumentSession);
        var options = TestOptions(new NeoRpcOptions { RequestRatePerSecond = 1, RequestRateBurst = 1, AbuseClosureThreshold = 1 });
        var builder = new NeoRpcBuilder(options);
        builder.AddServiceActivator(activator);
        builder.AddCommand<Request, Response>("abuse.allocate", async (request, context, _) =>
        {
            context.Resources.Add(resource, 1);
            await activator.InvokeAsync(context, static _ => ValueTask.CompletedTask);
            return new Response(request.Id, context.ViewLabel);
        }, RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response, CommandPolicy);
        var host = builder.Build();
        var session = host.OpenSession(new NeoRpcSessionIdentity("fixture", "abuse-session"), (_, _) => ValueTask.CompletedTask);

        await session.ReceiveAsync(Invoke("first", "abuse.allocate", "{\"id\":\"first\"}"));
        var abuseClosure = session.ReceiveAsync(Invoke("second", "abuse.allocate", "{\"id\":\"second\"}")).AsTask();
        await resource.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var concurrentSessionDispose = session.DisposeAsync().AsTask();
        var concurrentHostDispose = host.DisposeAsync().AsTask();
        Assert.IsFalse(abuseClosure.IsCompleted);
        Assert.IsFalse(concurrentSessionDispose.IsCompleted);
        Assert.IsFalse(concurrentHostDispose.IsCompleted);

        resource.ReleaseDispose.TrySetResult();
        await service.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(abuseClosure.IsCompleted);
        Assert.IsFalse(concurrentSessionDispose.IsCompleted);
        Assert.IsFalse(concurrentHostDispose.IsCompleted);

        service.ReleaseDispose.TrySetResult();
        await Task.WhenAll(abuseClosure, concurrentSessionDispose, concurrentHostDispose).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, resource.DisposeCount);
        Assert.AreEqual(1, service.DisposeCount);
        Assert.AreEqual(0, host.ActiveSessionCount);
        Assert.AreEqual(0, host.GetDiagnosticSnapshot().ActiveResources);
        await session.DisposeAsync();
        await host.DisposeAsync();
        Assert.AreEqual(1, resource.DisposeCount);
        Assert.AreEqual(1, service.DisposeCount);
    }

    [TestMethod]
    public async Task SubscriptionAuthorizationHasAtomicPendingAndActiveWinners()
    {
        await RunSubscriptionRaceAsync(authorizationWins: false, closeSession: false);
        await RunSubscriptionRaceAsync(authorizationWins: true, closeSession: false);
        await RunSubscriptionRaceAsync(authorizationWins: false, closeSession: true);

        static async Task RunSubscriptionRaceAsync(bool authorizationWins, bool closeSession)
        {
            var authorization = new BlockingAuthorization();
            var options = new NeoRpcOptions { AuthorizationService = authorization };
            var (host, session, frames) = Create(builder => builder.AddEvent("documents.changed", RpcTestJsonContext.Default.Response, new() { Permission = "documents:read" }), options);
            await using (host) await using (session)
            {
                var subscribing = session.ReceiveAsync("{\"neoastra\":1,\"kind\":\"subscribe\",\"id\":\"pending-sub\",\"event\":\"documents.changed\"}").AsTask();
                await authorization.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                if (authorizationWins)
                {
                    authorization.Release.TrySetResult();
                    await subscribing;
                    Assert.AreEqual(1, session.ActiveSubscriptionCount);
                    await session.ReceiveAsync("{\"neoastra\":1,\"kind\":\"unsubscribe\",\"id\":\"pending-sub\"}");
                    Assert.AreEqual(0, session.ActiveSubscriptionCount);
                    await session.ReceiveAsync("{\"neoastra\":1,\"kind\":\"subscribe\",\"id\":\"pending-sub\",\"event\":\"documents.changed\"}");
                    Assert.AreEqual("duplicate_request", frames.Select(ErrorCode).Last(static code => code is not null));
                }
                else
                {
                    if (closeSession) await session.DisposeAsync();
                    else await session.ReceiveAsync("{\"neoastra\":1,\"kind\":\"unsubscribe\",\"id\":\"pending-sub\"}");
                    authorization.Release.TrySetResult();
                    await subscribing;
                    Assert.AreEqual(0, session.ActiveSubscriptionCount);
                    Assert.IsFalse(frames.Any(frame => Kind(frame) == "subscribed" && !Parse(frame).TryGetProperty("error", out _)));
                }
            }
        }
    }

    [TestMethod]
    public async Task RequestHandlerAndResponseSerializationFailuresRemainDistinct()
    {
        var invoked = 0;
        var (host, session, frames) = Create(builder =>
        {
            builder.AddCommand<Request, Response>("phase.normal", (request, _, _) => { invoked++; return ValueTask.FromResult(new Response(request.Id, "ok")); }, RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response, CommandPolicy);
            builder.AddCommand<Request, Response>("phase.jsonException", (_, _, _) => throw new JsonException("application secret json failure"), RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response, CommandPolicy);
            builder.AddCommand<Request, Response>("phase.notSupported", (_, _, _) => throw new NotSupportedException("application secret unsupported failure"), RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response, CommandPolicy);
            builder.AddCommand<Request, SerializationFailureResponse>("phase.serialize", (_, _, _) => ValueTask.FromResult(new SerializationFailureResponse { Value = "secret result" }), RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.SerializationFailureResponse, CommandPolicy);
        });
        await using (host) await using (session)
        {
            await session.ReceiveAsync(Invoke("malformed", "phase.normal", "{\"id\":1}"));
            await session.ReceiveAsync(Invoke("null", "phase.normal", "null"));
            await session.ReceiveAsync(Invoke("app-json", "phase.jsonException", "{\"id\":\"x\"}"));
            await session.ReceiveAsync(Invoke("app-unsupported", "phase.notSupported", "{\"id\":\"x\"}"));
            await session.ReceiveAsync(Invoke("serialize", "phase.serialize", "{\"id\":\"x\"}"));
            Assert.AreEqual(0, invoked);
            Assert.AreEqual("invalid_request", ErrorCode(frames.ElementAt(0)));
            Assert.AreEqual("invalid_request", ErrorCode(frames.ElementAt(1)));
            Assert.AreEqual("internal_error", ErrorCode(frames.ElementAt(2)));
            Assert.AreEqual("internal_error", ErrorCode(frames.ElementAt(3)));
            Assert.AreEqual("serialization_failed", ErrorCode(frames.ElementAt(4)));
            Assert.IsFalse(frames.Any(frame => frame.Contains("secret", StringComparison.OrdinalIgnoreCase)));
        }
    }

    [TestMethod]
    public async Task GlobalAdmissionReservesCapacityAcrossViews()
    {
        var entered = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new NeoRpcOptions { MaximumConcurrentInvocations = 3, MaximumConcurrentInvocationsPerSession = 2 };
        var builder = new NeoRpcBuilder(TestOptions(options));
        builder.AddCommand<Request, Response>("fair.wait", async (request, context, _) =>
        {
            if (request.Id != "cold") { Interlocked.Increment(ref entered); await release.Task; }
            return new Response(request.Id, context.ViewLabel);
        }, RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response, CommandPolicy);
        await using var host = builder.Build();
        var hotFrames1 = new ConcurrentQueue<string>();
        var hotFrames2 = new ConcurrentQueue<string>();
        var coldFrames = new ConcurrentQueue<string>();
        await using var hot1 = host.OpenSession(new NeoRpcSessionIdentity("hot-view", "hot-1"), (json, _) => { hotFrames1.Enqueue(json); return ValueTask.CompletedTask; });
        await using var hot2 = host.OpenSession(new NeoRpcSessionIdentity("hot-view", "hot-2"), (json, _) => { hotFrames2.Enqueue(json); return ValueTask.CompletedTask; });
        await using var cold = host.OpenSession(new NeoRpcSessionIdentity("cold-view", "cold"), (json, _) => { coldFrames.Enqueue(json); return ValueTask.CompletedTask; });
        var first = hot1.ReceiveAsync(Invoke("hot-a", "fair.wait", "{\"id\":\"a\"}")).AsTask();
        var second = hot1.ReceiveAsync(Invoke("hot-b", "fair.wait", "{\"id\":\"b\"}")).AsTask();
        await WaitUntilAsync(() => Volatile.Read(ref entered) == 2);
        await hot2.ReceiveAsync(Invoke("hot-c", "fair.wait", "{\"id\":\"c\"}"));
        await cold.ReceiveAsync(Invoke("cold-a", "fair.wait", "{\"id\":\"cold\"}"));
        Assert.AreEqual("too_many_requests", ErrorCode(hotFrames2.Single()));
        Assert.IsTrue(Parse(coldFrames.Single()).GetProperty("ok").GetBoolean());
        release.TrySetResult();
        await Task.WhenAll(first, second);
    }

    [TestMethod]
    public async Task AuthorizationExplicitErrorsMappingAndRedactionAreStable()
    {
        var options = new NeoRpcOptions
        {
            AuthorizationService = new DenyAuthorization(),
            ErrorMappers = [new InvalidOperationMapper(), new UnsafeMapper()],
        };
        var (host, session, frames) = Create(builder =>
        {
            builder.AddCommand<Request, Response>("secure.denied", (_, _, _) => throw new Exception("must not execute"), RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response, new() { Permission = "secure:read" });
            builder.AddCommand<Request, Response>("mapped.fail", (_, _, _) => throw new InvalidOperationException("host secret path C:\\secret"), RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response, CommandPolicy);
            builder.AddCommand<Request, Response>("explicit.fail", (_, _, _) => throw new NeoRpcException("documents:not_found", "Not found."), RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response, CommandPolicy);
            builder.AddCommand<Request, Response>("internal.fail", (_, _, _) => throw new Exception("C:\\secret\\password"), RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response, CommandPolicy);
        }, options);
        await using (host) await using (session)
        {
            await session.ReceiveAsync(Invoke("a", "secure.denied", "{\"id\":\"x\"}"));
            await session.ReceiveAsync(Invoke("b", "mapped.fail", "{\"id\":\"x\"}"));
            await session.ReceiveAsync(Invoke("c", "explicit.fail", "{\"id\":\"x\"}"));
            await session.ReceiveAsync(Invoke("d", "internal.fail", "{\"id\":\"x\"}"));
            var results = frames.ToArray();
            Assert.AreEqual("permission_denied", ErrorCode(results[0]));
            Assert.AreEqual("mapped_failure", ErrorCode(results[1]));
            Assert.AreEqual("documents:not_found", ErrorCode(results[2]));
            Assert.AreEqual("internal_error", ErrorCode(results[3]));
            Assert.IsFalse(frames.Any(frame => frame.Contains("secret", StringComparison.OrdinalIgnoreCase)));
        }
    }

    [TestMethod]
    public async Task ContractHashMismatchIsRejectedBeforeApplicationDispatch()
    {
        var invoked = 0;
        var (host, session, frames) = Create(builder => builder.AddCommand<Request, Response>("documents.open", (request, context, _) => { invoked++; return ValueTask.FromResult(new Response(request.Id, context.ViewLabel)); }, RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response, CommandPolicy), new NeoRpcOptions { ContractHash = "expected" });
        await using (host) await using (session)
        {
            await session.ReceiveAsync(Invoke("missing", "documents.open", "{\"id\":\"x\"}"));
            await session.ReceiveAsync("{\"neoastra\":1,\"kind\":\"invoke\",\"id\":\"wrong\",\"command\":\"documents.open\",\"contract\":\"stale\",\"args\":{\"id\":\"x\"}}");
            await session.ReceiveAsync("{\"neoastra\":1,\"kind\":\"invoke\",\"id\":\"right\",\"command\":\"documents.open\",\"contract\":\"expected\",\"args\":{\"id\":\"x\"}}");
            var results = frames.ToArray();
            Assert.AreEqual("protocol_mismatch", ErrorCode(results[0]));
            StringAssert.Contains(results[0], "Use the generated frontend RPC bindings");
            Assert.AreEqual("protocol_mismatch", ErrorCode(results[1]));
            StringAssert.Contains(results[1], "Regenerate the frontend RPC bindings");
            Assert.IsTrue(Parse(results[2]).GetProperty("ok").GetBoolean());
            Assert.AreEqual(1, invoked);
        }
    }

    [TestMethod]
    public async Task EventsChannelsAndResourcesAreSessionOwned()
    {
        NeoRpcEvent<Response>? changed = null;
        var disposed = 0;
        var (host, session, frames) = Create(builder =>
        {
            changed = builder.AddEvent("documents.changed", RpcTestJsonContext.Default.Response, new() { Permission = "test:event", OverflowBehavior = NeoRpcOverflowBehavior.DropOldest });
            builder.AddChannelCommand<Request, Response>("documents.stream", (request, _, _) => ValueTask.FromResult(new NeoRpcChannel<Response>(Items(request.Id), RpcTestJsonContext.Default.Response)), RpcTestJsonContext.Default.Request, CommandPolicy);
            builder.AddCommand<Request, ResourceResponse>("documents.resource", (_, context, _) => ValueTask.FromResult(new ResourceResponse(context.Resources.Add(new TrackedDisposable(() => Interlocked.Increment(ref disposed))).Id)), RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.ResourceResponse, CommandPolicy);
        });
        await using (host) await using (session)
        {
            var sourceOrigin = new Uri("https://trusted.example");
            await session.ReceiveAsync("{\"neoastra\":1,\"kind\":\"subscribe\",\"id\":\"sub-1\",\"event\":\"documents.changed\"}", sourceOrigin, true);
            Assert.AreEqual(1, await changed!.PublishAsync(new Response("A", "fixture"), context => context.SourceOrigin == sourceOrigin && context.IsMainFrame));
            await WaitUntilAsync(() => frames.Any(frame => Kind(frame) == "event"));
            await session.ReceiveAsync(Invoke("channel", "documents.stream", "{\"id\":\"x\"}"));
            await WaitUntilAsync(() => frames.Any(frame => Kind(frame) == "channel_complete"));
            Assert.IsTrue(frames.Count(frame => Kind(frame) == "channel_item") == 2);
            await session.ReceiveAsync(Invoke("resource", "documents.resource", "{\"id\":\"x\"}"));
            var resource = frames.Select(Parse).First(root => root.TryGetProperty("id", out var id) && id.GetString() == "resource").GetProperty("value").GetProperty("id").GetString();
            await session.ReceiveAsync($"{{\"neoastra\":1,\"kind\":\"resource_close\",\"resource\":\"{resource}\"}}");
            Assert.AreEqual(1, disposed);
            await session.ReceiveAsync("{\"neoastra\":1,\"kind\":\"unsubscribe\",\"id\":\"sub-1\"}");
            Assert.AreEqual(0, session.ActiveSubscriptionCount);
        }
    }

    [TestMethod]
    public async Task EventOverflowPoliciesRemainBoundedAndDeclarationOwned()
    {
        foreach (var policy in Enum.GetValues<NeoRpcOverflowBehavior>())
        {
            var eventSendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseEventSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var builder = new NeoRpcBuilder(TestOptions(new NeoRpcOptions { MaximumQueuedEventsPerSubscription = 1, MaximumQueuedEventBytesPerSubscription = 1024 }));
            var changed = builder.AddEvent("documents.changed", RpcTestJsonContext.Default.Response, new() { Permission = "test:event", OverflowBehavior = policy });
            await using var host = builder.Build();
            await using var session = host.OpenSession(new NeoRpcSessionIdentity("fixture", $"overflow-{policy}"), async (json, cancellationToken) =>
            {
                if (Kind(json) != "event") return;
                eventSendStarted.TrySetResult();
                await releaseEventSend.Task.WaitAsync(cancellationToken);
            });
            await session.ReceiveAsync("{\"neoastra\":1,\"kind\":\"subscribe\",\"id\":\"sub\",\"event\":\"documents.changed\"}");
            Assert.AreEqual(1, await changed.PublishAsync(new Response("first", "fixture")));
            await eventSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(1, await changed.PublishAsync(new Response("second", "fixture")));
            var finalAccepted = await changed.PublishAsync(new Response("third", "fixture"));
            Assert.AreEqual(policy is NeoRpcOverflowBehavior.DropOldest or NeoRpcOverflowBehavior.Coalesce ? 1 : 0, finalAccepted, policy.ToString());
            if (policy == NeoRpcOverflowBehavior.Fail) await WaitUntilAsync(() => session.ActiveSubscriptionCount == 0);
            else
            {
                releaseEventSend.TrySetResult();
                await session.ReceiveAsync("{\"neoastra\":1,\"kind\":\"unsubscribe\",\"id\":\"sub\"}");
            }
        }
    }

    private static (NeoRpcHost Host, NeoRpcSession Session, ConcurrentQueue<string> Frames) Create(Action<NeoRpcBuilder> configure, NeoRpcOptions? options = null)
    {
        var frames = new ConcurrentQueue<string>();
        var builder = new NeoRpcBuilder(TestOptions(options));
        configure(builder);
        var host = builder.Build();
        var session = host.OpenSession(new NeoRpcSessionIdentity("fixture", "document-session"), (json, _) => { frames.Enqueue(json); return ValueTask.CompletedTask; });
        return (host, session, frames);
    }

    private static NeoRpcCommandOptions CommandPolicy => new() { Permission = "test:invoke", MaximumConcurrency = 4096 };
    private static NeoRpcOptions TestOptions(NeoRpcOptions? options = null)
    {
        options ??= new NeoRpcOptions();
        options.AuthorizationService ??= AllowAuthorization.Instance;
        return options;
    }

    private static string Invoke(string id, string command, string args) => $"{{\"neoastra\":1,\"kind\":\"invoke\",\"id\":\"{id}\",\"command\":\"{command}\",\"args\":{args}}}";
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static string? ErrorCode(string json) { var root = Parse(json); return root.TryGetProperty("error", out var error) ? error.GetProperty("code").GetString() : null; }
    private static string? Kind(string json) => Parse(json).GetProperty("kind").GetString();
    private static void AssertSingleTerminal(ConcurrentQueue<string> frames, string id, string? errorCode)
    {
        var terminal = frames.Where(frame => { var root = Parse(frame); return root.TryGetProperty("id", out var value) && value.GetString() == id; }).ToArray();
        Assert.AreEqual(1, terminal.Length, id);
        Assert.AreEqual(errorCode, ErrorCode(terminal[0]), id);
    }
    private static async Task WaitUntilAsync(Func<bool> predicate) { for (var i = 0; i < 100 && !predicate(); i++) await Task.Delay(5); Assert.IsTrue(predicate()); }
    private static async IAsyncEnumerable<Response> Items(string value) { yield return new(value, "1"); await Task.Yield(); yield return new(value, "2"); }

    private sealed class DenyAuthorization : INeoRpcAuthorizationService { public ValueTask<NeoRpcAuthorizationResult> AuthorizeAsync(NeoRpcAuthorizationRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(request.Permission == "secure:read" ? NeoRpcAuthorizationResult.DenyPermission() : NeoRpcAuthorizationResult.Allow()); }
    private sealed class AllowAuthorization : INeoRpcAuthorizationService
    {
        internal static AllowAuthorization Instance { get; } = new();
        public ValueTask<NeoRpcAuthorizationResult> AuthorizeAsync(NeoRpcAuthorizationRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(NeoRpcAuthorizationResult.Allow());
    }
    private sealed class InvalidOperationMapper : INeoRpcErrorMapper { public bool TryMap(Exception exception, NeoRpcContext context, out NeoRpcError error) { error = new("mapped_failure", "Mapped safely.", context.CorrelationId); return exception is InvalidOperationException; } }
    private sealed class UnsafeMapper : INeoRpcErrorMapper { public bool TryMap(Exception exception, NeoRpcContext context, out NeoRpcError error) { error = new("Bad.Code", "unsafe\nmessage", "bad\ncorrelation"); return true; } }
    private sealed class BlockingAuthorization : INeoRpcAuthorizationService
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<NeoRpcAuthorizationResult> AuthorizeAsync(NeoRpcAuthorizationRequest request, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false); // Deliberately ignore cancellation to verify the terminal-state guard.
            return NeoRpcAuthorizationResult.Allow();
        }
    }
    private sealed class BlockingFeatureList : IReadOnlyList<string>
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count => 1;
        public string this[int index] => index == 0 ? "rpc" : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<string> GetEnumerator()
        {
            Entered.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
            yield return "rpc";
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class TestRpcDispatcher : SynchronizationContext, INeoRpcDispatcher, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];
        private readonly Thread _thread;
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TestRpcDispatcher()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "NeoAstra RPC test dispatcher" };
            _thread.Start();
            _started.Task.GetAwaiter().GetResult();
        }

        internal int ThreadId { get; private set; }

        public override void Post(SendOrPostCallback callback, object? state) => _queue.Add((callback, state));

        internal void Invoke(Action callback)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(_ =>
            {
                try { callback(); completion.TrySetResult(); }
                catch (Exception exception) { completion.TrySetException(exception); }
            }, null);
            completion.Task.GetAwaiter().GetResult();
        }

        public ValueTask<object?> InvokeAsync(Func<ValueTask<object?>> callback, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Environment.CurrentManagedThreadId == ThreadId) return callback();
            var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async _ =>
            {
                try { completion.TrySetResult(await callback()); }
                catch (Exception exception) { completion.TrySetException(exception); }
            }, null);
            return new ValueTask<object?>(completion.Task);
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join();
            _queue.Dispose();
        }

        private void Run()
        {
            ThreadId = Environment.CurrentManagedThreadId;
            SetSynchronizationContext(this);
            _started.TrySetResult();
            foreach (var (callback, state) in _queue.GetConsumingEnumerable()) callback(state);
        }
    }
    private sealed class TrackedDisposable(Action action) : IDisposable { public void Dispose() => action(); }
    private sealed class BlockingAsyncDisposable : IAsyncDisposable
    {
        private int _disposeCount;
        internal TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseDispose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            DisposeEntered.TrySetResult();
            await ReleaseDispose.Task.ConfigureAwait(false);
        }
    }
    private sealed class ConcurrentService(int id, Action dispose) : IDisposable
    {
        internal static int NextId;
        internal int Id { get; } = id;
        public void Dispose() => dispose();
    }

    private sealed class ChannelService : IAsyncDisposable
    {
        internal TaskCompletionSource EnumerationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseItems { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CleanupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int EnumerationCount;
        internal int EnumeratorDisposeCount;
        internal int DisposeCount;
        internal int YieldCount;
        internal int ItemCount = 1;
        internal bool HoldCleanup;
        internal bool IgnoreCancellation;
        internal bool ThrowOnMove;
        internal bool ThrowOnDispose;
        internal bool ThrowOnGetEnumerator;
        internal bool ThrowOnSerialize;

        internal NeoRpcChannel<Response> CreateChannel() => new(ThrowOnGetEnumerator ? new FailingEnumerable() : Items(),
            ThrowOnSerialize ? JsonMetadataServices.CreateValueInfo<Response>(new JsonSerializerOptions(), new FailingConverter<Response>()) : RpcTestJsonContext.Default.Response);

        private async IAsyncEnumerable<Response> Items([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref EnumerationCount);
            EnumerationEntered.TrySetResult();
            try
            {
                await ReleaseItems.Task.WaitAsync(IgnoreCancellation ? CancellationToken.None : cancellationToken);
                Assert.AreEqual(0, DisposeCount, "A channel enumerated a disposed service.");
                if (ThrowOnMove) throw new InvalidOperationException("source failed");
                for (var index = 0; index < ItemCount; index++)
                {
                    Interlocked.Increment(ref YieldCount);
                    yield return new Response(index.ToString(), "fixture");
                }
            }
            finally
            {
                CleanupEntered.TrySetResult();
                if (HoldCleanup) await ReleaseCleanup.Task;
                Interlocked.Increment(ref EnumeratorDisposeCount);
            }
        }

        public ValueTask DisposeAsync()
        {
            Assert.AreEqual(EnumerationCount, EnumeratorDisposeCount, "Service disposal preceded enumerator disposal.");
            Interlocked.Increment(ref DisposeCount);
            if (ThrowOnDispose) throw new InvalidOperationException("service disposal failed");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingEnumerable : IAsyncEnumerable<Response>
    {
        public IAsyncEnumerator<Response> GetAsyncEnumerator(CancellationToken cancellationToken = default) => throw new InvalidOperationException("enumerator creation failed");
    }

    private sealed class FailingConverter<T> : JsonConverter<T>
    {
        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => throw new JsonException("serialization failed");
    }

    private sealed class RpcDiagnosticSink : INeoRpcDiagnosticSink
    {
        internal ConcurrentQueue<NeoRpcDiagnostic> Values { get; } = new();
        internal TaskCompletionSource TeardownWarning { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Write(NeoRpcDiagnostic diagnostic)
        {
            Values.Enqueue(diagnostic);
            if (diagnostic.Code == "teardown_timeout") TeardownWarning.TrySetResult();
        }
    }
}

public sealed record Request(string Id);
public sealed record Response(string Title, string ViewLabel);
public sealed record ResourceResponse(string Id);
public sealed record ServiceResponse(int Id);
public sealed class SerializationFailureResponse { [JsonConverter(typeof(ThrowingStringConverter))] public string Value { get; set; } = string.Empty; }
public sealed class ThrowingStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString();
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => throw new JsonException("secret response serialization failure");
}
public enum WireContractKind { First, Second }
public sealed class WirePolicyDto
{
    [NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] public long Signed { get; set; }
    [NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcUInt64JsonConverter))] public ulong Unsigned { get; set; }
    [NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcNullableInt64JsonConverter))] public long? Optional { get; set; }
    public WireContractKind Kind { get; set; }
}

[JsonSerializable(typeof(Request))]
[JsonSerializable(typeof(Response))]
[JsonSerializable(typeof(ResourceResponse))]
[JsonSerializable(typeof(ServiceResponse))]
[JsonSerializable(typeof(SerializationFailureResponse))]
[JsonSerializable(typeof(WirePolicyDto))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
internal sealed partial class RpcTestJsonContext : JsonSerializerContext;
