// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NeoAstra;

internal enum NeoTransportReceiveKind
{
    Legacy,
    Consumed,
    Application,
}

// An application frame lies in the message at FrameStart, for FrameLength bytes.
internal readonly record struct NeoTransportReceiveResult(NeoTransportReceiveKind Kind, int FrameStart = 0, int FrameLength = 0);

internal enum NeoTransportEnvelopeKind
{
    // Not an envelope of the transport: a message of the application.
    Legacy,
    // An envelope dropped without a diagnostic.
    Dropped,
    InvalidJson,
    // An envelope that fails its host view binding or its shape.
    Invalid,
    Valid,
}

internal enum NeoTransportFrameKind
{
    // The discriminator or the kind of the frame is missing or invalid.
    Invalid,
    Hello,
    Close,
    HelloAck,
    Diagnostic,
    Application,
}

internal readonly record struct NeoTransportEnvelope(
    NeoTransportEnvelopeKind Kind,
    string? RendererDocumentId = null,
    NeoTransportFrameKind FrameKind = NeoTransportFrameKind.Invalid,
    int FrameStart = 0,
    int FrameLength = 0);

internal sealed class NeoTransportCoordinator
{
    internal const int ProtocolMajor = 1;
    internal const int ProtocolMinor = 0;
    private static readonly string[] SupportedFeatures = ["invoke", "cancel", "events"];

    private readonly NeoAstraOptions _viewOptions;
    private readonly Action<string> _sendRaw;
    private readonly Action<NeoTransportDiagnosticEventArgs> _diagnose;
    private readonly NeoDispatcher? _dispatcher;
    private readonly Func<string> _idFactory;
    private readonly Action<NeoTransportSessionSnapshot?>? _sessionChanged;
    private readonly Queue<NeoTransportDiagnosticEventArgs> _diagnostics = [];
    // Renderer document IDs are untrusted correlation values. Retaining closed values for the view lifetime
    // prevents a previous document's queued frames from being rebound to the current navigation.
    private readonly HashSet<string> _closedRendererDocumentIdSet = [];
    private readonly string _platform;
    private readonly string _backend;
    private PendingHello? _pendingHello;
    private ActiveSession? _active;
    // Whether the native library raises NEOASTRA_EVENT_NAVIGATION_COMMITTED; without it, the replacement of a document
    // is inferred from the hello of another document during a navigation, or from the successful end of the navigation.
    private readonly bool _reportsCommit;
    private bool _navigationPending;
    private bool _replacementSeen;
    private bool _revokedUntilNavigation;
    private bool _closed;
    private int _handshakeAttempts;
    private long _navigationGeneration;

    internal NeoTransportCoordinator(
        NeoAstraOptions viewOptions,
        NeoRuntimeInfo runtimeInfo,
        Action<string> sendRaw,
        Action<NeoTransportDiagnosticEventArgs> diagnose,
        NeoDispatcher? dispatcher = null,
        Func<string>? idFactory = null,
        Action<NeoTransportSessionSnapshot?>? sessionChanged = null)
    {
        _viewOptions = new NeoAstraOptions
        {
            ViewLabel = viewOptions.ViewLabel,
            BridgePolicy = viewOptions.BridgePolicy,
            MaximumMessageSize = viewOptions.MaximumMessageSize,
            Transport = new NeoTransportOptions
            {
                MaximumJsonDepth = viewOptions.Transport.MaximumJsonDepth,
                MaximumHandshakeAttempts = viewOptions.Transport.MaximumHandshakeAttempts,
                MaximumDiagnosticQueue = viewOptions.Transport.MaximumDiagnosticQueue,
                HandshakeTimeout = viewOptions.Transport.HandshakeTimeout,
            },
        };
        _sendRaw = sendRaw;
        _diagnose = diagnose;
        _dispatcher = dispatcher;
        _idFactory = idFactory ?? (() => Guid.NewGuid().ToString("N"));
        _sessionChanged = sessionChanged;
        HostViewBinding = RandomNumberGenerator.GetHexString(32).ToLowerInvariant();
        (_platform, _backend) = GetPlatformMetadata(runtimeInfo);
        _reportsCommit = runtimeInfo.ReportsNavigationCommit;
    }

