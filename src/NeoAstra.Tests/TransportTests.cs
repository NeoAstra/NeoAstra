using System.Text;
using System.Text.Json;

namespace NeoAstra.Tests;

[TestClass]
public sealed class TransportTests
{
    private static readonly NeoRuntimeInfo RuntimeInfo = new("webview2", "1", "1", "windows", "x64", 0, false);

    [TestMethod]
    public void DefaultTransportAcceptsFramesBeyondFormerHardLimitInBothDirections()
    {
        var coordinator = CreateCoordinator([]);
        Connect(coordinator, "large-document");
        var frame = "{\"neoastra\":1,\"kind\":\"invoke\",\"value\":\"" + new string('x', 17 * 1024 * 1024) + "\"}";
        var envelope = Envelope(coordinator, "large-document", frame);
        Assert.AreEqual(NeoTransportReceiveKind.Application, coordinator.Receive(envelope).Kind);
        Assert.AreEqual(envelope, WrapOutbound(coordinator, frame));
        Assert.AreEqual((uint)int.MaxValue, new NeoAstraOptions().MaximumMessageSize);
    }

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

        var (platform, backend) = OperatingSystem.IsWindows() ? ("windows", "webview2") : OperatingSystem.IsMacOS() ? ("macos", "wkwebview") : ("linux", "webkitgtk");
        Assert.AreEqual($"{coordinator.HostViewBinding}|4096|17|2500|{platform}|{backend}|main|true", bootstrap);
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
        // A native library that does not report the commit: the hello of another document tells that the old one is gone.
        var sent = new List<string>();
        var diagnostics = new List<NeoTransportDiagnosticEventArgs>();
        var nextId = 0;
        var coordinator = CreateCoordinator(sent, diagnostics, () => $"session-{++nextId}");
        Connect(coordinator, "old-document");
        Assert.IsTrue(coordinator.IsConnected);

        coordinator.NavigationStarted();
        Assert.IsTrue(coordinator.IsConnected, "The old document runs until the navigation replaces it.");
        Assert.AreEqual(NeoTransportReceiveKind.Application, coordinator.Receive(Envelope(coordinator, "old-document", "{\"neoastra\":1,\"kind\":\"invoke\"}")).Kind);
        coordinator.Receive(Envelope(coordinator, "new-document", Hello()));
        Assert.IsFalse(coordinator.IsConnected);
        Assert.HasCount(1, sent, "Nothing is posted for the replaced document, whose frames would reach the new one.");
        coordinator.Receive(Envelope(coordinator, "old-document", Hello()));
        coordinator.NavigationCompleted(succeeded: true);
        Assert.IsTrue(coordinator.IsConnected);

