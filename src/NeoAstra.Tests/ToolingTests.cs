// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NeoAstra.Desktop;
using NeoAstra.Rpc;
using NeoAstra.Tooling;

namespace NeoAstra.Tests;

[TestClass]
public sealed class ToolingTests
{
    [TestMethod]
    public void ProjectConfiguration_ResolvesFromConfigDirectoryAndRedactsSecrets()
    {
        using var fixture = new ProjectFixture();
        var project = NeoProjectConfiguration.Load(fixture.ConfigurationPath);
        Assert.AreEqual(Path.Combine(fixture.Root, "Client App"), project.FrontendRoot);
        Assert.AreEqual(Path.Combine(fixture.Root, "Client App", "dist"), project.DistDirectory);
        Assert.AreEqual(new Uri("http://127.0.0.1:5173/"), project.DevUrl);
        Assert.AreEqual("pnpm", project.DevCommand.Arguments[0]);
        CollectionAssert.Contains(project.ExcludedPrefixes.ToArray(), "/api"); CollectionAssert.Contains(project.ExcludedPrefixes.ToArray(), "/_neoastra");
        var inspect = project.ToInspectJson(redactSecrets: true);
        StringAssert.Contains(inspect, "[REDACTED]");
        Assert.DoesNotContain("top-secret", inspect, StringComparison.Ordinal);
    }

