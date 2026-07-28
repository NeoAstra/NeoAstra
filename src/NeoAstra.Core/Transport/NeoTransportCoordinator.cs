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

internal readonly record struct NeoTransportReceiveResult(NeoTransportReceiveKind Kind, string? ApplicationJson = null);

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
    private bool _navigationPending;
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

    internal void NavigationStarted()
    {
        if (_closed) return;
        CloseActive("navigation", sendClose: true);
        if (_pendingHello is { } pending) RememberClosedRendererDocumentId(pending.RendererDocumentId);
        _navigationPending = true;
        _pendingHello = null;
        _handshakeAttempts = 0;
        _navigationGeneration++;
    }

    internal void NavigationCompleted(bool succeeded)
    {
        if (_closed) return;
        _navigationPending = false;
        if (!succeeded)
        {
            if (_pendingHello is { } failed) RememberClosedRendererDocumentId(failed.RendererDocumentId);
            _pendingHello = null;
            return;
        }

        if (_pendingHello is { } pending)
        {
            _pendingHello = null;
            AcceptHello(pending);
        }
    }

    internal NeoTransportReceiveResult Receive(string json)
    {
        if (_closed) return new(NeoTransportReceiveKind.Consumed);
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > _viewOptions.MaximumMessageSize)
        {
            AddDiagnostic(NeoTransportDiagnosticLevel.Warning, "payload_too_large", "A transport frame exceeded the configured byte limit.");
            return new(NeoTransportReceiveKind.Consumed);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = _viewOptions.Transport.MaximumJsonDepth,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
        }
        catch (JsonException)
        {
            AddDiagnostic(NeoTransportDiagnosticLevel.Warning, "invalid_frame", "A transport message contained invalid or excessively deep JSON.");
            return LooksLikeTransport(json) ? new(NeoTransportReceiveKind.Consumed) : new(NeoTransportReceiveKind.Legacy);
        }

        using (document)
        {
            var root = document.RootElement;
            if (!TryReadEnvelope(root, out var rendererDocumentId, out var frame)) return new(NeoTransportReceiveKind.Legacy);
            if (frame.ValueKind != JsonValueKind.Object) return new(NeoTransportReceiveKind.Consumed);
            if (_closedRendererDocumentIdSet.Contains(rendererDocumentId))
            {
                AddDiagnostic(NeoTransportDiagnosticLevel.Debug, "late_frame", "A late frame for a closed document session was ignored.");
                return new(NeoTransportReceiveKind.Consumed);
            }

            if (!frame.TryGetProperty("neoastra", out var discriminator) || discriminator.ValueKind != JsonValueKind.Number ||
                !discriminator.TryGetInt32(out var discriminatorValue) || discriminatorValue != 1 ||
                !frame.TryGetProperty("kind", out var kindValue) || kindValue.ValueKind != JsonValueKind.String)
            {
                AddDiagnostic(NeoTransportDiagnosticLevel.Warning, "invalid_frame", "A transport frame has an invalid discriminator or kind.");
                return new(NeoTransportReceiveKind.Consumed);
            }

            var kind = kindValue.GetString()!;
            if (kind == "hello")
            {
                HandleHello(rendererDocumentId, frame);
                return new(NeoTransportReceiveKind.Consumed);
            }
            if (kind == "close")
            {
                if (_active?.RendererDocumentId == rendererDocumentId) CloseActive("client_close", sendClose: false);
                return new(NeoTransportReceiveKind.Consumed);
            }
            if (_active is null)
            {
                AddDiagnostic(NeoTransportDiagnosticLevel.Warning, "invalid_frame", "An application frame was rejected before transport handshake completion.");
                return new(NeoTransportReceiveKind.Consumed);
            }
            if (_active.NavigationGeneration != _navigationGeneration ||
                !string.Equals(_active.RendererDocumentId, rendererDocumentId, StringComparison.Ordinal))
            {
                AddDiagnostic(NeoTransportDiagnosticLevel.Debug, "late_frame", "A frame for an inactive document session was ignored.");
                return new(NeoTransportReceiveKind.Consumed);
            }
            if (kind is "hello_ack" or "diagnostic")
            {
                AddDiagnostic(NeoTransportDiagnosticLevel.Warning, "invalid_frame", "A client used a host-reserved transport frame kind.");
                return new(NeoTransportReceiveKind.Consumed);
            }

            return new(NeoTransportReceiveKind.Application, frame.GetRawText());
        }
    }

    internal string WrapOutbound(string json)
    {
        if (_active is null || _closed) return json;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = _viewOptions.Transport.MaximumJsonDepth });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Transport frames must be JSON objects.");
        }
        catch (JsonException)
        {
            throw;
        }
        var envelope = BuildEnvelope(_active.RendererDocumentId, writer => writer.WriteRawValue(json, skipInputValidation: true));
        if (Encoding.UTF8.GetByteCount(envelope) > _viewOptions.MaximumMessageSize)
        {
            throw new ArgumentException("The transport envelope exceeds the configured message size.", nameof(json));
        }
        return envelope;
    }

    internal void Close(string reason)
    {
        if (_closed) return;
        CloseActive(reason, sendClose: true);
        if (_pendingHello is { } pending) RememberClosedRendererDocumentId(pending.RendererDocumentId);
        _pendingHello = null;
        _closed = true;
    }

    internal void ExpireHandshake(long generation)
    {
        if (_closed || generation != _navigationGeneration || _active is not null || _pendingHello is null) return;
        RememberClosedRendererDocumentId(_pendingHello.Value.RendererDocumentId);
        _pendingHello = null;
        AddDiagnostic(NeoTransportDiagnosticLevel.Warning, "handshake_timeout", "The transport handshake timed out.");
    }

    private bool TryReadEnvelope(JsonElement root, out string rendererDocumentId, out JsonElement frame)
    {
        rendererDocumentId = string.Empty;
        frame = default;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("__neoastraTransport", out var marker) || marker.ValueKind != JsonValueKind.Number || marker.GetInt32() != 1) return false;
        if (!root.TryGetProperty("hostViewBinding", out var hostView) || hostView.ValueKind != JsonValueKind.String ||
            !string.Equals(hostView.GetString(), HostViewBinding, StringComparison.Ordinal) ||
            !root.TryGetProperty("rendererDocumentId", out var rendererDocumentValue) || rendererDocumentValue.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(rendererDocumentId = rendererDocumentValue.GetString()!) || rendererDocumentId.Length > 128 ||
            !root.TryGetProperty("frame", out frame) || frame.ValueKind != JsonValueKind.Object)
        {
            AddDiagnostic(NeoTransportDiagnosticLevel.Warning, "invalid_frame", "A transport envelope failed its host view binding or shape validation.");
            return true;
        }
        return true;
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
        if (_active is { } active)
        {
            if (string.Equals(active.RendererDocumentId, rendererDocumentId, StringComparison.Ordinal)) SendHelloAck(active);
            else AddDiagnostic(NeoTransportDiagnosticLevel.Debug, "late_frame", "A duplicate hello for an inactive document was ignored.");
            return;
        }
        if (!TryParseHello(rendererDocumentId, frame, out var hello, out var errorCode))
        {
            AddDiagnostic(NeoTransportDiagnosticLevel.Warning, errorCode, "The transport hello frame was rejected.");
            SendRejected(rendererDocumentId, errorCode);
            RememberClosedRendererDocumentId(rendererDocumentId);
            return;
        }
        if (_navigationPending)
        {
            _pendingHello = hello;
            ScheduleHandshakeTimeout(_navigationGeneration);
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
        _active = new ActiveSession(hello.RendererDocumentId, hello.NavigationGeneration, _idFactory(), hello.ProtocolMinor, hello.Features);
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

    private static bool LooksLikeTransport(string json) => json.Contains("__neoastraTransport", StringComparison.Ordinal);

    private static (string Platform, string Backend) GetPlatformMetadata(NeoRuntimeInfo runtimeInfo)
    {
        if (OperatingSystem.IsWindows()) return ("windows", "webview2");
        if (OperatingSystem.IsMacOS()) return ("macos", "wkwebview");
        if (OperatingSystem.IsLinux()) return ("linux", "webkitgtk");
        throw new PlatformNotSupportedException($"Unsupported NeoAstra transport backend '{runtimeInfo.BackendName}'.");
    }

    private readonly record struct PendingHello(string RendererDocumentId, long NavigationGeneration, int ProtocolMinor, string[] Features);
    private sealed record ActiveSession(string RendererDocumentId, long NavigationGeneration, string DocumentSessionId, int ProtocolMinor, string[] Features);
}

internal readonly record struct NeoTransportSessionSnapshot(string DocumentSessionId, int ProtocolMinor, IReadOnlyList<string> Features, bool WholeViewTrust);
