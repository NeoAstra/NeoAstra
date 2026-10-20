// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using NeoAstra.Rpc;

namespace NeoAstra.Tests;

/// <summary>
/// The frames of the host as UTF-8 in pooled buffers: the bytes they hold, and the owners that give them back.
/// </summary>
[TestClass]
public sealed class RpcFrameTests
{
    [TestMethod]
    public void AValueIsSerializedIntoItsFrame_AsSerializeToUtf8BytesWritesIt()
    {
        // The options of the contract of the application shape the value, as they did when it was serialized on its own.
        var options = new JsonSerializerOptions(RpcTestJsonContext.Default.Options)
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = RpcTestJsonContext.Default,
        };
        var typeInfo = (JsonTypeInfo<Response>)options.GetTypeInfo(typeof(Response));
        foreach (var value in new[] { new Response("<b>caf\u00e9</b> & \u2028", "view"), new Response(new string('x', 70_000), "large") })
        {
            var expected = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
            var result = NeoRpcSession.BuildValueResult("call-1", value, typeInfo);
            try
            {
                CollectionAssert.AreEqual(expected, result.Frame.Span.Slice(result.ValueStart, result.ValueLength).ToArray());
                CollectionAssert.AreEqual(ReferenceResult("call-1", expected), result.Frame.Span.ToArray(), "The frame is the one the frame writer wrote around the serialized value.");
            }
            finally
            {
                result.Frame.Release();
            }
        }
    }

    [TestMethod]
    public void AValueThatCannotBeSerialized_GivesItsFrameBack()
    {
        var before = NeoTransportFrame.Outstanding;
        var typeInfo = (JsonTypeInfo<JsonElement>)new JsonSerializerOptions { MaxDepth = 1, TypeInfoResolver = CapabilityTestJsonContext.Default }.GetTypeInfo(typeof(JsonElement));
        using var deep = JsonDocument.Parse("[[[1]]]");
        Assert.Throws<Exception>(() => NeoRpcSession.BuildValueResult("call-1", deep.RootElement, typeInfo));
        Assert.AreEqual(before, NeoTransportFrame.Outstanding);
    }

    [TestMethod]
    public async Task EveryFrameASessionWrites_IsGivenBack_WhateverBecomesOfIt()
    {
        var baseline = NeoTransportFrame.Outstanding;
        var builder = new NeoRpcBuilder(new NeoRpcOptions { MaximumUnacknowledgedChannelItems = 4 });
        builder.AddCommand<Request, Response>("frames.echo", static (request, _, _) => ValueTask.FromResult(new Response(request.Id, "view")),
            RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response);
        builder.AddCommand<Request, Response>("frames.slow", static async (request, _, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return new Response(request.Id, "view");
            },
            RpcTestJsonContext.Default.Request, RpcTestJsonContext.Default.Response);
        builder.AddChannelCommand<Request, Response>("frames.stream", static (_, _, _) => ValueTask.FromResult(new NeoRpcChannel<Response>(Endless(), RpcTestJsonContext.Default.Response)),
            RpcTestJsonContext.Default.Request);
        var changed = builder.AddEvent("frames.changed", RpcTestJsonContext.Default.Response);
        var host = builder.Build();
        var frames = new ConcurrentQueue<string>();
        var batches = 0;
        var session = host.OpenSessionCore(new NeoRpcSessionIdentity("fixture", "frames-session"),
            static (_, _) => throw new AssertFailedException("A view session sends in batches."),
            async batch =>
            {
                var count = Interlocked.Increment(ref batches);
                NeoRpcFrameRefusal[]? refusals = null;
                for (var index = 0; index < batch.Count; index++)
                {
                    var frame = batch[index].ToString();
                    if (frame.Contains("\"title\":\"refused\"", StringComparison.Ordinal)) (refusals ??= new NeoRpcFrameRefusal[batch.Count])[index] = NeoRpcFrameRefusal.TooLarge;
                    else frames.Enqueue(frame);
                }
                await Task.Yield();
                // The transport fails once the flow is done: the frames it held fail with it and the session closes.
                if (frames.Any(static frame => frame.Contains("\"fail\"", StringComparison.Ordinal))) throw new InvalidOperationException("The view is gone.");
                return refusals;
            }, null, null, NeoRpcOutboundLimits.Of(4096, 32));

        // Answers sent, refused by the view, refused before they are sent (too large), errors, and a call canceled while it runs.
        await session.ReceiveAsync(Invoke("echo", "frames.echo", "{\"id\":\"x\"}"));
        await session.ReceiveAsync(Invoke("refused", "frames.echo", "{\"id\":\"refused\"}"));
        await session.ReceiveAsync(Invoke("large", "frames.echo", $"{{\"id\":\"{new string('x', 5000)}\"}}"));
        await session.ReceiveAsync(Invoke("missing", "frames.missing", "{}"));
        var slow = session.ReceiveAsync(Invoke("slow", "frames.slow", "{\"id\":\"x\"}")).AsTask();
        await session.ReceiveAsync("{\"neoastra\":1,\"kind\":\"cancel\",\"id\":\"slow\"}");
        await slow.WaitAsync(TimeSpan.FromSeconds(5));
        // Events delivered, refused, and withdrawn with their subscription.
        await session.ReceiveAsync("{\"neoastra\":1,\"kind\":\"subscribe\",\"id\":\"sub\",\"event\":\"frames.changed\"}");
        await WaitUntilAsync(() => session.ActiveSubscriptionCount == 1);
        for (var index = 0; index < 20; index++) await changed.PublishAsync(new Response(index == 3 ? "refused" : index.ToString(), "view"));
        await WaitUntilAsync(() => frames.Any(static frame => frame.Contains("\"kind\":\"event\"", StringComparison.Ordinal)));
        await session.ReceiveAsync("{\"neoastra\":1,\"kind\":\"unsubscribe\",\"id\":\"sub\"}");
        // Channel items queued up to the window and withdrawn when the channel closes.
        await session.ReceiveAsync(Invoke("stream", "frames.stream", "{\"id\":\"x\"}"));
        await WaitUntilAsync(() => frames.Any(static frame => frame.Contains("\"channel\":\"ch-", StringComparison.Ordinal) && frame.Contains("\"kind\":\"result\"", StringComparison.Ordinal)));
        await session.ReceiveAsync("{\"neoastra\":1,\"kind\":\"channel_close\",\"channel\":\"ch-1\"}");
        await WaitUntilAsync(() => session.ActiveChannelCount == 0);
        // A transport that fails, and the teardown of what is still queued.
        try { await session.ReceiveAsync(Invoke("fail", "frames.echo", "{\"id\":\"fail\"}")).AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception) { /* The call fails with its batch. */ }
        await session.DisposeAsync();
        await host.DisposeAsync();

        Assert.IsTrue(frames.Any(static frame => frame.Contains("\"kind\":\"event\"", StringComparison.Ordinal)));
        Assert.IsTrue(frames.Any(static frame => frame.Contains("payload_too_large", StringComparison.Ordinal)));
        await WaitUntilAsync(() => NeoTransportFrame.Outstanding == baseline);

        static async IAsyncEnumerable<Response> Endless([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (var index = 0; !cancellationToken.IsCancellationRequested; index++) yield return new Response(index.ToString(), "view");
            await Task.CompletedTask;
        }
    }

    // A result frame as the session wrote it when the value was serialized on its own: one Utf8JsonWriter, the value raw.
    private static byte[] ReferenceResult(string id, byte[] value)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject(); writer.WriteNumber("neoastra", 1); writer.WriteString("kind", "result"); writer.WriteString("id", id);
            writer.WriteBoolean("ok", true); writer.WritePropertyName("value"); writer.WriteRawValue(value, true); writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static string Invoke(string id, string command, string args)
        => $"{{\"neoastra\":1,\"kind\":\"invoke\",\"id\":\"{id}\",\"command\":\"{command}\",\"args\":{args}}}";

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (var index = 0; index < 400 && !predicate(); index++) await Task.Delay(5);
        Assert.IsTrue(predicate());
    }
}
