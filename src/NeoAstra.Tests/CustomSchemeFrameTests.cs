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
