// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

#if NEOASTRA_TOOL
namespace NeoAstra.Tool.Shared.Mcp;
#else
namespace NeoAstra.Mcp;
#endif

/// <summary>A tool that an application offers through the relay, as MCP lists it.</summary>
internal sealed record NeoMcpToolInfo(string Name, string Description, JsonElement InputSchema, bool IsReadOnly);

/// <summary>An image of a tool result.</summary>
internal sealed record NeoMcpImage(string ContentType, ReadOnlyMemory<byte> Data);

/// <summary>The result of a tool call: a text, the images that go with it, and whether the tool failed.</summary>
internal sealed record NeoMcpToolOutcome(string Text, IReadOnlyList<NeoMcpImage> Images, bool IsError)
{
    internal static NeoMcpToolOutcome Error(string text) => new(text, [], true);
}

/// <summary>What an application says about itself when it connects to the relay.</summary>
internal sealed record NeoMcpApplicationInfo(string Application, string Platform, string Device, int ProcessId);

/// <summary>An application connected to the relay, as the relay lists it for the MCP server of an agent.</summary>
internal sealed record NeoMcpConnectedApplication(string Id, NeoMcpApplicationInfo Info, IReadOnlyList<NeoMcpToolInfo> Tools);

/// <summary>The kinds of messages between an application and the relay.</summary>
internal enum NeoMcpMessageKind
{
    /// <summary>The first message of the relay to an application: a nonce that the application proves the token of the session with.</summary>
    Challenge,

    /// <summary>The first message of an application: who it is, the tools it offers, its proof, and a nonce of its own.</summary>
    Hello,

    /// <summary>The answer of the relay to a hello: its proof of the token for the nonce of the application.</summary>
    Welcome,

    /// <summary>The relay asks the application to run a tool.</summary>
    Call,

    /// <summary>The application answers a call.</summary>
    Result,

    /// <summary>The relay no longer waits for the answer of a call.</summary>
    Cancel,

    /// <summary>The relay tells the MCP server of an agent which applications are connected.</summary>
    Applications,

    /// <summary>The application opens a stream of its tunnel to the development server, for a connection that its view made.</summary>
    StreamOpen,

    /// <summary>Bytes of a stream of the tunnel, in either direction, in a binary message rather than JSON.</summary>
    StreamData,

    /// <summary>The end of a stream of the tunnel, from either side.</summary>
    StreamClose,
}

/// <summary>A message between an application and the relay, as read from the connection.</summary>
internal sealed class NeoMcpMessage
{
    internal NeoMcpMessageKind Kind { get; init; }

    /// <summary>Gets the identifier of a call, a result, or a cancellation.</summary>
    internal string? Id { get; init; }

    /// <summary>Gets the name of the tool of a call.</summary>
    internal string? Name { get; init; }

    /// <summary>Gets the arguments of a call, or <see langword="null"/> when it has none.</summary>
    internal JsonElement? Arguments { get; init; }

    internal int ProtocolVersion { get; init; }

    internal NeoMcpApplicationInfo? Application { get; init; }

    internal IReadOnlyList<NeoMcpToolInfo>? Tools { get; init; }

    internal NeoMcpToolOutcome? Outcome { get; init; }

    /// <summary>Gets the application that a call or a cancellation of the MCP server of an agent is for.</summary>
    internal string? Target { get; init; }

    internal IReadOnlyList<NeoMcpConnectedApplication>? Applications { get; init; }

    /// <summary>Gets the nonce of a challenge or of a hello.</summary>
    internal string? Nonce { get; init; }

    /// <summary>Gets the proof of the token of a hello or of a welcome.</summary>
    internal string? Proof { get; init; }

    /// <summary>Gets the identifier of a stream of the tunnel.</summary>
    internal string? Stream { get; init; }

    /// <summary>Gets the bytes of a stream of the tunnel.</summary>
    internal byte[]? Data { get; init; }
}

