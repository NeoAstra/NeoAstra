// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text.Json;

namespace NeoAstra.Tests;

/// <summary>Peer connections (WebRTC) on a real WebView2 view, whose engine has no switch for them.</summary>
/// <remarks>The engines that turn them off, WKWebView and WebKitGTK, are covered by the browser conformance harness and the native tests.</remarks>
[TestClass]
public sealed class PeerConnectionTests
{
    private static readonly Dictionary<string, string> Pages = new()
    {
        ["peer.html"] = PeerConnectionProbe.Document,
        ["peer-frame.html"] = PeerConnectionProbe.FrameDocument,
    };

    private static readonly (string Key, string Address, string? Sandbox)[] Frames =
    [
        ("sandboxed", $"{LiveBrowser.Origin}/peer-frame.html", "allow-scripts"),
        ("same-origin", $"{LiveBrowser.Origin}/peer-frame.html", null),
    ];

    [TestMethod]
    public async Task AViewThatIsAskedNothingKeepsItsPeerConnections()
    {
        await LiveBrowser.RunAsync(Pages, async session =>
        {
            Assert.IsFalse(session.View.ArePeerConnectionsOff);
            AssertEveryDocumentHasPeerConnections(await ProbeAsync(session));
            Assert.AreEqual("\"probe\"", await session.View.EvaluateScriptAsync(
                "(() => { const connection = new RTCPeerConnection(); const channel = connection.createDataChannel('probe'); const label = channel.label; connection.close(); return label; })()",
                session.CancellationToken));
        });
    }

    // WebView2 has no setting for peer connections and Chromium no switch that removes them: a view that asks to have them
    // turned off keeps them, and says so, so that an application does not show there what it must not let reach the network.
    [TestMethod]
    public async Task AViewOfWebView2SaysThatItKeepsItsPeerConnections()
    {
        var options = new NeoAstraOptions { ViewLabel = "main", BrowserFeatures = new NeoBrowserFeatures { PeerConnections = false } };
        await LiveBrowser.RunAsync(Pages, async session =>
        {
            Assert.IsFalse(session.Environment.RuntimeInfo.ControlsPeerConnections);
            Assert.IsFalse(session.View.ArePeerConnectionsOff);
            AssertEveryDocumentHasPeerConnections(await ProbeAsync(session));
        }, options);
    }

    private static async Task<Dictionary<string, string>> ProbeAsync(LiveBrowserSession session)
    {
        await session.NavigateAsync("peer.html");
        session.Stage = "the peer connection probe";
        await session.RunAsync(PeerConnectionProbe.Script(Frames));
        await session.WaitUntilAsync($"globalThis.peerReport.count === {PeerConnectionProbe.OwnContexts.Length + Frames.Length}");
        using var json = JsonDocument.Parse(await session.View.EvaluateScriptAsync("globalThis.peerReport", session.CancellationToken) ?? "{}");
        var report = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in json.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String) report[property.Name] = property.Value.GetString()!;
        }

        Console.WriteLine(string.Join("; ", report.Select(static context => $"{context.Key}=[{context.Value}]")));
        return report;
    }

    private static void AssertEveryDocumentHasPeerConnections(Dictionary<string, string> report)
    {
        Assert.AreEqual(PeerConnectionProbe.OwnContexts.Length + Frames.Length, report.Count);
        foreach (var (context, names) in report)
        {
            // A worker has no RTCPeerConnection on any engine, but it has to have run.
            if (context == "worker") Assert.AreNotEqual("error", names);
            else Assert.IsTrue(PeerConnectionProbe.Has(names, "RTCPeerConnection"), $"The context '{context}' has [{names}].");
        }
    }
}