    [TestMethod]
    public void ProjectConfiguration_UsesStrictFrontendConventionsWhenConfigIsAbsent()
    {
        var root = Path.Combine(Path.GetTempPath(), "neoastra-conventions-" + Guid.NewGuid().ToString("N"));
        try
        {
            var frontend = Path.Combine(root, "ClientApp");
            Directory.CreateDirectory(frontend);
            File.WriteAllText(Path.Combine(frontend, "package.json"), "{}");
            File.WriteAllText(Path.Combine(frontend, "package-lock.json"), "{}");
            var project = NeoProjectConfiguration.Load(Path.Combine(root, "neoastra.json"));
            Assert.AreEqual("npm", project.PackageManager);
            Assert.AreEqual(Path.Combine(frontend, "dist"), project.DistDirectory);
            CollectionAssert.AreEqual(new[] { "npm", "run", "build" }, project.BuildCommand.Arguments.ToArray());

            File.WriteAllText(Path.Combine(frontend, "pnpm-lock.yaml"), "lockfileVersion: 9");
            Assert.AreEqual("configuration_lockfile", Assert.ThrowsExactly<NeoToolException>(() => NeoProjectConfiguration.Load(Path.Combine(root, "neoastra.json"))).Code);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void PackageManagerCommand_PrefersDirectNpmThenKnownManagersAndSupportsOverride()
    {
        var direct = NeoPackageManagerCommandResolver.Discover(
            "npm",
            Environment.CurrentDirectory,
            executable => executable == "npm");
        CollectionAssert.AreEqual(new[] { "npm" }, direct.Arguments.ToArray());

        var discoveryDirectory = Path.Combine(
            Path.GetTempPath(),
            "neoastra-package-manager-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(discoveryDirectory);
        try
        {
            var npmExecutable = OperatingSystem.IsWindows() ? "npm.cmd" : "npm";
            var fnm = NeoPackageManagerCommandResolver.Discover(
                "npm",
                discoveryDirectory,
                executable => executable == "fnm");
            CollectionAssert.AreEqual(
                new[] { "fnm", "exec", "--using=default", npmExecutable },
                fnm.Arguments.ToArray());

            File.WriteAllText(Path.Combine(discoveryDirectory, ".nvmrc"), "22\n");
            var projectVersionFnm = NeoPackageManagerCommandResolver.Discover(
                "npm",
                discoveryDirectory,
                executable => executable == "fnm");
            CollectionAssert.AreEqual(
                new[] { "fnm", "exec", npmExecutable },
                projectVersionFnm.Arguments.ToArray());
        }
        finally
        {
            Directory.Delete(discoveryDirectory, recursive: true);
        }

        var volta = NeoPackageManagerCommandResolver.Discover(
            "npm",
            Environment.CurrentDirectory,
            executable => executable == "volta");
        CollectionAssert.AreEqual(new[] { "volta", "run", "npm" }, volta.Arguments.ToArray());

        using var fixture = new ProjectFixture();
        File.WriteAllText(
            fixture.ConfigurationPath,
            File.ReadAllText(fixture.ConfigurationPath).Replace(
                "\"packageManager\": \"none\"",
                "\"packageManager\": \"npm\", \"packageManagerCommand\": [\"custom-node\", \"npm\"]",
                StringComparison.Ordinal));
        var project = NeoProjectConfiguration.Load(fixture.ConfigurationPath);
        var effective = NeoPackageManagerCommandResolver.Apply(
            project,
            new NeoCommand(["npm", "run", "build"]));
        CollectionAssert.AreEqual(
            new[] { "custom-node", "npm", "run", "build" },
            effective.Arguments.ToArray());
        CollectionAssert.AreEqual(
            new[] { "custom-node", "npm" },
            project.PackageManagerCommand.Arguments.ToArray());
    }

    [TestMethod]
    public async Task CommandLine_ProvidesGeneratedHelpAndOrderIndependentOptions()
    {
        using var fixture = new ProjectFixture();
        var help = await RunToolAsync(fixture.Root, ["--help"]);
        Assert.AreEqual(0, help.ExitCode);
        StringAssert.Contains(help.StandardOutput, "Commands:");
        StringAssert.Contains(help.StandardOutput, "frontend");

        var inspect = await RunToolAsync(
            fixture.Root,
            ["inspect", "--config", fixture.ConfigurationPath]);
        Assert.AreEqual(0, inspect.ExitCode);
        StringAssert.Contains(inspect.StandardOutput, "\"identifier\": \"dev.neoastra.fixture\"");
    }

    [TestMethod]
    public void ProjectConfiguration_RejectsUnknownDuplicateVersionAndRemoteOrigin()
    {
        using var fixture = new ProjectFixture();
        var text = File.ReadAllText(fixture.ConfigurationPath);
        File.WriteAllText(fixture.ConfigurationPath, text.Replace("\"version\": 1,", "\"version\": 2,"));
        Assert.AreEqual("configuration_version", Assert.ThrowsExactly<NeoToolException>(() => NeoProjectConfiguration.Load(fixture.ConfigurationPath)).Code);
        File.WriteAllText(fixture.ConfigurationPath, text.Replace("\"version\": 1,", "\"version\": 1, \"version\": 1,"));
        StringAssert.Contains(Assert.ThrowsExactly<NeoToolException>(() => NeoProjectConfiguration.Load(fixture.ConfigurationPath)).Code, "version");
        File.WriteAllText(fixture.ConfigurationPath, text.Replace("http://127.0.0.1:5173", "http://localhost:5173"));
        Assert.AreEqual("development_origin", Assert.ThrowsExactly<NeoToolException>(() => NeoProjectConfiguration.Load(fixture.ConfigurationPath)).Code);
        File.WriteAllText(fixture.ConfigurationPath, text.Replace("\"app\":", "\"unknown\": true, \"app\":"));
        StringAssert.Contains(Assert.ThrowsExactly<NeoToolException>(() => NeoProjectConfiguration.Load(fixture.ConfigurationPath)).Code, "unknown");
    }

    [TestMethod]
    public void ProjectSchema_HasUniqueKeysAndRepresentativeRuntimeParity()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => ValidateUniqueJsonProperties(Encoding.UTF8.GetBytes("{\"value\":1,\"value\":2}")));
        var schemaPath = Path.Combine(FindRepositoryRoot(), "schemas", "neoastra-project-v1.schema.json"); var schemaBytes = File.ReadAllBytes(schemaPath); ValidateUniqueJsonProperties(schemaBytes);
        using var schema = JsonDocument.Parse(schemaBytes); var root = schema.RootElement; var frontend = root.GetProperty("properties").GetProperty("frontend").GetProperty("properties");
        Assert.AreEqual(1, frontend.EnumerateObject().Count(static property => property.NameEquals("contractCommand")));
        CollectionAssert.AreEqual(new[] { "dotnet", "build", "--no-restore" }, frontend.GetProperty("contractCommand").GetProperty("default").EnumerateArray().Select(static item => item.GetString()).ToArray());
        Assert.AreEqual("^[A-Za-z_][A-Za-z0-9_]*$", root.GetProperty("$defs").GetProperty("environmentName").GetProperty("pattern").GetString());
        var assets = root.GetProperty("properties").GetProperty("assets").GetProperty("properties");
        Assert.IsTrue(assets.GetProperty("spaRoutePrefixes").GetProperty("uniqueItems").GetBoolean()); Assert.IsTrue(assets.GetProperty("excludedPrefixes").GetProperty("uniqueItems").GetBoolean());
        Assert.AreEqual(128, assets.GetProperty("referrerPolicy").GetProperty("maxLength").GetInt32()); Assert.IsTrue(assets.GetProperty("origin").TryGetProperty("not", out _)); Assert.IsTrue(assets.GetProperty("csp").TryGetProperty("allOf", out _));

        using var fixture = new ProjectFixture(); var text = File.ReadAllText(fixture.ConfigurationPath);
        CollectionAssert.AreEqual(new[] { "dotnet", "build", "--no-restore" }, NeoProjectConfiguration.Load(fixture.ConfigurationPath).ContractCommand.Arguments.ToArray());
        AssertConfigurationRejected(text.Replace("\"TOKEN\":", "\"TOKEN-NAME\":"), "frontend_environment");
        AssertConfigurationRejected(text.Replace("\"csp\":", "\"spaRoutePrefixes\": [\"/notes\", \"/notes\"], \"csp\":"), "spaRoutePrefixes");
        AssertConfigurationRejected(text.Replace("\"cacheHashedAssets\": true,", $"\"cacheHashedAssets\": true, \"referrerPolicy\": \"{new string('x', 129)}\","), "referrerPolicy");
        AssertConfigurationRejected(text.Replace("app://fixture", "http://fixture"), "origin");
        AssertConfigurationRejected(text.Replace("default-src 'self'", "default-src *"), "csp");

        void AssertConfigurationRejected(string json, string codeFragment) { File.WriteAllText(fixture.ConfigurationPath, json); StringAssert.Contains(Assert.ThrowsExactly<NeoToolException>(() => NeoProjectConfiguration.Load(fixture.ConfigurationPath)).Code, codeFragment); }
    }

    [TestMethod]
    public void AdvancedCapabilities_ResolveForReleaseAndRestrictThePreviewView()
    {
        var root = FindRepositoryRoot(); var bytes = File.ReadAllBytes(Path.Combine(root, "samples", "NeoAstra.Sample.Advanced", "capabilities", "main.json"));
        var catalog = new NeoPermissionCatalogBuilder()
            .Add(new NeoPermissionDeclaration("tour:read", 1, ["tour.hello"], NeoPermissionRisk.Low, NeoScopeFamily.None))
            .Add(new NeoPermissionDeclaration("tour:control", 1, ["tour.delay", "tour.stream", "tour.setDirty", "tour.showPreview"], NeoPermissionRisk.Low, NeoScopeFamily.None))
            .Add(new NeoPermissionDeclaration("tour:events", 1, ["tour.activity"], NeoPermissionRisk.Low, NeoScopeFamily.None))
            .AddNeoAstraDesktopPermissions()
            .Build();
        var platform = OperatingSystem.IsWindows() ? NeoCapabilityPlatform.Windows : OperatingSystem.IsMacOS() ? NeoCapabilityPlatform.MacOS : NeoCapabilityPlatform.Linux;
        var manifest = NeoCapabilityManifest.Resolve(bytes, catalog, new() { Platform = platform, Release = true, Profile = NeoSecurityProfile.ProductionLocalApp });
        NeoRpcContext Context(string view) => new(new NeoRpcSessionIdentity(view, "reference-session") { IsMainFrame = true, WholeViewTrust = true, Platform = platform }, "reference-correlation", default, null!);
        var main = manifest.Match(new(Context("main"), "tour.hello", "tour:read", false, default));
        var preview = manifest.Match(new(Context("preview"), "tour.hello", "tour:read", false, default));
        var deniedDesktop = manifest.Match(new(Context("preview"), "desktop.system.theme", "system-info:theme", false, default));
        Assert.IsTrue(main.Allowed); Assert.AreEqual(NeoCapabilityDecisionCodes.Allowed, main.Code);
        Assert.IsTrue(preview.Allowed); Assert.AreEqual(NeoCapabilityDecisionCodes.Allowed, preview.Code);
        Assert.IsFalse(deniedDesktop.Allowed); Assert.AreEqual(NeoCapabilityDecisionCodes.PermissionMissing, deniedDesktop.Code);
    }

    [TestMethod]
    public void OriginPolicy_RequiresExactIpLoopbackUnlessExplicitlyOptedIn()
    {
        Assert.AreEqual("127.0.0.1", NeoOriginPolicy.ValidateDevelopmentUrl("http://127.0.0.1:5173", false).Host);
        Assert.AreEqual("[::1]", NeoOriginPolicy.ValidateDevelopmentUrl("http://[::1]:5173", false).Host);
        Assert.ThrowsExactly<NeoToolException>(() => NeoOriginPolicy.ValidateDevelopmentUrl("http://127.0.0.2:5173", false));
        Assert.ThrowsExactly<NeoToolException>(() => NeoOriginPolicy.ValidateDevelopmentUrl("http://localhost:5173", true));
        Assert.AreEqual("192.0.2.1", NeoOriginPolicy.ValidateDevelopmentUrl("http://192.0.2.1:5173", true).Host);
    }

    [TestMethod]
    public void ProductionBuildPolicy_RejectsImplicitPackageInstallation()
    {
        using var fixture = new ProjectFixture(); var text = File.ReadAllText(fixture.ConfigurationPath).Replace("\"packageManager\": \"none\"", "\"packageManager\": \"pnpm\"");
        File.WriteAllText(fixture.ConfigurationPath, text.Replace("[\"pnpm\", \"build\"]", "[\"pnpm\", \"--silent\", \"install\"]"));
        Assert.AreEqual("implicit_install", Assert.ThrowsExactly<NeoToolException>(() => NeoCommandPolicy.EnsureProductionBuildDoesNotInstall(NeoProjectConfiguration.Load(fixture.ConfigurationPath))).Code);
    }

    [TestMethod]
    public async Task Init_ValidatesBeforeMutationAndAtomicallyPreservesForcedBackup()
    {
        using var fixture = new ProjectFixture(); var original = File.ReadAllBytes(fixture.ConfigurationPath); var backup = fixture.ConfigurationPath + ".bak";
        var invalidNew = Path.Combine(fixture.Root, "invalid-new.json");
        Assert.AreEqual(2, (await RunToolAsync(fixture.Root, InitArguments(invalidNew, "invalid", force: false))).ExitCode);
        Assert.IsFalse(File.Exists(invalidNew)); Assert.IsFalse(File.Exists(invalidNew + ".bak")); Assert.AreEqual(0, Directory.GetFiles(fixture.Root, "*.tmp").Length);

        Assert.AreEqual(2, (await RunToolAsync(fixture.Root, InitArguments(fixture.ConfigurationPath, "invalid", force: true))).ExitCode);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(fixture.ConfigurationPath)); Assert.IsFalse(File.Exists(backup)); Assert.AreEqual(0, Directory.GetFiles(fixture.Root, "*.tmp").Length);

        Assert.AreEqual(0, (await RunToolAsync(fixture.Root, InitArguments(fixture.ConfigurationPath, "dev.neoastra.replaced", force: true))).ExitCode);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(backup)); Assert.AreEqual("dev.neoastra.replaced", NeoProjectConfiguration.Load(fixture.ConfigurationPath).Identifier); Assert.AreEqual(0, Directory.GetFiles(fixture.Root, "*.tmp").Length);

