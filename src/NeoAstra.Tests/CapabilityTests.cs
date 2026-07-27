// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NeoAstra.Rpc;

namespace NeoAstra.Tests;

[TestClass]
public sealed class CapabilityTests
{
    [TestMethod]
    public void ManifestIsDeterministicStrictVersionedAndRejectsBroadening()
    {
        var catalog = Catalog();
        const string valid = """
        {"$schema":"neoastra-capabilities-v1.schema.json","version":1,"capabilities":[{"id":"main","views":["main"],"platforms":["windows"],"origins":["https://EXAMPLE.test:443"],"permissions":["documents:open"]}]}
        """;
        var first = Resolve(valid, catalog, NeoCapabilityPlatform.Windows);
        var second = Resolve(valid, catalog, NeoCapabilityPlatform.Windows);
        Assert.AreEqual(first.Json, second.Json);
        Assert.AreEqual(first.Hash, second.Hash);
        StringAssert.Contains(first.Json, "https://example.test:443");
        var ipv6 = Resolve(valid.Replace("https://EXAMPLE.test:443", "http://[::1]:5173"), catalog, NeoCapabilityPlatform.Windows);
        StringAssert.Contains(ipv6.Json, "http://[::1]:5173");
        Assert.AreEqual(64, first.Hash.Length);
        var nonTarget = Resolve(valid, catalog, NeoCapabilityPlatform.Linux);
        Assert.AreEqual(0, JsonDocument.Parse(nonTarget.Json).RootElement.GetProperty("capabilities").GetArrayLength());

        AssertCode("unknown_version", valid.Replace("\"version\":1", "\"version\":2"), catalog);
        AssertCode("duplicate_property", valid.Replace("\"version\":1", "\"version\":1,\"version\":1"), catalog);
        AssertCode("invalid_schema", valid.Replace("\"permissions\"", "\"unexpected\":true,\"permissions\""), catalog);
        AssertCode("unknown_permission", valid.Replace("documents:open", "unknown:permission"), catalog);
        AssertCode("invalid_schema", valid.Replace("\"documents:open\"", "{\"id\":\"documents:open\",\"version\":\"1\"}"), catalog);
        const string duplicate = """{"$schema":"neoastra-capabilities-v1.schema.json","version":1,"capabilities":[{"id":"main","views":["main"],"permissions":["documents:open"]},{"id":"main","views":["other"],"permissions":["documents:open"]}]}""";
        AssertCode("duplicate_id", duplicate, catalog);
        AssertCode("development_grant", valid.Replace("\"permissions\"", "\"developmentOnly\":true,\"permissions\""), catalog);

        const string overlap = """
        {"$schema":"neoastra-capabilities-v1.schema.json","version":1,"capabilities":[{"id":"a","views":["main"],"permissions":["documents:open"]},{"id":"b","views":["main"],"permissions":["documents:open"]}]}
        """;
        AssertCode("capability_overlap", overlap, catalog);
    }

    [TestMethod]
    public async Task PluginRegistrationAndPermissionSetsGrantNothingImplicitly()
    {
        Assert.Throws<ArgumentException>(() => new NeoPluginPermissionCatalog("unsafe.plugin", "1.0.0", "2.0.0", [new NeoPermissionDeclaration("plugin:root", 1, ["plugin.root"], NeoPermissionRisk.High, NeoScopeFamily.None)], new Dictionary<string, IReadOnlyList<string>> { ["broad"] = ["plugin:root"] }));
        var plugin = new NeoPluginPermissionCatalog("sample.plugin", "1.0.0", "2.0.0", [new NeoPermissionDeclaration("plugin:danger", 1, ["plugin.run"], NeoPermissionRisk.Sensitive, NeoScopeFamily.None)], new Dictionary<string, IReadOnlyList<string>> { ["reviewed"] = ["plugin:danger"] });
        var catalog = new NeoPermissionCatalogBuilder().Add(new NeoPermissionDeclaration("documents:open", 1, ["documents.open"], NeoPermissionRisk.Low, NeoScopeFamily.None)).AddPlugin(plugin).Build();
        const string json = """{"$schema":"neoastra-capabilities-v1.schema.json","version":1,"capabilities":[{"id":"main","views":["main"],"permissions":["documents:open"]}]}""";
        var manifest = Resolve(json, catalog, NeoCapabilityPlatform.Windows);
        using (var resolved = JsonDocument.Parse(manifest.Json))
            Assert.IsFalse(resolved.RootElement.GetProperty("capabilities")[0].GetProperty("permissions").EnumerateArray().Any(value => value.GetProperty("id").GetString() == "plugin:danger"));
        Assert.IsTrue(manifest.Json.Contains("sample.plugin", StringComparison.Ordinal));
        Assert.AreEqual(1, catalog.Plugins.Count);
        var invoked = false; var frames = new ConcurrentQueue<string>();
        var builder = new NeoRpcBuilder(new NeoRpcOptions { AuthorizationService = new NeoCapabilityAuthorizationService(manifest), CapabilityManifest = manifest });
        builder.AddCommand<JsonElement, JsonElement>("plugin.run", (request, _, _) => { invoked = true; return ValueTask.FromResult(request); }, CapabilityTestJsonContext.Default.JsonElement, CapabilityTestJsonContext.Default.JsonElement, new() { Permission = "plugin:danger" });
        await using var host = builder.Build();
        await using var session = host.OpenSession(new NeoRpcSessionIdentity("main", "plugin-session") { Platform = NeoCapabilityPlatform.Windows, IsMainFrame = true }, (frame, _) => { frames.Enqueue(frame); return ValueTask.CompletedTask; });
        await session.ReceiveAsync(Invoke("plugin", "plugin.run", "{}"));
        Assert.IsFalse(invoked);
        Assert.AreEqual("permission_denied", ErrorCode(frames.Single()));
    }

