// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using NeoAstra.Rpc;

namespace NeoAstra.Tests;

/// <summary>
/// What a session does with a frame that the transport of its view cannot carry, too large for its messages or deeper
/// than its page accepts: the call, subscription, or channel of that frame fails alone, and the session keeps serving.
/// </summary>
[TestClass]
public sealed class RpcOutboundLimitTests
{
    private static NeoRpcCommandOptions Trusted => new();

    [TestMethod]
    public async Task AResultTheConnectionRefusesAsTooLarge_IsAnsweredWithPayloadTooLarge_AndTheSessionKeepsServing()
    {
        // The public send callback reports a frame too large for its connection: before, that closed the session.
        var frames = new ConcurrentQueue<string>();
        await using var host = EchoHost();
        await using var session = host.OpenSession(new NeoRpcSessionIdentity("fixture", "refusing-session"), (json, _) =>
        {
            if (json.Contains("\"ok\":true", StringComparison.Ordinal) && json.Contains("big", StringComparison.Ordinal))
                throw new NeoRpcException(NeoRpcErrorCodes.PayloadTooLarge, "Too large for this connection.");
            frames.Enqueue(json);
            return ValueTask.CompletedTask;
        });
        await session.ReceiveAsync(Invoke("first", "big"));
        await session.ReceiveAsync(Invoke("second", "small"));

        Assert.AreEqual(NeoRpcErrorCodes.PayloadTooLarge, ErrorCode(Single(frames, "first")), "The caller is answered.");
        Assert.IsNull(ErrorCode(Single(frames, "second")));
        Assert.IsFalse(session.Closed.IsCancellationRequested, "A frame refused alone does not close the session.");
        Assert.AreEqual(0, session.ActiveInvocationCount);
    }

    [TestMethod]
    public async Task AResultTheViewRefusesInABatch_IsAnsweredWithPayloadTooLarge_AndTheOtherFramesAreDelivered()
    {
        var frames = new ConcurrentQueue<string>();
        await using var host = EchoHost();
        await using var session = host.OpenSessionCore(new NeoRpcSessionIdentity("fixture", "batch-refusing-session"),
            (_, _) => throw new AssertFailedException("A view session sends in batches."),
            batch =>
            {
                NeoRpcFrameRefusal[]? refusals = null;
                for (var index = 0; index < batch.Count; index++)
                {
                    var frame = batch[index].ToString();
                    if (frame.Contains("\"ok\":true", StringComparison.Ordinal) && frame.Contains("big", StringComparison.Ordinal))
                        (refusals ??= new NeoRpcFrameRefusal[batch.Count])[index] = NeoRpcFrameRefusal.TooLarge;
                    else frames.Enqueue(frame);
                }
                return ValueTask.FromResult(refusals);
            }, null, null);
        await Task.WhenAll(session.ReceiveAsync(Invoke("first", "big")).AsTask(), session.ReceiveAsync(Invoke("second", "small")).AsTask());

        Assert.AreEqual(NeoRpcErrorCodes.PayloadTooLarge, ErrorCode(Single(frames, "first")));
        Assert.IsNull(ErrorCode(Single(frames, "second")));
        Assert.IsFalse(session.Closed.IsCancellationRequested);
    }

    [TestMethod]
    public async Task AResultLargerThanTheMessagesOfTheView_IsRefusedBeforeItIsSent()
    {
        var frames = new ConcurrentQueue<string>();
        await using var host = EchoHost();
        await using var session = OpenLimited(host, frames, maximumMessageSize: 512, maximumJsonDepth: 32);
        await session.ReceiveAsync(Invoke("large", new string('x', 1024)));
        await session.ReceiveAsync(Invoke("small", "x"));

        Assert.AreEqual(NeoRpcErrorCodes.PayloadTooLarge, ErrorCode(Single(frames, "large")));
        Assert.IsNull(ErrorCode(Single(frames, "small")));
        Assert.IsTrue(frames.All(static frame => Encoding.UTF8.GetByteCount(frame) <= 512 - NeoRpcOutboundLimits.MinimumEnvelopeBytes), "No frame beyond the limit was sent.");
        Assert.IsFalse(session.Closed.IsCancellationRequested);
    }