        var replacement = File.ReadAllBytes(fixture.ConfigurationPath); var preservedBackup = File.ReadAllBytes(backup);
        Assert.AreEqual(2, (await RunToolAsync(fixture.Root, InitArguments(fixture.ConfigurationPath, "dev.neoastra.second", force: true))).ExitCode);
        CollectionAssert.AreEqual(replacement, File.ReadAllBytes(fixture.ConfigurationPath)); CollectionAssert.AreEqual(preservedBackup, File.ReadAllBytes(backup)); Assert.AreEqual(0, Directory.GetFiles(fixture.Root, "*.tmp").Length);
    }

    [TestMethod]
    public void ReadinessProbe_RejectsEveryRedirectAndUnexpectedOrigin()
    {
        var configured = new Uri("http://127.0.0.1:5173/ready");
        using var sameOrigin = new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://127.0.0.1:5173/final") } };
        using var otherOrigin = new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://127.0.0.1:5174/ready") } };
        Assert.AreEqual("readiness_redirect", Assert.ThrowsExactly<NeoToolException>(() => NeoReadinessProbe.ValidateResponse(configured, sameOrigin)).Code);
        Assert.AreEqual("readiness_redirect", Assert.ThrowsExactly<NeoToolException>(() => NeoReadinessProbe.ValidateResponse(configured, otherOrigin)).Code);
        using var ready = new HttpResponseMessage(HttpStatusCode.NotFound); Assert.IsTrue(NeoReadinessProbe.ValidateResponse(configured, ready));
        using var waiting = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); Assert.IsFalse(NeoReadinessProbe.ValidateResponse(configured, waiting));
    }

    [TestMethod]
    public void AssetManifestBuilder_IsDeterministicSortedAndRejectsSourceMapsAndCaseCollisions()
    {
        using var fixture = new ProjectFixture(); var dist = Path.Combine(fixture.Root, "Client App", "dist"); Directory.CreateDirectory(Path.Combine(dist, "assets"));
        File.WriteAllText(Path.Combine(dist, "index.html"), "<!doctype html>"); File.WriteAllText(Path.Combine(dist, "assets", "z.01234567.js"), "z"); File.WriteAllText(Path.Combine(dist, "assets", "index-D_Fr1Co1.js"), "i"); File.WriteAllText(Path.Combine(dist, "assets", "a.css"), "a");
        var project = NeoProjectConfiguration.Load(fixture.ConfigurationPath); var first = Path.Combine(fixture.Root, "first.json"); var second = Path.Combine(fixture.Root, "second.json");
        Assert.AreEqual(NeoAssetManifestBuilder.Build(project, first), NeoAssetManifestBuilder.Build(project, second));
        CollectionAssert.AreEqual(File.ReadAllBytes(first), File.ReadAllBytes(second));
        var manifest = NeoAssetManifest.Load(first); CollectionAssert.AreEqual(manifest.Assets.OrderBy(static entry => entry.Path, StringComparer.Ordinal).ToArray(), manifest.Assets.ToArray());
        Assert.IsTrue(manifest.Assets.Where(entry => entry.Path.EndsWith(".js", StringComparison.Ordinal)).All(entry => entry.CacheControl == "public,max-age=31536000,immutable"));
        File.WriteAllText(Path.Combine(dist, "app.js.map"), "{}"); Assert.AreEqual("asset_source_map", Assert.ThrowsExactly<NeoToolException>(() => NeoAssetManifestBuilder.Build(project, first)).Code); File.Delete(Path.Combine(dist, "app.js.map"));
        File.WriteAllText(Path.Combine(dist, "A.txt"), "a"); File.WriteAllText(Path.Combine(dist, "a.txt"), "b");
        if (!OperatingSystem.IsWindows()) Assert.AreEqual("asset_case_collision", Assert.ThrowsExactly<NeoToolException>(() => NeoAssetManifestBuilder.Build(project, first)).Code);
    }

    [TestMethod]
    public void AssetManifestCopy_VerifiesHashesAndCopiesOnlyListedFiles()
    {
        using var fixture = new ProjectFixture(); var dist = Path.Combine(fixture.Root, "Client App", "dist"); Directory.CreateDirectory(dist); File.WriteAllText(Path.Combine(dist, "index.html"), "one");
        var project = NeoProjectConfiguration.Load(fixture.ConfigurationPath); var manifest = Path.Combine(fixture.Root, "assets.json"); NeoAssetManifestBuilder.Build(project, manifest);
        File.WriteAllText(Path.Combine(dist, "index.html"), "changed");
        Assert.AreEqual("asset_copy_hash", Assert.ThrowsExactly<NeoToolException>(() => NeoAssetManifestBuilder.CopyManifestAssets(manifest, dist, Path.Combine(fixture.Root, "publish"))).Code);
    }

    [TestMethod]
    public void AssetFileSnapshot_UsesOneStreamForExactLengthAndHash()
    {
        using var fixture = new ProjectFixture(); var path = Path.Combine(fixture.Root, "snapshot.bin"); var bytes = Enumerable.Range(0, 200_000).Select(static value => (byte)(value * 31)).ToArray(); File.WriteAllBytes(path, bytes);
        var snapshot = NeoAssetManifestBuilder.ReadFileSnapshot(path);
        Assert.AreEqual(bytes.LongLength, snapshot.Length); Assert.AreEqual(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), snapshot.Sha256);
    }

    [TestMethod]
    public void AssetManifestBuilder_RejectsConfiguredSizeOverflowAndDirectoryLinks()
    {
        using var fixture = new ProjectFixture(); var dist = Path.Combine(fixture.Root, "Client App", "dist"); Directory.CreateDirectory(dist); File.WriteAllText(Path.Combine(dist, "index.html"), "too large");
        File.WriteAllText(fixture.ConfigurationPath, File.ReadAllText(fixture.ConfigurationPath).Replace("\"csp\":", "\"maximumFileBytes\": 1, \"csp\":"));
        var project = NeoProjectConfiguration.Load(fixture.ConfigurationPath); Assert.AreEqual("asset_size", Assert.ThrowsExactly<NeoToolException>(() => NeoAssetManifestBuilder.Build(project, Path.Combine(fixture.Root, "assets.json"))).Code);
        File.WriteAllText(fixture.ConfigurationPath, File.ReadAllText(fixture.ConfigurationPath).Replace("\"maximumFileBytes\": 1, ", string.Empty));
        var outside = Path.Combine(fixture.Root, "outside"); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "escaped.js"), "escaped");
        try { Directory.CreateSymbolicLink(Path.Combine(dist, "linked"), outside); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return; }
        project = NeoProjectConfiguration.Load(fixture.ConfigurationPath); Assert.AreEqual("asset_link", Assert.ThrowsExactly<NeoToolException>(() => NeoAssetManifestBuilder.Build(project, Path.Combine(fixture.Root, "assets.json"))).Code);
        Directory.Delete(Path.Combine(dist, "linked"));
        var linkedAncestor = Path.Combine(fixture.Root, "Client App", "linked-root"); Directory.CreateSymbolicLink(linkedAncestor, outside);
        Directory.CreateDirectory(Path.Combine(outside, "dist")); File.WriteAllText(Path.Combine(outside, "dist", "index.html"), "linked ancestor");
        File.WriteAllText(fixture.ConfigurationPath, File.ReadAllText(fixture.ConfigurationPath).Replace("Client App/dist", "Client App/linked-root/dist"));
        project = NeoProjectConfiguration.Load(fixture.ConfigurationPath); Assert.AreEqual("asset_link", Assert.ThrowsExactly<NeoToolException>(() => NeoAssetManifestBuilder.Build(project, Path.Combine(fixture.Root, "assets.json"))).Code);
    }

    [TestMethod]
    public void AssetManifestBuilder_EnforcesFileCountDuringEnumeration()
    {
        using var fixture = new ProjectFixture(); var dist = Path.Combine(fixture.Root, "Client App", "dist"); Directory.CreateDirectory(dist); File.WriteAllText(Path.Combine(dist, "index.html"), "entry"); File.WriteAllText(Path.Combine(dist, "second.js"), "second");
        File.WriteAllText(fixture.ConfigurationPath, File.ReadAllText(fixture.ConfigurationPath).Replace("\"csp\":", "\"maximumFiles\": 1, \"csp\":"));
        Assert.AreEqual("asset_count", Assert.ThrowsExactly<NeoToolException>(() => NeoAssetManifestBuilder.Build(NeoProjectConfiguration.Load(fixture.ConfigurationPath), Path.Combine(fixture.Root, "assets.json"))).Code);
    }

    [TestMethod]
    public void AssetPaths_RejectPortableDeviceNamesAndTrailingDotOrSpaceOnEveryPlatform()
    {
        foreach (var path in new[] { "CON", "assets/con.txt", "NUL.json", "aux.css", "PRN.js", "COM1.svg", "com9.data", "LPT1", "lpt9.txt", "assets/name.", "assets/name " })
        {
            Assert.AreEqual("asset_path", Assert.ThrowsExactly<NeoToolException>(() => NeoAssetManifestBuilder.ValidateRelativePath(path)).Code, path);
            var entries = new[] { new NeoAssetEntry(path, 0, new string('0', 64), NeoAssetManifest.GetContentType(path), "no-cache"), new NeoAssetEntry("index.html", 0, new string('0', 64), "text/html; charset=utf-8", "no-cache") }.OrderBy(static entry => entry.Path, StringComparer.Ordinal).ToArray();
            Assert.ThrowsExactly<ArgumentException>(() => new NeoAssetManifest(1, "index.html", "index.html", "app://fixture", "default-src 'self'; object-src 'none'", "no-referrer", [], ["/api", "/_neoastra"], entries), path);
        }
    }

    [TestMethod]
    public void FrontendFingerprint_TracksEffectiveInputsAndExcludesBuildOutput()
    {
        using var fixture = new ProjectFixture();
        var project = NeoProjectConfiguration.Load(fixture.ConfigurationPath);
        var source = Path.Combine(project.FrontendRoot, "source.js");
        var generated = Path.Combine(fixture.Root, "generated.js");
        var output = Path.Combine(fixture.Root, "obj", "inputs.sha256");
        Directory.CreateDirectory(project.DistDirectory);
        File.WriteAllText(source, "console.log('first');");
        File.WriteAllText(Path.Combine(project.DistDirectory, "index.html"), "first");

        var initial = NeoFrontendFingerprint.Write(project, output, prebuilt: false, "Debug", []);
        File.WriteAllText(Path.Combine(project.DistDirectory, "index.html"), "changed output");
        Assert.AreEqual(initial, NeoFrontendFingerprint.Write(project, output, prebuilt: false, "Debug", []), "Normal-build output must not feed its own fingerprint.");

        File.WriteAllText(source, "console.log('second');");
        var sourceChanged = NeoFrontendFingerprint.Write(project, output, prebuilt: false, "Debug", []);
        Assert.AreNotEqual(initial, sourceChanged);
        File.Delete(source);
        var sourceDeleted = NeoFrontendFingerprint.Write(project, output, prebuilt: false, "Debug", []);
        Assert.AreNotEqual(sourceChanged, sourceDeleted);

        var changedConfiguration = File.ReadAllText(fixture.ConfigurationPath)
            .Replace("Fixture", "Changed fixture", StringComparison.Ordinal)
            .Replace("\"packageManager\": \"none\"", "\"packageManager\": \"none\", \"lockfile\": \"external.lock\"", StringComparison.Ordinal);
        File.WriteAllText(fixture.ConfigurationPath, changedConfiguration);
        var lockfile = Path.Combine(fixture.Root, "external.lock");
        File.WriteAllText(lockfile, "lock-v1");
        project = NeoProjectConfiguration.Load(fixture.ConfigurationPath);
        var configurationChanged = NeoFrontendFingerprint.Write(project, output, prebuilt: false, "Debug", []);
        Assert.AreNotEqual(sourceDeleted, configurationChanged);
        File.WriteAllText(lockfile, "lock-v2");
        Assert.AreNotEqual(configurationChanged, NeoFrontendFingerprint.Write(project, output, prebuilt: false, "Debug", []));

        File.WriteAllText(generated, "generated-v1");
        var generatedChanged = NeoFrontendFingerprint.Write(project, output, prebuilt: false, "Debug", [generated]);
        File.WriteAllText(generated, "generated-v2");
        Assert.AreNotEqual(generatedChanged, NeoFrontendFingerprint.Write(project, output, prebuilt: false, "Debug", [generated]));

        var prebuilt = NeoFrontendFingerprint.Write(project, output, prebuilt: true, "Debug", []);
        File.WriteAllText(Path.Combine(project.DistDirectory, "index.html"), "changed prebuilt output");
        Assert.AreNotEqual(prebuilt, NeoFrontendFingerprint.Write(project, output, prebuilt: true, "Debug", []));
    }

    [TestMethod]
    public async Task FrontendDependencyRestore_UsesCommittedNpmLockfileAndIncrementalState()
    {
        using var fixture = new ProjectFixture();
        var frontend = Path.Combine(fixture.Root, "Client App");
        var lockfile = Path.Combine(frontend, "package-lock.json");
        File.WriteAllText(Path.Combine(frontend, "package.json"), "{\"name\":\"restore-fixture\"}");
        File.WriteAllText(lockfile, "{\"lockfileVersion\":3}");
        File.WriteAllText(fixture.ConfigurationPath, File.ReadAllText(fixture.ConfigurationPath)
            .Replace("\"packageManager\": \"none\"", "\"packageManager\": \"npm\", \"lockfile\": \"Client App/package-lock.json\"", StringComparison.Ordinal));
        var project = NeoProjectConfiguration.Load(fixture.ConfigurationPath);
        var first = new FakeProcess(); first.Exit(0);
        var changed = new FakeProcess(); changed.Exit(0);
        var failed = new FakeProcess(); failed.Exit(19);
        var factory = new FakeFactory([], first, changed, failed);
        var restorer = new NeoFrontendDependencyRestorer(factory);

        Assert.IsTrue(await restorer.RestoreAsync(project));
        CollectionAssert.AreEqual(new[] { "npm", "ci", "--no-audit", "--no-fund" }, factory.Starts[0].Command.Arguments.ToArray());
        Assert.IsFalse(await restorer.RestoreAsync(project));
        Assert.HasCount(1, factory.Starts);

        File.WriteAllText(lockfile, "{\"lockfileVersion\":3,\"changed\":true}");
        Assert.IsTrue(await restorer.RestoreAsync(project));
        Assert.HasCount(2, factory.Starts);

        File.WriteAllText(lockfile, "{\"lockfileVersion\":3,\"changed\":\"again\"}");
        Assert.AreEqual("package_restore_failed", (await Assert.ThrowsExactlyAsync<NeoToolException>(() => restorer.RestoreAsync(project))).Code);
        Assert.IsFalse(File.Exists(Path.Combine(frontend, "node_modules", ".neoastra-restore.sha256")));

        File.Delete(lockfile);
        Assert.AreEqual("lockfile_missing", (await Assert.ThrowsExactlyAsync<NeoToolException>(() => restorer.RestoreAsync(project))).Code);
    }

    [TestMethod]
    public async Task IntegratedFrontendBuild_IsIncrementalAndFeedsBuildRunAndPublishOutputs()
    {
        using var fixture = new IntegratedBuildFixture();
        await fixture.RunDotNetAsync("restore", fixture.ProjectPath, "--nologo");

        var first = await fixture.BuildAsync();
        StringAssert.Contains(first, "FRONTEND_EXECUTED");
        Assert.AreEqual("first", File.ReadAllText(fixture.BuiltIndex));

        var unchanged = await fixture.BuildAsync();
        Assert.IsFalse(unchanged.Contains("FRONTEND_EXECUTED", StringComparison.Ordinal));

        File.WriteAllText(fixture.SourcePath, "second");
        StringAssert.Contains(await fixture.BuildAsync(), "FRONTEND_EXECUTED");
        Assert.AreEqual("second", File.ReadAllText(fixture.BuiltIndex));

        var added = Path.Combine(fixture.FrontendRoot, "added.js");
        File.WriteAllText(added, "added");
        StringAssert.Contains(await fixture.BuildAsync(), "FRONTEND_EXECUTED");
        File.Delete(added);
        StringAssert.Contains(await fixture.BuildAsync(), "FRONTEND_EXECUTED");

        File.WriteAllText(fixture.ConfigurationPath, File.ReadAllText(fixture.ConfigurationPath).Replace("Integrated fixture", "Changed fixture", StringComparison.Ordinal));
        StringAssert.Contains(await fixture.BuildAsync(), "FRONTEND_EXECUTED");

        fixture.WriteContract(2);
        StringAssert.Contains(await fixture.BuildAsync(), "FRONTEND_EXECUTED");

        File.WriteAllText(fixture.SourcePath, "opted-out");
        var optedOut = await fixture.BuildAsync("-p:NeoAstraBuildFrontend=false");
        Assert.IsFalse(optedOut.Contains("FRONTEND_EXECUTED", StringComparison.Ordinal));
        Assert.IsFalse(File.Exists(fixture.BuiltIndex));
        StringAssert.Contains(await fixture.BuildAsync(), "FRONTEND_EXECUTED");

        var publishDirectory = Path.Combine(fixture.Root, "publish");
        var published = await fixture.RunDotNetAsync("publish", fixture.ProjectPath, "--no-restore", "--nologo", "-c", "Debug", "-o", publishDirectory);
        Assert.IsFalse(published.Contains("FRONTEND_EXECUTED", StringComparison.Ordinal));
        Assert.AreEqual("opted-out", File.ReadAllText(Path.Combine(publishDirectory, "assets", "index.html")));

        File.WriteAllText(Path.Combine(fixture.PreparedAssets, "stale.js"), "stale");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(fixture.BuiltIndex)!, "stale.js"), "stale");
        File.WriteAllText(fixture.SourcePath, "stale-removed");
        StringAssert.Contains(await fixture.BuildAsync(), "FRONTEND_EXECUTED");
        Assert.IsFalse(File.Exists(Path.Combine(fixture.PreparedAssets, "stale.js")));
        Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(fixture.BuiltIndex)!, "stale.js")));

        File.WriteAllText(fixture.DistIndex, "explicit prebuilt");
        var prebuilt = await fixture.BuildAsync("-p:NeoAstraPrebuiltAssets=true", $"-p:NeoAstraPrebuiltAssetDirectory={fixture.DistDirectory}");
        Assert.IsFalse(prebuilt.Contains("FRONTEND_EXECUTED", StringComparison.Ordinal));
        Assert.AreEqual("explicit prebuilt", File.ReadAllText(fixture.BuiltIndex));

        await fixture.RunDotNetAsync("clean", fixture.ProjectPath, "--nologo", "-c", "Debug");
        StringAssert.Contains(await fixture.BuildAsync(), "FRONTEND_EXECUTED");

        await fixture.RunDotNetAsync("restore", fixture.DisabledProjectPath, "--nologo");
        await fixture.RunDotNetAsync("build", fixture.DisabledProjectPath, "--no-restore", "--nologo", "-c", "Debug");
    }

    [TestMethod]
    public async Task FrontendDependencyRestoreFailure_IsReportedAsMsBuildError()
    {
        using var fixture = new IntegratedBuildFixture();
        await fixture.RunDotNetAsync("restore", fixture.ProjectPath, "--nologo");
        fixture.ConfigureNpmRestore();

        var result = await fixture.BuildWithoutPathAsync();

        Assert.AreNotEqual(0, result.ExitCode);
        StringAssert.Contains(result.Output, "error NEOASTRA1001:");
        StringAssert.Contains(result.Output, "executable_missing: Configured executable 'npm' was not found on PATH");
        Assert.DoesNotContain("MSB3073", result.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task DevelopmentOrchestrator_OrdersReadinessBeforeBackendAndStopsBothOnFailure()
    {
        using var fixture = new ProjectFixture(); var project = NeoProjectConfiguration.Load(fixture.ConfigurationPath); var order = new List<string>();
        var contract = new FakeProcess(); contract.Exit(0); var frontend = new FakeProcess(); var backend = new FakeProcess(); var factory = new FakeFactory(order, contract, frontend, backend); var readiness = new FakeReadiness(order);
        var task = new NeoDevelopmentOrchestrator(factory, readiness).RunAsync(project, CancellationToken.None);
        await readiness.Started.Task.WaitAsync(TimeSpan.FromSeconds(1)); readiness.Release.SetResult();
        await factory.BackendStarted.Task.WaitAsync(TimeSpan.FromSeconds(1)); backend.Exit(7);
        Assert.AreEqual(7, await task.WaitAsync(TimeSpan.FromSeconds(1)));
        CollectionAssert.AreEqual(new[] { "start:contract", "start:frontend", "readiness", "start:backend" }, order);
        Assert.IsTrue(frontend.Stopped); Assert.IsTrue(backend.Stopped);
        CollectionAssert.AreEqual(project.ContractCommand.Arguments.ToArray(), factory.Starts[0].Command.Arguments.ToArray());
        CollectionAssert.AreEqual(project.DevCommand.Arguments.ToArray(), factory.Starts[1].Command.Arguments.ToArray());
        Assert.AreEqual("false", factory.Starts[0].Environment["NeoAstraBuildFrontend"]);
        Assert.AreEqual("false", factory.Starts[2].Environment["NeoAstraBuildFrontend"]);
    }

    [TestMethod]
    public async Task DevelopmentOrchestrator_FailsFastWhenFrontendExitsBeforeReadiness()
    {
        using var fixture = new ProjectFixture(); var project = NeoProjectConfiguration.Load(fixture.ConfigurationPath); var order = new List<string>();
        var contract = new FakeProcess(); contract.Exit(0); var frontend = new FakeProcess(); frontend.Exit(7); var factory = new FakeFactory(order, contract, frontend); var readiness = new FakeReadiness(order);
        Assert.AreEqual(7, await new NeoDevelopmentOrchestrator(factory, readiness).RunAsync(project, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1)));
        CollectionAssert.AreEqual(new[] { "start:contract", "start:frontend", "readiness" }, order);
        Assert.AreEqual(2, factory.Starts.Count); Assert.IsTrue(frontend.Stopped);
    }

    private sealed class IntegratedBuildFixture : IDisposable
    {
        private readonly string _targetsPath;
        private readonly string _toolPath;

        internal IntegratedBuildFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "neoastra-integrated-build-" + Guid.NewGuid().ToString("N"));
            FrontendRoot = Path.Combine(Root, "ClientApp");
            DistDirectory = Path.Combine(FrontendRoot, "dist");
            SourcePath = Path.Combine(FrontendRoot, "source.txt");
            DistIndex = Path.Combine(DistDirectory, "index.html");
            ConfigurationPath = Path.Combine(Root, "neoastra.json");
            ProjectPath = Path.Combine(Root, "Fixture.csproj");
            DisabledProjectPath = Path.Combine(Root, "Disabled", "Disabled.csproj");
            var repository = FindRepositoryRoot();
            var propsPath = Path.Combine(repository, "src", "NeoAstra", "Build", "Sdk", "NeoAstra.props");
            _targetsPath = Path.Combine(repository, "src", "NeoAstra", "Build", "Sdk", "NeoAstra.Build.targets");
            _toolPath = Path.Combine(repository, "src", "NeoAstra.Tool", "bin", "Release", "net10.0", "NeoAstra.Tool.dll");
            Assert.IsTrue(File.Exists(_toolPath), "NeoAstra.Tool must be built before its MSBuild integration test.");

            Directory.CreateDirectory(FrontendRoot);
            File.WriteAllText(SourcePath, "first");
            File.WriteAllText(Path.Combine(FrontendRoot, "frontend.proj"), """
                <Project>
                  <Target Name="BuildFrontend">
                    <Message Text="FRONTEND_EXECUTED" Importance="high" />
                    <MakeDir Directories="dist" />
                    <Copy SourceFiles="source.txt" DestinationFiles="dist/index.html" />
                  </Target>
                </Project>
                """);
            File.WriteAllText(ConfigurationPath, """
                {
                  "$schema": "neoastra-project-v1.schema.json",
                  "version": 1,
                  "app": { "identifier": "dev.neoastra.integrated", "displayName": "Integrated fixture" },
                  "frontend": {
                    "root": "ClientApp", "devCommand": ["dotnet", "--info"], "devUrl": "http://127.0.0.1:5173",
                    "buildCommand": ["dotnet", "msbuild", "frontend.proj", "-nologo", "-v:minimal", "-t:BuildFrontend"],
                    "dist": "ClientApp/dist", "spaFallback": "index.html", "packageManager": "none"
                  },
                  "assets": {
                    "origin": "app://integrated", "cacheHashedAssets": true,
                    "csp": "default-src 'self'; script-src 'self'; object-src 'none'; base-uri 'none'"
                  },
                  "capabilities": []
                }
                """);
            WriteContract(1);
            File.WriteAllText(ProjectPath, $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <Import Project="{{Xml(propsPath)}}" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <NeoAstraToolPath>{{Xml(_toolPath)}}</NeoAstraToolPath>
                    <NeoRpcTypeScriptOutput>{{Xml(Path.Combine(Root, "generated.ts"))}}</NeoRpcTypeScriptOutput>
                    <NeoRpcManifestOutput>{{Xml(Path.Combine(Root, "generated.manifest.json"))}}</NeoRpcManifestOutput>
                  </PropertyGroup>
                  <Import Project="{{Xml(_targetsPath)}}" />
                </Project>
                """);

            Directory.CreateDirectory(Path.GetDirectoryName(DisabledProjectPath)!);
            File.WriteAllText(DisabledProjectPath, $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <Import Project="{{Xml(propsPath)}}" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <NeoAstraFrontendEnabled>false</NeoAstraFrontendEnabled>
                    <NeoAstraToolPath>{{Xml(Path.Combine(Root, "missing-tool.dll"))}}</NeoAstraToolPath>
                  </PropertyGroup>
                  <Import Project="{{Xml(_targetsPath)}}" />
                </Project>
                """);
        }

        internal string Root { get; }
        internal string FrontendRoot { get; }
        internal string DistDirectory { get; }
        internal string DistIndex { get; }
        internal string ConfigurationPath { get; }
        internal string SourcePath { get; }
        internal string ProjectPath { get; }
        internal string DisabledProjectPath { get; }
        internal string BuiltIndex => Path.Combine(Root, "bin", "Debug", "net10.0", "assets", "index.html");
        internal string PreparedAssets => Path.Combine(Root, "obj", "Debug", "net10.0", "neoastra", "frontend", "assets");

        internal void WriteContract(int version)
        {
            var manifest = Encoding.UTF8.GetBytes($"{{\"version\":{version}}}\n");
            File.WriteAllBytes(Path.Combine(Root, "generated.manifest.json"), manifest);
            var hash = Convert.ToHexString(SHA256.HashData(manifest)).ToLowerInvariant();
            File.WriteAllText(Path.Combine(Root, "generated.ts"), $"// <auto-generated by NeoAstra.Generator; contract {hash}>\n");
        }

        internal Task<string> BuildAsync(params string[] additionalArguments) =>
            RunDotNetCoreAsync(["build", ProjectPath, "--no-restore", "--nologo", "-c", "Debug", .. additionalArguments]);

        internal Task<string> RunDotNetAsync(params string[] arguments) => RunDotNetCoreAsync(arguments);

        internal void ConfigureNpmRestore()
        {
            File.WriteAllText(Path.Combine(FrontendRoot, "package.json"), "{\"name\":\"msbuild-error-fixture\"}");
            File.WriteAllText(Path.Combine(FrontendRoot, "package-lock.json"), "{\"lockfileVersion\":3}");
            File.WriteAllText(ConfigurationPath, File.ReadAllText(ConfigurationPath)
                .Replace("\"packageManager\": \"none\"", "\"packageManager\": \"npm\", \"lockfile\": \"ClientApp/package-lock.json\"", StringComparison.Ordinal));
        }

        internal async Task<(int ExitCode, string Output)> BuildWithoutPathAsync()
        {
            var dotnet = NeoChildProcess.ResolveExecutable("dotnet", Root);
            var start = new ProcessStartInfo
            {
                FileName = dotnet.Path,
                WorkingDirectory = Root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in dotnet.PrefixArguments) start.ArgumentList.Add(argument);
            foreach (var argument in new[] { "build", ProjectPath, "--no-restore", "--nologo", "-c", "Debug", $"-p:NeoAstraDotNetHost={dotnet.Path}" }) start.ArgumentList.Add(argument);
            start.Environment["PATH"] = string.Empty;
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            return (process.ExitCode, await stdout + await stderr);
        }

        private async Task<string> RunDotNetCoreAsync(IReadOnlyList<string> arguments)
        {
            var start = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = Root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            var output = await stdout + await stderr;
            Assert.AreEqual(0, process.ExitCode, output);
            return output;
        }

        public void Dispose() { try { Directory.Delete(Root, recursive: true); } catch { } }

        private static string Xml(string value) => System.Security.SecurityElement.Escape(value)!;
    }

    private sealed class ProjectFixture : IDisposable
    {
        internal ProjectFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "neoastra-tooling-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(Root, "Client App"));
            ConfigurationPath = Path.Combine(Root, "neoastra.json"); File.WriteAllText(ConfigurationPath, """
                {
                  "$schema": "neoastra-project-v1.schema.json",
                  "version": 1,
                  "app": { "identifier": "dev.neoastra.fixture", "displayName": "Fixture" },
                  "frontend": {
                    "root": "Client App", "devCommand": ["pnpm", "dev", "--host", "127.0.0.1", "an argument with spaces", "\"quoted\""],
                    "backendCommand": ["dotnet", "watch", "run"], "devUrl": "http://127.0.0.1:5173",
                    "buildCommand": ["pnpm", "build"], "dist": "Client App/dist", "spaFallback": "index.html",
                    "packageManager": "none", "environment": { "TOKEN": "top-secret" }, "secretEnvironment": ["TOKEN"]
                  },
                  "assets": {
                    "origin": "app://fixture", "cacheHashedAssets": true,
                    "csp": "default-src 'self'; script-src 'self'; object-src 'none'; base-uri 'none'"
                  },
                  "capabilities": []
                }
                """);
        }
        internal string Root { get; }
        internal string ConfigurationPath { get; }
        public void Dispose() { try { Directory.Delete(Root, recursive: true); } catch { } }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "schemas", "neoastra-project-v1.schema.json"))) return directory.FullName;
        throw new DirectoryNotFoundException("The repository root was not found.");
    }

    private static void ValidateUniqueJsonProperties(ReadOnlySpan<byte> utf8Json)
    {
        using var document = JsonDocument.Parse(utf8Json.ToArray(), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false, MaxDepth = 64 }); Visit(document.RootElement);
        static void Visit(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object) { var names = new HashSet<string>(StringComparer.Ordinal); foreach (var property in value.EnumerateObject()) { if (!names.Add(property.Name)) throw new InvalidDataException($"Duplicate JSON property '{property.Name}'."); Visit(property.Value); } }
            else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Visit(item);
        }
    }

    private static string[] InitArguments(string configPath, string identifier, bool force)
    {
        var arguments = new List<string> { "init", "--config", configPath, "--frontend-root", "Client App", "--dev-command", "npm", "--dev-command", "run", "--dev-url", "http://127.0.0.1:5173", "--build-command", "npm", "--build-command", "run", "--dist", "Client App/dist", "--identifier", identifier, "--display-name", "Init fixture", "--package-manager", "none" };
        if (force) arguments.Add("--force"); return arguments.ToArray();
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunToolAsync(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var tool = Path.Combine(FindRepositoryRoot(), "src", "NeoAstra.Tool", "bin", "Release", "net10.0", "NeoAstra.Tool.dll"); Assert.IsTrue(File.Exists(tool), "NeoAstra.Tool must be built before its CLI integration test.");
        var start = new ProcessStartInfo { FileName = "dotnet", WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true }; start.ArgumentList.Add(tool); foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); return (process.ExitCode, await stdout, await stderr);
    }

    private sealed class FakeProcess : INeoChildProcess
    {
        private readonly TaskCompletionSource<int> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int? ExitCode { get; private set; }
        public Task<int> Completion => _completion.Task;
        internal bool Stopped { get; private set; }
        internal void Exit(int code) { ExitCode = code; _completion.TrySetResult(code); }
        public Task StopAsync(TimeSpan timeout) { Stopped = true; Exit(ExitCode ?? 0); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Stopped = true; Exit(ExitCode ?? 0); return ValueTask.CompletedTask; }
    }
    private sealed class FakeFactory(List<string> order, params FakeProcess[] processes) : INeoProcessFactory
    {
        private int _index; internal List<NeoProcessStart> Starts { get; } = []; internal TaskCompletionSource BackendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public INeoChildProcess Start(NeoProcessStart start) { order.Add("start:" + start.Label); Starts.Add(start); if (start.Label == "backend") BackendStarted.TrySetResult(); return processes[_index++]; }
    }
    private sealed class FakeReadiness(List<string> order) : INeoReadinessProbe
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task WaitAsync(Uri url, TimeSpan timeout, CancellationToken cancellationToken) { order.Add("readiness"); Started.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
    }
}