    [TestMethod]
    public void EveryScopeFamilyValidatesAndUnknownOrUnsafeFieldsFailClosed()
    {
        var root = JsonSerializer.Serialize(Path.GetFullPath(Path.GetTempPath()));
        var executable = JsonSerializer.Serialize(Path.GetFullPath(Path.Combine(Path.GetTempPath(), OperatingSystem.IsWindows() ? "tool.exe" : "tool")));
        var scopes = new Dictionary<string, string>
        {
            ["files:read"] = $$"""{"roots":[{"token":"tmp","path":{{root}}}],"operations":["read"]}""",
            ["opener:open"] = """{"schemes":["https"],"hosts":["docs.example"],"ports":[443],"pathPrefixes":["/help"]}""",
            ["process:run"] = $$"""{"executables":[{"id":"tool","path":{{executable}},"arguments":["--safe"],"environment":[]}]}""",
            ["clipboard:use"] = """{"formats":["text"],"operations":["read","write"]}""",
            ["notifications:show"] = """{"appIdentity":"acme","categories":["default"],"maximumPayloadBytes":1024,"urgencies":["normal"]}""",
            ["dialogs:open"] = """{"kinds":["openFile"],"initialLocations":["documents"],"extensions":["txt"]}""",
            ["network:request"] = """{"schemes":["https"],"hosts":["api.example"],"methods":["GET"],"headers":["accept"],"maximumBodyBytes":0,"maximumResponseBytes":4096}""",
            ["persistence:remember"] = """{"identities":["main"],"kinds":["nativeGrant"],"maximumDurationSeconds":3600}""",
            ["shortcuts:register"] = """{"accelerators":["Ctrl+Shift+P"]}""",
        };
        var permissions = string.Join(',', scopes.Select(pair => $$"""{"id":"{{pair.Key}}","scope":{{pair.Value}}}"""));
        var json = $$"""{"$schema":"neoastra-capabilities-v1.schema.json","version":1,"capabilities":[{"id":"all","views":["main"],"permissions":[{{permissions}}]}]}""";
        var manifest = Resolve(json, Catalog(includeScopes: true), CurrentPlatform());
        Assert.AreEqual(9, manifest.GrantSummaries.Single().Contains("permissions=9", StringComparison.Ordinal) ? 9 : 0);
        AssertCode("scope_invalid", json.Replace("\"operations\":[\"read\"]", "\"operations\":[\"read\"],\"ambientCurrentDirectory\":true"), Catalog(includeScopes: true), CurrentPlatform());
        AssertCode("scope_invalid", json.Replace("\"schemes\":[\"https\"]", "\"schemes\":[\"javascript\"]"), Catalog(includeScopes: true), CurrentPlatform());
    }