    [TestMethod]
    public async Task AResultDeeperThanTheTransportAccepts_IsAnsweredWithSerializationFailed()
    {
        // A transport depth of 4: the frame object and three levels of value. The page drops a deeper frame unanswered.
        var builder = new NeoRpcBuilder(new NeoRpcOptions());
        builder.AddCommand<JsonElement, JsonElement>("flow.json", static (request, _, _) => ValueTask.FromResult(request),
            CapabilityTestJsonContext.Default.JsonElement, CapabilityTestJsonContext.Default.JsonElement, Trusted);
        await using var host = builder.Build();
        var frames = new ConcurrentQueue<string>();
        await using var session = OpenLimited(host, frames, maximumMessageSize: 1024 * 1024, maximumJsonDepth: 4);
        await session.ReceiveAsync("{\"neoastra\":1,\"kind\":\"invoke\",\"id\":\"deep\",\"command\":\"flow.json\",\"args\":[[[[1,2,3,4,5,6,7,8]]]]}");
        await session.ReceiveAsync("{\"neoastra\":1,\"kind\":\"invoke\",\"id\":\"fits\",\"command\":\"flow.json\",\"args\":[[[1,2,3,4,5,6,7,8,9,10]]]}");

        Assert.AreEqual(NeoRpcErrorCodes.SerializationFailed, ErrorCode(Single(frames, "deep")));
        var fits = Single(frames, "fits");
        Assert.IsNull(ErrorCode(fits));
        Assert.AreEqual(4, Depth(fits), "A frame may use every level of the transport.");
        Assert.IsTrue(frames.All(static frame => Depth(frame) <= 4));
        Assert.IsFalse(session.Closed.IsCancellationRequested);
    }

    [TestMethod]
    public async Task AnEventTheViewCannotReceive_FailsItsSubscriptionAlone()
    {
        var diagnostics = new ConcurrentQueue<NeoRpcDiagnostic>();
        var builder = new NeoRpcBuilder(new NeoRpcOptions { DiagnosticSink = new DiagnosticSink(diagnostics) });
        var deep = builder.AddEvent("flow.deep", CapabilityTestJsonContext.Default.JsonElement);
        var large = builder.AddEvent("flow.large", CapabilityTestJsonContext.Default.JsonElement);
        builder.AddCommand<Request, Response>("flow.echo", static (request, _, _) => ValueTask.FromResult(new Response(request.Id, "view")),
            RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response, Trusted);
        await using var host = builder.Build();
        var frames = new ConcurrentQueue<string>();
        await using var session = OpenLimited(host, frames, maximumMessageSize: 512, maximumJsonDepth: 4);
        await session.ReceiveAsync(Subscribe("flow.deep"));
        await session.ReceiveAsync(Subscribe("flow.large"));

        // Too deep: refused when it is published, for the session it cannot reach.
        Assert.AreEqual(0, await deep.PublishAsync(JsonDocument.Parse("[[[[1,2,3,4,5,6,7,8]]]]").RootElement));
        await WaitUntilAsync(() => session.ActiveSubscriptionCount == 1);
        // Too large: refused when its frame is written.
        Assert.AreEqual(1, await large.PublishAsync(JsonDocument.Parse($"\"{new string('x', 1024)}\"").RootElement));
        await WaitUntilAsync(() => session.ActiveSubscriptionCount == 0);

        Assert.IsFalse(frames.Any(static frame => Kind(frame) == "event"), "No event the page cannot receive is sent.");
        Assert.IsTrue(diagnostics.Any(static value => value.Code == NeoRpcErrorCodes.SerializationFailed));
        Assert.IsTrue(diagnostics.Any(static value => value.Code == NeoRpcErrorCodes.PayloadTooLarge));
        await session.ReceiveAsync("{\"neoastra\":1,\"kind\":\"invoke\",\"id\":\"after\",\"command\":\"flow.echo\",\"args\":{\"id\":\"x\"}}");
        Assert.IsNull(ErrorCode(Single(frames, "after")), "The session keeps serving.");

        static string Subscribe(string name) => $"{{\"neoastra\":1,\"kind\":\"subscribe\",\"id\":\"{name}\",\"event\":\"{name}\"}}";
    }

