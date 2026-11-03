// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
using System.Text.Json;
using NeoAstra.Desktop;
using NeoAstra.Passkeys;
using NeoAstra.Rpc;

namespace NeoAstra.Tests;

// The passkeys plugin reaches Authentication Services and the Credential Manager only on a device; its contract, the options it
// reads, its permissions, and its renderer commands are checked on every host.
[TestClass]
public sealed class PasskeysPluginTests
{
    private const string Creation = """
        {"rp":{"id":"example.com","name":"Example"},"user":{"id":"dXNlci0x","name":"ada@example.com","displayName":"Ada"},
         "challenge":"Y2hhbGxlbmdlLTEyMzQ1Njc4OTA","pubKeyCredParams":[{"type":"public-key","alg":-7}],
         "excludeCredentials":[{"type":"public-key","id":"Y3JlZC0x"}],"authenticatorSelection":{"userVerification":"required"},"attestation":"none"}
        """;
    private const string Request = """{"rpId":"example.com","challenge":"Y2hhbGxlbmdl","allowCredentials":[{"type":"public-key","id":"Y3JlZC0x"},{"type":"public-key","id":"Y3JlZC0y"}]}""";
    private const string Registration = """{"id":"Y3JlZC0x","rawId":"Y3JlZC0x","type":"public-key","response":{"clientDataJSON":"e30","attestationObject":"o2Nm"},"clientExtensionResults":{}}""";

    [TestMethod]
    public async Task Metadata_MatchesTheEmbeddedManifestAndThePermissionPolicy()
    {
        await using var plugin = new NeoPasskeysPlugin(new NeoFakePasskeys());
        var metadata = plugin.Metadata;
        using var stream = typeof(NeoPasskeysPlugin).Assembly.GetManifestResourceStream("NeoAstra.Passkeys.neoastra.plugin.json");
        Assert.IsNotNull(stream);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        Assert.AreEqual(NeoPasskeysPlugin.Id, root.GetProperty("id").GetString());
        Assert.AreEqual("none-by-default", root.GetProperty("rendererAuthority").GetString());
        var commands = root.GetProperty("commands").EnumerateArray().ToDictionary(static item => item.GetProperty("name").GetString()!, static item => item.GetProperty("permission").GetString()!, StringComparer.Ordinal);
        CollectionAssert.AreEquivalent(metadata.Commands.Select(static item => item.Name).ToArray(), commands.Keys.ToArray());
        Assert.IsTrue(metadata.Commands.All(item => commands[item.Name] == item.Permission));
        var permissions = root.GetProperty("permissions").EnumerateArray().ToDictionary(static item => item.GetProperty("id").GetString()!, static item => item.GetProperty("operations").EnumerateArray().Select(static value => value.GetString()!).ToArray(), StringComparer.Ordinal);
        foreach (var permission in NeoPasskeysPlugin.PermissionCatalog.Permissions)
        {
            CollectionAssert.AreEquivalent(permission.Commands.ToArray(), permissions[permission.Id]);
            Assert.AreEqual(NeoPermissionRisk.Sensitive, permission.Risk);
        }

        var builder = new NeoRpcBuilder();
        builder.AddNeoAstraPasskeysHandlers(new NeoFakePasskeys());
        await using var rpc = builder.Build();
        foreach (var command in metadata.Commands)
        {
            Assert.IsTrue(rpc.TryGetCommand(command.Name, out var descriptor));
            Assert.IsNotNull(descriptor);
            var permission = NeoPasskeysPlugin.PermissionCatalog.Permissions.Single(item => item.Id == command.Permission);
            Assert.AreEqual(command.Permission, descriptor.Options.Permission);
            Assert.IsTrue(descriptor.Options.Timeout <= permission.DefaultTimeout);
            Assert.IsTrue(descriptor.Options.MaximumConcurrency <= permission.MaximumConcurrency);
        }
    }

    [TestMethod]
    public void Options_AreReadAsTheWebAuthnJsonOfAServer()
    {
        var creation = NeoPasskeys.ParseCreation(Creation);
        Assert.AreEqual("example.com", creation.RelyingParty);
        CollectionAssert.AreEqual("challenge-1234567890"u8.ToArray(), creation.Challenge);
        CollectionAssert.AreEqual("user-1"u8.ToArray(), creation.UserId);
        Assert.AreEqual("ada@example.com", creation.UserName);
        Assert.AreEqual("Ada", creation.DisplayName);
        Assert.AreEqual(1, creation.Excluded.Count);
        CollectionAssert.AreEqual("cred-1"u8.ToArray(), creation.Excluded[0]);
        Assert.AreEqual("required", creation.UserVerification);
        Assert.AreEqual("none", creation.Attestation);

        var request = NeoPasskeys.ParseRequest(Request);
        Assert.AreEqual("example.com", request.RelyingParty);
        Assert.AreEqual(2, request.Allowed.Count);
        Assert.AreEqual("preferred", request.UserVerification);
        Assert.AreEqual("Y3JlZC0x", NeoPasskeys.Base64Url("cred-1"u8));

        Assert.ThrowsExactly<ArgumentNullException>(() => NeoPasskeys.ParseRequest(null!));
        Assert.ThrowsExactly<ArgumentException>(() => NeoPasskeys.ParseRequest("[]"));
        Assert.ThrowsExactly<ArgumentException>(() => NeoPasskeys.ParseRequest("""{"rpId":"example.com"}"""));
        Assert.ThrowsExactly<ArgumentException>(() => NeoPasskeys.ParseRequest("""{"rpId":"example.com","challenge":"not base64!"}"""));
        Assert.ThrowsExactly<ArgumentException>(() => NeoPasskeys.ParseCreation(Request));
        Assert.ThrowsExactly<ArgumentException>(() => NeoPasskeys.ParseRequest(new string(' ', NeoPasskeys.MaximumOptionsLength + 1)));
    }