        coordinator.Receive(Envelope(coordinator, "old-document", Hello()));
        var replayedApplication = coordinator.Receive(Envelope(coordinator, "old-document", "{\"neoastra\":1,\"kind\":\"invoke\",\"id\":\"old\"}"));
        var application = coordinator.Receive(Envelope(coordinator, "new-document", "{\"neoastra\":1,\"kind\":\"invoke\",\"id\":\"new\"}"));
        Assert.AreEqual(NeoTransportReceiveKind.Consumed, replayedApplication.Kind);
        Assert.AreEqual(NeoTransportReceiveKind.Application, application.Kind);
        Assert.HasCount(2, sent);
        Assert.IsTrue(diagnostics.Any(value => value.Code == "late_frame"));
        using var last = JsonDocument.Parse(sent[^1]);
        Assert.AreEqual("session-2", last.RootElement.GetProperty("frame").GetProperty("runtime").GetProperty("documentSessionId").GetString());
    }

    [TestMethod]
    public void CommittedNavigationRotatesTheSessionAndTheNewDocumentNeedNotWaitForItsLoad()
    {
        var sent = new List<string>();
        var nextId = 0;
        var coordinator = CreateCoordinator(sent, idFactory: () => $"session-{++nextId}", runtimeInfo: CommitRuntimeInfo);
        ConnectCommitted(coordinator, "old-document");
        Assert.AreEqual("session-1", coordinator.CurrentSession!.Value.DocumentSessionId);

        coordinator.NavigationStarted();
        Assert.AreEqual(NeoTransportReceiveKind.Application, coordinator.Receive(Envelope(coordinator, "old-document", "{\"neoastra\":1,\"kind\":\"invoke\"}")).Kind);
        coordinator.NavigationCommitted();
        Assert.IsFalse(coordinator.IsConnected);
        Assert.HasCount(1, sent, "Nothing is posted for the replaced document, whose frames would reach the new one.");
        Assert.AreEqual(NeoTransportReceiveKind.Consumed, coordinator.Receive(Envelope(coordinator, "old-document", "{\"neoastra\":1,\"kind\":\"invoke\"}")).Kind);

        // The new document is answered at once, while it still loads.
        coordinator.Receive(Envelope(coordinator, "new-document", Hello()));
        Assert.HasCount(2, sent);
        Assert.AreEqual("session-2", coordinator.CurrentSession!.Value.DocumentSessionId);
        coordinator.NavigationCompleted(succeeded: true);
        coordinator.Receive(Envelope(coordinator, "old-document", Hello()));
        Assert.HasCount(2, sent);
        Assert.AreEqual("session-2", coordinator.CurrentSession!.Value.DocumentSessionId);
        Assert.AreEqual(NeoTransportReceiveKind.Application, coordinator.Receive(Envelope(coordinator, "new-document", "{\"neoastra\":1,\"kind\":\"invoke\"}")).Kind);
    }

    [TestMethod]
    public void NavigationThatDoesNotReplaceTheDocumentKeepsItsSession()
    {
        // A download, a 204 answer, Stop(), or a newer navigation: the started navigation never commits.
        foreach (var runtimeInfo in new[] { RuntimeInfo, CommitRuntimeInfo })
        {
            var sent = new List<string>();
            var sessions = new List<NeoTransportSessionSnapshot?>();
            var nextId = 0;
            var coordinator = new NeoTransportCoordinator(Options(), runtimeInfo, sent.Add, _ => { }, idFactory: () => $"session-{++nextId}", sessionChanged: sessions.Add);
            Connect(coordinator, "page");

            coordinator.NavigationStarted();
            coordinator.NavigationStarted();
            coordinator.NavigationCompleted(succeeded: false);

            Assert.IsTrue(coordinator.IsConnected);
            Assert.AreEqual("session-1", coordinator.CurrentSession!.Value.DocumentSessionId);
            Assert.HasCount(1, sessions, "The RPC session of the page was never closed.");
            Assert.HasCount(1, sent, "The page is not told to close.");
            Assert.AreEqual(NeoTransportReceiveKind.Application, coordinator.Receive(Envelope(coordinator, "page", "{\"neoastra\":1,\"kind\":\"invoke\"}")).Kind);
            coordinator.Receive(Envelope(coordinator, "page", Hello()));
            Assert.HasCount(2, sent);
            using var ack = JsonDocument.Parse(sent[^1]);
            Assert.AreEqual("session-1", ack.RootElement.GetProperty("frame").GetProperty("runtime").GetProperty("documentSessionId").GetString());
        }
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
    public void FramesOfASessionAreNeverPostedToAnotherDocument()
    {
        var sent = new List<string>();
        var nextId = 0;
        var coordinator = CreateCoordinator(sent, idFactory: () => $"session-{++nextId}", runtimeInfo: CommitRuntimeInfo);
        ConnectCommitted(coordinator, "document-a");
        ConnectCommitted(coordinator, "document-b");
        const string frame = "{\"neoastra\":1,\"kind\":\"result\",\"id\":\"1\",\"ok\":true}";
        var posted = new List<string>();

        // Session A wrote these frames before the navigation replaced its document.
        var stale = PostTrustedFrames(coordinator, "session-1", [frame, frame], posted);
        Assert.IsTrue(stale.SessionClosed);
        Assert.IsEmpty(posted);

        // Nor are they posted raw while no document has a session: the page of the view would see them.
        coordinator.NavigationStarted();
        coordinator.NavigationCommitted();
        Assert.IsTrue(PostTrustedFrames(coordinator, "session-2", [frame], posted).SessionClosed);
        Assert.IsEmpty(posted);

        coordinator.Receive(Envelope(coordinator, "document-c", Hello()));
        var current = PostTrustedFrames(coordinator, "session-3", [frame], posted);
        Assert.IsFalse(current.SessionClosed);
        Assert.HasCount(1, posted);
        using var envelope = JsonDocument.Parse(posted[0]);
        Assert.AreEqual("document-c", envelope.RootElement.GetProperty("rendererDocumentId").GetString());
        Assert.AreEqual("1", envelope.RootElement.GetProperty("frame").GetProperty("id").GetString());
    }

    [TestMethod]
    public void AFrameOverTheViewLimitFailsAloneAndTheRestOfItsBatchIsPosted()
    {
        var options = Options();
        options.MaximumMessageSize = 512;
        var sent = new List<string>();
        var coordinator = CreateCoordinator(sent, options: options);
        Connect(coordinator, "document");
        var posted = new List<string>();
        string Frame(string id, int padding) => $"{{\"neoastra\":1,\"kind\":\"result\",\"id\":\"{id}\",\"ok\":true,\"value\":\"{new string('x', padding)}\"}}";

        var result = PostTrustedFrames(coordinator, "fixed-session", [Frame("a", 1), Frame("big", 600), Frame("c", 1)], posted);

        Assert.IsFalse(result.SessionClosed);
        CollectionAssert.AreEqual(new[] { 1 }, result.RejectedFrames!.ToArray());
        CollectionAssert.AreEqual(new[] { "a", "c" }, posted.Select(value => JsonDocument.Parse(value).RootElement.GetProperty("frame").GetProperty("id").GetString()).ToArray());
        Assert.IsTrue(coordinator.IsConnected);
    }

    [TestMethod]
    public void HelloOfADocumentThatLoadsLongerThanTheHandshakeTimeoutIsStillAnswered()
    {
        // A native library that does not report the commit holds the hello until the navigation ends.
        var sent = new List<string>();
        var diagnostics = new List<NeoTransportDiagnosticEventArgs>();
        var coordinator = CreateCoordinator(sent, diagnostics);
        coordinator.NavigationStarted();
        coordinator.Receive(Envelope(coordinator, "slow-document", Hello()));

        coordinator.ExpireHandshake(1);
        Assert.IsTrue(diagnostics.Any(value => value.Code == "handshake_timeout"));
        coordinator.NavigationCompleted(succeeded: true);

        Assert.IsTrue(coordinator.IsConnected, "The host's timeout does not end the handshake of a document that still loads.");
        Assert.HasCount(1, sent);
        using var ack = JsonDocument.Parse(sent[0]);
        Assert.AreEqual("hello_ack", ack.RootElement.GetProperty("frame").GetProperty("kind").GetString());
        Assert.AreEqual("slow-document", ack.RootElement.GetProperty("rendererDocumentId").GetString());
    }

    [TestMethod]
    public void HostRevokedSessionNotifiesItsDocumentAndOnlyANavigationReconnects()
    {
        var sent = new List<string>();
        var diagnostics = new List<NeoTransportDiagnosticEventArgs>();
        var sessions = new List<NeoTransportSessionSnapshot?>();
        var nextId = 0;
        var coordinator = new NeoTransportCoordinator(Options(), RuntimeInfo, sent.Add, diagnostics.Add, idFactory: () => $"session-{++nextId}", sessionChanged: sessions.Add);
        Connect(coordinator, "document-a");
        Assert.HasCount(1, sent);

        Assert.IsFalse(coordinator.CloseSession("another-session", "rpc_session_closed"), "Only the active session can be revoked.");
        Assert.IsTrue(coordinator.IsConnected);
        Assert.IsTrue(coordinator.CloseSession("session-1", "rpc_session_closed"));
        Assert.IsFalse(coordinator.IsConnected);
        Assert.IsNull(sessions[^1]);
        Assert.HasCount(2, sent);
        using (var close = JsonDocument.Parse(sent[^1]))
        {
            Assert.AreEqual("document-a", close.RootElement.GetProperty("rendererDocumentId").GetString());
            Assert.AreEqual("close", close.RootElement.GetProperty("frame").GetProperty("kind").GetString());
            Assert.AreEqual("rpc_session_closed", close.RootElement.GetProperty("frame").GetProperty("reason").GetString());
        }
        Assert.IsFalse(coordinator.CloseSession("session-1", "rpc_session_closed"), "A revoked session cannot be revoked twice.");

        // Neither the revoked document nor another renderer identity in the same navigation may handshake again.
        coordinator.Receive(Envelope(coordinator, "document-a", Hello()));
        Assert.AreEqual(NeoTransportReceiveKind.Consumed, coordinator.Receive(Envelope(coordinator, "document-a", "{\"neoastra\":1,\"kind\":\"invoke\",\"id\":\"late\"}")).Kind);
        Assert.HasCount(2, sent);
        coordinator.Receive(Envelope(coordinator, "document-b", Hello()));
        Assert.IsFalse(coordinator.IsConnected);
        Assert.HasCount(3, sent);
        using (var rejection = JsonDocument.Parse(sent[^1]))
        {
            Assert.AreEqual("close", rejection.RootElement.GetProperty("frame").GetProperty("kind").GetString());
            Assert.AreEqual("connection_closed", rejection.RootElement.GetProperty("frame").GetProperty("code").GetString());
        }
        Assert.IsTrue(diagnostics.Any(value => value.Code == "connection_closed"));

        // A reload is a new navigation and goes through the normal handshake.
        Connect(coordinator, "document-c");
        Assert.IsTrue(coordinator.IsConnected);
        Assert.AreEqual("session-2", sessions[^1]!.Value.DocumentSessionId);
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

    // The bits of a process failure, as native/include/neoastra.h defines them.
    private const ulong Crashed = 1UL << 32;
    private const ulong RecreateView = 1UL << 33;
    private const ulong DocumentIntact = 1UL << 35;

    [TestMethod]
    public void RendererLossLeavesTheTransportReadyForTheReloadedDocument()
    {
        var sent = new List<string>();
        var sessions = new List<NeoTransportSessionSnapshot?>();
        var nextId = 0;
        var coordinator = new NeoTransportCoordinator(Options(), RuntimeInfo, sent.Add, _ => { }, idFactory: () => $"session-{++nextId}", sessionChanged: sessions.Add);
        Connect(coordinator, "crashed-document");

        global::NeoAstra.NeoAstra.NotifyTransportOfProcessFailure(coordinator, (ulong)NeoProcessFailureKind.WebProcessExited | Crashed | RecreateView);
        Assert.IsFalse(coordinator.IsConnected);
        Assert.IsNull(sessions[^1], "The RPC session of the lost document is closed.");
        Assert.HasCount(1, sent, "Nothing is posted to a renderer that is gone.");

        // The application reloads the view: the new document handshakes as after any navigation.
        Connect(coordinator, "reloaded-document");
        Assert.IsTrue(coordinator.IsConnected);
        Assert.AreEqual("session-2", sessions[^1]!.Value.DocumentSessionId);
        Assert.HasCount(2, sent);
        using (var ack = JsonDocument.Parse(sent[^1]))
        {
            Assert.AreEqual("reloaded-document", ack.RootElement.GetProperty("rendererDocumentId").GetString());
            Assert.AreEqual("hello_ack", ack.RootElement.GetProperty("frame").GetProperty("kind").GetString());
        }
        Assert.AreEqual(NeoTransportReceiveKind.Application, coordinator.Receive(Envelope(coordinator, "reloaded-document", "{\"neoastra\":1,\"kind\":\"invoke\"}")).Kind);
        Assert.AreEqual(NeoTransportReceiveKind.Consumed, coordinator.Receive(Envelope(coordinator, "crashed-document", "{\"neoastra\":1,\"kind\":\"invoke\"}")).Kind,
            "A late frame of the lost document is not rebound.");
    }

    [TestMethod]
    public void ProcessFailuresThatLeaveTheMainDocumentKeepItsSession()
    {
        // A GPU, utility, sandbox, or plugin process; a renderer of subframes only; a renderer that stopped answering.
        ulong[] failures =
        [
            (ulong)NeoProcessFailureKind.Unknown | Crashed | DocumentIntact,
            (ulong)NeoProcessFailureKind.WebProcessExited | Crashed | DocumentIntact,
            (ulong)NeoProcessFailureKind.ProcessUnresponsive | RecreateView | DocumentIntact,
        ];
        foreach (var failure in failures)
        {
            var sent = new List<string>();
            var coordinator = CreateCoordinator(sent);
            Connect(coordinator, "running-document");

            global::NeoAstra.NeoAstra.NotifyTransportOfProcessFailure(coordinator, failure);

            Assert.IsTrue(coordinator.IsConnected, $"Failure 0x{failure:x} leaves the session of the running document.");
            Assert.HasCount(1, sent);
            Assert.AreEqual(NeoTransportReceiveKind.Application, coordinator.Receive(Envelope(coordinator, "running-document", "{\"neoastra\":1,\"kind\":\"invoke\"}")).Kind);
        }
    }

    [TestMethod]
    public void TheEnvelopeOfATrustedFrame_IsTheBytesTheTransportWritesForItsOwnFrames()
    {
        // Renderer document IDs are the page's: escaping, characters beyond ASCII, and IDs longer than the room a frame
        // keeps for its envelope give the same bytes as the writer of the envelope of the transport frames.
        string[] documents = ["document", "doc<&>\"'\\+`", "caf\u00e9-\ud83d\ude00-\u2028", new string('x', 128), new string('\u00e9', 128)];
        const string frame = "{\"neoastra\":1,\"kind\":\"result\",\"id\":\"1\",\"ok\":true,\"value\":{\"text\":\"\\u003Cb\\u003E \u00e9\"}}";
        foreach (var document in documents)
        {
            var coordinator = CreateCoordinator([]);
            coordinator.NavigationStarted();
            coordinator.Receive($"{{\"__neoastraTransport\":1,\"hostViewBinding\":\"{coordinator.HostViewBinding}\",\"rendererDocumentId\":{JsonSerializer.Serialize(document)},\"frame\":{Hello()}}}");
            coordinator.NavigationCompleted(succeeded: true);
            Assert.IsTrue(coordinator.IsConnected, document);
            var expected = ReferenceEnvelope(coordinator.HostViewBinding, document, frame);
            var posted = new List<string>();
            var trusted = NeoTransportFrame.FromString(frame);
            try
            {
                Assert.IsFalse(global::NeoAstra.NeoAstra.PostTrustedFrames(coordinator, "fixed-session", [trusted, trusted], messages =>
                {
                    foreach (var message in messages) posted.Add(Convert.ToHexString(message.Span));
                }).SessionClosed);
                Assert.AreEqual(frame, trusted.ToString(), "Wrapping a frame leaves the frame as it was.");
            }
            finally
            {
                trusted.Release();
            }
            CollectionAssert.AreEqual(new[] { Convert.ToHexString(expected), Convert.ToHexString(expected) }, posted, document);
            Assert.AreEqual(Encoding.UTF8.GetString(expected), WrapOutbound(coordinator, frame), document);
        }
    }

    [TestMethod]
    public void AnEnvelopeIsRefusedByTheBytesOfTheWholeMessage()
    {
        var options = Options();
        options.MaximumMessageSize = 512;
        var coordinator = CreateCoordinator([], options: options);
        Connect(coordinator, "document");
        var prefix = ReferenceEnvelope(coordinator.HostViewBinding, "document", "").Length - 1;
        // A frame of the given UTF-8 length, most of it two-byte characters.
        static string Frame(int length) => "{\"neoastra\":1,\"kind\":\"result\",\"v\":\"" + new string('\u00e9', (length - 37) / 2) + new string('x', (length - 37) % 2) + "\"}";
        Assert.AreEqual(512 - prefix - 1, Encoding.UTF8.GetByteCount(Frame(512 - prefix - 1)));
        var posted = new List<string>();
        var result = PostTrustedFrames(coordinator, "fixed-session", [Frame(512 - prefix - 1), Frame(512 - prefix)], posted);
        CollectionAssert.AreEqual(new[] { 1 }, result.RejectedFrames!.ToArray());
        Assert.HasCount(1, posted);
        Assert.AreEqual(512, Encoding.UTF8.GetByteCount(posted[0]), "The largest envelope that fits is exactly the limit.");
        Assert.ThrowsExactly<ArgumentException>(() => WrapOutbound(coordinator, Frame(512 - prefix)));
    }

    [TestMethod]
    public void JsonPostedByTheApplication_IsValidatedOnce_AsJsonDocumentParseDid()
    {
        var options = Options();
        options.Transport.MaximumJsonDepth = 4;
        var coordinator = CreateCoordinator([], options: options);
        // Without a session: any JSON value up to 64 levels, posted as it is.
        Assert.AreEqual("[1,2]", WrapOutbound(coordinator, "[1,2]"));
        Assert.AreEqual(" \"text\" ", WrapOutbound(coordinator, " \"text\" "));
        Assert.AreEqual(new string('[', 64) + new string(']', 64), WrapOutbound(coordinator, new string('[', 64) + new string(']', 64)));
        foreach (var invalid in new[] { "", " ", "{", "{} x", "1 2", "{\"a\":1,}", "/*c*/1", new string('[', 65) + new string(']', 65) })
        {
            Assert.Throws<JsonException>(() => WrapOutbound(coordinator, invalid), invalid);
            Assert.Throws<JsonException>(() => JsonDocument.Parse(invalid).Dispose(), invalid);
        }
        Assert.Throws<ArgumentException>(() => NeoTransportFrame.FromString("\"\ud800\""), "A lone surrogate is refused, as JsonDocument.Parse refused it.");
        Assert.Throws<ArgumentException>(() => JsonDocument.Parse("\"\ud800\"").Dispose());

        // With a session: an object no deeper than the transport accepts, in its envelope.
        Connect(coordinator, "document");
        Assert.AreEqual(Encoding.UTF8.GetString(ReferenceEnvelope(coordinator.HostViewBinding, "document", "{\"a\":{\"b\":{\"c\":1}}}")), WrapOutbound(coordinator, "{\"a\":{\"b\":{\"c\":1}}}"));
        Assert.AreEqual(Encoding.UTF8.GetString(ReferenceEnvelope(coordinator.HostViewBinding, "document", "{\"a\":{\"b\":{\"c\":{}}}}")), WrapOutbound(coordinator, "{\"a\":{\"b\":{\"c\":{}}}}"));
        Assert.Throws<JsonException>(() => WrapOutbound(coordinator, "{\"a\":{\"b\":{\"c\":{\"d\":{}}}}}"));
        Assert.Throws<JsonException>(() => WrapOutbound(coordinator, "[1]"));
    }

    [TestMethod]
    public void AFrameGrowsAndMovesWithoutLosingWhatItHolds()
    {
        var frame = NeoTransportFrame.Create();
        try
        {
            var text = string.Concat(Enumerable.Range(0, 5000).Select(static index => (char)('a' + index % 26)));
            foreach (var character in text) frame.Write((byte)character);
            Assert.AreEqual(text, frame.ToString());
            var prefix = new string('p', NeoTransportFrame.EnvelopeReserve + 10);
            Assert.AreEqual(prefix + text + "}", Encoding.UTF8.GetString(frame.Wrap(Encoding.UTF8.GetBytes(prefix)).Span));
            Assert.AreEqual("ab" + text + "}", Encoding.UTF8.GetString(frame.Wrap("ab"u8).Span));
            Assert.AreEqual(text, frame.ToString());
        }
        finally
        {
            frame.Release();
            frame.Release();
        }
        Assert.ThrowsExactly<ObjectDisposedException>(() => frame.ToString(), "A released frame is not read.");
    }

    [TestMethod]
    public void AMessageOfThePage_IsReadFromItsUtf8_AndEachConsumerOwnsItsFrame()
    {
        var view = (global::NeoAstra.NeoAstra)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(global::NeoAstra.NeoAstra));
        var strings = new List<string>();
        view.MessageReceived += (_, args) => strings.Add(args.Json);

        // Without a transport, bytes that are not UTF-8 reach the application as the string of them did.
        byte[] invalid = [(byte)'"', 0xff, (byte)'a', 0xc3, (byte)'"'];
        view.DispatchWebMessage(invalid, null, true);
        CollectionAssert.AreEqual(new[] { Encoding.UTF8.GetString(invalid) }, strings);

        var coordinator = CreateCoordinator([]);
        typeof(global::NeoAstra.NeoAstra).GetField("_transport", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(view, coordinator);
        Connect(coordinator, "document");
        var baseline = NeoTransportFrame.Outstanding;
        var frames = new List<NeoTransportApplicationMessage>();
        view.TransportApplicationMessageReceived += frames.Add;
        view.TransportApplicationMessageReceived += frames.Add;
        const string frame = "{\"neoastra\":1,\"kind\":\"invoke\",\"id\":\"1\",\"args\":{\"text\":\"caf\\u00e9\"}}";
        view.DispatchWebMessage(Encoding.UTF8.GetBytes(Envelope(coordinator, "document", frame)), null, true);

        Assert.HasCount(2, frames);
        Assert.AreNotSame(frames[0].Frame, frames[1].Frame, "Each consumer gets a frame of its own to release.");
        Assert.AreEqual(frame, frames[0].Frame.ToString());
        Assert.AreEqual(frame, frames[1].Frame.ToString());
        Assert.AreEqual("fixed-session", frames[0].Session.DocumentSessionId);
        Assert.AreEqual(frame, strings[^1], "The application still receives the frame as a string.");
        foreach (var message in frames) message.Frame.Release();
        Assert.AreEqual(baseline, NeoTransportFrame.Outstanding);
    }

    // The envelope as the transport wrote it before it wrote frames in place: one Utf8JsonWriter, the frame as a raw value.
    // An empty frame gives the prefix and the closing brace.
    private static byte[] ReferenceEnvelope(string hostViewBinding, string rendererDocumentId, string frame)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("__neoastraTransport", 1);
            writer.WriteString("hostViewBinding", hostViewBinding);
            writer.WriteString("rendererDocumentId", rendererDocumentId);
            writer.WritePropertyName("frame");
            if (frame.Length == 0) writer.WriteNullValue();
            else writer.WriteRawValue(frame, skipInputValidation: true);
            writer.WriteEndObject();
        }
        var bytes = buffer.WrittenSpan.ToArray();
        return frame.Length == 0 ? [.. bytes[..^5], (byte)'}'] : bytes;
    }

    private static NeoTrustedPostResult PostTrustedFrames(NeoTransportCoordinator coordinator, string documentSessionId, string[] frames, List<string> posted)
    {
        var trusted = frames.Select(static frame => NeoTransportFrame.FromString(frame)).ToArray();
        try
        {
            return global::NeoAstra.NeoAstra.PostTrustedFrames(coordinator, documentSessionId, trusted, messages =>
            {
                foreach (var message in messages) posted.Add(Encoding.UTF8.GetString(message.Span));
            });
        }
        finally
        {
            foreach (var frame in trusted) frame.Release();
        }
    }

    private static string WrapOutbound(NeoTransportCoordinator coordinator, string json)
    {
        var frame = NeoTransportFrame.FromString(json);
        try { return Encoding.UTF8.GetString(coordinator.WrapOutbound(frame).Span); }
        finally { frame.Release(); }
    }

    private static NeoTransportCoordinator CreateCoordinator(
        List<string> sent,
        List<NeoTransportDiagnosticEventArgs>? diagnostics = null,
        Func<string>? idFactory = null,
        NeoAstraOptions? options = null,
        NeoRuntimeInfo? runtimeInfo = null)
    {
        diagnostics ??= [];
        return new NeoTransportCoordinator(options ?? Options(), runtimeInfo ?? RuntimeInfo, sent.Add, diagnostics.Add, idFactory: idFactory ?? (() => "fixed-session"));
    }

    // A native library that reports NEOASTRA_EVENT_NAVIGATION_COMMITTED (NEOASTRA_BUILD_FEATURE_NAVIGATION_COMMIT).
    private static readonly NeoRuntimeInfo CommitRuntimeInfo = new("webview2", "1", "1", "windows", "x64", 1UL << 2, false);

    private static void ConnectCommitted(NeoTransportCoordinator coordinator, string rendererDocumentId)
    {
        coordinator.NavigationStarted();
        coordinator.NavigationCommitted();
        coordinator.Receive(Envelope(coordinator, rendererDocumentId, Hello()));
        coordinator.NavigationCompleted(succeeded: true);
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
