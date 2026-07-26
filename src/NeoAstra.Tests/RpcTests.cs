// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        var options = new NeoRpcOptions { MaximumConcurrentInvocations = 2, MaximumConcurrentInvocationsPerSession = 1, InvocationTimeout = TimeSpan.FromMilliseconds(40) };
        var (host, session, frames) = Create(builder => builder.AddCommand<Request, Response>("slow.wait", async (request, _, token) =>
        {
            await gate.Task.WaitAsync(token);
            return new Response(request.Id, "done");
        }, RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response, CommandPolicy), options);
        await using (host) await using (session)
        {
            var first = session.ReceiveAsync(Invoke("slow-1", "slow.wait", "{\"id\":\"a\"}")).AsTask();
            await Task.Delay(10);
            await session.ReceiveAsync(Invoke("slow-2", "slow.wait", "{\"id\":\"b\"}"));
            await first;
            Assert.IsTrue(frames.Any(frame => ErrorCode(frame) == "too_many_requests"));
            Assert.IsTrue(frames.Any(frame => ErrorCode(frame) == "timeout"));

            var canceled = session.ReceiveAsync(Invoke("slow-3", "slow.wait", "{\"id\":\"c\"}")).AsTask();
            await Task.Delay(5);
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
            await session.ReceiveAsync(Invoke("wrong", "documents.open", "{\"id\":\"x\"}"));
            await session.ReceiveAsync("{\"neoastra\":1,\"kind\":\"invoke\",\"id\":\"right\",\"command\":\"documents.open\",\"contract\":\"expected\",\"args\":{\"id\":\"x\"}}");
            var results = frames.ToArray();
            Assert.AreEqual("protocol_mismatch", ErrorCode(results[0]));
            Assert.IsTrue(Parse(results[1]).GetProperty("ok").GetBoolean());
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