    [TestMethod]
    public async Task Renderer_CreatesAndSignsInWithTheGrantedPermissions()
    {
        var passkeys = new NeoFakePasskeys { Creation = NeoDesktopResult<string>.Success(Registration) };
        await using var session = Session(passkeys, ["passkeys:create", "passkeys:get"], out var frames);
        var created = await ResponseAsync(session, frames, "1", "passkeys.create", Creation);
        var value = created.GetProperty("value");
        Assert.AreEqual("Success", value.GetProperty("status").GetString());
        // The answer goes to the page as the object it is.
        Assert.AreEqual("Y3JlZC0x", value.GetProperty("value").GetProperty("id").GetString());
        using (var options = JsonDocument.Parse(passkeys.LastOptions!)) Assert.AreEqual("example.com", options.RootElement.GetProperty("rp").GetProperty("id").GetString());
        var signedIn = (await ResponseAsync(session, frames, "2", "passkeys.get", Request)).GetProperty("value");
        Assert.AreEqual("NotFound", signedIn.GetProperty("status").GetString());
        Assert.AreEqual(NeoPasskeys.NoCredentialCode, signedIn.GetProperty("code").GetString());
        Assert.AreEqual("passkeys_invalid", (await ResponseAsync(session, frames, "3", "passkeys.get", """{"rpId":"example.com"}""")).GetProperty("error").GetProperty("code").GetString());

        await using var getOnly = Session(passkeys, ["passkeys:get"], out var getFrames);
        Assert.AreEqual("permission_denied", (await ResponseAsync(getOnly, getFrames, "4", "passkeys.create", Creation)).GetProperty("error").GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task System_AnswersUnsupportedOnADesktopAndValidatesTheOptions()
    {
        var passkeys = NeoPasskeys.CreateSystem();
        Assert.AreEqual(NeoSupportLevel.None, passkeys.Support.SupportLevel);
        Assert.AreEqual(NeoDesktopStatus.Unsupported, (await passkeys.CreateAsync(Creation)).Status);
        Assert.AreEqual(NeoDesktopStatus.Unsupported, (await passkeys.GetAsync(Request)).Status);
        await Assert.ThrowsAsync<ArgumentException>(async () => await passkeys.GetAsync("{}"));
        Assert.AreEqual(NeoSupportLevel.Native, new AndroidPasskeys().Support.SupportLevel);
    }

    [TestMethod]
    public void Android_ReadsTheAnswersOfTheJavaService()
    {
        var success = AndroidPasskeys.Result(JsonDocument.Parse($$"""{"status":"Success","response":{{JsonSerializer.Serialize(Registration, PasskeysTestJson.Default.String)}}}"""));
        Assert.AreEqual(Registration, success.Value);
        var excluded = AndroidPasskeys.Result(JsonDocument.Parse("""{"status":"Conflict","code":"excluded"}"""));
        Assert.AreEqual(NeoDesktopStatus.Conflict, excluded.Status);
        Assert.AreEqual(NeoPasskeys.ExcludedCode, excluded.Code);
        Assert.AreEqual(NeoDesktopStatus.Failed, AndroidPasskeys.Result(JsonDocument.Parse("""{"status":"Success"}""")).Status);
        // A desktop backend has no platform services to reach the Java service with.
        DesktopPlatformCall.AssertNotSupported(() => AndroidPasskeys.Call("get", Request));
    }

    private static NeoRpcSession Session(INeoPasskeys passkeys, string[] permissions, out ConcurrentQueue<string> frames)
    {
        var manifest = NeoCapabilityManifest.Resolve(System.Text.Encoding.UTF8.GetBytes($$"""
            {"$schema":"neoastra-capabilities-v1.schema.json","version":1,"capabilities":[{"id":"main","views":["main"],"permissions":[{{string.Join(",", permissions.Select(static item => $"\"{item}\""))}}]}]}
            """), new NeoPermissionCatalogBuilder().AddNeoAstraPasskeysPermissions().Build(), new() { Platform = NeoCapabilityPlatform.Android, Release = true, Profile = NeoSecurityProfile.ProductionLocalApp });
        var builder = new NeoRpcBuilder(new NeoRpcOptions { CapabilityManifest = manifest, SecurityProfile = manifest.Profile, AuthorizationService = new NeoCapabilityAuthorizationService(manifest) });
        builder.AddNeoAstraPasskeysHandlers(passkeys);
        var rpc = builder.Build();
        var queue = new ConcurrentQueue<string>();
        frames = queue;
        return rpc.OpenSession(new NeoRpcSessionIdentity("main", "document-1") { Platform = NeoCapabilityPlatform.Android, IsMainFrame = true }, (json, _) => { queue.Enqueue(json); return ValueTask.CompletedTask; });
    }

    private static async Task<JsonElement> ResponseAsync(NeoRpcSession session, ConcurrentQueue<string> frames, string id, string command, string options)
    {
        await session.ReceiveAsync($$$"""{"neoastra":1,"kind":"invoke","id":"{{{id}}}","command":"{{{command}}}","args":{"options":{{{options.ReplaceLineEndings(" ")}}}}}""");
        for (var index = 0; index < 400; index++)
        {
            foreach (var json in frames)
            {
                var frame = JsonDocument.Parse(json).RootElement.Clone();
                if (frame.GetProperty("kind").GetString() == "result" && frame.TryGetProperty("id", out var value) && value.GetString() == id) return frame;
            }
            await Task.Delay(5);
        }
        Assert.Fail($"No response to {command}.");
        return default;
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class PasskeysTestJson : System.Text.Json.Serialization.JsonSerializerContext;
