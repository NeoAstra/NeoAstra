// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
using System.Text.Json;
using NeoAstra.Desktop;
using NeoAstra.Desktop.Notifications;
using NeoAstra.Push;
using NeoAstra.Rpc;

namespace NeoAstra.Tests;

// The push plugin reaches APNs and Firebase only on a device; its contract, its permissions, its renderer commands and events,
// and what it takes from the application delegate of iOS are checked on every host.
[TestClass]
public sealed class PushPluginTests
{
    private const string Payload = """{"aps":{"alert":{"title":"Hello","body":"From APNs"}},"order":42}""";

    [TestMethod]
    public async Task Metadata_MatchesTheEmbeddedManifestAndThePermissionPolicy()
    {
        await using var plugin = new NeoPushPlugin(new NeoFakePush());
        var metadata = plugin.Metadata;
        using var stream = typeof(NeoPushPlugin).Assembly.GetManifestResourceStream("NeoAstra.Push.neoastra.plugin.json");
        Assert.IsNotNull(stream);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        Assert.AreEqual(NeoPushPlugin.Id, root.GetProperty("id").GetString());
        Assert.AreEqual("none-by-default", root.GetProperty("rendererAuthority").GetString());
        var commands = root.GetProperty("commands").EnumerateArray().ToDictionary(static item => item.GetProperty("name").GetString()!, static item => item.GetProperty("permission").GetString()!, StringComparer.Ordinal);
        var events = root.GetProperty("events").EnumerateArray().ToDictionary(static item => item.GetProperty("name").GetString()!, static item => item.GetProperty("permission").GetString()!, StringComparer.Ordinal);
        CollectionAssert.AreEquivalent(metadata.Commands.Select(static item => item.Name).ToArray(), commands.Keys.ToArray());
        CollectionAssert.AreEquivalent(metadata.Events.Select(static item => item.Name).ToArray(), events.Keys.ToArray());
        Assert.IsTrue(metadata.Commands.All(item => commands[item.Name] == item.Permission));
        Assert.IsTrue(metadata.Events.All(item => events[item.Name] == item.Permission));
        var permissions = root.GetProperty("permissions").EnumerateArray().ToDictionary(static item => item.GetProperty("id").GetString()!, static item => item.GetProperty("operations").EnumerateArray().Select(static value => value.GetString()!).ToArray(), StringComparer.Ordinal);
        foreach (var permission in NeoPushPlugin.PermissionCatalog.Permissions)
        {
            CollectionAssert.AreEquivalent(permission.Commands.ToArray(), permissions[permission.Id]);
            Assert.AreEqual(NeoPermissionRisk.Sensitive, permission.Risk);
        }

        var builder = new NeoRpcBuilder();
        builder.AddNeoAstraPushHandlers(new NeoFakePush());
        await using var rpc = builder.Build();
        foreach (var command in metadata.Commands)
        {
            Assert.IsTrue(rpc.TryGetCommand(command.Name, out var descriptor));
            Assert.IsNotNull(descriptor);
            var permission = NeoPushPlugin.PermissionCatalog.Permissions.Single(item => item.Id == command.Permission);
            Assert.AreEqual(command.Permission, descriptor.Options.Permission);
            Assert.IsTrue(descriptor.Options.Timeout <= permission.DefaultTimeout);
            Assert.IsTrue(descriptor.Options.MaximumConcurrency <= permission.MaximumConcurrency);
        }
        Assert.IsTrue(metadata.Events.All(item => rpc.TryGetEvent(item.Name, out var descriptor) && descriptor!.Options.Permission == item.Permission));
    }

