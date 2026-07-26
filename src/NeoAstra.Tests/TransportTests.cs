using System.Text.Json;

namespace NeoAstra.Tests;

[TestClass]
public sealed class TransportTests
{
    private static readonly NeoRuntimeInfo RuntimeInfo = new("webview2", "1", "1", "windows", "x64", 0, false);

    [TestMethod]
    public void HandshakeNegotiatesFeaturesAndDuplicateHelloReturnsSameSession()
    {
        var sent = new List<string>();
        var coordinator = CreateCoordinator(sent);
        coordinator.NavigationStarted();
        var hello = Envelope(coordinator, "document-a", Hello(features: ["invoke", "unknown"]));

        Assert.AreEqual(NeoTransportReceiveKind.Consumed, coordinator.Receive(hello).Kind);
        Assert.IsEmpty(sent);
        coordinator.NavigationCompleted(succeeded: true);
        Assert.HasCount(1, sent);
        using var first = JsonDocument.Parse(sent[0]);
        var firstFrame = first.RootElement.GetProperty("frame");
        Assert.AreEqual("hello_ack", firstFrame.GetProperty("kind").GetString());
        Assert.AreEqual("fixed-session", firstFrame.GetProperty("runtime").GetProperty("documentSessionId").GetString());
        Assert.AreNotEqual("document-a", firstFrame.GetProperty("runtime").GetProperty("documentSessionId").GetString());
        Assert.AreEqual(coordinator.HostViewBinding, first.RootElement.GetProperty("hostViewBinding").GetString());
        Assert.AreEqual("document-a", first.RootElement.GetProperty("rendererDocumentId").GetString());
        CollectionAssert.AreEqual(new[] { "invoke" }, firstFrame.GetProperty("features").EnumerateArray().Select(value => value.GetString()).ToArray());

        coordinator.Receive(hello);
        Assert.HasCount(2, sent);
        using var second = JsonDocument.Parse(sent[1]);
        Assert.AreEqual("fixed-session", second.RootElement.GetProperty("frame").GetProperty("runtime").GetProperty("documentSessionId").GetString());
    }

    [TestMethod]
    public void BootstrapReceivesHostCentralizedLimitsAndImmutableMetadata()
    {
        var options = Options();
        options.MaximumMessageSize = 4096;
        options.Transport.MaximumDiagnosticQueue = 17;
        options.Transport.HandshakeTimeout = TimeSpan.FromMilliseconds(2500);
        var coordinator = CreateCoordinator([], options: options);
        const string template = "__NEOASTRA_HOST_VIEW_BINDING__|__NEOASTRA_MAXIMUM_FRAME_BYTES__|__NEOASTRA_MAXIMUM_DIAGNOSTIC_QUEUE__|__NEOASTRA_HANDSHAKE_TIMEOUT_MILLISECONDS__|__NEOASTRA_PLATFORM__|__NEOASTRA_BACKEND__|__NEOASTRA_VIEW_LABEL__|__NEOASTRA_WHOLE_VIEW_TRUST__";

        var bootstrap = coordinator.CreateBootstrapScript(template);

        Assert.AreEqual($"{coordinator.HostViewBinding}|4096|17|2500|windows|webview2|main|true", bootstrap);
    }

    [TestMethod]
    public void ProtocolMismatchMalformedAndPreHandshakeFramesAreRejected()
    {
        var sent = new List<string>();
        var diagnostics = new List<NeoTransportDiagnosticEventArgs>();
        var coordinator = CreateCoordinator(sent, diagnostics);
        coordinator.NavigationStarted();
        coordinator.Receive(Envelope(coordinator, "document-a", "{\"neoastra\":1,\"kind\":\"invoke\"}"));
        coordinator.Receive(Envelope(coordinator, "document-a", Hello(major: 2)));
        coordinator.Receive("{\"__neoastraTransport\":1,");

        Assert.IsFalse(coordinator.IsConnected);
        Assert.HasCount(1, sent);
        using (var rejection = JsonDocument.Parse(sent[0]))
        {
            Assert.AreEqual("protocol_mismatch", rejection.RootElement.GetProperty("frame").GetProperty("code").GetString());
        }
        CollectionAssert.Contains(diagnostics.Select(value => value.Code).ToArray(), "invalid_frame");
        CollectionAssert.Contains(diagnostics.Select(value => value.Code).ToArray(), "protocol_mismatch");
    }

    [TestMethod]
    public void NavigationClosesOldSessionAndLateFramesCannotReachReplacement()
    {
        var sent = new List<string>();
        var diagnostics = new List<NeoTransportDiagnosticEventArgs>();
        var nextId = 0;
        var coordinator = CreateCoordinator(sent, diagnostics, () => $"session-{++nextId}");
        Connect(coordinator, "old-document");
        Assert.IsTrue(coordinator.IsConnected);

        coordinator.NavigationStarted();
        Assert.IsFalse(coordinator.IsConnected);
        coordinator.Receive(Envelope(coordinator, "old-document", "{\"neoastra\":1,\"kind\":\"invoke\"}"));
        coordinator.Receive(Envelope(coordinator, "old-document", Hello()));
        Assert.HasCount(2, sent);
        coordinator.Receive(Envelope(coordinator, "new-document", Hello()));
        coordinator.NavigationCompleted(succeeded: true);
        Assert.IsTrue(coordinator.IsConnected);

        coordinator.Receive(Envelope(coordinator, "old-document", Hello()));
        var replayedApplication = coordinator.Receive(Envelope(coordinator, "old-document", "{\"neoastra\":1,\"kind\":\"invoke\",\"id\":\"old\"}"));
        var application = coordinator.Receive(Envelope(coordinator, "new-document", "{\"neoastra\":1,\"kind\":\"invoke\",\"id\":\"new\"}"));
        Assert.AreEqual(NeoTransportReceiveKind.Consumed, replayedApplication.Kind);
        Assert.AreEqual(NeoTransportReceiveKind.Application, application.Kind);
        Assert.HasCount(3, sent);
        Assert.IsTrue(diagnostics.Any(value => value.Code == "late_frame"));
        using var last = JsonDocument.Parse(sent[^1]);
        Assert.AreEqual("session-2", last.RootElement.GetProperty("frame").GetProperty("runtime").GetProperty("documentSessionId").GetString());
    }