    [TestMethod]
    public async Task DispatchUsesTrustedViewPlatformOriginAndValidatedArguments()
    {
        var catalog = Catalog(includeUrl: true);
        const string json = """
        {"$schema":"neoastra-capabilities-v1.schema.json","version":1,"capabilities":[
          {"id":"main","views":["main"],"platforms":["windows"],"origins":["https://app.example"],"permissions":["documents:open",{"id":"opener:open","scope":{"schemes":["https"],"hosts":["docs.example"],"ports":[443],"pathPrefixes":["/help"]}}]},
          {"id":"settings","views":["settings"],"platforms":["windows"],"permissions":["documents:open"]}
        ]}
        """;
        var manifest = Resolve(json, catalog, NeoCapabilityPlatform.Windows);
        var audit = new AuditSink(); var invoked = 0; NeoRpcContext captured = default;
        var options = new NeoRpcOptions { AuthorizationService = new NeoCapabilityAuthorizationService(manifest, audit), CapabilityManifest = manifest };
        var builder = new NeoRpcBuilder(options);
        builder.AddCommand<JsonElement, JsonElement>("documents.open", (request, context, _) => { invoked++; captured = context; return ValueTask.FromResult(request); }, CapabilityTestJsonContext.Default.JsonElement, CapabilityTestJsonContext.Default.JsonElement, new() { Permission = "documents:open" });
        builder.AddCommand<JsonElement, JsonElement>("opener.open", (request, context, _) => { invoked++; captured = context; return ValueTask.FromResult(request); }, CapabilityTestJsonContext.Default.JsonElement, CapabilityTestJsonContext.Default.JsonElement, new() { Permission = "opener:open" });
        await using var host = builder.Build();
        var frames = new ConcurrentQueue<string>();
        await using var main = host.OpenSession(new NeoRpcSessionIdentity("main", "trusted-session") { Platform = NeoCapabilityPlatform.Windows, IsMainFrame = true }, (frame, _) => { frames.Enqueue(frame); return ValueTask.CompletedTask; });
        await main.ReceiveAsync(Invoke("ok", "documents.open", "{}"), new Uri("https://APP.example:443"), true);
        await main.ReceiveAsync(Invoke("url-ok", "opener.open", "{\"url\":\"https://docs.example/help/start?q=redacted\"}"), new Uri("https://app.example"), true);
        await main.ReceiveAsync(Invoke("bad-url", "opener.open", "{\"url\":\"https://evil.example/help\"}"), new Uri("https://app.example"), true);
        await main.ReceiveAsync(Invoke("ambiguous-url", "opener.open", "{\"url\":\"https://docs.example/help/%2e%2e/private\"}"), new Uri("https://app.example"), true);
        await main.ReceiveAsync(Invoke("unknown-origin", "documents.open", "{}"), null, true);
        await main.ReceiveAsync(Invoke("wrong-origin", "documents.open", "{}"), new Uri("https://other.example"), true);
        await main.ReceiveAsync(Invoke("subframe", "documents.open", "{}"), new Uri("https://app.example"), false);
        Assert.AreEqual(2, invoked);
        Assert.IsNotNull(captured.Authorization);
        Assert.AreEqual("opener:open", captured.Authorization.Permission);
        CollectionAssert.Contains(frames.Select(ErrorCode).ToArray(), "scope_denied");
        Assert.AreEqual(3, frames.Count(frame => ErrorCode(frame) == "permission_denied"));
        Assert.AreEqual(2, frames.Count(frame => ErrorCode(frame) == "scope_denied"));
        Assert.IsFalse(audit.Events.Any(value => value.ToString()!.Contains("?q=", StringComparison.Ordinal) || value.ToString()!.Contains("evil.example", StringComparison.Ordinal)));

        var settingsFrames = new ConcurrentQueue<string>();
        await using var settings = host.OpenSession(new NeoRpcSessionIdentity("settings", "settings-session") { Platform = NeoCapabilityPlatform.Windows, IsMainFrame = true }, (frame, _) => { settingsFrames.Enqueue(frame); return ValueTask.CompletedTask; });
        await settings.ReceiveAsync(Invoke("settings-doc", "documents.open", "{}"));
        await settings.ReceiveAsync(Invoke("settings-url", "opener.open", "{\"url\":\"https://docs.example/help\"}"));
        Assert.AreEqual(3, invoked, string.Join(',', audit.Events.Select(value => $"{value.Operation}:{value.DecisionCode}:{value.Platform}")));
        Assert.AreEqual("permission_denied", ErrorCode(settingsFrames.Last()));

        var deniedFrames = new ConcurrentQueue<string>();
        await using var wrongView = host.OpenSession(new NeoRpcSessionIdentity("other", "other-session") { Platform = NeoCapabilityPlatform.Windows, IsMainFrame = true }, (frame, _) => { deniedFrames.Enqueue(frame); return ValueTask.CompletedTask; });
        await wrongView.ReceiveAsync(Invoke("wrong-view", "documents.open", "{}"), new Uri("https://app.example"), true);
        Assert.AreEqual("permission_denied", ErrorCode(deniedFrames.Single()));
        var wrongPlatformFrames = new ConcurrentQueue<string>();
        await using var wrongPlatform = host.OpenSession(new NeoRpcSessionIdentity("main", "wrong-platform") { Platform = NeoCapabilityPlatform.MacOS, IsMainFrame = true }, (frame, _) => { wrongPlatformFrames.Enqueue(frame); return ValueTask.CompletedTask; });
        await wrongPlatform.ReceiveAsync(Invoke("wrong-platform", "documents.open", "{}"), new Uri("https://app.example"), true);
        Assert.AreEqual("permission_denied", ErrorCode(wrongPlatformFrames.Single()));
        Assert.IsTrue(audit.Events.Any(value => value.DecisionCode == NeoCapabilityDecisionCodes.NoMatchingCapability));
    }

