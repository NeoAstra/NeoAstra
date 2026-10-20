// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text;
using System.Text.Json;

namespace NeoAstra.Tests;

/// <summary>
/// The envelope of a message of the page, read in one pass of a reader, says what the JsonDocument the transport built
/// for every message said: the same messages are refused, dropped, or delivered, with the same frame.
/// </summary>
[TestClass]
public sealed class TransportEnvelopeTests
{
    private const string Binding = "0123456789abcdef0123456789abcdef";

    [TestMethod]
    public void TheEnvelopeIsReadAsTheDocumentOfTheMessageReadIt()
    {
        foreach (var depth in new[] { 4, 32 })
        {
            foreach (var message in Corpus())
            {
                var expected = Oracle(message, depth);
                var bytes = Encoding.UTF8.GetBytes(message);
                // The ID of the active document is not decoded again, and changes nothing.
                foreach (var known in new[] { null, "document" })
                {
                    var actual = NeoTransportCoordinator.ReadEnvelope(bytes, depth, Binding, known);
                    var frame = actual.Kind == NeoTransportEnvelopeKind.Valid ? Encoding.UTF8.GetString(bytes, actual.FrameStart, actual.FrameLength) : null;
                    Assert.AreEqual(expected, (actual.Kind, actual.RendererDocumentId, actual.Kind == NeoTransportEnvelopeKind.Valid ? actual.FrameKind : NeoTransportFrameKind.Invalid, frame), $"depth {depth}, known {known}: {message}");
                }
            }
        }
    }

