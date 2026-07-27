// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NeoAstra.Tooling;

namespace NeoAstra.Tests;

[TestClass]
public sealed class DeliveryTests
{
    [TestMethod]
    public void BundleConfigurationAndStagingAreStrictDeterministicAndRedacted()
    {
        using var fixture = new BundleFixture(); var project = NeoProjectConfiguration.Load(fixture.Configuration);
        Assert.IsNotNull(project.Bundle); Assert.AreEqual(project.Identifier, project.Bundle.NotificationIdentity); Assert.DoesNotContain(fixture.PublicKey, project.ToInspectJson(true), StringComparison.Ordinal);
        var first = NeoBundleOrchestrator.Run(project, fixture.Request(Path.Combine(fixture.Root, "first"), dryRun: false));
        var second = NeoBundleOrchestrator.Run(project, fixture.Request(Path.Combine(fixture.Root, "second"), dryRun: false));
        CollectionAssert.AreEqual(File.ReadAllBytes(first.StagingManifest), File.ReadAllBytes(second.StagingManifest));
        Assert.AreEqual(Hash(first.Artifact), Hash(second.Artifact));
        using var archive = ZipFile.OpenRead(first.Artifact); Assert.IsTrue(archive.Entries.Count >= 2); Assert.IsTrue(archive.Entries.All(static entry => entry.LastWriteTime.Year == 1980));
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Root, "first", "sbom.spdx.json"))); Assert.IsTrue(File.Exists(Path.Combine(fixture.Root, "first", "sbom.cyclonedx.json"))); Assert.IsTrue(File.Exists(Path.Combine(fixture.Root, "first", "provenance.json"))); Assert.IsTrue(File.Exists(Path.Combine(fixture.Root, "first", "SHA256SUMS")));
        var plan = File.ReadAllText(Path.Combine(fixture.Root, "first", "inspect", "command-plan.json")); Assert.DoesNotContain("secret-signing-value", plan, StringComparison.Ordinal); StringAssert.Contains(plan, "arguments");
        var platformInput = string.Join('\n', Directory.EnumerateFiles(Path.Combine(fixture.Root, "first", "inspect", fixture.Rid)).Where(path => Path.GetExtension(path) is ".xml" or ".plist" or ".desktop").Select(File.ReadAllText)); StringAssert.Contains(platformInput, "neoastra-fixture");
    }

    [TestMethod]
    public void BundleRejectsUndeclaredMissingForbiddenCollisionWrongAbiAndCrossHostSigning()
    {
        using var fixture = new BundleFixture(); var project = NeoProjectConfiguration.Load(fixture.Configuration);
        File.WriteAllText(Path.Combine(fixture.Publish, "neoastra-native.json"), File.ReadAllText(Path.Combine(fixture.Publish, "neoastra-native.json")).Replace("\"abiMinor\":9", "\"abiMinor\":8", StringComparison.Ordinal));
        Assert.AreEqual("bundle_native_abi", Assert.ThrowsExactly<NeoToolException>(() => NeoBundleOrchestrator.Run(project, fixture.Request(Path.Combine(fixture.Root, "wrong-abi"), false))).Code); fixture.WriteNativeIdentity();
        File.Delete(Path.Combine(fixture.Publish, fixture.ExecutableFile)); Assert.AreEqual("bundle_declared_file", Assert.ThrowsExactly<NeoToolException>(() => NeoBundleOrchestrator.Run(project, fixture.Request(Path.Combine(fixture.Root, "missing"), false))).Code);
        fixture.WriteExecutable(); File.WriteAllText(fixture.Configuration, File.ReadAllText(fixture.Configuration).Replace($"\"{fixture.ExecutableFile}\",", "\"source.cs\",")); File.WriteAllText(Path.Combine(fixture.Publish, "source.cs"), "development"); project = NeoProjectConfiguration.Load(fixture.Configuration); Assert.AreEqual("bundle_forbidden_file", Assert.ThrowsExactly<NeoToolException>(() => NeoBundleOrchestrator.Run(project, fixture.Request(Path.Combine(fixture.Root, "forbidden"), false))).Code);
        if (!OperatingSystem.IsWindows()) { File.WriteAllText(fixture.Configuration, File.ReadAllText(fixture.Configuration).Replace("\"source.cs\"", $"\"{fixture.ExecutableFile}\", \"{fixture.ExecutableFile.ToUpperInvariant()}\"")); File.Copy(Path.Combine(fixture.Publish, fixture.ExecutableFile), Path.Combine(fixture.Publish, fixture.ExecutableFile.ToUpperInvariant())); project = NeoProjectConfiguration.Load(fixture.Configuration); Assert.AreEqual("bundle_collision", Assert.ThrowsExactly<NeoToolException>(() => NeoBundleOrchestrator.Run(project, fixture.Request(Path.Combine(fixture.Root, "collision"), false))).Code); }
    }

    [TestMethod]
    public void SignedUpdateManifestAuthenticatesCanonicalPolicyAndFailsClosed()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); var publicKey = key.ExportSubjectPublicKeyInfo(); var policy = Policy(publicKey); var json = Sign(Manifest(), key);
        var verified = NeoUpdateManifestVerifier.Verify(json, policy); Assert.AreEqual(new Version(2, 0, 0, 0), verified.Version); Assert.AreEqual("win-x64", verified.Artifact.RuntimeIdentifier);
        AssertCode("update_signature", () => NeoUpdateManifestVerifier.Verify(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(json).Replace("2.0.0.0", "3.0.0.0", StringComparison.Ordinal)), policy));
        AssertCode("update_critical_field", () => NeoUpdateManifestVerifier.Verify(Sign(Manifest().Replace("\"signature\"", "\"critical\":true,\"signature\"", StringComparison.Ordinal), key), policy));
        AssertCode("update_wrong_app", () => NeoUpdateManifestVerifier.Verify(Sign(Manifest().Replace("dev.neoastra.app", "dev.neoastra.other", StringComparison.Ordinal), key), policy));
        AssertCode("update_wrong_channel", () => NeoUpdateManifestVerifier.Verify(Sign(Manifest().Replace("\"stable\"", "\"beta\"", StringComparison.Ordinal), key), policy));
        AssertCode("update_replay_downgrade", () => NeoUpdateManifestVerifier.Verify(Sign(Manifest().Replace("2.0.0.0", "1.0.0.0", StringComparison.Ordinal).Replace("\"build\":2", "\"build\":1", StringComparison.Ordinal), key), policy));
        AssertCode("update_wrong_rid", () => NeoUpdateManifestVerifier.Verify(Sign(Manifest().Replace("win-x64", "linux-x64", StringComparison.Ordinal).Replace("\"zip\"", "\"tar.gz\"", StringComparison.Ordinal), key), policy));
        AssertCode("update_url_policy", () => NeoUpdateManifestVerifier.Verify(Sign(Manifest().Replace("https://updates.example.test/app.zip", "https://evil.example.test/app.zip", StringComparison.Ordinal), key), policy));
        AssertCode("update_key", () => NeoUpdateManifestVerifier.Verify(json, policy with { RevokedKeys = new HashSet<string>(["current"], StringComparer.Ordinal) }));
        AssertCode("update_manifest_size", () => NeoUpdateManifestVerifier.Verify(new byte[1025], policy with { MaximumManifestBytes = 1024 }));
        AssertCode("update_store_managed", () => NeoUpdateManifestVerifier.Verify(json, policy with { IsStoreManaged = true }));
        AssertCode("update_rollout", () => NeoUpdateManifestVerifier.Verify(Sign(Manifest().Replace("\"rolloutPercent\":100", "\"rolloutPercent\":0", StringComparison.Ordinal), key), policy with { RolloutIdentity = "stable-installation-id" }));
        using var rotated = ECDsa.Create(ECCurve.NamedCurves.nistP256); var rotatedJson = Sign(Manifest().Replace("\"current\"", "\"next\"", StringComparison.Ordinal), rotated); var rotatedPolicy = policy with { PublicKeys = new Dictionary<string, byte[]> { ["current"] = publicKey, ["next"] = rotated.ExportSubjectPublicKeyInfo() } }; Assert.AreEqual("next", NeoUpdateManifestVerifier.Verify(rotatedJson, rotatedPolicy).SigningKeyId);
    }

    [TestMethod]
    public void AtomicUpdateInstallHealthRollbackAndLoopPreventionAreBounded()
    {
        using var fixture = new TemporaryFixture(); var artifact = Path.Combine(fixture.Root, "artifact.zip"); File.WriteAllText(artifact, "authenticated"); var install = Path.Combine(fixture.Root, "install"); var state = Path.Combine(fixture.Root, "state"); Directory.CreateDirectory(install); File.WriteAllText(Path.Combine(install, "version"), "old");
        var payload = Path.Combine(fixture.Root, "payload"); Directory.CreateDirectory(payload); File.WriteAllText(Path.Combine(payload, "version"), "new"); var handoff = NeoAtomicUpdateInstaller.Prepare("dev.neoastra.app", "2.0.0.0", artifact, install, state); NeoAtomicUpdateInstaller.InstallAuthenticatedPayload(handoff, payload, install, state); Assert.AreEqual("new", File.ReadAllText(Path.Combine(install, "version")));
        var victim = Path.Combine(fixture.Root, "victim"); Directory.CreateDirectory(victim); var stateFile = Path.Combine(state, "handoff.json"); var stateJson = JsonNode.Parse(File.ReadAllText(stateFile))!.AsObject(); stateJson["previousPath"] = victim; File.WriteAllText(stateFile, stateJson.ToJsonString()); AssertCode("update_state_path", () => NeoAtomicUpdateInstaller.MarkHealthy(install, state)); Assert.IsTrue(Directory.Exists(victim)); stateJson["previousPath"] = handoff.PreviousPath; File.WriteAllText(stateFile, stateJson.ToJsonString());
        Assert.IsTrue(NeoAtomicUpdateInstaller.RequiresRollback(state, TimeSpan.Zero, DateTimeOffset.UtcNow.AddSeconds(1))); NeoAtomicUpdateInstaller.Rollback(install, state); Assert.AreEqual("old", File.ReadAllText(Path.Combine(install, "version")));
        var pending = NeoAtomicUpdateInstaller.Prepare("dev.neoastra.app", "2.0.0.0", artifact, install, state); Assert.AreEqual(2, pending.Attempt); Assert.AreEqual("update_rollback_loop", Assert.ThrowsExactly<NeoToolException>(() => NeoAtomicUpdateInstaller.Prepare("dev.neoastra.app", "2.0.0.0", artifact, install, state)).Code);
    }

    [TestMethod]
    public void AuthenticatedExtractionAndInterruptedSwitchFailClosed()
    {
        using var fixture = new TemporaryFixture(); var artifact = Path.Combine(fixture.Root, "bad.zip"); using (var archive = ZipFile.Open(artifact, ZipArchiveMode.Create)) archive.CreateEntry("../escape.exe");
        var digest = Hash(artifact).ToLowerInvariant(); var package = new NeoUpdateArtifact(new Uri("https://updates.example.test/bad.zip"), new FileInfo(artifact).Length, digest, "zip", Convert.ToBase64String(new byte[64]), "win-x64");
        var verified = new NeoVerifiedUpdateManifest(1, "dev.neoastra.app", "stable", new Version(2, 0, 0, 0), 2, DateTimeOffset.UtcNow, new Version(1, 0, 0, 0), new Version(1, 0, 0, 0), new Version(2, 0, 0, 0), "current", package, 100, null, []);
        AssertCode("update_package_path", () => NeoAuthenticatedPackageExtractor.ExtractPortable(verified, artifact, Path.Combine(fixture.Root, "extract")));
        var mismatch = Path.Combine(fixture.Root, "mismatch.zip"); using (var archive = ZipFile.Open(mismatch, ZipArchiveMode.Create)) { var identity = archive.CreateEntry("App/neoastra-package.json"); using var writer = new StreamWriter(identity.Open(), Encoding.UTF8); writer.Write("{\"schemaVersion\":1,\"applicationId\":\"dev.neoastra.other\",\"version\":\"2.0.0.0\",\"rid\":\"win-x64\",\"executable\":\"App.exe\"}"); } var mismatchArtifact = package with { Length = new FileInfo(mismatch).Length, Sha256 = Hash(mismatch).ToLowerInvariant() }; AssertCode("update_package_identity", () => NeoAuthenticatedPackageExtractor.ExtractPortable(verified with { Artifact = mismatchArtifact }, mismatch, Path.Combine(fixture.Root, "extract-mismatch")));

        var install = Path.Combine(fixture.Root, "install"); var state = Path.Combine(fixture.Root, "state"); Directory.CreateDirectory(install); File.WriteAllText(Path.Combine(install, "old.txt"), "old"); var handoff = NeoAtomicUpdateInstaller.Prepare("dev.neoastra.app", "2.0.0.0", artifact, install, state); Directory.Move(install, handoff.PreviousPath); var stateFile = Path.Combine(state, "handoff.json"); File.WriteAllText(stateFile, File.ReadAllText(stateFile).Replace("\"pending\"", "\"switching\"", StringComparison.Ordinal)); NeoAtomicUpdateInstaller.RecoverInterrupted(install, state); Assert.AreEqual("old", File.ReadAllText(Path.Combine(install, "old.txt")));
        var payload = Path.Combine(fixture.Root, "payload"); Directory.CreateDirectory(payload); File.WriteAllText(Path.Combine(payload, "new.txt"), "new"); var next = NeoAtomicUpdateInstaller.Prepare("dev.neoastra.app", "2.0.0.0", artifact, install, state); AssertCode("update_quit_handoff", () => NeoAtomicUpdateInstaller.InstallAuthenticatedPayload(next, payload, install, state, Environment.ProcessId)); File.AppendAllText(artifact, "changed"); AssertCode("update_artifact_changed", () => NeoAtomicUpdateInstaller.InstallAuthenticatedPayload(next, payload, install, state));
    }

    private static NeoUpdateClientPolicy Policy(byte[] key) => new("dev.neoastra.app", "stable", "win-x64", new Version(1, 0, 0, 0), 1, new Version(1, 0, 0, 0), new Uri("https://updates.example.test/feed.json"), new Dictionary<string, byte[]> { ["current"] = key }, new HashSet<string>(StringComparer.Ordinal));
    private static string Manifest() => """
        {"schemaVersion":1,"applicationId":"dev.neoastra.app","channel":"stable","version":"2.0.0.0","build":2,"releasedAt":"2026-07-27T00:00:00Z","minimumUpdaterVersion":"1.0.0.0","minimumAppVersion":"1.0.0.0","maximumAppVersion":"2.0.0.0","signingKeyId":"current","artifacts":[{"rid":"win-x64","url":"https://updates.example.test/app.zip","length":12,"sha256":"0000000000000000000000000000000000000000000000000000000000000000","format":"zip","signature":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=="}],"rolloutPercent":100,"signature":"placeholder"}
        """;
    private static byte[] Sign(string json, ECDsa key) { var canonical = NeoUpdateManifestVerifier.CanonicalForSigning(Encoding.UTF8.GetBytes(json)); var signature = Convert.ToBase64String(key.SignData(canonical, HashAlgorithmName.SHA256)); var node = JsonNode.Parse(json)!.AsObject(); node["signature"] = signature; return Encoding.UTF8.GetBytes(node.ToJsonString()); }
    private static void AssertCode(string code, Action action) => Assert.AreEqual(code, Assert.ThrowsExactly<NeoToolException>(action).Code);
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }

    private sealed class BundleFixture : IDisposable
    {
        internal BundleFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "neoastra-delivery-" + Guid.NewGuid().ToString("N")); Publish = Path.Combine(Root, "publish"); Directory.CreateDirectory(Publish); Configuration = Path.Combine(Root, "neoastra.json"); var rid = OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsMacOS() ? "osx-x64" : "linux-x64"; Rid = rid; ExecutableFile = OperatingSystem.IsWindows() ? "Fixture.exe" : "Fixture"; NativeFile = OperatingSystem.IsWindows() ? "neoastra_native.dll" : OperatingSystem.IsMacOS() ? "libneoastra_native.dylib" : "libneoastra_native.so"; WriteExecutable(); WriteMachineFile(Path.Combine(Publish, NativeFile)); WriteNativeIdentity(); File.WriteAllBytes(Path.Combine(Root, "icon.png"), [0x89, 0x50, 0x4e, 0x47]); File.WriteAllText(Path.Combine(Root, "NOTICE.txt"), "notice"); File.WriteAllText(Path.Combine(Root, "assets.json"), "{}"); using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
            var configuration = """
                {"$schema":"neoastra-project-v1.schema.json","version":1,"app":{"identifier":"dev.neoastra.fixture","displayName":"Fixture"},"frontend":{"root":".","devCommand":["dotnet","--info"],"devUrl":"http://127.0.0.1:5173","buildCommand":["dotnet","--info"],"dist":".","spaFallback":"index.html","packageManager":"none"},"assets":{"origin":"app://fixture","cacheHashedAssets":true,"csp":"default-src 'self'; object-src 'none'"},"capabilities":[],"bundle":{"identifier":"dev.neoastra.fixture","displayName":"Fixture","version":"1.2.3","numericVersion":"1.2.3.0","publisher":"CN=Fixture","executable":"Fixture","icons":["icon.png"],"rids":["$RID$"],"targets":["portable","installer"],"files":["$EXECUTABLE$","$NATIVE$","neoastra-native.json"],"notices":["NOTICE.txt"],"fileAssociations":[{"extension":".neoastra-fixture","mimeType":"application/x-neoastra-fixture","role":"viewer"}],"urlSchemes":["neoastra-fixture"],"minimumOsVersion":"10.0","runtimeDependencies":["system-webview"],"notificationIdentity":"dev.neoastra.fixture","update":{"channel":"stable","feed":"https://updates.example.test/feed.json","currentKeyId":"current","publicKeys":{"current":"$KEY$"},"mode":"experimental"}}}
                """.Replace("$RID$", rid, StringComparison.Ordinal).Replace("$EXECUTABLE$", ExecutableFile, StringComparison.Ordinal).Replace("$NATIVE$", NativeFile, StringComparison.Ordinal).Replace("$KEY$", PublicKey, StringComparison.Ordinal);
            File.WriteAllText(Configuration, configuration);
        }
        internal string Root { get; } internal string Publish { get; } internal string Configuration { get; } internal string Rid { get; } internal string ExecutableFile { get; } internal string NativeFile { get; } internal string PublicKey { get; }
        internal NeoBundleRequest Request(string output, bool dryRun) => new(Rid, Publish, Path.Combine(Root, "assets.json"), output, dryRun, false, null, false);
        internal void WriteExecutable() => WriteMachineFile(Path.Combine(Publish, ExecutableFile));
        internal void WriteNativeIdentity() { var native = Path.Combine(Publish, NativeFile); File.WriteAllText(Path.Combine(Publish, "neoastra-native.json"), $"{{\"schemaVersion\":1,\"rid\":\"{Rid}\",\"file\":\"{NativeFile}\",\"sha256\":\"{Hash(native).ToLowerInvariant()}\",\"abiMajor\":1,\"abiMinor\":9}}\n"); }
        private void WriteMachineFile(string path) { var bytes = new byte[128]; if (OperatingSystem.IsWindows()) { bytes[0] = (byte)'M'; bytes[1] = (byte)'Z'; BitConverter.GetBytes(64).CopyTo(bytes, 0x3c); bytes[64] = (byte)'P'; bytes[65] = (byte)'E'; BitConverter.GetBytes((ushort)0x8664).CopyTo(bytes, 68); } else if (OperatingSystem.IsMacOS()) { BitConverter.GetBytes(0xfeedfacfu).CopyTo(bytes, 0); BitConverter.GetBytes(0x01000007u).CopyTo(bytes, 4); } else { bytes[0] = 0x7f; bytes[1] = (byte)'E'; bytes[2] = (byte)'L'; bytes[3] = (byte)'F'; BitConverter.GetBytes((ushort)62).CopyTo(bytes, 18); } File.WriteAllBytes(path, bytes); }
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    private sealed class TemporaryFixture : IDisposable { internal TemporaryFixture() { Root = Path.Combine(Path.GetTempPath(), "neoastra-update-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); } internal string Root { get; } public void Dispose() { try { Directory.Delete(Root, true); } catch { } } }
}
