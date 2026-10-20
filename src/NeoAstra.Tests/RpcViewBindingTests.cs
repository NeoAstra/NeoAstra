// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
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

    private static string Invoke(string id, string command, string args) => $"{{\"neoastra\":1,\"kind\":\"invoke\",\"id\":\"{id}\",\"command\":\"{command}\",\"args\":{args}}}";
}
