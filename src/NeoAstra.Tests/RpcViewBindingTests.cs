// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using NeoAstra.Rpc;

namespace NeoAstra.Tests;

/// <summary>How a view binding ties the frames and services of an RPC session to the document and view that own them.</summary>
[TestClass]
public sealed class RpcViewBindingTests
{
    private static NeoRpcCommandOptions Trusted => new() { MaximumConcurrency = 4096 };

    [TestMethod]
    public async Task AFrameTheViewRejectsAsTooLarge_FailsAlone_AndTheSessionGoesOn()
    {
        var posted = new ConcurrentQueue<string>();
        var builder = new NeoRpcBuilder(new NeoRpcOptions());
        builder.AddCommand<Request, Response>("flow.echo", (request, _, _) => ValueTask.FromResult(new Response(request.Id, "view")),
            RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response, Trusted);
        await using var host = builder.Build();
        // The view posts every frame of a batch but the ones over its message size, and says which.
        await using var session = host.OpenSessionCore(new NeoRpcSessionIdentity("fixture", "rejecting-session"),
            (_, _) => throw new AssertFailedException("A view session sends in batches."),
            frames =>
            {
                var rejected = new List<int>();
                for (var index = 0; index < frames.Count; index++)
                {
                    if (frames[index].Contains("\"big\"", StringComparison.Ordinal)) rejected.Add(index);
                    else posted.Enqueue(frames[index]);
                }
                return rejected.Count == 0 ? ValueTask.CompletedTask : ValueTask.FromException(new NeoRpcFramesRejectedException(rejected));
            }, null, null);

        var big = session.ReceiveAsync(Invoke("big", "flow.echo", "{\"id\":\"big\"}")).AsTask();
        var small = session.ReceiveAsync(Invoke("small", "flow.echo", "{\"id\":\"small\"}")).AsTask();
        try { await Task.WhenAll(big, small).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (NeoRpcException exception) when (exception.Code == NeoRpcErrorCodes.PayloadTooLarge) { /* The call whose answer was too large. */ }

        Assert.IsFalse(session.Closed.IsCancellationRequested, "A frame too large for the view is not a failure of the transport.");
        var answered = posted.Select(static frame => JsonDocument.Parse(frame).RootElement).Where(static root => root.GetProperty("kind").GetString() == "result").ToArray();
        CollectionAssert.Contains(answered.Select(static root => root.GetProperty("id").GetString()).ToArray(), "small");
        Assert.AreEqual(0, session.ActiveInvocationCount);

        // The session still answers.
        await session.ReceiveAsync(Invoke("after", "flow.echo", "{\"id\":\"after\"}")).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(posted.Any(static frame => frame.Contains("\"after\"", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ASessionTheBindingCannotOpen_IsRevokedOnTheTransport_AfterTheHandshakeReturns()
    {
        await using var host = new NeoRpcBuilder(new NeoRpcOptions()).Build();
        var view = CreateDetachedView("binding-view");
        var revoked = new ConcurrentQueue<(string DocumentSessionId, bool DuringTransition)>();
        var inTransition = false;
        await using var binding = new NeoRpcViewBinding(host, view,
            snapshot => throw new InvalidOperationException("open failure"),
            documentSessionId => { revoked.Enqueue((documentSessionId, Volatile.Read(ref inTransition))); return true; });

        // The transport announces the session while it answers the hello: the revocation cannot run inside that call.
        Volatile.Write(ref inTransition, true);
        QueueTransition(binding, new NeoTransportSessionSnapshot("unopened-document", 0, Array.Empty<string>(), true));
        Volatile.Write(ref inTransition, false);

        await WaitUntilAsync(() => !revoked.IsEmpty);
        var (documentSessionId, duringTransition) = revoked.Single();
        Assert.AreEqual("unopened-document", documentSessionId, "A document whose RPC session could not open is told so instead of waiting on calls nobody answers.");
        Assert.IsFalse(duringTransition);
    }

    private static global::NeoAstra.NeoAstra CreateDetachedView(string viewLabel)
    {
        var view = (global::NeoAstra.NeoAstra)RuntimeHelpers.GetUninitializedObject(typeof(global::NeoAstra.NeoAstra));
        typeof(global::NeoAstra.NeoAstra).GetField("_viewLabel", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, viewLabel);
        return view;
    }

    private static void QueueTransition(NeoRpcViewBinding binding, NeoTransportSessionSnapshot? snapshot) =>
        typeof(NeoRpcViewBinding).GetMethod("QueueTransition", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(binding, [snapshot]);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (var index = 0; index < 400 && !predicate(); index++) await Task.Delay(5);
        Assert.IsTrue(predicate());
    }

    private static string Invoke(string id, string command, string args) => $"{{\"neoastra\":1,\"kind\":\"invoke\",\"id\":\"{id}\",\"command\":\"{command}\",\"args\":{args}}}";
}