    [TestMethod]
    public async Task DefaultDenialLinuxProvenanceRateAndResourceLimitsAreStable()
    {
        var catalog = Catalog();
        const string capability = """{"$schema":"neoastra-capabilities-v1.schema.json","version":1,"capabilities":[{"id":"linux","views":["main"],"platforms":["linux"],"permissions":["documents:open"]}]}""";
        var linuxManifest = Resolve(capability, catalog, NeoCapabilityPlatform.Linux);
        var invoked = 0; var frames = new ConcurrentQueue<string>(); var audit = new AuditSink();
        var builder = new NeoRpcBuilder(new NeoRpcOptions { AuthorizationService = new NeoCapabilityAuthorizationService(linuxManifest, audit), CapabilityManifest = linuxManifest, RequestRatePerSecond = 1, RequestRateBurst = 1, AbuseClosureThreshold = 100 });
        builder.AddCommand<JsonElement, JsonElement>("documents.open", (request, _, _) => { invoked++; return ValueTask.FromResult(request); }, CapabilityTestJsonContext.Default.JsonElement, CapabilityTestJsonContext.Default.JsonElement, new() { Permission = "documents:open" });
        await using var host = builder.Build();
        await using var untrusted = host.OpenSession(new NeoRpcSessionIdentity("main", "linux-untrusted") { Platform = NeoCapabilityPlatform.Linux, SourceOrigin = new Uri("https://spoofed.example"), WholeViewTrust = false }, (frame, _) => { frames.Enqueue(frame); return ValueTask.CompletedTask; });
        await untrusted.ReceiveAsync(Invoke("spoof", "documents.open", "{\"origin\":\"https://spoofed.example\"}"), new Uri("https://spoofed.example"), true);
        Assert.AreEqual(0, invoked);
        Assert.AreEqual("permission_denied", ErrorCode(frames.Single()));

        var trustedFrames = new ConcurrentQueue<string>();
        await using var trusted = host.OpenSession(new NeoRpcSessionIdentity("main", "linux-trusted") { Platform = NeoCapabilityPlatform.Linux, WholeViewTrust = true, IsMainFrame = true }, (frame, _) => { trustedFrames.Enqueue(frame); return ValueTask.CompletedTask; });
        await trusted.ReceiveAsync(Invoke("first", "documents.open", "{}"));
        await trusted.ReceiveAsync(Invoke("second", "documents.open", "{}"));
        Assert.AreEqual(1, invoked);
        Assert.AreEqual("too_many_requests", ErrorCode(trustedFrames.Last()));
        Assert.IsTrue(audit.Events.Any(value => value.Platform == NeoCapabilityPlatform.Linux && value.WholeViewTrust && !value.OriginAuthenticated));

        var defaultBuilder = new NeoRpcBuilder();
        defaultBuilder.AddCommand<JsonElement, JsonElement>("documents.open", (request, _, _) => { invoked++; return ValueTask.FromResult(request); }, CapabilityTestJsonContext.Default.JsonElement, CapabilityTestJsonContext.Default.JsonElement, new() { Permission = "documents:open" });
        await using var defaultHost = defaultBuilder.Build(); var defaultFrames = new ConcurrentQueue<string>();
        await using var defaultSession = defaultHost.OpenSession(new NeoRpcSessionIdentity("main", "default-denied"), (frame, _) => { defaultFrames.Enqueue(frame); return ValueTask.CompletedTask; });
        await defaultSession.ReceiveAsync(Invoke("denied", "documents.open", "{}"));
        Assert.AreEqual("permission_denied", ErrorCode(defaultFrames.Single()));

        var snapshot = host.GetDiagnosticSnapshot();
        Assert.AreEqual(linuxManifest.Hash, snapshot.ManifestHash);
        Assert.IsFalse(string.Join(',', snapshot.Grants).Contains("spoofed.example", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task FilesystemAndProcessScopesDenyTraversalAndArgumentInjectionBeforeDispatch()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "neoastra-capability-root"));
        Directory.CreateDirectory(root);
        var executable = Path.GetFullPath(Path.Combine(root, OperatingSystem.IsWindows() ? "tool.exe" : "tool"));
        var json = "{\"$schema\":\"neoastra-capabilities-v1.schema.json\",\"version\":1,\"capabilities\":[{\"id\":\"native\",\"views\":[\"main\"],\"permissions\":[{\"id\":\"files:read\",\"scope\":{\"roots\":[{\"token\":\"project\",\"path\":" + JsonSerializer.Serialize(root) + "}],\"operations\":[\"read\"]}},{\"id\":\"process:run\",\"scope\":{\"executables\":[{\"id\":\"tool\",\"path\":" + JsonSerializer.Serialize(executable) + ",\"arguments\":[\"--safe\"],\"environment\":[]}]}}]}]}";
        var manifest = Resolve(json, Catalog(includeScopes: true), CurrentPlatform());
        var invoked = 0; var frames = new ConcurrentQueue<string>();
        var builder = new NeoRpcBuilder(new NeoRpcOptions { AuthorizationService = new NeoCapabilityAuthorizationService(manifest), CapabilityManifest = manifest });
        builder.AddCommand<JsonElement, JsonElement>("files.read", (request, _, _) => { invoked++; return ValueTask.FromResult(request); }, CapabilityTestJsonContext.Default.JsonElement, CapabilityTestJsonContext.Default.JsonElement, new() { Permission = "files:read" });
        builder.AddCommand<JsonElement, JsonElement>("process.run", (request, _, _) => { invoked++; return ValueTask.FromResult(request); }, CapabilityTestJsonContext.Default.JsonElement, CapabilityTestJsonContext.Default.JsonElement, new() { Permission = "process:run" });
        await using var host = builder.Build();
        await using var session = host.OpenSession(new NeoRpcSessionIdentity("main", "scopes") { Platform = CurrentPlatform(), WholeViewTrust = true, IsMainFrame = true }, (frame, _) => { frames.Enqueue(frame); return ValueTask.CompletedTask; });
        await session.ReceiveAsync(Invoke("file-ok", "files.read", "{\"root\":\"project\",\"relativePath\":\"docs/readme.txt\",\"operation\":\"read\"}"));
        await session.ReceiveAsync(Invoke("file-bad", "files.read", "{\"root\":\"project\",\"relativePath\":\"../secret.txt\",\"operation\":\"read\"}"));
        await session.ReceiveAsync(Invoke("file-path-spoof", "files.read", "{\"root\":\"project\",\"relativePath\":\"docs/readme.txt\",\"path\":\"/tmp/evil\",\"operation\":\"read\"}"));
        if (OperatingSystem.IsWindows()) await session.ReceiveAsync(Invoke("file-device", "files.read", "{\"root\":\"project\",\"relativePath\":\"CON\",\"operation\":\"read\"}"));
        await session.ReceiveAsync(Invoke("process-ok", "process.run", "{\"executable\":\"tool\",\"arguments\":[\"--safe\"]}"));
        await session.ReceiveAsync(Invoke("process-bad", "process.run", "{\"executable\":\"tool\",\"arguments\":[\"--safe;rm -rf /\"]}"));
        await session.ReceiveAsync(Invoke("process-path-spoof", "process.run", "{\"executable\":\"tool\",\"path\":\"/tmp/evil\",\"arguments\":[\"--safe\"]}"));
        Assert.AreEqual(2, invoked);
        Assert.AreEqual(OperatingSystem.IsWindows() ? 5 : 4, frames.Count(frame => ErrorCode(frame) == "scope_denied"));
    }

