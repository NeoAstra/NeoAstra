// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
using System.Text;

namespace NeoAstra.Tests;

[TestClass]
public sealed class CustomSchemeFrameTests
{
    [TestMethod]
    public async Task AProviderResponseAppliesItsContentSecurityPolicy()
    {
        var site = new HeaderSite("app");
        site.Add("csp.html", "text/html; charset=utf-8",
            "<!doctype html><title>CSP</title><script src=\"listen.js\"></script><script>globalThis.inlineRan = true;</script><script src=\"external.js\"></script>",
            new Dictionary<string, string>
            {
                ["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; connect-src 'self'",
                ["Cache-Control"] = "no-store",
                ["X-Content-Type-Options"] = "nosniff",
            });
        site.Add("listen.js", "text/javascript; charset=utf-8",
            "globalThis.violations = []; document.addEventListener('securitypolicyviolation', event => globalThis.violations.push(event.effectiveDirective));",
            new Dictionary<string, string> { ["Cache-Control"] = "no-store" });
        site.Add("external.js", "text/javascript; charset=utf-8", "globalThis.externalRan = true;",
            new Dictionary<string, string> { ["Cache-Control"] = "max-age=60", ["X-Content-Type-Options"] = "nosniff", ["X-Neo-Test"] = "served" });

        await LiveBrowser.RunAsync(new Dictionary<string, string>(), async session =>
        {
            await session.NavigateAsync("csp.html");
            Assert.AreEqual("true", await session.View.EvaluateScriptAsync("globalThis.externalRan === true", session.CancellationToken), "The script of the same origin did not run.");
            Assert.AreEqual("true", await session.View.EvaluateScriptAsync("globalThis.inlineRan === undefined", session.CancellationToken), "The inline script ran despite the policy of the response.");
            await session.WaitUntilAsync("globalThis.violations.includes('script-src-elem') || globalThis.violations.includes('script-src')");

            session.Stage = "the headers of a response that a script fetches";
            await session.RunAsync("fetch('external.js').then(response => { globalThis.fetched = [response.headers.get('cache-control'), response.headers.get('x-content-type-options'), response.headers.get('x-neo-test')].join('|'); }); true");
            await session.WaitUntilAsync("typeof globalThis.fetched === 'string'");
            Assert.AreEqual("\"max-age=60|nosniff|served\"", await session.View.EvaluateScriptAsync("globalThis.fetched", session.CancellationToken));
        }, customSchemes: [NeoCustomScheme.Application("app", site)]);
    }

    // WebView2 gives every frame a chrome.webview of its own, but raises the WebMessageReceived event of the view for the
    // messages of the top-level document only, and posts the messages of the host to that document only.
    [TestMethod]
    public async Task AFrameOfAnotherSchemeOrOriginCannotReachTheBridge()
    {
        using var server = new LoopbackPageServer(FrameProbe.Document);
        var application = new HeaderSite("app");
        application.Add("index.html", "text/html; charset=utf-8", "<!doctype html><title>Host</title><script src=\"index.js\"></script>",
            new Dictionary<string, string> { ["Content-Security-Policy"] = $"default-src 'self'; script-src 'self'; frame-src mcpapp: {server.Origin}" });
        application.Add("index.js", "text/javascript; charset=utf-8", FrameProbe.HostScript(
        [
            ("mcpapp://view-a/view/frame.html?name=same-origin", "allow-scripts allow-same-origin allow-forms"),
            ("mcpapp://view-b/view/frame.html?name=opaque", "allow-scripts allow-forms"),
            ("mcpapp://view-c/view/frame.html?name=unsandboxed", null),
            ($"{server.Origin}/frame.html?name=web", "allow-scripts allow-same-origin"),
        ]), new Dictionary<string, string>());
        var views = new HeaderSite("mcpapp");
        views.Add("view/frame.html", "text/html; charset=utf-8", FrameProbe.Document,
            new Dictionary<string, string> { ["Cache-Control"] = "no-store", ["X-Content-Type-Options"] = "nosniff" });

        await LiveBrowser.RunAsync(new Dictionary<string, string>(), async session =>
        {
            var view = session.View;
            var messages = new ConcurrentQueue<NeoWebMessageReceivedEventArgs>();
            view.MessageReceived += (_, message) => messages.Enqueue(message);
            await session.NavigateAsync("index.html");

            session.Stage = "the reports of the frames";
            await session.WaitUntilAsync("globalThis.frameReports.length === 4");
            // Each frame saw an adapter and posted through it: the test would see a message that got through.
            Assert.AreEqual("true", await view.EvaluateScriptAsync("globalThis.frameReports.every(report => report.webview)", session.CancellationToken),
                await view.EvaluateScriptAsync("JSON.stringify(globalThis.frameReports)", session.CancellationToken));
            Assert.AreEqual("false", await view.EvaluateScriptAsync("globalThis.frameReports.some(report => report.transport)", session.CancellationToken), "A frame has the transport of the page.");
            var frameRequest = views.Requests.FirstOrDefault(static request => request.Uri.Host == "view-a");
            Assert.IsNotNull(frameRequest, "The provider of the second scheme did not serve the document of the frame.");
            Assert.AreEqual("/view/frame.html", frameRequest.Uri.AbsolutePath);
            Assert.AreEqual(NeoResourceKind.Document, frameRequest.Kind);

            // The page itself still reaches the host, after the messages of the frames.
            session.Stage = "a message of the page";
            await session.RunAsync("chrome.webview.postMessage({ kind: 'page-marker' }); true");
            var marker = await WaitForMessageAsync(messages, "page-marker", session.CancellationToken);
            Assert.IsTrue(marker.IsMainFrame);
            Assert.AreEqual(LiveBrowser.Origin, marker.SourceOrigin?.GetLeftPart(UriPartial.Authority));

            session.Stage = "a message of the host";
            await view.PostMessageAsync("{\"kind\":\"host-marker\"}", session.CancellationToken);
            await session.RunAsync("chrome.webview.postMessage({ kind: 'page-marker-2' }); true");
            await WaitForMessageAsync(messages, "page-marker-2", session.CancellationToken);
            await Task.Delay(300, session.CancellationToken);
            var leaked = messages.Where(static message => message.Json.Contains(FrameProbe.AttackKind, StringComparison.Ordinal))
                .Select(static message => $"{message.Json} from {message.SourceOrigin} main={message.IsMainFrame}").ToArray();
            Assert.IsEmpty(leaked, $"The host received messages of frames: {string.Join("; ", leaked)}");
            Assert.AreEqual("0", await view.EvaluateScriptAsync("globalThis.frameReceived.length", session.CancellationToken),
                await view.EvaluateScriptAsync("JSON.stringify(globalThis.frameReceived)", session.CancellationToken));
        }, viewOptions: new NeoAstraOptions { ViewLabel = "main", BridgePolicy = NeoBridgePolicy.TrustedOrigins, BridgeOrigins = [LiveBrowser.Origin] },
        customSchemes: [NeoCustomScheme.Application("app", application), NeoCustomScheme.Create("mcpapp", views)]);
    }

    private static async Task<NeoWebMessageReceivedEventArgs> WaitForMessageAsync(ConcurrentQueue<NeoWebMessageReceivedEventArgs> messages, string kind, CancellationToken cancellationToken)
    {
        while (true)
        {
            var found = messages.FirstOrDefault(message => message.Json.Contains(kind, StringComparison.Ordinal));
            if (found is not null) return found;
            await Task.Delay(20, cancellationToken);
        }
    }

    // Serves fixed documents with the headers of each, and keeps the requests it saw.
    private sealed class HeaderSite(string scheme) : INeoResourceProvider
    {
        private readonly Dictionary<string, (string MimeType, byte[] Body, IReadOnlyDictionary<string, string> Headers)> _files = new(StringComparer.Ordinal);

        internal ConcurrentQueue<NeoResourceRequest> Requests { get; } = new();

        internal void Add(string path, string mimeType, string body, IReadOnlyDictionary<string, string> headers)
            => _files.Add(path, (mimeType, Encoding.UTF8.GetBytes(body), headers));

        public NeoResourceResponse? GetResponse(NeoResourceRequest request)
        {
            if (request.Uri.Scheme != scheme) return NeoResourceResponse.Empty(400, "Bad Request");
            Requests.Enqueue(request);
            return _files.TryGetValue(request.Uri.AbsolutePath.TrimStart('/'), out var file)
                ? NeoResourceResponse.FromBytes(file.Body, file.MimeType, file.Headers)
                : null;
        }
    }
}