/// <summary>
/// The messages between an application and the relay of the NeoAstra tool: one JSON object per text message of a WebSocket. The
/// application connects to <see cref="Path"/>; the relay sends a <c>challenge</c>, the application answers with its
/// <c>hello</c>, which proves the token of the development session for the nonce of the relay and carries a nonce of its own,
/// and the relay proves the token in turn in its <c>welcome</c>. The token itself never goes over the connection, which on a
/// phone crosses the local network. The application then answers each <c>call</c> with a <c>result</c> of the same identifier;
/// a <c>cancel</c> tells it that the relay no longer waits. Calls may overlap.
/// </summary>
/// <remarks>
/// The MCP server of an agent connects to <see cref="AgentPath"/> with the same token. The relay tells it the connected
/// applications in an <c>applications</c> message whenever they change, and it sends <c>call</c> and <c>cancel</c> with the
/// <c>target</c> application; the relay answers with the <c>result</c> of the application under the identifier of the agent.
/// </remarks>
internal static class NeoMcpWire
{
    /// <summary>The version of these messages, which an application announces in its <c>hello</c>.</summary>
    internal const int ProtocolVersion = 1;

    /// <summary>The path of the relay that applications connect to.</summary>
    internal const string Path = "/neoastra/mcp";

    /// <summary>The path of the relay that the MCP server of an agent connects to.</summary>
    internal const string AgentPath = "/neoastra/mcp/agent";

    /// <summary>The largest message accepted from the other side, which a screenshot of a large display stays far below.</summary>
    internal const int MaximumMessageSize = 64 * 1024 * 1024;

    /// <summary>The role of the application in a proof.</summary>
    internal const string ApplicationRole = "application";

    /// <summary>The role of the relay in a proof.</summary>
    internal const string RelayRole = "relay";