    [TestMethod]
    public async Task ResourceExhaustionReturnsStableErrorAndReclaimsReservedResources()
    {
        const string json = """{"$schema":"neoastra-capabilities-v1.schema.json","version":1,"capabilities":[{"id":"main","views":["main"],"permissions":["documents:open"]}]}""";
        var manifest = Resolve(json, Catalog(), CurrentPlatform()); var disposed = 0; var frames = new ConcurrentQueue<string>();
        var builder = new NeoRpcBuilder(new NeoRpcOptions { AuthorizationService = new NeoCapabilityAuthorizationService(manifest), CapabilityManifest = manifest, MaximumResourcesPerSession = 1 });
        builder.AddCommand<JsonElement, JsonElement>("documents.open", (request, context, _) => { var resource = new TrackedResource(() => Interlocked.Increment(ref disposed)); try { context.Resources.Add(resource, 1); } catch { resource.DisposeAsync().GetAwaiter().GetResult(); throw; } return ValueTask.FromResult(request); }, CapabilityTestJsonContext.Default.JsonElement, CapabilityTestJsonContext.Default.JsonElement, new() { Permission = "documents:open" });
        await using var host = builder.Build();
        var session = host.OpenSession(new NeoRpcSessionIdentity("main", "resource-limit") { Platform = CurrentPlatform(), WholeViewTrust = true, IsMainFrame = true }, (frame, _) => { frames.Enqueue(frame); return ValueTask.CompletedTask; });
        await session.ReceiveAsync(Invoke("resource-first", "documents.open", "{}"));
        await session.ReceiveAsync(Invoke("resource-second", "documents.open", "{}"));
        Assert.AreEqual("too_many_requests", ErrorCode(frames.Last()));
        Assert.AreEqual(1, host.GetDiagnosticSnapshot().ActiveResources);
        await session.DisposeAsync();
        Assert.AreEqual(2, disposed, "Both the retained first resource and rejected second resource must be disposed.");
        Assert.AreEqual(0, host.GetDiagnosticSnapshot().ActiveResources);
    }