    [TestMethod]
    public async Task Renderer_RegistersAndReceivesTheMessagesWithTheGrantedPermissions()
    {
        var push = new NeoFakePush
        {
            Registration = NeoDesktopResult<NeoPushRegistration>.Success(new("a1b2c3", NeoPushService.Apns, true)),
            LaunchMessage = new("launch-1", null, Payload),
        };
        await using var session = Session(push, ["push:register", "push:receive"], out var frames);
        var registration = await InvokeAsync(session, frames, "1", "push.register");
        Assert.AreEqual("Success", registration.GetProperty("status").GetString());
        Assert.AreEqual("a1b2c3", registration.GetProperty("value").GetProperty("token").GetString());
        Assert.AreEqual("Apns", registration.GetProperty("value").GetProperty("service").GetString());
        var launch = (await InvokeAsync(session, frames, "2", "push.launch-message")).GetProperty("value");
        Assert.AreEqual("launch-1", launch.GetProperty("messageId").GetString());
        // The page receives the payload as the object it is.
        Assert.AreEqual(42, launch.GetProperty("payload").GetProperty("order").GetInt32());

        await session.ReceiveAsync("""{"neoastra":1,"kind":"subscribe","id":"s1","event":"push.message-opened"}""");
        await session.ReceiveAsync("""{"neoastra":1,"kind":"subscribe","id":"s2","event":"push.token-changed"}""");
        await WaitUntilAsync(() => frames.Count(static json => json.Contains("\"s2\"", StringComparison.Ordinal)) > 0);
        push.Open(new("tap-1", "reply", Payload));
        push.ChangeToken(new("d4e5f6", NeoPushService.Apns, true));
        await WaitUntilAsync(() => Events(frames).Length == 2);
        var opened = Events(frames).Single(static frame => frame.GetProperty("value").TryGetProperty("messageId", out _)).GetProperty("value");
        Assert.AreEqual("tap-1", opened.GetProperty("messageId").GetString());
        Assert.AreEqual("reply", opened.GetProperty("actionId").GetString());
        Assert.AreEqual("Hello", opened.GetProperty("payload").GetProperty("aps").GetProperty("alert").GetProperty("title").GetString());
        Assert.AreEqual("d4e5f6", Events(frames).Single(static frame => frame.GetProperty("value").TryGetProperty("token", out _)).GetProperty("value").GetProperty("token").GetString());
    }

    [TestMethod]
    public async Task Renderer_DeniesWhatTheCapabilityDocumentDoesNotGrant()
    {
        var push = new NeoFakePush { Registration = NeoDesktopResult<NeoPushRegistration>.Success(new("a1b2c3", NeoPushService.Apns, true)) };
        await using var session = Session(push, ["push:receive"], out var frames);
        Assert.AreEqual("permission_denied", await ErrorAsync(session, frames, "1", "push.register"));
        await session.ReceiveAsync("""{"neoastra":1,"kind":"subscribe","id":"s1","event":"push.token-changed"}""");
        await WaitUntilAsync(() => frames.Any(static json => json.Contains("\"s1\"", StringComparison.Ordinal)));
        push.ChangeToken(new("d4e5f6", NeoPushService.Apns, true));
        await Task.Delay(50);
        Assert.AreEqual(0, Events(frames).Length);
    }

    [TestMethod]
    public async Task System_AnswersUnsupportedOnADesktop()
    {
        var push = NeoPush.CreateSystem(new NeoFakeNotifications());
        Assert.AreEqual(NeoSupportLevel.None, push.Support.SupportLevel);
        Assert.IsNull(push.LaunchMessage);
        Assert.AreEqual(NeoDesktopStatus.Unsupported, (await push.RegisterAsync()).Status);
        Assert.ThrowsExactly<ArgumentNullException>(() => NeoPush.CreateSystem(null!));
    }

    [TestMethod]
    public void ApplePush_ValidatesWhatTheApplicationForwards()
    {
        Assert.ThrowsExactly<ArgumentException>(() => NeoApplePush.DidRegister([]));
        Assert.ThrowsExactly<ArgumentException>(() => NeoApplePush.DidRegister(new byte[257]));
        Assert.ThrowsExactly<ArgumentNullException>(() => NeoApplePush.DidFailToRegister(null!));
        // Without a push service of iOS, a token and a launch without a notification go nowhere.
        NeoApplePush.DidRegister([0xA1, 0xB2]);
        NeoApplePush.DidLaunch(0);
        Assert.IsTrue(NeoPush.IsPayload(Payload));
        Assert.IsFalse(NeoPush.IsPayload("[1]"));
        Assert.IsFalse(NeoPush.IsPayload("not json"));
    }