    // What NeoTransportCoordinator.Receive read from the JsonDocument of a message before it read the message in one pass.
    private static (NeoTransportEnvelopeKind, string?, NeoTransportFrameKind, string?) Oracle(string json, int depth)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = depth, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false }); }
        catch (JsonException) { return (NeoTransportEnvelopeKind.InvalidJson, null, NeoTransportFrameKind.Invalid, null); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("__neoastraTransport", out var marker) || marker.ValueKind != JsonValueKind.Number)
                return (NeoTransportEnvelopeKind.Legacy, null, NeoTransportFrameKind.Invalid, null);
            int value;
            // GetInt32 threw, and the message was dropped with the exception.
            try { value = marker.GetInt32(); }
            catch (FormatException) { return (NeoTransportEnvelopeKind.Dropped, null, NeoTransportFrameKind.Invalid, null); }
            if (value != 1) return (NeoTransportEnvelopeKind.Legacy, null, NeoTransportFrameKind.Invalid, null);
            string id;
            if (!root.TryGetProperty("hostViewBinding", out var host) || host.ValueKind != JsonValueKind.String || !string.Equals(host.GetString(), Binding, StringComparison.Ordinal) ||
                !root.TryGetProperty("rendererDocumentId", out var renderer) || renderer.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(id = renderer.GetString()!) || id.Length > 128 ||
                !root.TryGetProperty("frame", out var frame) || frame.ValueKind != JsonValueKind.Object)
                return (NeoTransportEnvelopeKind.Invalid, null, NeoTransportFrameKind.Invalid, null);
            var kind = NeoTransportFrameKind.Invalid;
            if (frame.TryGetProperty("neoastra", out var discriminator) && discriminator.ValueKind == JsonValueKind.Number &&
                discriminator.TryGetInt32(out var discriminatorValue) && discriminatorValue == 1 &&
                frame.TryGetProperty("kind", out var kindValue) && kindValue.ValueKind == JsonValueKind.String)
            {
                kind = kindValue.GetString() switch
                {
                    "hello" => NeoTransportFrameKind.Hello,
                    "close" => NeoTransportFrameKind.Close,
                    "hello_ack" => NeoTransportFrameKind.HelloAck,
                    "diagnostic" => NeoTransportFrameKind.Diagnostic,
                    _ => NeoTransportFrameKind.Application,
                };
            }
            return (NeoTransportEnvelopeKind.Valid, id, kind, frame.GetRawText());
        }
    }

    private static IEnumerable<string> Corpus()
    {
        string[] markers = ["1", "1.0", "1e0", "2", "-1", "0", "01", "\"1\"", "99999999999", "2147483648", "-2147483649", "null", "[1]", "{\"a\":1}", "true"];
        string[] bindings = [Q(Binding), Q("other"), Q("\\u0030" + Binding[1..]), Q(Binding.ToUpperInvariant()), "1", "null", "[]", Q("")];
        string[] renderers = [Q("document"), Q(""), Q(new string('x', 128)), Q(new string('x', 129)), Q("d\\u006fcument"), Q(string.Concat(Enumerable.Repeat("\\u00e9", 128))),
            Q(string.Concat(Enumerable.Repeat("\\u00e9", 129))), Q(new string((char)0xE9, 128)), Q(new string((char)0xE9, 129)), Q(string.Concat(Enumerable.Repeat("\\ud83d\\ude00", 64))),
            Q(string.Concat(Enumerable.Repeat("\\ud83d\\ude00", 65))), Q("a\\\"b"), "5", "null", "{}", "[\"document\"]"];
        string[] frames =
        [
            "{\"neoastra\":1,\"kind\":\"invoke\",\"id\":\"1\",\"args\":{\"a\":[1,2,{\"b\":null}]}}",
            "{\"neoastra\":1,\"kind\":\"hello\",\"protocol\":{\"major\":1,\"minor\":0}}",
            "{\"neoastra\":1,\"kind\":\"close\"}", "{\"neoastra\":1,\"kind\":\"hello_ack\"}", "{\"neoastra\":1,\"kind\":\"diagnostic\"}",
            "{\"neoastra\":1,\"kind\":\"h\\u0065llo\"}", "{\"neo\\u0061stra\":1,\"k\\u0069nd\":\"close\"}", "{\"neoastra\":1.0,\"kind\":\"invoke\"}",
            "{\"neoastra\":2,\"kind\":\"invoke\"}", "{\"neoastra\":\"1\",\"kind\":\"invoke\"}", "{\"neoastra\":1}", "{\"kind\":\"invoke\"}", "{\"neoastra\":1,\"kind\":5}",
            "{\"neoastra\":1,\"kind\":\"invoke\",\"kind\":\"hello\"}", "{\"neoastra\":1,\"kind\":\"hello\",\"kind\":7}", "{\"neoastra\":2,\"kind\":\"invoke\",\"neoastra\":1}",
            "{\"neoastra\":1,\"kind\":\"invoke\",\"neoastra\":2}", "{\"nested\":{\"neoastra\":1,\"kind\":\"invoke\"}}", "{\"neoastra\":1,\"kind\":\"\"}",
            "{ \"neoastra\" : 1 , \"kind\" : \"invoke\" , \"deep\" : [[[[[[1]]]]]] }", "{}", "[]", "null", "\"frame\"", "{\"neoastra\":1,\"kind\":\"invoke\",\"text\":\"\\u0000 \\\" \\\\ \\/ \\ud800\"}",
        ];
        var names = new[] { "__neoastraTransport", "hostViewBinding", "rendererDocumentId", "frame" };
        string Envelope(params (string Name, string Value)[] properties) => "{" + string.Join(",", properties.Select(static property => Q(property.Name) + ":" + property.Value)) + "}";

        // Each value of each property in a valid envelope.
        foreach (var marker in markers) yield return Envelope((names[0], marker), (names[1], bindings[0]), (names[2], renderers[0]), (names[3], frames[0]));
        foreach (var binding in bindings) yield return Envelope((names[0], "1"), (names[1], binding), (names[2], renderers[0]), (names[3], frames[0]));
        foreach (var renderer in renderers) yield return Envelope((names[0], "1"), (names[1], bindings[0]), (names[2], renderer), (names[3], frames[0]));
        foreach (var frame in frames) yield return Envelope((names[0], "1"), (names[1], bindings[0]), (names[2], renderers[0]), (names[3], frame));
        // Missing, repeated, escaped, and reordered properties; messages that are not envelopes; invalid JSON.
        for (var missing = 0; missing < names.Length; missing++)
            yield return Envelope(new[] { (names[0], "1"), (names[1], bindings[0]), (names[2], renderers[0]), (names[3], frames[0]) }.Where((_, index) => index != missing).ToArray());
        yield return Envelope((names[3], frames[0]), (names[2], renderers[0]), (names[1], bindings[0]), (names[0], "1"));
        yield return Envelope((names[0], "1"), (names[1], bindings[0]), (names[2], renderers[0]), (names[3], frames[0]), (names[0], "2"));
        yield return Envelope((names[0], "2"), (names[1], bindings[0]), (names[2], renderers[0]), (names[3], frames[0]), (names[0], "1"));
        yield return Envelope((names[0], "1"), (names[1], bindings[0]), (names[2], renderers[0]), (names[3], frames[0]), (names[3], "null"));
        yield return Envelope((names[0], "1"), (names[1], bindings[0]), (names[2], renderers[0]), (names[3], "null"), (names[3], frames[1]));
        yield return Envelope((names[0], "1"), (names[1], bindings[0]), (names[2], renderers[0]), (names[3], frames[0]), (names[3], frames[2]));
        yield return Envelope((names[0], "1"), (names[1], bindings[0]), (names[2], renderers[0]), (names[2], Q("")), (names[3], frames[0]));
        yield return Envelope((names[0], "1"), (names[1], Q("other")), (names[1], bindings[0]), (names[2], renderers[0]), (names[3], frames[0]));
        yield return Envelope(("__neoastra\\u0054ransport", "1"), ("host\\u0056iewBinding", bindings[0]), ("renderer\\u0044ocumentId", renderers[0]), ("fr\\u0061me", frames[0]));
        yield return Envelope((names[0], "1"), ("extra", "{\"__neoastraTransport\":2,\"frame\":{}}"), (names[1], bindings[0]), (names[2], renderers[0]), (names[3], frames[0]));
        yield return " \r\n\t" + Envelope((names[0], "1"), (names[1], bindings[0]), (names[2], renderers[0]), (names[3], frames[0])) + " \n";
        foreach (var other in new[] { "[]", "[1,{\"a\":2}]", "\"text\"", "12", "null", "{}", "{\"type\":\"legacy\"}", "{\"__neoastraTransport\":1}" }) yield return other;
        foreach (var invalid in new[] { "", " ", "{", "{\"__neoastraTransport\":1,", "{\"__neoastraTransport\":1} x", "{\"a\":1,}", "/*c*/{}", "{'a':1}", "[1,2", "{\"a\":1}{}", "\ufeff{}", "{\"__neoastraTransport\":01}" })
            yield return invalid;
        // Nesting at and beyond the limits, in the frame and in another property.
        foreach (var levels in new[] { 2, 3, 4, 5, 31, 32, 33 })
        {
            var nested = new string('[', levels) + new string(']', levels);
            yield return Envelope((names[0], "1"), (names[1], bindings[0]), (names[2], renderers[0]), (names[3], "{\"neoastra\":1,\"kind\":\"invoke\",\"v\":" + nested + "}"));
            yield return Envelope((names[0], "1"), ("extra", nested), (names[1], bindings[0]), (names[2], renderers[0]), (names[3], frames[0]));
        }
        // Random envelopes: properties drawn with their values, repeated, dropped and shuffled.
        var random = new Random(1729);
        for (var index = 0; index < 4000; index++)
        {
            var properties = new List<(string, string)>();
            var count = random.Next(0, 7);
            for (var property = 0; property < count; property++)
            {
                properties.Add(random.Next(9) switch
                {
                    0 or 1 => (names[0], random.Next(3) == 0 ? markers[random.Next(markers.Length)] : "1"),
                    2 or 3 => (names[1], random.Next(3) == 0 ? bindings[random.Next(bindings.Length)] : bindings[0]),
                    4 or 5 => (names[2], renderers[random.Next(renderers.Length)]),
                    6 or 7 => (names[3], frames[random.Next(frames.Length)]),
                    _ => ("extra", frames[random.Next(frames.Length)]),
                });
            }
            yield return Envelope([.. properties]);
        }
    }

    private static string Q(string value) => "\"" + value + "\"";
}