    [TestMethod]
    public void ReleaseProfilesAndLinuxOriginRequirementsFailClosed()
    {
        Assert.Throws<InvalidOperationException>(() => new NeoRpcBuilder(new NeoRpcOptions { SecurityProfile = NeoSecurityProfile.DevelopmentLocalApp, Release = true }));
        Assert.Throws<InvalidOperationException>(() => new NeoRpcBuilder(new NeoRpcOptions { IncludeDevelopmentErrorDetails = true }));
        Assert.Throws<ArgumentException>(() => new NeoRpcBuilder(new NeoRpcOptions { SecurityProfile = NeoSecurityProfile.DevelopmentLocalApp, Release = false, DevelopmentOrigin = new Uri("https://example.com:5173") }));
        _ = new NeoRpcBuilder(new NeoRpcOptions { SecurityProfile = NeoSecurityProfile.DevelopmentLocalApp, Release = false, DevelopmentOrigin = new Uri("http://127.0.0.1:5173") });
        const string basic = """{"$schema":"neoastra-capabilities-v1.schema.json","version":1,"capabilities":[{"id":"main","views":["main"],"permissions":["documents:open"]}]}""";
        var productionManifest = Resolve(basic, Catalog(), NeoCapabilityPlatform.Windows);
        Assert.Throws<NeoCapabilityValidationException>(() => NeoCapabilityManifest.Resolve(Encoding.UTF8.GetBytes(basic.Replace("\"main\"],", "\"main-*\"],")), Catalog(), new() { Platform = NeoCapabilityPlatform.Windows, Release = true, AllowReviewedViewPatterns = true }));
        Assert.Throws<NeoCapabilityValidationException>(() => NeoCapabilityManifest.Resolve(Encoding.UTF8.GetBytes(basic), Catalog(), new() { Platform = NeoCapabilityPlatform.Windows, Release = true, Profile = NeoSecurityProfile.RemoteContent }));
        Assert.Throws<InvalidOperationException>(() => new NeoRpcBuilder(new NeoRpcOptions { SecurityProfile = NeoSecurityProfile.RemoteContent, CapabilityManifest = productionManifest, AuthorizationService = new NeoCapabilityAuthorizationService(productionManifest) }));
        var mismatchedRegistration = new NeoRpcBuilder(new NeoRpcOptions { CapabilityManifest = productionManifest, AuthorizationService = new NeoCapabilityAuthorizationService(productionManifest) });
        mismatchedRegistration.AddCommand<JsonElement, JsonElement>("plugin.run", static (request, _, _) => ValueTask.FromResult(request), CapabilityTestJsonContext.Default.JsonElement, CapabilityTestJsonContext.Default.JsonElement, new() { Permission = "documents:open" });
        Assert.Throws<InvalidOperationException>(() => mismatchedRegistration.Build());
        var excessiveConcurrency = new NeoRpcBuilder(new NeoRpcOptions { CapabilityManifest = productionManifest, AuthorizationService = new NeoCapabilityAuthorizationService(productionManifest) });
        excessiveConcurrency.AddCommand<JsonElement, JsonElement>("documents.open", static (request, _, _) => ValueTask.FromResult(request), CapabilityTestJsonContext.Default.JsonElement, CapabilityTestJsonContext.Default.JsonElement, new() { Permission = "documents:open", MaximumConcurrency = 9 });
        Assert.Throws<InvalidOperationException>(() => excessiveConcurrency.Build());
        const string impossible = """{"$schema":"neoastra-capabilities-v1.schema.json","version":1,"capabilities":[{"id":"linux","views":["main"],"platforms":["linux"],"origins":["https://app.example"],"permissions":["documents:open"]}]}""";
        AssertCode("origin_unavailable", impossible, Catalog(), NeoCapabilityPlatform.Linux);
    }