    // This host-generated value is injected into the isolated bootstrap and never exposed by the
    // public client API. It admits envelopes to this view; DocumentSessionId is the host-issued,
    // navigation-lifetime identity created only after the current navigation's hello is accepted.
    internal string HostViewBinding { get; }

    internal bool IsConnected => _active is not null && !_closed;

    internal NeoTransportSessionSnapshot? CurrentSession => _active is { } active
        ? new(active.DocumentSessionId, active.ProtocolMinor, active.Features, _viewOptions.BridgePolicy == NeoBridgePolicy.TrustEntireView)
        : null;

    internal string CreateBootstrapScript(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source
            .Replace("__NEOASTRA_HOST_VIEW_BINDING__", JavaScriptEncoder.Default.Encode(HostViewBinding), StringComparison.Ordinal)
            .Replace("__NEOASTRA_MAXIMUM_FRAME_BYTES__", _viewOptions.MaximumMessageSize.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__NEOASTRA_MAXIMUM_DIAGNOSTIC_QUEUE__", _viewOptions.Transport.MaximumDiagnosticQueue.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__NEOASTRA_HANDSHAKE_TIMEOUT_MILLISECONDS__", ((long)_viewOptions.Transport.HandshakeTimeout.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__NEOASTRA_PLATFORM__", _platform, StringComparison.Ordinal)
            .Replace("__NEOASTRA_BACKEND__", _backend, StringComparison.Ordinal)
            .Replace("__NEOASTRA_VIEW_LABEL__", JavaScriptEncoder.Default.Encode(_viewOptions.ViewLabel!), StringComparison.Ordinal)
            .Replace("__NEOASTRA_WHOLE_VIEW_TRUST__", _viewOptions.BridgePolicy == NeoBridgePolicy.TrustEntireView ? "true" : "false", StringComparison.Ordinal);
    }

    // A navigation that started may never replace the document: a download, an answer without content, Stop(), or a
    // newer navigation. The document keeps running and keeps its session until a navigation commits a new document.
    internal void NavigationStarted()
    {
        if (_closed) return;
        // A hello held for the navigation this one supersedes cannot bind to it.
        if (_pendingHello is { } pending) RememberClosedRendererDocumentId(pending.RendererDocumentId);
        _pendingHello = null;
        _navigationPending = true;
        _replacementSeen = false;
        _handshakeAttempts = 0;
        _navigationGeneration++;
    }

    // The navigation committed a new document: the old one is gone. Its session closes without a close frame, which
    // would reach the new document, and the new document handshakes at once, without waiting for its load to finish.
    internal void NavigationCommitted()
    {
        if (_closed) return;
        ReplaceDocument();
        // The attempts of the old document since the navigation started do not count against the new one.
        _handshakeAttempts = 0;
    }

    internal void NavigationCompleted(bool succeeded)
    {
        if (_closed) return;
        var started = _navigationPending;
        _navigationPending = false;
        // A native library that does not report the commit: a navigation that loaded a document replaced the old one,
        // unless the hello of the new document already told it.
        if (!_reportsCommit && started && succeeded && !_replacementSeen) ReplaceDocument();
        _replacementSeen = false;
        // The document that sent a held hello is the one that runs now, whether the navigation committed it or failed
        // before replacing it.
        if (_pendingHello is { } held)
        {
            _pendingHello = null;
            AcceptHello(held);
        }
    }

    private void ReplaceDocument()
    {
        CloseActive("navigation", sendClose: false);
        _replacementSeen = true;
        _revokedUntilNavigation = false;
    }

    // For callers that have a string, such as tests: the page gives UTF-8.
    internal NeoTransportReceiveResult Receive(string json)
    {
        if (_closed) return new(NeoTransportReceiveKind.Consumed);
        ArgumentNullException.ThrowIfNull(json);
        return Receive(Encoding.UTF8.GetBytes(json));
    }

    // A message of the page, as the UTF-8 the native library gives, which must be valid UTF-8: one forward-only pass of a
    // reader authenticates its envelope, checks its depth and its JSON, and finds where its frame lies, without building a
    // document or copying anything. Only a hello is parsed further, to negotiate the session. An application frame is
    // given back as where it lies in the message (FrameStart, FrameLength), to be parsed once by its consumer.
    internal NeoTransportReceiveResult Receive(ReadOnlySpan<byte> utf8)
    {
        if (_closed) return new(NeoTransportReceiveKind.Consumed);
        if ((ulong)utf8.Length > _viewOptions.MaximumMessageSize)
        {
            AddDiagnostic(NeoTransportDiagnosticLevel.Warning, "payload_too_large", "A transport frame exceeded the configured byte limit.");
            return new(NeoTransportReceiveKind.Consumed);
        }

        var envelope = ReadEnvelope(utf8, _viewOptions.Transport.MaximumJsonDepth, HostViewBinding, _active?.RendererDocumentId);
        switch (envelope.Kind)
        {
            case NeoTransportEnvelopeKind.InvalidJson:
                AddDiagnostic(NeoTransportDiagnosticLevel.Warning, "invalid_frame", "A transport message contained invalid or excessively deep JSON.");
                return LooksLikeTransport(utf8) ? new(NeoTransportReceiveKind.Consumed) : new(NeoTransportReceiveKind.Legacy);
            case NeoTransportEnvelopeKind.Legacy:
                return new(NeoTransportReceiveKind.Legacy);
            case NeoTransportEnvelopeKind.Dropped:
                return new(NeoTransportReceiveKind.Consumed);
            case NeoTransportEnvelopeKind.Invalid:
                AddDiagnostic(NeoTransportDiagnosticLevel.Warning, "invalid_frame", "A transport envelope failed its host view binding or shape validation.");
                return new(NeoTransportReceiveKind.Consumed);
        }

        var rendererDocumentId = envelope.RendererDocumentId!;
        if (_closedRendererDocumentIdSet.Contains(rendererDocumentId))
        {
            AddDiagnostic(NeoTransportDiagnosticLevel.Debug, "late_frame", "A late frame for a closed document session was ignored.");
            return new(NeoTransportReceiveKind.Consumed);
        }
        if (envelope.FrameKind == NeoTransportFrameKind.Invalid)
        {
            AddDiagnostic(NeoTransportDiagnosticLevel.Warning, "invalid_frame", "A transport frame has an invalid discriminator or kind.");
            return new(NeoTransportReceiveKind.Consumed);
        }

        var frame = utf8.Slice(envelope.FrameStart, envelope.FrameLength);
        if (envelope.FrameKind == NeoTransportFrameKind.Hello)
        {
            // The frame is valid JSON within the depth limit already.
            var reader = new Utf8JsonReader(frame, new JsonReaderOptions { MaxDepth = _viewOptions.Transport.MaximumJsonDepth });
            using var hello = JsonDocument.ParseValue(ref reader);
            HandleHello(rendererDocumentId, hello.RootElement);
            return new(NeoTransportReceiveKind.Consumed);
        }
        if (envelope.FrameKind == NeoTransportFrameKind.Close)
        {
            if (_active?.RendererDocumentId == rendererDocumentId) CloseActive("client_close", sendClose: false);
            return new(NeoTransportReceiveKind.Consumed);
        }
        if (_active is null)
        {
            AddDiagnostic(NeoTransportDiagnosticLevel.Warning, "invalid_frame", "An application frame was rejected before transport handshake completion.");
            return new(NeoTransportReceiveKind.Consumed);
        }
        if (!string.Equals(_active.RendererDocumentId, rendererDocumentId, StringComparison.Ordinal))
        {
            AddDiagnostic(NeoTransportDiagnosticLevel.Debug, "late_frame", "A frame for an inactive document session was ignored.");
            return new(NeoTransportReceiveKind.Consumed);
        }
        if (envelope.FrameKind is NeoTransportFrameKind.HelloAck or NeoTransportFrameKind.Diagnostic)
        {
            AddDiagnostic(NeoTransportDiagnosticLevel.Warning, "invalid_frame", "A client used a host-reserved transport frame kind.");
            return new(NeoTransportReceiveKind.Consumed);
        }

        return new(NeoTransportReceiveKind.Application, envelope.FrameStart, envelope.FrameLength);
    }

    // What the envelope of a message says, read as JsonDocument read it: the whole message must be valid JSON within the
    // depth limit, a property given several times counts by its last occurrence, and names and strings compare unescaped.
    // A message whose marker is a number but not an Int32 is dropped without a word, as JsonElement.GetInt32 threw for it.
    // The renderer document ID is decoded only when it may be valid (at most 128 UTF-16 characters, which no more than six
    // bytes each encode), and is not decoded at all when it is the known one, the ID of the active document. The kind of the
    // frame is compared, not decoded.
    internal static NeoTransportEnvelope ReadEnvelope(ReadOnlySpan<byte> utf8, int maximumDepth, string hostViewBinding, string? knownRendererDocumentId = null)
    {
        var marker = Marker.Missing;
        var binding = false;
        string? rendererDocumentId = null;
        var frameStart = -1;
        var frameLength = 0;
        var frameKind = NeoTransportFrameKind.Invalid;
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = maximumDepth });
        try
        {
            reader.Read();
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                while (reader.Read()) { }
                return new(NeoTransportEnvelopeKind.Legacy);
            }
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("__neoastraTransport"u8))
                {
                    reader.Read();
                    marker = reader.TokenType != JsonTokenType.Number ? Marker.NotNumber
                        : !reader.TryGetInt32(out var value) ? Marker.NotInt32
                        : value == 1 ? Marker.One : Marker.Other;
                }
                else if (reader.ValueTextEquals("hostViewBinding"u8))
                {
                    reader.Read();
                    binding = reader.TokenType == JsonTokenType.String && reader.ValueTextEquals(hostViewBinding);
                }
                else if (reader.ValueTextEquals("rendererDocumentId"u8))
                {
                    reader.Read();
                    rendererDocumentId = reader.TokenType != JsonTokenType.String || reader.ValueSpan.Length > MaximumRendererDocumentIdLength * 6 ? null
                        : knownRendererDocumentId is not null && reader.ValueTextEquals(knownRendererDocumentId) ? knownRendererDocumentId
                        : reader.GetString() is { Length: > 0 and <= MaximumRendererDocumentIdLength } value ? value : null;
                }
                else if (reader.ValueTextEquals("frame"u8))
                {
                    reader.Read();
                    frameStart = -1;
                    if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        frameStart = checked((int)reader.TokenStartIndex);
                        frameKind = ReadFrame(ref reader);
                        frameLength = checked((int)reader.BytesConsumed) - frameStart;
                    }
                }
                else reader.Read();
                reader.Skip();
            }
            while (reader.Read()) { }
        }
        // GetString and ValueTextEquals refuse bytes that are not UTF-8, which the caller is to have repaired already.
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return new(NeoTransportEnvelopeKind.InvalidJson);
        }
        return marker switch
        {
            Marker.Missing or Marker.NotNumber or Marker.Other => new(NeoTransportEnvelopeKind.Legacy),
            Marker.NotInt32 => new(NeoTransportEnvelopeKind.Dropped),
            _ when !binding || rendererDocumentId is null || frameStart < 0 => new(NeoTransportEnvelopeKind.Invalid),
            _ => new(NeoTransportEnvelopeKind.Valid, rendererDocumentId, frameKind, frameStart, frameLength),
        };
    }

    // The discriminator and the kind of a frame, from its start to its end, its nested values skipped.
    private static NeoTransportFrameKind ReadFrame(ref Utf8JsonReader reader)
    {
        var discriminator = false;
        var kind = NeoTransportFrameKind.Invalid;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("neoastra"u8))
            {
                reader.Read();
                discriminator = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value) && value == 1;
            }
            else if (reader.ValueTextEquals("kind"u8))
            {
                reader.Read();
                kind = reader.TokenType != JsonTokenType.String ? NeoTransportFrameKind.Invalid
                    : reader.ValueTextEquals("hello"u8) ? NeoTransportFrameKind.Hello
                    : reader.ValueTextEquals("close"u8) ? NeoTransportFrameKind.Close
                    : reader.ValueTextEquals("hello_ack"u8) ? NeoTransportFrameKind.HelloAck
                    : reader.ValueTextEquals("diagnostic"u8) ? NeoTransportFrameKind.Diagnostic
                    : NeoTransportFrameKind.Application;
            }
            else reader.Read();
            reader.Skip();
        }
        return discriminator ? kind : NeoTransportFrameKind.Invalid;
    }

    private const int MaximumRendererDocumentIdLength = 128;

    private enum Marker { Missing, NotNumber, NotInt32, One, Other }

    // A frame NeoAstra wrote itself (an RPC answer, event or channel item) is a valid JSON object already: only its
    // envelope is written, on the thread that owns the transport state, around the frame where it lies, without parsing
    // or copying it. It is wrapped only for the document session that wrote it, while that session is the active one: the
    // frame of a session that a navigation closed would otherwise reach the next document, whose channel IDs restart, or
    // its page listeners.
    internal NeoTransportOutboundStatus WrapOutboundTrusted(string documentSessionId, NeoTransportFrame frame, out ReadOnlyMemory<byte> envelope)
    {
        envelope = default;
        if (_closed || _active is not { } active || !string.Equals(active.DocumentSessionId, documentSessionId, StringComparison.Ordinal))
        {
            return NeoTransportOutboundStatus.SessionClosed;
        }
        if (!Fits(active, frame)) return NeoTransportOutboundStatus.TooLarge;
        envelope = frame.Wrap(active.EnvelopePrefix);
        return NeoTransportOutboundStatus.Wrapped;
    }

    // The JSON an application posts (NeoAstra.PostMessageAsync), validated in one pass: a frame of the active session must
    // be an object no deeper than the transport accepts, and is wrapped in its envelope; without a session it is posted as
    // it is. JsonDocument.Parse took it no deeper than 64 levels in both cases. JSON it refuses throws JsonException, and an
    // envelope over the configured message size ArgumentException.
    internal ReadOnlyMemory<byte> WrapOutbound(NeoTransportFrame frame)
    {
        var active = _closed ? null : _active;
        ValidateJson(frame.Span, active is null ? DefaultMaximumJsonDepth : Math.Min(DefaultMaximumJsonDepth, _viewOptions.Transport.MaximumJsonDepth), requireObject: active is not null);
        if (active is null) return frame.Memory;
        if (!Fits(active, frame)) throw new ArgumentException("The transport envelope exceeds the configured message size.", "json");
        return frame.Wrap(active.EnvelopePrefix);
    }

    // What JsonDocument.Parse accepts, without building the document; a JsonException otherwise. Without a transport, a view
    // posts what this allows.
    internal static void ValidateJson(ReadOnlySpan<byte> utf8, int maximumDepth = DefaultMaximumJsonDepth, bool requireObject = false)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = maximumDepth });
        reader.Read();
        if (requireObject && reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Transport frames must be JSON objects.");
        while (reader.Read()) { }
    }

    private const int DefaultMaximumJsonDepth = 64;

    private bool Fits(ActiveSession active, NeoTransportFrame frame)
        => (long)active.EnvelopePrefix.Length + frame.Length + 1 <= _viewOptions.MaximumMessageSize;

    // Revokes the active session on the host's initiative, for example when the RPC session bound to it
    // closed. The document is told through a close frame so its client fails fast instead of waiting on a
    // host that no longer answers. Nothing may handshake again until a navigation replaces the document: only
    // the new document, which goes through the normal handshake, establishes a replacement session.
    internal bool CloseSession(string documentSessionId, string reason)
    {
        ArgumentNullException.ThrowIfNull(documentSessionId);
        if (_closed || _active is not { } active || !string.Equals(active.DocumentSessionId, documentSessionId, StringComparison.Ordinal)) return false;
        CloseActive(reason, sendClose: true);
        _revokedUntilNavigation = true;
        return true;
    }

    internal void Close(string reason)
    {
        if (_closed) return;
        CloseActive(reason, sendClose: true);
        if (_pendingHello is { } pending) RememberClosedRendererDocumentId(pending.RendererDocumentId);
        _pendingHello = null;
        _closed = true;
    }

    // The renderer of the main document is gone with the document: its session ends, and nothing is posted to it. Unlike
    // Close, the view stays usable, so that the document of a reload or a navigation handshakes as after any navigation.
    internal void RendererLost()
    {
        if (_closed) return;
        CloseActive("renderer_lost", sendClose: false);
        if (_pendingHello is { } pending) RememberClosedRendererDocumentId(pending.RendererDocumentId);
        _pendingHello = null;
        _navigationPending = false;
        _replacementSeen = false;
        _revokedUntilNavigation = false;
        _handshakeAttempts = 0;
        _navigationGeneration++;
    }

    internal void ExpireHandshake(long generation)
    {
        // A held hello waits for the end of its navigation, which a slow load delays: it stays held and is answered then,
        // and the document keeps its renderer ID, so that a hello it sends again is also answered.
        if (_closed || generation != _navigationGeneration || _active is not null || _pendingHello is null) return;
        AddDiagnostic(NeoTransportDiagnosticLevel.Warning, "handshake_timeout", "The transport handshake is still waiting for the navigation of its document to end.");
    }

    private void HandleHello(string rendererDocumentId, JsonElement frame)
    {
        _handshakeAttempts++;
        if (_handshakeAttempts > _viewOptions.Transport.MaximumHandshakeAttempts)
        {
            AddDiagnostic(NeoTransportDiagnosticLevel.Warning, "invalid_frame", "The document exceeded the maximum transport handshake attempts.");
            SendRejected(rendererDocumentId, "invalid_frame");
            if (_active?.RendererDocumentId == rendererDocumentId) CloseActive("handshake_attempt_limit", sendClose: false);
            else RememberClosedRendererDocumentId(rendererDocumentId);
            if (_pendingHello?.RendererDocumentId == rendererDocumentId) _pendingHello = null;
            return;
        }
        // Without a reported commit, the hello of another document while a navigation is under way is the sign that the
        // navigation replaced the document. With it, a hello before the commit comes from the document that still runs.
        var inferredReplacement = !_reportsCommit && _navigationPending;
        if (_revokedUntilNavigation && !inferredReplacement)
        {
            AddDiagnostic(NeoTransportDiagnosticLevel.Warning, "connection_closed", "A hello was rejected because the host closed this document's session.");
            SendRejected(rendererDocumentId, "connection_closed");
            RememberClosedRendererDocumentId(rendererDocumentId);
            return;
        }
        if (_active is { } active)
        {
            if (string.Equals(active.RendererDocumentId, rendererDocumentId, StringComparison.Ordinal))
            {
                SendHelloAck(active);
                return;
            }
            if (!inferredReplacement)
            {
                AddDiagnostic(NeoTransportDiagnosticLevel.Debug, "late_frame", "A duplicate hello for an inactive document was ignored.");
                return;
            }
        }
        if (!TryParseHello(rendererDocumentId, frame, out var hello, out var errorCode))
        {
            AddDiagnostic(NeoTransportDiagnosticLevel.Warning, errorCode, "The transport hello frame was rejected.");
            SendRejected(rendererDocumentId, errorCode);
            RememberClosedRendererDocumentId(rendererDocumentId);
            return;
        }
        if (inferredReplacement)
        {
            if (_active is not null || _revokedUntilNavigation) ReplaceDocument();
            // Which document sent it is known only once the navigation ends.
            var waiting = _pendingHello is not null;
            _pendingHello = hello;
            if (!waiting) ScheduleHandshakeTimeout(_navigationGeneration);
            return;
        }
        AcceptHello(hello);
    }

    private bool TryParseHello(string rendererDocumentId, JsonElement frame, out PendingHello hello, out string errorCode)
    {
        hello = default;
        errorCode = "invalid_frame";
        if (!frame.TryGetProperty("protocol", out var protocol) || protocol.ValueKind != JsonValueKind.Object ||
            !protocol.TryGetProperty("major", out var majorValue) || !majorValue.TryGetInt32(out var major) ||
            !protocol.TryGetProperty("minor", out var minorValue) || !minorValue.TryGetInt32(out var minor) || minor < 0 ||
            !frame.TryGetProperty("features", out var featuresValue) || featuresValue.ValueKind != JsonValueKind.Array ||
            !frame.TryGetProperty("client", out var client) || client.ValueKind != JsonValueKind.Object ||
            !client.TryGetProperty("name", out var clientName) || clientName.ValueKind != JsonValueKind.String ||
            !client.TryGetProperty("version", out var clientVersion) || clientVersion.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        if (major != ProtocolMajor)
        {
            errorCode = "protocol_mismatch";
            return false;
        }
        var requested = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in featuresValue.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= 64 } feature || featuresValue.GetArrayLength() > 32) return false;
            requested.Add(feature);
        }
        var selected = SupportedFeatures.Where(requested.Contains).ToArray();
        hello = new PendingHello(rendererDocumentId, _navigationGeneration, Math.Min(minor, ProtocolMinor), selected);
        return true;
    }

    private void AcceptHello(PendingHello hello)
    {
        if (_closed || hello.NavigationGeneration != _navigationGeneration || _closedRendererDocumentIdSet.Contains(hello.RendererDocumentId)) return;
        _active = new ActiveSession(hello.RendererDocumentId, _idFactory(), hello.ProtocolMinor, hello.Features, BuildEnvelopePrefix(hello.RendererDocumentId));
        _sessionChanged?.Invoke(new NeoTransportSessionSnapshot(_active.DocumentSessionId, _active.ProtocolMinor, _active.Features, _viewOptions.BridgePolicy == NeoBridgePolicy.TrustEntireView));
        SendHelloAck(_active);
    }

    private void SendHelloAck(ActiveSession session)
    {
        var frame = BuildJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("neoastra", 1);
            writer.WriteString("kind", "hello_ack");
            writer.WritePropertyName("protocol");
            writer.WriteStartObject(); writer.WriteNumber("major", ProtocolMajor); writer.WriteNumber("minor", session.ProtocolMinor); writer.WriteEndObject();
            writer.WritePropertyName("features"); writer.WriteStartArray(); foreach (var feature in session.Features) writer.WriteStringValue(feature); writer.WriteEndArray();
            writer.WritePropertyName("runtime");
            writer.WriteStartObject();
            writer.WriteString("viewLabel", _viewOptions.ViewLabel);
            writer.WriteString("documentSessionId", session.DocumentSessionId);
            writer.WriteString("platform", _platform);
            writer.WriteString("backend", _backend);
            writer.WriteBoolean("wholeViewTrust", _viewOptions.BridgePolicy == NeoBridgePolicy.TrustEntireView);
            writer.WriteEndObject();
            writer.WritePropertyName("limits");
            writer.WriteStartObject();
            writer.WriteNumber("maximumFrameBytes", _viewOptions.MaximumMessageSize);
            writer.WriteNumber("maximumJsonDepth", _viewOptions.Transport.MaximumJsonDepth);
            writer.WriteNumber("maximumHandshakeAttempts", _viewOptions.Transport.MaximumHandshakeAttempts);
            writer.WriteNumber("maximumPreHandshakeFrames", 0);
            writer.WriteNumber("maximumDiagnosticQueue", _viewOptions.Transport.MaximumDiagnosticQueue);
            writer.WriteNumber("handshakeTimeoutMilliseconds", (long)_viewOptions.Transport.HandshakeTimeout.TotalMilliseconds);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
        TrySend(BuildEnvelope(session.RendererDocumentId, writer => writer.WriteRawValue(frame, skipInputValidation: true)));
    }

    private void SendRejected(string rendererDocumentId, string code)
    {
        var frame = BuildJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("neoastra", 1);
            writer.WriteString("kind", "close");
            writer.WriteString("code", code);
            writer.WriteEndObject();
        });
        TrySend(BuildEnvelope(rendererDocumentId, writer => writer.WriteRawValue(frame, skipInputValidation: true)));
    }

    private void CloseActive(string reason, bool sendClose)
    {
        if (_active is not { } active) return;
        if (sendClose)
        {
            var frame = BuildJson(writer =>
            {
                writer.WriteStartObject(); writer.WriteNumber("neoastra", 1); writer.WriteString("kind", "close"); writer.WriteString("reason", reason); writer.WriteEndObject();
            });
            TrySend(BuildEnvelope(active.RendererDocumentId, writer => writer.WriteRawValue(frame, skipInputValidation: true)));
        }
        RememberClosedRendererDocumentId(active.RendererDocumentId);
        _active = null;
        _sessionChanged?.Invoke(null);
    }

    private string BuildEnvelope(string rendererDocumentId, Action<Utf8JsonWriter> writeFrame)
        => BuildJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("__neoastraTransport", 1);
            writer.WriteString("hostViewBinding", HostViewBinding);
            writer.WriteString("rendererDocumentId", rendererDocumentId);
            writer.WritePropertyName("frame");
            writeFrame(writer);
            writer.WriteEndObject();
        });

    // {"__neoastraTransport":1,"hostViewBinding":"…","rendererDocumentId":"…","frame": written by the writer of BuildEnvelope,
    // with its escaping: the prefix, the frame and a closing brace are the bytes BuildEnvelope writes for that frame.
    private byte[] BuildEnvelopePrefix(string rendererDocumentId)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(160);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("__neoastraTransport", 1);
            writer.WriteString("hostViewBinding", HostViewBinding);
            writer.WriteString("rendererDocumentId", rendererDocumentId);
            writer.WritePropertyName("frame");
            writer.Flush();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static string BuildJson(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) write(writer);
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    private void TrySend(string json)
    {
        try { _sendRaw(json); }
        catch { AddDiagnostic(NeoTransportDiagnosticLevel.Error, "internal_transport_error", "The host could not deliver a transport frame."); }
    }

    private void AddDiagnostic(NeoTransportDiagnosticLevel level, string code, string message)
    {
        var value = new NeoTransportDiagnosticEventArgs(level, code, message);
        if (_diagnostics.Count == _viewOptions.Transport.MaximumDiagnosticQueue) _diagnostics.Dequeue();
        _diagnostics.Enqueue(value);
        _diagnose(value);
    }

    private void RememberClosedRendererDocumentId(string value)
    {
        _closedRendererDocumentIdSet.Add(value);
    }

    private void ScheduleHandshakeTimeout(long generation)
    {
        if (_dispatcher is null) return;
        _ = ScheduleHandshakeTimeoutAsync(generation);
    }

    private async Task ScheduleHandshakeTimeoutAsync(long generation)
    {
        await Task.Delay(_viewOptions.Transport.HandshakeTimeout).ConfigureAwait(false);
        try { _dispatcher!.Post(() => ExpireHandshake(generation)); }
        catch (ObjectDisposedException) { }
    }

    private static bool LooksLikeTransport(ReadOnlySpan<byte> utf8) => utf8.IndexOf("__neoastraTransport"u8) >= 0;

    private static (string Platform, string Backend) GetPlatformMetadata(NeoRuntimeInfo runtimeInfo)
    {
        if (OperatingSystem.IsWindows()) return ("windows", "webview2");
        if (OperatingSystem.IsMacOS()) return ("macos", "wkwebview");
        if (OperatingSystem.IsLinux()) return ("linux", "webkitgtk");
        throw new PlatformNotSupportedException($"Unsupported NeoAstra transport backend '{runtimeInfo.BackendName}'.");
    }

    private readonly record struct PendingHello(string RendererDocumentId, long NavigationGeneration, int ProtocolMinor, string[] Features);
    // EnvelopePrefix is the envelope of the frames of the session up to its frame, as BuildEnvelope writes it.
    private sealed record ActiveSession(string RendererDocumentId, string DocumentSessionId, int ProtocolMinor, string[] Features, byte[] EnvelopePrefix);
}

internal enum NeoTransportOutboundStatus
{
    Wrapped,
    // The document session that wrote the frame is not the active one any more.
    SessionClosed,
    // The envelope of the frame exceeds NeoAstraOptions.MaximumMessageSize.
    TooLarge,
}

// What became of the frames of one batch of a document session: none was posted when its session is closed; otherwise
// every frame was posted but the rejected ones, given by their index in the batch, whose envelope exceeds the view limit.
internal readonly record struct NeoTrustedPostResult(bool SessionClosed, IReadOnlyList<int>? RejectedFrames);

internal readonly record struct NeoTransportSessionSnapshot(string DocumentSessionId, int ProtocolMinor, IReadOnlyList<string> Features, bool WholeViewTrust);