    /// <summary>A new random nonce.</summary>
    internal static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>Proves the token for a nonce: an HMAC-SHA256 of the role and the nonce, keyed with the token.</summary>
    internal static string Proof(string token, string role, string nonce)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(role + ":" + nonce)));
    }

    /// <summary>Checks a proof in constant time.</summary>
    internal static bool IsValidProof(string token, string role, string nonce, string? proof)
    {
        if (proof is null) return false;
        var expected = Encoding.ASCII.GetBytes(Proof(token, role, nonce));
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(proof));
    }

    internal static byte[] Challenge(string nonce)
    {
        ArgumentException.ThrowIfNullOrEmpty(nonce);
        return Write(writer =>
        {
            writer.WriteString("type", "challenge");
            writer.WriteString("nonce", nonce);
        });
    }

    internal static byte[] Welcome(string proof)
    {
        ArgumentException.ThrowIfNullOrEmpty(proof);
        return Write(writer =>
        {
            writer.WriteString("type", "welcome");
            writer.WriteString("proof", proof);
        });
    }

    internal static byte[] Hello(NeoMcpApplicationInfo application, IReadOnlyList<NeoMcpToolInfo> tools, string proof, string nonce)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentException.ThrowIfNullOrEmpty(proof);
        ArgumentException.ThrowIfNullOrEmpty(nonce);
        return Write(writer =>
        {
            writer.WriteString("type", "hello");
            writer.WriteNumber("protocol", ProtocolVersion);
            writer.WriteString("proof", proof);
            writer.WriteString("nonce", nonce);
            writer.WriteString("application", application.Application);
            writer.WriteString("platform", application.Platform);
            writer.WriteString("device", application.Device);
            writer.WriteNumber("processId", application.ProcessId);
            WriteTools(writer, tools);
        });
    }

    internal static byte[] ApplicationList(IReadOnlyList<NeoMcpConnectedApplication> applications)
    {
        ArgumentNullException.ThrowIfNull(applications);
        return Write(writer =>
        {
            writer.WriteString("type", "applications");
            writer.WriteStartArray("applications");
            foreach (var application in applications)
            {
                writer.WriteStartObject();
                writer.WriteString("id", application.Id);
                writer.WriteString("application", application.Info.Application);
                writer.WriteString("platform", application.Info.Platform);
                writer.WriteString("device", application.Info.Device);
                writer.WriteNumber("processId", application.Info.ProcessId);
                WriteTools(writer, application.Tools);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        });
    }

    private static void WriteTools(Utf8JsonWriter writer, IReadOnlyList<NeoMcpToolInfo> tools)
    {
        writer.WriteStartArray("tools");
        foreach (var tool in tools)
        {
            writer.WriteStartObject();
            writer.WriteString("name", tool.Name);
            writer.WriteString("description", tool.Description);
            writer.WritePropertyName("inputSchema");
            tool.InputSchema.WriteTo(writer);
            writer.WriteBoolean("readOnly", tool.IsReadOnly);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    internal static byte[] Call(string id, string name, JsonElement? arguments) => Call(id, name, arguments, null);

    /// <summary>Writes a call, for the application <paramref name="target"/> when the MCP server of an agent sends it.</summary>
    internal static byte[] Call(string id, string name, JsonElement? arguments, string? target)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(name);
        return Write(writer =>
        {
            writer.WriteString("type", "call");
            writer.WriteString("id", id);
            if (target is not null) writer.WriteString("target", target);
            writer.WriteString("name", name);
            if (arguments is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } value)
            {
                writer.WritePropertyName("arguments");
                value.WriteTo(writer);
            }
        });
    }

    internal static byte[] Result(string id, NeoMcpToolOutcome outcome)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(outcome);
        return Write(writer =>
        {
            writer.WriteString("type", "result");
            writer.WriteString("id", id);
            writer.WriteBoolean("isError", outcome.IsError);
            writer.WriteString("text", outcome.Text);
            writer.WriteStartArray("images");
            foreach (var image in outcome.Images)
            {
                writer.WriteStartObject();
                writer.WriteString("contentType", image.ContentType);
                writer.WriteBase64String("data", image.Data.Span);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        });
    }

    internal static byte[] Cancel(string id) => Cancel(id, null);

    internal static byte[] Cancel(string id, string? target)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return Write(writer =>
        {
            writer.WriteString("type", "cancel");
            writer.WriteString("id", id);
            if (target is not null) writer.WriteString("target", target);
        });
    }

    internal static byte[] StreamOpen(string stream) => StreamMessage("open", stream);

    internal static byte[] StreamClose(string stream) => StreamMessage("close", stream);

    /// <summary>The first byte of a binary message of stream data, which no JSON message starts with.</summary>
    internal const byte StreamDataMarker = 0x01;

    /// <summary>The size of the header of a binary message of stream data: its marker and the number of the stream.</summary>
    internal const int StreamDataHeaderSize = 5;

    /// <summary>Writes bytes of a stream of the tunnel as they are, after a marker and the number of the stream, instead of base64 in JSON.</summary>
    /// <exception cref="ArgumentException">The stream is not the number of a stream.</exception>
    internal static byte[] StreamData(string stream, ReadOnlySpan<byte> data)
    {
        if (!uint.TryParse(stream, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number))
            throw new ArgumentException("A stream of the tunnel is a number.", nameof(stream));
        var message = new byte[StreamDataHeaderSize + data.Length];
        message[0] = StreamDataMarker;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(1, 4), number);
        data.CopyTo(message.AsSpan(StreamDataHeaderSize));
        return message;
    }

    private static byte[] StreamMessage(string type, string stream)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);
        return Write(writer =>
        {
            writer.WriteString("type", type);
            writer.WriteString("stream", stream);
        });
    }

    /// <summary>Reads a message.</summary>
    /// <exception cref="FormatException">The message is not one of these messages.</exception>
    internal static NeoMcpMessage Parse(ReadOnlyMemory<byte> utf8Json)
    {
        if (!utf8Json.IsEmpty && utf8Json.Span[0] == StreamDataMarker)
        {
            if (utf8Json.Length < StreamDataHeaderSize) throw new FormatException("A message of stream data is shorter than its header.");
            var number = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(utf8Json.Span.Slice(1, 4));
            return new NeoMcpMessage
            {
                Kind = NeoMcpMessageKind.StreamData,
                Stream = number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Data = utf8Json.Span[StreamDataHeaderSize..].ToArray(),
            };
        }
        try
        {
            using var document = JsonDocument.Parse(utf8Json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("A message is a JSON object.");
            return RequiredString(root, "type") switch
            {
                "challenge" => new NeoMcpMessage { Kind = NeoMcpMessageKind.Challenge, Nonce = RequiredString(root, "nonce") },
                "welcome" => new NeoMcpMessage { Kind = NeoMcpMessageKind.Welcome, Proof = RequiredString(root, "proof") },
                "hello" => new NeoMcpMessage
                {
                    Kind = NeoMcpMessageKind.Hello,
                    Proof = RequiredString(root, "proof"),
                    Nonce = RequiredString(root, "nonce"),
                    ProtocolVersion = root.TryGetProperty("protocol", out var protocol) && protocol.TryGetInt32(out var version) ? version : throw new FormatException("A hello names its protocol."),
                    Application = new(RequiredString(root, "application"), RequiredString(root, "platform"), RequiredString(root, "device"),
                        root.TryGetProperty("processId", out var process) && process.TryGetInt32(out var processId) ? processId : 0),
                    Tools = Tools(root),
                },
                "call" => new NeoMcpMessage
                {
                    Kind = NeoMcpMessageKind.Call,
                    Id = RequiredString(root, "id"),
                    Target = OptionalString(root, "target"),
                    Name = RequiredString(root, "name"),
                    Arguments = root.TryGetProperty("arguments", out var arguments) && arguments.ValueKind != JsonValueKind.Null ? arguments.Clone() : null,
                },
                "result" => new NeoMcpMessage
                {
                    Kind = NeoMcpMessageKind.Result,
                    Id = RequiredString(root, "id"),
                    Outcome = new(RequiredString(root, "text"), Images(root), root.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True),
                },
                "cancel" => new NeoMcpMessage { Kind = NeoMcpMessageKind.Cancel, Id = RequiredString(root, "id"), Target = OptionalString(root, "target") },
                "applications" => new NeoMcpMessage { Kind = NeoMcpMessageKind.Applications, Applications = Applications(root) },
                "open" => new NeoMcpMessage { Kind = NeoMcpMessageKind.StreamOpen, Stream = RequiredString(root, "stream") },
                "close" => new NeoMcpMessage { Kind = NeoMcpMessageKind.StreamClose, Stream = RequiredString(root, "stream") },
                var other => throw new FormatException($"The message type '{other}' is not known."),
            };
        }
        catch (JsonException exception)
        {
            throw new FormatException("A message is not valid JSON.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new FormatException("A message has a value of the wrong kind.", exception);
        }
    }

    private static List<NeoMcpConnectedApplication> Applications(JsonElement root)
    {
        if (!root.TryGetProperty("applications", out var applications) || applications.ValueKind != JsonValueKind.Array) throw new FormatException("The applications are an array.");
        var result = new List<NeoMcpConnectedApplication>(applications.GetArrayLength());
        foreach (var application in applications.EnumerateArray())
        {
            if (application.ValueKind != JsonValueKind.Object) throw new FormatException("An application is a JSON object.");
            result.Add(new(RequiredString(application, "id"),
                new(RequiredString(application, "application"), RequiredString(application, "platform"), RequiredString(application, "device"),
                    application.TryGetProperty("processId", out var process) && process.TryGetInt32(out var processId) ? processId : 0),
                Tools(application)));
        }
        return result;
    }

    private static List<NeoMcpToolInfo> Tools(JsonElement root)
    {
        if (!root.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array) throw new FormatException("A hello lists its tools.");
        var result = new List<NeoMcpToolInfo>(tools.GetArrayLength());
        foreach (var tool in tools.EnumerateArray())
        {
            if (!tool.TryGetProperty("inputSchema", out var schema) || schema.ValueKind != JsonValueKind.Object) throw new FormatException("A tool has an input schema.");
            result.Add(new(RequiredString(tool, "name"), RequiredString(tool, "description"), schema.Clone(), tool.TryGetProperty("readOnly", out var readOnly) && readOnly.ValueKind == JsonValueKind.True));
        }
        return result;
    }

    private static List<NeoMcpImage> Images(JsonElement root)
    {
        var result = new List<NeoMcpImage>();
        if (!root.TryGetProperty("images", out var images)) return result;
        if (images.ValueKind != JsonValueKind.Array) throw new FormatException("The images of a result are an array.");
        foreach (var image in images.EnumerateArray())
        {
            if (!image.TryGetProperty("data", out var data) || !data.TryGetBytesFromBase64(out var bytes)) throw new FormatException("An image has base64 data.");
            result.Add(new(RequiredString(image, "contentType"), bytes));
        }
        return result;
    }

    private static string? OptionalString(JsonElement element, string name)
        => !element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null ? null
            : value.ValueKind == JsonValueKind.String ? value.GetString() : throw new FormatException($"The property '{name}' is not a string.");

    private static string RequiredString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new FormatException($"The property '{name}' is missing or not a string.");

    private static byte[] Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }
}