    [TestMethod]
    public void PublishedSchemaMatchesRuntimeIdentifierAndOptionalDialogStructure()
    {
        using var schema = JsonDocument.Parse(File.ReadAllBytes(FindRepositoryFile("schemas", "neoastra-capabilities-v1.schema.json")));
        var definitions = schema.RootElement.GetProperty("$defs");
        Assert.AreEqual(128, definitions.GetProperty("policyIdentifier").GetProperty("maxLength").GetInt32());
        Assert.AreEqual(192, definitions.GetProperty("permissionIdentifier").GetProperty("maxLength").GetInt32());
        var dialog = definitions.GetProperty("dialogScope");
        Assert.IsFalse(dialog.GetProperty("required").EnumerateArray().Any(static value => value.GetString() == "extensions"));
        Assert.IsFalse(dialog.GetProperty("properties").GetProperty("extensions").TryGetProperty("minItems", out _));
        Assert.AreEqual(1, definitions.GetProperty("processScope").GetProperty("properties").GetProperty("executables").GetProperty("items").GetProperty("properties").GetProperty("path").GetProperty("minLength").GetInt32());
        Assert.AreEqual(128, definitions.GetProperty("shortcutScope").GetProperty("properties").GetProperty("accelerators").GetProperty("maxItems").GetInt32());

        var tooLongId = new string('a', 129);
        const string basic = "{\"$schema\":\"neoastra-capabilities-v1.schema.json\",\"version\":1,\"capabilities\":[{\"id\":\"ID\",\"views\":[\"main\"],\"permissions\":[\"documents:open\"]}]}";
        AssertCode("invalid_id", basic.Replace("ID", tooLongId), Catalog());
        Assert.Throws<ArgumentException>(() => new NeoPermissionDeclaration("documents:", 1, ["documents.open"], NeoPermissionRisk.Low, NeoScopeFamily.None));

        const string dialogWithoutExtensions = "{\"$schema\":\"neoastra-capabilities-v1.schema.json\",\"version\":1,\"capabilities\":[{\"id\":\"main\",\"views\":[\"main\"],\"permissions\":[{\"id\":\"dialogs:open\",\"scope\":{\"kinds\":[\"openFile\"],\"initialLocations\":[\"documents\"]}}]}]}";
        _ = Resolve(dialogWithoutExtensions, Catalog(includeScopes: true), CurrentPlatform());
        var dialogWithEmptyExtensions = dialogWithoutExtensions.Replace("}}]}]}", ",\"extensions\":[]}}]}]}");
        Assert.AreNotEqual(dialogWithoutExtensions, dialogWithEmptyExtensions);
        _ = Resolve(dialogWithEmptyExtensions, Catalog(includeScopes: true), CurrentPlatform());
    }

