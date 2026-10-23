using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using NeoAstra;
using NeoAstra.Tests;

internal static partial class Program
{
    private const string FrameScheme = "conformanceframe";
    private static readonly Uri CspUri = new("conformance://fixture/csp.html");
    private static readonly Uri FramesUri = new("conformance://fixture/frames.html");

    private sealed partial class ConformanceSuite
    {
        // The headers of a provider response, and frames of another scheme and of a web origin that a page of the
        // application scheme shows: what they reach of the bridge of the view, on each backend.
        private async ValueTask RunFrameScenariosAsync(NeoEnvironment environment, FrameFixtureProvider frames, BridgeMode bridgeMode)
        {
            await RunCaseAsync("the headers of a provider response, its content security policy included, apply to the page", () => WithFrameViewAsync(environment, bridgeMode, async (view, messages) =>
            {
                await NavigateAndWaitAsync(view, CspUri, options.Timeout);
                await WaitForDocumentLoadAsync(view, options.Timeout);
                using (var result = await EvaluateJsonAsync(view, "({ external: globalThis.externalRan === true, inline: globalThis.inlineRan === true })"))
                {
                    Require(result.RootElement.GetProperty("external").GetBoolean(), "The script of the same origin did not run.");
                    Require(!result.RootElement.GetProperty("inline").GetBoolean(), "The inline script ran despite the policy of the response.");
                }
                _ = await view.EvaluateScriptAsync("fetch('csp-external.js').then(response => { globalThis.fetched = [response.headers.get('cache-control'), response.headers.get('x-content-type-options'), response.headers.get('x-neo-test')].join('|'); }); true");
                await WaitUntilScriptAsync(view, "typeof globalThis.fetched === 'string'", "The fetch of a provider response did not complete.", options.Timeout);
                using var fetched = await EvaluateJsonAsync(view, "globalThis.fetched");
                Require(fetched.RootElement.GetString() == "max-age=60|nosniff|served", $"A fetch saw the headers '{fetched.RootElement.GetString()}'.");
            }));

            if (bridgeMode == BridgeMode.Disabled)
            {
                Skip("a frame of another scheme or origin cannot reach the bridge", "The bridge of this backend is disabled.");
                return;
            }

            await RunCaseAsync("a frame of another scheme or origin cannot reach the bridge", () => WithFrameViewAsync(environment, bridgeMode, async (view, messages) =>
            {
                frames.ClearRequests();
                await NavigateAndWaitAsync(view, FramesUri, options.Timeout);
                await WaitForDocumentLoadAsync(view, options.Timeout);
                await WaitUntilScriptAsync(view, "globalThis.__fixtureTransportConnected === true", "The @neoastra/client handshake did not complete.", options.Timeout);
                await WaitUntilScriptAsync(view, "globalThis.frameReports.filter(report => report.origin.startsWith('conformanceframe:') || report.origin === 'null').length === 3",
                    "The frames of the second scheme did not report.", options.Timeout);
                // A web frame needs the loopback interface and plain HTTP, which a platform may refuse: it is waited for
                // a moment, and reported.
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
                while (DateTime.UtcNow < deadline && await view.EvaluateScriptAsync("globalThis.frameReports.length === 4") != "true") await Task.Delay(25);
                Console.WriteLine($"INFO frame reports {await view.EvaluateScriptAsync("JSON.stringify(globalThis.frameReports)")}");
                Console.WriteLine($"INFO frame requests {string.Join(", ", frames.Requests.Select(static request => $"{request.Uri} kind={request.Kind} main={request.IsMainFrame}"))}");
                Require(await view.EvaluateScriptAsync("globalThis.frameReports.some(report => report.transport)") == "false", "A frame has the transport of the page.");
                Require(frames.Requests.Any(static request => request.Uri.Host == "view-a" && request.Uri.AbsolutePath == "/view/frame.html"),
                    "The provider of the second scheme did not serve the document of the frame.");

                // The page still reaches the host after the frames posted, and the host reaches the page.
                await WaitForMessageAsync(view, "page-marker", async () => _ = await view.EvaluateScriptAsync("globalThis.__fixturePostMessage({ kind: 'page-marker' }); true"), options.Timeout);
                await view.PostMessageAsync("{\"neoastra\":1,\"kind\":\"host\",\"value\":23}");
                await WaitUntilScriptAsync(view, "globalThis.__fixtureHostMessages.some(value => value.kind === 'host' && value.value === 23)",
                    "The page did not receive host JSON.", options.Timeout);
                await WaitForMessageAsync(view, "page-marker-2", async () => _ = await view.EvaluateScriptAsync("globalThis.__fixturePostMessage({ kind: 'page-marker-2' }); true"), options.Timeout);
                await Task.Delay(300);

                var leaked = messages.Where(static message => message.Json.Contains(FrameProbe.AttackKind, StringComparison.Ordinal))
                    .Select(static message => $"{message.Json} from {message.SourceOrigin} main={message.IsMainFrame}").ToArray();
                Require(leaked.Length == 0, $"The host received messages of frames: {string.Join("; ", leaked)}");
                var received = await view.EvaluateScriptAsync("JSON.stringify(globalThis.frameReceived)");
                Require(received == "\"[]\"", $"A frame received messages of the host: {received}");
            }));
        }