    [TestMethod]
    public void SupersededPendingDocumentHelloCannotBindToLaterNavigation()
    {
        var sent = new List<string>();
        var coordinator = CreateCoordinator(sent);
        coordinator.NavigationStarted();
        var supersededHello = Envelope(coordinator, "superseded-document", Hello());
        coordinator.Receive(supersededHello);

        coordinator.NavigationStarted();
        coordinator.Receive(supersededHello);
        coordinator.Receive(Envelope(coordinator, "current-document", Hello()));
        coordinator.NavigationCompleted(succeeded: true);

        Assert.IsTrue(coordinator.IsConnected);
        Assert.HasCount(1, sent);
        using var accepted = JsonDocument.Parse(sent[0]);
        Assert.AreEqual("current-document", accepted.RootElement.GetProperty("rendererDocumentId").GetString());
    }

    [TestMethod]
    public void LimitsTimeoutAndCloseAreDeterministic()
    {
        var options = Options();
        options.MaximumMessageSize = 512;
        options.Transport.MaximumJsonDepth = 4;
        options.Transport.MaximumHandshakeAttempts = 1;
        var sent = new List<string>();
        var diagnostics = new List<NeoTransportDiagnosticEventArgs>();
        var coordinator = CreateCoordinator(sent, diagnostics, options: options);
        coordinator.NavigationStarted();
        coordinator.Receive(Envelope(coordinator, "document-a", Hello()));
        coordinator.ExpireHandshake(1);
        Assert.IsTrue(diagnostics.Any(value => value.Code == "handshake_timeout"));

        coordinator.NavigationStarted();
        var oversized = "{\"__neoastraTransport\":1,\"padding\":\"" + new string('x', 600) + "\"}";
        coordinator.Receive(oversized);
        Assert.IsTrue(diagnostics.Any(value => value.Code == "payload_too_large"));
        var deep = Envelope(coordinator, "deep-document", "{\"neoastra\":1,\"kind\":\"invoke\",\"a\":{\"b\":{\"c\":{\"d\":1}}}}" );
        coordinator.Receive(deep);
        Assert.IsTrue(diagnostics.Any(value => value.Code == "invalid_frame"));

        coordinator.Close("view_disposed");
        Assert.IsFalse(coordinator.IsConnected);
        Assert.AreEqual(NeoTransportReceiveKind.Consumed, coordinator.Receive("{}").Kind);
    }

    [TestMethod]
    public void ExcessDuplicateHellosCloseTheAbusiveDocumentSession()
    {
        var options = Options();
        options.Transport.MaximumHandshakeAttempts = 1;
        var sent = new List<string>();
        var coordinator = CreateCoordinator(sent, options: options);
        Connect(coordinator, "document-a");

        coordinator.Receive(Envelope(coordinator, "document-a", Hello()));

        Assert.IsFalse(coordinator.IsConnected);
        Assert.HasCount(2, sent);
        using var rejection = JsonDocument.Parse(sent[^1]);
        Assert.AreEqual("close", rejection.RootElement.GetProperty("frame").GetProperty("kind").GetString());
        Assert.AreEqual("invalid_frame", rejection.RootElement.GetProperty("frame").GetProperty("code").GetString());
    }

    private static NeoTransportCoordinator CreateCoordinator(
        List<string> sent,
        List<NeoTransportDiagnosticEventArgs>? diagnostics = null,
        Func<string>? idFactory = null,
        NeoAstraOptions? options = null)
    {
        diagnostics ??= [];
        return new NeoTransportCoordinator(options ?? Options(), RuntimeInfo, sent.Add, diagnostics.Add, idFactory: idFactory ?? (() => "fixed-session"));
    }

    private static NeoAstraOptions Options() => new()
    {
        ViewLabel = "main",
        BridgePolicy = NeoBridgePolicy.TrustEntireView,
    };

    private static void Connect(NeoTransportCoordinator coordinator, string rendererDocumentId)
    {
        coordinator.NavigationStarted();
        coordinator.Receive(Envelope(coordinator, rendererDocumentId, Hello()));
        coordinator.NavigationCompleted(succeeded: true);
    }

    private static string Envelope(NeoTransportCoordinator coordinator, string rendererDocumentId, string frame)
        => $"{{\"__neoastraTransport\":1,\"hostViewBinding\":\"{coordinator.HostViewBinding}\",\"rendererDocumentId\":\"{rendererDocumentId}\",\"frame\":{frame}}}";

    private static string Hello(int major = 1, string[]? features = null)
    {
        features ??= ["invoke", "cancel", "events"];
        return $"{{\"neoastra\":1,\"kind\":\"hello\",\"protocol\":{{\"major\":{major},\"minor\":0}},\"features\":{JsonSerializer.Serialize(features)},\"client\":{{\"name\":\"@neoastra/client\",\"version\":\"0.1.0\"}}}}";
    }
}