    [TestMethod]
    public void PublishedSchemaPermissionPatternEnforcesRuntimeSegmentBounds()
    {
        using var schema = JsonDocument.Parse(File.ReadAllBytes(FindRepositoryFile("schemas", "neoastra-capabilities-v1.schema.json")));
        var permissionIdentifier = schema.RootElement.GetProperty("$defs").GetProperty("permissionIdentifier");
        Assert.AreEqual(192, permissionIdentifier.GetProperty("maxLength").GetInt32());
        var pattern = permissionIdentifier.GetProperty("pattern").GetString()!;

        var validLongPermission = $"{new string('a', 100)}:{new string('b', 50)}";
        Assert.IsGreaterThan(128, validLongPermission.Length);
        Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(validLongPermission, pattern));
        Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch($"{new string('a', 129)}:b", pattern));
        Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch("documents:", pattern));
    }

    [TestMethod]
    public async Task CatalogAndToolInputsRejectAmbiguityAndResourceExhaustion()
    {
        var declaration = new NeoPermissionDeclaration("bounded:item", 1, ["bounded.item"], NeoPermissionRisk.Low, NeoScopeFamily.None);
        Assert.Throws<ArgumentException>(() => new NeoPluginPermissionCatalog("bounded.plugin", "1.0.0", "2.0.0", Enumerable.Repeat(declaration, 513)));
        var plugin = new NeoPluginPermissionCatalog("bounded.plugin", "1.0.0", "2.0.0", [declaration]);
        var builder = new NeoPermissionCatalogBuilder();
        for (var index = 0; index < 256; index++) builder.AddPlugin(new NeoPluginPermissionCatalog($"plugin.{index}", "1.0.0", "2.0.0", [new NeoPermissionDeclaration($"plugin{index}:item", 1, [$"plugin{index}.item"], NeoPermissionRisk.Low, NeoScopeFamily.None)]));
        Assert.Throws<InvalidOperationException>(() => builder.AddPlugin(plugin));

        var temp = Path.Combine(Path.GetTempPath(), $"neoastra-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var duplicate = Path.Combine(temp, "duplicate.json");
            await File.WriteAllTextAsync(duplicate, "{\"version\":1,\"permissions\":[{\"id\":\"bounded:item\",\"id\":\"other:item\",\"version\":1,\"commands\":[\"bounded.item\"],\"risk\":\"low\",\"scopeFamily\":\"none\"}]}");
            await AssertToolRejectsAsync(duplicate, temp);

            var complex = Path.Combine(temp, "complex.json");
            await File.WriteAllTextAsync(complex, "{\"version\":1,\"permissions\":[],\"padding\":[" + string.Join(',', Enumerable.Repeat("0", 50_001)) + "]}");
            await AssertToolRejectsAsync(complex, temp);

            var oversized = Path.Combine(temp, "oversized.json");
            await File.WriteAllBytesAsync(oversized, new byte[1024 * 1024 + 1]);
            await AssertToolRejectsAsync(oversized, temp);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [TestMethod]
    public async Task ToolRejectsUnknownBuildConfiguration()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"neoastra-configuration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var catalog = FindRepositoryFile("src", "NeoAstra.Tests", "Fixtures", "Capabilities", "catalog.json");
            await AssertToolRejectsAsync(catalog, temp, "Relese");
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    private static NeoPermissionCatalog Catalog(bool includeUrl = false, bool includeScopes = false)
    {
        var builder = new NeoPermissionCatalogBuilder().Add(new NeoPermissionDeclaration("documents:open", 1, ["documents.open"], NeoPermissionRisk.Sensitive, NeoScopeFamily.None));
        if (includeUrl || includeScopes) builder.Add(Scoped("opener:open", "opener.open", NeoScopeFamily.Url));
        if (includeScopes)
        {
            builder.Add(Scoped("files:read", "files.read", NeoScopeFamily.Filesystem)); builder.Add(Scoped("process:run", "process.run", NeoScopeFamily.Process));
            builder.Add(Scoped("clipboard:use", "clipboard.use", NeoScopeFamily.Clipboard)); builder.Add(Scoped("notifications:show", "notifications.show", NeoScopeFamily.Notifications));
            builder.Add(Scoped("dialogs:open", "dialogs.open", NeoScopeFamily.Dialogs)); builder.Add(Scoped("network:request", "network.request", NeoScopeFamily.Network)); builder.Add(Scoped("persistence:remember", "persistence.remember", NeoScopeFamily.Persistence)); builder.Add(Scoped("shortcuts:register", "shortcuts.register", NeoScopeFamily.Shortcuts));
        }
        return builder.Build();
        static NeoPermissionDeclaration Scoped(string id, string command, NeoScopeFamily family) => new(id, 1, [command], NeoPermissionRisk.High, family) { ScopeRequired = true, UnionSafe = true };
    }

    private static NeoCapabilityManifest Resolve(string json, NeoPermissionCatalog catalog, NeoCapabilityPlatform platform) => NeoCapabilityManifest.Resolve(Encoding.UTF8.GetBytes(json), catalog, new() { Platform = platform, Release = true, Profile = NeoSecurityProfile.ProductionLocalApp });
    private static void AssertCode(string code, string json, NeoPermissionCatalog catalog, NeoCapabilityPlatform platform = NeoCapabilityPlatform.Windows) { var error = Assert.Throws<NeoCapabilityValidationException>(() => Resolve(json, catalog, platform)); Assert.AreEqual(code, error.Code); }
    private static string Invoke(string id, string command, string arguments) => $$"""{"neoastra":1,"kind":"invoke","id":"{{id}}","command":"{{command}}","args":{{arguments}}}""";
    private static string? ErrorCode(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.TryGetProperty("error", out var error) ? error.GetProperty("code").GetString() : null; }
    private static NeoCapabilityPlatform CurrentPlatform() => OperatingSystem.IsWindows() ? NeoCapabilityPlatform.Windows : OperatingSystem.IsMacOS() ? NeoCapabilityPlatform.MacOS : NeoCapabilityPlatform.Linux;
    private static string FindRepositoryFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("The repository fixture could not be located.");
    }
    private static async Task AssertToolRejectsAsync(string catalog, string temp, string configurationName = "Release")
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var tool = FindRepositoryFile("src", "NeoAstra.Capabilities.Tool", "bin", configuration, "net10.0", "NeoAstra.Capabilities.Tool.dll");
        var capabilities = FindRepositoryFile("src", "NeoAstra.Tests", "Fixtures", "Capabilities", "capabilities.json");
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { tool, "resolve", "--capabilities", capabilities, "--catalog", catalog, "--platform", "windows", "--configuration", configurationName, Path.Combine(temp, "rejected.json") }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.AreEqual(1, process.ExitCode);
        Assert.AreEqual("configuration_error: The permission catalog or capability configuration is invalid.", error.Trim());
    }
    private sealed class AuditSink : INeoCapabilityDiagnosticSink { internal ConcurrentQueue<NeoCapabilityDiagnostic> Events { get; } = new(); public void Write(NeoCapabilityDiagnostic diagnostic) => Events.Enqueue(diagnostic); }
    private sealed class TrackedResource(Action dispose) : IAsyncDisposable { private int _disposed; public ValueTask DisposeAsync() { if (Interlocked.Exchange(ref _disposed, 1) == 0) dispose(); return ValueTask.CompletedTask; } }
}

[JsonSerializable(typeof(JsonElement))]
internal sealed partial class CapabilityTestJsonContext : JsonSerializerContext;
