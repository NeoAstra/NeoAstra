using System.Text.Json;
using NeoAstra;
using NeoAstra.Tests;

internal static partial class Program
{
    private static readonly Uri PeerUri = new("conformance://fixture/peer.html");

    // The frames of the peer connection probe: one of the application scheme with an opaque origin, and two of other
    // schemes, the second sandboxed as a host frames content that it does not trust.
    private static readonly (string Key, string Address, string? Sandbox)[] PeerFrames =
    [
        ("sandboxed", "conformance://fixture/peer-frame.html", "allow-scripts"),
        ("scheme", $"{FrameScheme}://view-p/view/peer.html", null),
        ("scheme-sandboxed", $"{SecondFrameScheme}://view-q/view/peer.html", "allow-scripts"),
    ];

    private sealed partial class ConformanceSuite
    {
        // Peer connections (WebRTC): a view that is asked nothing keeps what its engine gives, and a view that has them
        // turned off gives them to no document, where the engine has a switch for them. Where it has none, the view says so.
        private async ValueTask RunPeerConnectionScenariosAsync(NeoEnvironment environment)
        {
            await RunCaseAsync("a view that is asked nothing keeps the peer connections of its engine", () => WithViewAsync(environment, null, async (view, _) =>
            {
                Require(!view.ArePeerConnectionsOff, "A view that was asked nothing reports its peer connections as turned off.");
                var report = await ProbePeerConnectionsAsync(view, PeerUri);
                // Whether WebKitGTK has WebRTC by default depends on its build; the other engines have it.
                if (OperatingSystem.IsLinux()) return;
                RequireEveryDocument(report, present: true);
                using var label = await EvaluateJsonAsync(view,
                    "(() => { const connection = new RTCPeerConnection(); const channel = connection.createDataChannel('probe'); const label = channel.label; connection.close(); return label; })()");
                Require(label.RootElement.GetString() == "probe", "A peer connection of a view that keeps them made no data channel.");
            }));

            const string turnedOff = "a view without peer connections gives them to no document, frame, or worker";
            const string refused = "a view of an engine without a switch for peer connections says that it keeps them";
            var features = new NeoBrowserFeatures { PeerConnections = false };
            if (environment.RuntimeInfo.ControlsPeerConnections)
            {
                await RunCaseAsync(turnedOff, () => WithViewAsync(environment, features, async (view, _) =>
                {
                    Require(view.ArePeerConnectionsOff, "The engine did not take the switch of peer connections.");
                    RequireEveryDocument(await ProbePeerConnectionsAsync(view, PeerUri), present: false);
                    // An empty document at the top of the view has none either. It has no frame of another address to
                    // load from, so the frames of the probe stay empty and only its own contexts report.
                    RequireEveryDocument(await ProbePeerConnectionsAsync(view, new Uri("about:blank")), present: false);
                    // A document that the view loads later, and loads again, has none.
                    RequireEveryDocument(await ProbePeerConnectionsAsync(view, PeerUri), present: false);
                    var reloaded = WaitForNavigationAsync(view, PeerUri, options.Timeout);
                    view.Reload();
                    await reloaded;
                    await WaitUntilScriptAsync(view, "document.readyState === 'complete' && typeof globalThis.peerReport === 'undefined'", "A reload did not load the document again.", options.Timeout);
                    RequireEveryDocument(await ProbePeerConnectionsAsync(view, null), present: false);
                }));
                Skip(refused, "The engine of this backend turns peer connections off.");
            }
            else
            {
                Skip(turnedOff, "The engine of this backend has no switch for peer connections, or the native library predates it.");
                await RunCaseAsync(refused, () => WithViewAsync(environment, features, async (view, _) =>
                {
                    Require(!view.ArePeerConnectionsOff, "The view reports peer connections as turned off on an engine without a switch for them.");
                    RequireEveryDocument(await ProbePeerConnectionsAsync(view, PeerUri), present: true);
                }));
            }
        }

        // Loads the address, or keeps the document when there is none, runs the probe, and returns what each context reported.
        private async ValueTask<Dictionary<string, string>> ProbePeerConnectionsAsync(NeoAstra.NeoAstra view, Uri? address)
        {
            // A frame of another address cannot load from an empty document, which has no origin to be framed by.
            var frames = address is { Scheme: "about" } ? [] : PeerFrames;
            if (address is not null)
            {
                await NavigateAndWaitAsync(view, address, options.Timeout);
                await WaitForDocumentLoadAsync(view, options.Timeout);
            }

            _ = await view.EvaluateScriptAsync(PeerConnectionProbe.Script(frames));
            var expected = PeerConnectionProbe.OwnContexts.Length + frames.Length;
            await WaitUntilScriptAsync(view, $"globalThis.peerReport.count === {expected}", "The contexts of the peer connection probe did not all report.", options.Timeout);
            using var json = await EvaluateJsonAsync(view, "globalThis.peerReport");
            var report = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in json.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String) report[property.Name] = property.Value.GetString()!;
            }

            Console.WriteLine($"INFO peer connections at {address?.ToString() ?? "the reloaded document"}: {string.Join("; ", report.Select(static context => $"{context.Key}=[{context.Value}]"))}");
            return report;
        }

        // Every document has RTCPeerConnection, or no document and no worker has any interface of peer connections.
        private static void RequireEveryDocument(Dictionary<string, string> report, bool present)
        {
            foreach (var (context, names) in report)
            {
                if (present)
                {
                    // A worker has no RTCPeerConnection on any engine, but it has to have run.
                    Require(context == "worker" ? names != "error" : PeerConnectionProbe.Has(names, "RTCPeerConnection"), $"The context '{context}' has no RTCPeerConnection: [{names}].");
                }
                else
                {
                    Require(names.Length == 0, $"The context '{context}' of a view without peer connections has [{names}].");
                }
            }
        }
    }
}
