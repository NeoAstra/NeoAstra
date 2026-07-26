// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Security.Cryptography;

namespace NeoAstra.Tests;

[TestClass]
public sealed class AssetHostTests
{
    [TestMethod]
    public void ManifestHost_ServesManifestAssetsSpaRoutesAndSecurityHeaders()
    {
        using var fixture = new AssetFixture(); var provider = fixture.Provider;
        var script = provider.GetResponse(fixture.Request("/assets/app.01234567.js", "GET", "*/*"));
        Assert.IsNotNull(script); Assert.AreEqual("text/javascript; charset=utf-8", script.MimeType); Assert.AreEqual("nosniff", script.Headers["X-Content-Type-Options"]); Assert.AreEqual(AssetFixture.Csp, script.Headers["Content-Security-Policy"]); Assert.AreEqual("public,max-age=31536000,immutable", script.Headers["Cache-Control"]);
        var route = provider.GetResponse(fixture.Request("/notes/42", "GET", "text/html")); Assert.IsNotNull(route); Assert.AreEqual("text/html; charset=utf-8", route.MimeType); Assert.AreEqual(Path.Combine(fixture.Root, "index.html"), route.FilePath);
        Assert.IsNull(provider.GetResponse(fixture.Request("/assets/missing.js", "GET", "text/html")));
        Assert.IsNull(provider.GetResponse(fixture.Request("/api/items", "GET", "text/html")));
        Assert.IsNull(provider.GetResponse(new NeoResourceRequest(new Uri("app://fixture/notes/42"), "GET", new Dictionary<string, string> { ["Accept"] = "text/html" }, null, NeoResourceKind.Fetch, false, default)));
        Assert.AreEqual(403, provider.GetResponse(new NeoResourceRequest(new Uri("app://other/index.html"), "GET", new Dictionary<string, string>(), null, NeoResourceKind.Document, true, default))!.StatusCode);
        var head = provider.GetResponse(fixture.Request("/index.html?ignored=yes", "HEAD", "text/html")); Assert.IsNotNull(head); Assert.IsNull(head.FilePath); Assert.AreEqual("text/html; charset=utf-8", head.MimeType); Assert.AreEqual(200, head.StatusCode);
        Assert.AreEqual(405, provider.GetResponse(fixture.Request("/index.html", "POST", "text/html"))!.StatusCode);
        var diagnostics = fixture.Manifest.CreateDiagnostics(new Uri("http://127.0.0.1:5173/path"));
        Assert.AreEqual(2, diagnostics.AssetCount); Assert.AreEqual("http://127.0.0.1:5173", diagnostics.DevelopmentOrigin); Assert.IsFalse(diagnostics.ContainsSourceMaps); Assert.AreEqual(64, diagnostics.ManifestSha256.Length);
        Assert.AreEqual("/", new NeoAssetManifest(1, "index.html", "index.html", "app://fixture", AssetFixture.Csp, "no-referrer", ["/"], ["/api", "/_neoastra"], fixture.Manifest.Assets).SpaRoutePrefixes[0]);
        Assert.ThrowsExactly<ArgumentException>(() => new NeoAssetManifest(1, "index.html", "index.html", "app:///", AssetFixture.Csp, "no-referrer", [], ["/api", "/_neoastra"], fixture.Manifest.Assets));
        Assert.ThrowsExactly<ArgumentException>(() => new NeoAssetManifest(1, "index.html", "index.html", "data://fixture", AssetFixture.Csp, "no-referrer", [], ["/api", "/_neoastra"], fixture.Manifest.Assets));
        Assert.ThrowsExactly<ArgumentException>(() => new NeoAssetManifest(1, "index.html", "index.html", "app://fixture", AssetFixture.Csp, "no-referrer", [], ["/custom"], fixture.Manifest.Assets));
    }

    [TestMethod]
    public void ManifestHost_RejectsEncodedSeparatorsTraversalNulAndIntegrityDrift()
    {
        using var fixture = new AssetFixture(); var provider = fixture.Provider;
        Assert.AreEqual(400, provider.GetResponse(fixture.Request("/%2e%2e/secret", "GET", "text/html"))!.StatusCode);
        Assert.AreEqual(400, provider.GetResponse(fixture.Request("/assets%2fapp.01234567.js", "GET", "*/*"))!.StatusCode);
        Assert.AreEqual(400, provider.GetResponse(fixture.Request("//assets/app.01234567.js", "GET", "*/*"))!.StatusCode);
        Assert.AreEqual(400, provider.GetResponse(fixture.Request("/%00", "GET", "text/html"))!.StatusCode);
        File.AppendAllText(Path.Combine(fixture.Root, "index.html"), "drift");
        Assert.AreEqual(500, provider.GetResponse(fixture.Request("/index.html", "GET", "text/html"))!.StatusCode);
    }

    [TestMethod]
    public void ManifestHost_RejectsARegularRootBeneathALinkedAncestor()
    {
        using var fixture = new AssetFixture(); var outside = Path.Combine(fixture.Root, "outside"); var nestedRoot = Path.Combine(outside, "assets"); Directory.CreateDirectory(nestedRoot); var link = Path.Combine(fixture.Root, "linked-parent");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { Assert.Inconclusive($"Symbolic-link creation is unavailable: {exception.GetType().Name}."); return; }
        try { Assert.ThrowsExactly<ArgumentException>(() => new NeoManifestResourceProvider(Path.Combine(link, "assets"), fixture.Manifest)); }
        finally { Directory.Delete(link); }
    }

    private sealed class AssetFixture : IDisposable
    {
        internal const string Csp = "default-src 'self'; script-src 'self'; object-src 'none'; base-uri 'none'";
        internal AssetFixture()
        {
            Root = Path.Combine(AppContext.BaseDirectory, "neoastra-host-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(Root, "assets"));
            File.WriteAllText(Path.Combine(Root, "index.html"), "<!doctype html>"); File.WriteAllText(Path.Combine(Root, "assets", "app.01234567.js"), "export {};");
            var entries = new[] { Entry("assets/app.01234567.js", "public,max-age=31536000,immutable"), Entry("index.html", "no-cache") }.OrderBy(static item => item.Path, StringComparer.Ordinal).ToArray();
            Manifest = new NeoAssetManifest(1, "index.html", "index.html", "app://fixture", Csp, "no-referrer", ["/notes"], ["/api", "/_neoastra"], entries); Provider = new NeoManifestResourceProvider(Root, Manifest);
        }
        internal string Root { get; }
        internal NeoAssetManifest Manifest { get; }
        internal NeoManifestResourceProvider Provider { get; }
        internal NeoResourceRequest Request(string path, string method, string accept) => new(new Uri("app://fixture" + path), method, new Dictionary<string, string> { ["Accept"] = accept }, null, NeoResourceKind.Document, true, default);
        private NeoAssetEntry Entry(string path, string cache) { var full = Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar)); var bytes = File.ReadAllBytes(full); return new(path, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), NeoAssetManifest.GetContentType(path), cache); }
        public void Dispose() { try { Directory.Delete(Root, recursive: true); } catch { } }
    }
}