    [TestMethod]
    [DataRow("refused", NeoRpcErrorCodes.PayloadTooLarge, DisplayName = "An item the view refuses when it is posted")]
    [DataRow("large", NeoRpcErrorCodes.PayloadTooLarge, DisplayName = "An item larger than the messages of the view")]
    [DataRow("deep", NeoRpcErrorCodes.SerializationFailed, DisplayName = "An item deeper than the transport accepts")]
    public async Task AChannelItemTheViewCannotReceive_FailsItsChannelWithTheReason(string item, string code)
    {
        var builder = new NeoRpcBuilder(new NeoRpcOptions());
        builder.AddChannelCommand<Request, JsonElement>("flow.stream", (_, _, _) => ValueTask.FromResult(new NeoRpcChannel<JsonElement>(Items(item), CapabilityTestJsonContext.Default.JsonElement)),
            RpcTestJsonContext.Default.Request, Trusted);
        builder.AddCommand<Request, Response>("flow.echo", static (request, _, _) => ValueTask.FromResult(new Response(request.Id, "view")),
            RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response, Trusted);
        await using var host = builder.Build();
        var frames = new ConcurrentQueue<string>();
        await using var session = host.OpenSessionCore(new NeoRpcSessionIdentity("fixture", "channel-limit-session"),
            (_, _) => throw new AssertFailedException("A view session sends in batches."),
            batch =>
            {
                NeoRpcFrameRefusal[]? refusals = null;
                for (var index = 0; index < batch.Count; index++)
                {
                    var frame = batch[index].ToString();
                    if (frame.Contains("refused", StringComparison.Ordinal)) (refusals ??= new NeoRpcFrameRefusal[batch.Count])[index] = NeoRpcFrameRefusal.TooLarge;
                    else frames.Enqueue(frame);
                }
                return ValueTask.FromResult(refusals);
            }, null, null, NeoRpcOutboundLimits.Of(512, 4));
        await session.ReceiveAsync("{\"neoastra\":1,\"kind\":\"invoke\",\"id\":\"stream\",\"command\":\"flow.stream\",\"args\":{\"id\":\"x\"}}");
        await WaitUntilAsync(() => session.ActiveChannelCount == 0 && frames.Any(static frame => Kind(frame) is "channel_complete" or "channel_error"));

        var terminal = frames.Single(static frame => Kind(frame) is "channel_complete" or "channel_error");
        Assert.AreEqual("channel_error", Kind(terminal));
        Assert.AreEqual(code, ErrorCode(terminal));
        Assert.AreEqual(1, frames.Count(static frame => Kind(frame) == "channel_item"), "The item before the one refused was delivered.");
        await session.ReceiveAsync("{\"neoastra\":1,\"kind\":\"invoke\",\"id\":\"after\",\"command\":\"flow.echo\",\"args\":{\"id\":\"x\"}}");
        Assert.IsNull(ErrorCode(Single(frames, "after")), "The session keeps serving.");
        Assert.IsFalse(session.Closed.IsCancellationRequested);

        static async IAsyncEnumerable<JsonElement> Items(string item, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return JsonDocument.Parse("\"first\"").RootElement;
            yield return JsonDocument.Parse(item switch
            {
                "refused" => "\"refused\"",
                "large" => $"\"{new string('x', 1024)}\"",
                _ => "[[[[1,2,3,4,5,6,7,8]]]]",
            }).RootElement;
            // Never reached when the channel fails: it stays open until it is closed.
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public void TheLimitsOfAView_LeaveRoomForTheLeastEnvelope_AndForTheFrameObject()
    {
        var limits = NeoRpcOutboundLimits.Of(1024, 32);
        Assert.AreEqual(1024 - 112, limits.MaximumFrameBytes);
        Assert.AreEqual(31, limits.MaximumValueDepth);
        Assert.IsFalse(limits.IsTooLarge(Frame(new string('x', 912))));
        Assert.IsTrue(limits.IsTooLarge(Frame(new string('x', 913))));
        Assert.IsTrue(limits.IsTooLarge(Frame(new string('é', 457))), "UTF-8 bytes are counted, not characters.");
        Assert.IsFalse(NeoRpcOutboundLimits.None.IsTooLarge(Frame(new string('x', 100_000))));
        Assert.AreEqual(0, NeoRpcOutboundLimits.MeasureDepth("1"u8));
        Assert.AreEqual(1, NeoRpcOutboundLimits.MeasureDepth("{}"u8));
        Assert.AreEqual(3, NeoRpcOutboundLimits.MeasureDepth("{\"a\":[1,{\"b\":2}],\"c\":[]}"u8));
        var shallow = NeoRpcOutboundLimits.Of(1024, 3);
        Assert.IsFalse(shallow.IsTooDeep("[[1,2,3,4,5,6,7,8]]"u8));
        Assert.IsTrue(shallow.IsTooDeep("[[[1,2,3,4,5,6,7,8]]]"u8));
        var measured = -1;
        Assert.IsTrue(shallow.IsTooDeep(Encoding.UTF8.GetBytes("[[[1,2,3,4,5,6,7,8]]]"), ref measured));
        Assert.AreEqual(3, measured, "The depth of an event is measured once for every session.");
    }

    private static NeoRpcHost EchoHost()
    {
        var builder = new NeoRpcBuilder(new NeoRpcOptions());
        builder.AddCommand<Request, Response>("flow.echo", static (request, _, _) => ValueTask.FromResult(new Response(request.Id, "view")),
            RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response, Trusted);
        return builder.Build();
    }

    private static NeoRpcSession OpenLimited(NeoRpcHost host, ConcurrentQueue<string> frames, uint maximumMessageSize, int maximumJsonDepth)
        => host.OpenSessionCore(new NeoRpcSessionIdentity("fixture", $"limited-{Guid.NewGuid():N}"),
            (_, _) => throw new AssertFailedException("A view session sends in batches."),
            batch => { foreach (var frame in batch) frames.Enqueue(frame.ToString()); return ValueTask.FromResult<NeoRpcFrameRefusal[]?>(null); },
            null, null, NeoRpcOutboundLimits.Of(maximumMessageSize, maximumJsonDepth));

    private static NeoTransportFrame Frame(string text) => NeoTransportFrame.FromString(text);

    private static string Invoke(string id, string value)
        => $"{{\"neoastra\":1,\"kind\":\"invoke\",\"id\":\"{id}\",\"command\":\"flow.echo\",\"args\":{{\"id\":\"{value}\"}}}}";

    private static string Single(ConcurrentQueue<string> frames, string id)
        => frames.Single(frame => JsonDocument.Parse(frame).RootElement.TryGetProperty("id", out var value) && value.GetString() == id);

    private static string? Kind(string json) => JsonDocument.Parse(json).RootElement.GetProperty("kind").GetString();

    private static string? ErrorCode(string json)
        => JsonDocument.Parse(json).RootElement.TryGetProperty("error", out var error) ? error.GetProperty("code").GetString() : null;

    private static int Depth(string json) => NeoRpcOutboundLimits.MeasureDepth(Encoding.UTF8.GetBytes(json));

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = System.Diagnostics.Stopwatch.GetTimestamp() + 10 * System.Diagnostics.Stopwatch.Frequency;
        while (!predicate() && System.Diagnostics.Stopwatch.GetTimestamp() < deadline) await Task.Delay(5);
        Assert.IsTrue(predicate());
    }

    private sealed class DiagnosticSink(ConcurrentQueue<NeoRpcDiagnostic> values) : INeoRpcDiagnosticSink
    {
        public void Write(NeoRpcDiagnostic diagnostic) => values.Enqueue(diagnostic);
    }
}