        private async ValueTask WithFrameViewAsync(NeoEnvironment environment, BridgeMode bridgeMode, Func<NeoAstra.NeoAstra, ConcurrentQueue<NeoWebMessageReceivedEventArgs>, ValueTask> body)
        {
            var window = CreateHiddenWindow("NeoAstra frame conformance");
            try
            {
                await using var view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), CreateViewOptions(null, bridgeMode)).AsTask().WaitAsync(options.Timeout);
                var messages = new ConcurrentQueue<NeoWebMessageReceivedEventArgs>();
                view.MessageReceived += (_, message) => messages.Enqueue(message);
                await body(view, messages);
            }
            finally
            {
                await window.DisposeAsync();
            }
        }
    }

    // Serves the pages of the frame scenarios with response headers, in front of the fixture assets of the application
    // scheme, and the frame documents of the second scheme. The web frame comes from a loopback server.
    private sealed class FrameFixtureProvider : INeoResourceProvider, IDisposable
    {
        private readonly INeoResourceProvider _assets;
        private readonly LoopbackPageServer _web = new(FrameProbe.Document);
        private readonly Dictionary<string, (string MimeType, byte[] Body, Dictionary<string, string> Headers)> _pages;
        private readonly ConcurrentQueue<NeoResourceRequest> _requests = new();

        internal FrameFixtureProvider(INeoResourceProvider assets)
        {
            _assets = assets;
            _pages = new(StringComparer.Ordinal)
            {
                ["/csp.html"] = ("text/html; charset=utf-8",
                    Encoding.UTF8.GetBytes("<!doctype html><title>CSP</title><script>globalThis.inlineRan = true;</script><script src=\"csp-external.js\"></script>"),
                    new() { ["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; connect-src 'self'", ["Cache-Control"] = "no-store", ["X-Content-Type-Options"] = "nosniff" }),
                ["/csp-external.js"] = ("text/javascript; charset=utf-8", Encoding.UTF8.GetBytes("globalThis.externalRan = true;"),
                    new() { ["Cache-Control"] = "max-age=60", ["X-Content-Type-Options"] = "nosniff", ["X-Neo-Test"] = "served" }),
                ["/frames.html"] = ("text/html; charset=utf-8",
                    Encoding.UTF8.GetBytes("<!doctype html><title>Frames</title><script type=\"module\" src=\"fixture.js\"></script><script src=\"frames.js\"></script>"),
                    new() { ["Content-Security-Policy"] = $"default-src 'self'; script-src 'self'; frame-src {FrameScheme}: {_web.Origin}" }),
                ["/frames.js"] = ("text/javascript; charset=utf-8", Encoding.UTF8.GetBytes(FrameProbe.HostScript(
                [
                    ($"{FrameScheme}://view-a/view/frame.html?name=same-origin", "allow-scripts allow-same-origin allow-forms"),
                    ($"{FrameScheme}://view-b/view/frame.html?name=opaque", "allow-scripts allow-forms"),
                    ($"{FrameScheme}://view-c/view/frame.html?name=unsandboxed", null),
                    ($"{_web.Origin}/frame.html?name=web", "allow-scripts allow-same-origin"),
                ])), new()),
            };
        }

        internal IEnumerable<NeoResourceRequest> Requests => _requests;

        // The provider of the second scheme.
        internal INeoResourceProvider Frames => new FrameDocuments(this);

        internal void ClearRequests() => _requests.Clear();

        public NeoResourceResponse? GetResponse(NeoResourceRequest request)
            => _pages.TryGetValue(request.Uri.AbsolutePath, out var page)
                ? NeoResourceResponse.FromBytes(page.Body, page.MimeType, page.Headers)
                : _assets.GetResponse(request);

        public void Dispose() => _web.Dispose();

        private sealed class FrameDocuments(FrameFixtureProvider owner) : INeoResourceProvider
        {
            public NeoResourceResponse? GetResponse(NeoResourceRequest request)
            {
                owner._requests.Enqueue(request);
                return request.Uri.AbsolutePath == "/view/frame.html"
                    ? NeoResourceResponse.FromBytes(Encoding.UTF8.GetBytes(FrameProbe.Document), "text/html; charset=utf-8",
                        new Dictionary<string, string> { ["Cache-Control"] = "no-store", ["X-Content-Type-Options"] = "nosniff" })
                    : null;
            }
        }
    }
}