    [TestMethod]
    public void AppleNotifications_ReportTheNotificationsThatNeoAstraDidNotShow()
    {
        // The presenter of iOS and macOS owns the delegate of the notification center, so it is the one that reports them.
        Assert.IsInstanceOfType<INeoExternalNotificationSource>(new MacNotifications("dev.neoastra.tests", attachOnBind: true));
        Assert.IsNotInstanceOfType<INeoExternalNotificationSource>(new NeoFakeNotifications());
    }

    private static NeoRpcSession Session(INeoPush push, string[] permissions, out ConcurrentQueue<string> frames)
    {
        var manifest = NeoCapabilityManifest.Resolve(System.Text.Encoding.UTF8.GetBytes($$"""
            {"$schema":"neoastra-capabilities-v1.schema.json","version":1,"capabilities":[{"id":"main","views":["main"],"permissions":[{{string.Join(",", permissions.Select(static item => $"\"{item}\""))}}]}]}
            """), new NeoPermissionCatalogBuilder().AddNeoAstraPushPermissions().Build(), new() { Platform = NeoCapabilityPlatform.IOS, Release = true, Profile = NeoSecurityProfile.ProductionLocalApp });
        var builder = new NeoRpcBuilder(new NeoRpcOptions { CapabilityManifest = manifest, SecurityProfile = manifest.Profile, AuthorizationService = new NeoCapabilityAuthorizationService(manifest) });
        builder.AddNeoAstraPushHandlers(push);
        var rpc = builder.Build();
        var queue = new ConcurrentQueue<string>();
        frames = queue;
        return rpc.OpenSession(new NeoRpcSessionIdentity("main", "document-1") { Platform = NeoCapabilityPlatform.IOS, IsMainFrame = true }, (json, _) => { queue.Enqueue(json); return ValueTask.CompletedTask; });
    }

    private static JsonElement[] Events(ConcurrentQueue<string> frames)
        => frames.Select(static json => JsonDocument.Parse(json).RootElement.Clone()).Where(static frame => frame.GetProperty("kind").GetString() == "event").ToArray();

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (var index = 0; index < 400 && !predicate(); index++) await Task.Delay(5);
        Assert.IsTrue(predicate());
    }

    private static async Task<JsonElement> InvokeAsync(NeoRpcSession session, ConcurrentQueue<string> frames, string id, string command)
    {
        var frame = await ResponseAsync(session, frames, id, command);
        Assert.IsTrue(frame.TryGetProperty("value", out var value), frame.ToString());
        return value;
    }

    private static async Task<string> ErrorAsync(NeoRpcSession session, ConcurrentQueue<string> frames, string id, string command)
    {
        var frame = await ResponseAsync(session, frames, id, command);
        Assert.IsTrue(frame.TryGetProperty("error", out var error), frame.ToString());
        return error.GetProperty("code").GetString()!;
    }

    private static async Task<JsonElement> ResponseAsync(NeoRpcSession session, ConcurrentQueue<string> frames, string id, string command)
    {
        await session.ReceiveAsync($$$"""{"neoastra":1,"kind":"invoke","id":"{{{id}}}","command":"{{{command}}}","args":{}}""");
        JsonElement? found = null;
        await WaitUntilAsync(() =>
        {
            foreach (var json in frames)
            {
                var frame = JsonDocument.Parse(json).RootElement.Clone();
                if (frame.GetProperty("kind").GetString() == "result" && frame.TryGetProperty("id", out var value) && value.GetString() == id) { found = frame; return true; }
            }
            return false;
        });
        return found!.Value;
    }
}