/// <summary>
/// The encrypted channel between an application and the relay after their handshake, which on an iPhone crosses the local
/// network. Each direction has a key of its own, derived with HKDF-SHA256 from the token of the session and the nonces of both
/// sides, and each message is sealed with AES-GCM under a counter of its direction as the nonce: a message that another machine
/// changed, replays, reorders, or writes cannot be opened, and the content of the messages, such as screenshots, cannot be read.
/// </summary>
internal sealed class NeoMcpChannel : IDisposable
{
    private const int TagSize = 16;
    private readonly AesGcm _sending;
    private readonly AesGcm _receiving;
    private readonly byte[] _sendingLabel;
    private readonly byte[] _receivingLabel;
    private ulong _sent;
    private ulong _received;

    private NeoMcpChannel(byte[] sendingKey, byte[] receivingKey, string sendingRole, string receivingRole)
    {
        _sending = new AesGcm(sendingKey, TagSize);
        _receiving = new AesGcm(receivingKey, TagSize);
        _sendingLabel = Encoding.ASCII.GetBytes(sendingRole);
        _receivingLabel = Encoding.ASCII.GetBytes(receivingRole);
        CryptographicOperations.ZeroMemory(sendingKey);
        CryptographicOperations.ZeroMemory(receivingKey);
    }

    /// <summary>Creates the channel of one side once the handshake proved the token both ways.</summary>
    /// <param name="token">The token of the session.</param>
    /// <param name="relayNonce">The nonce of the challenge of the relay.</param>
    /// <param name="applicationNonce">The nonce of the hello of the application.</param>
    /// <param name="isRelay">Whether the channel is the one of the relay.</param>
    internal static NeoMcpChannel Create(string token, string relayNonce, string applicationNonce, bool isRelay)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        var secret = Encoding.UTF8.GetBytes(token);
        var salt = Encoding.ASCII.GetBytes(relayNonce + "." + applicationNonce);
        var toApplication = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt, Encoding.ASCII.GetBytes("neoastra-mcp-1 " + NeoMcpWire.RelayRole));
        var toRelay = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt, Encoding.ASCII.GetBytes("neoastra-mcp-1 " + NeoMcpWire.ApplicationRole));
        CryptographicOperations.ZeroMemory(secret);
        return isRelay
            ? new(toApplication, toRelay, NeoMcpWire.RelayRole, NeoMcpWire.ApplicationRole)
            : new(toRelay, toApplication, NeoMcpWire.ApplicationRole, NeoMcpWire.RelayRole);
    }

    /// <summary>Seals the next message of this side; the caller sends the sealed messages in the order it sealed them.</summary>
    internal byte[] Seal(ReadOnlySpan<byte> message)
    {
        var sealedMessage = new byte[message.Length + TagSize];
        Span<byte> nonce = stackalloc byte[12];
        WriteCounter(nonce, checked(_sent++));
        _sending.Encrypt(nonce, message, sealedMessage.AsSpan(0, message.Length), sealedMessage.AsSpan(message.Length), _sendingLabel);
        return sealedMessage;
    }

    /// <summary>Opens the next message of the other side.</summary>
    /// <exception cref="FormatException">The message is not the next one that the other side sealed.</exception>
    internal byte[] Open(ReadOnlySpan<byte> sealedMessage)
    {
        if (sealedMessage.Length < TagSize) throw new FormatException("A sealed message is shorter than its tag.");
        var message = new byte[sealedMessage.Length - TagSize];
        Span<byte> nonce = stackalloc byte[12];
        WriteCounter(nonce, _received);
        try { _receiving.Decrypt(nonce, sealedMessage[..^TagSize], sealedMessage[^TagSize..], message, _receivingLabel); }
        catch (AuthenticationTagMismatchException exception) { throw new FormatException("A message of the connection was changed, replayed, or not sealed by the other side.", exception); }
        _received = checked(_received + 1);
        return message;
    }

    public void Dispose()
    {
        _sending.Dispose();
        _receiving.Dispose();
    }

    private static void WriteCounter(Span<byte> nonce, ulong counter)
    {
        nonce.Clear();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], counter);
    }
}
