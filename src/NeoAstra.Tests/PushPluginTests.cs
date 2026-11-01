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
            Registration = NeoDesktopResult<NeoPushRegistration>.Success(new("a1b2c3", NeoPushService.Apns, true) { ApnsEnvironment = NeoApnsEnvironment.Development, Topic = "dev.neoastra.tests" }),
            LaunchMessage = new("launch-1", null, Payload),
        };
        await using var session = Session(push, ["push:register", "push:receive"], out var frames);
        var registration = await InvokeAsync(session, frames, "1", "push.register");
        Assert.AreEqual("Success", registration.GetProperty("status").GetString());
        Assert.AreEqual("a1b2c3", registration.GetProperty("value").GetProperty("token").GetString());
        Assert.AreEqual("Apns", registration.GetProperty("value").GetProperty("service").GetString());
        Assert.AreEqual("Development", registration.GetProperty("value").GetProperty("apnsEnvironment").GetString());
        Assert.AreEqual("dev.neoastra.tests", registration.GetProperty("value").GetProperty("topic").GetString());
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
    public void Android_ReadsTheMessagesOfTheJavaService()
    {
        using var document = JsonDocument.Parse("""{"messageId":"0:17","payload":{"data":{"order":"42"},"notification":{"title":"Hello"}}}""");
        var message = AndroidPush.Message(document.RootElement);
        Assert.AreEqual("0:17", message.MessageId);
        Assert.IsNull(message.ActionId);
        using var payload = JsonDocument.Parse(message.Payload);
        Assert.AreEqual("42", payload.RootElement.GetProperty("data").GetProperty("order").GetString());
        using var empty = JsonDocument.Parse("{}");
        Assert.AreEqual("{}", AndroidPush.Message(empty.RootElement).Payload);
        // A desktop backend has no platform services to reach the Java service with.
        Assert.ThrowsExactly<NotSupportedException>(() => AndroidPush.Call("launch", null));
        Assert.IsNull(new AndroidPush().LaunchMessage);
    }

    [TestMethod]
    public void AppleNotifications_ReportTheNotificationsThatNeoAstraDidNotShow()
    {
        // The presenter of iOS and macOS owns the delegate of the notification center, so it is the one that reports them.
        Assert.IsInstanceOfType<INeoExternalNotificationSource>(new MacNotifications("dev.neoastra.tests", attachOnBind: true));
        Assert.IsNotInstanceOfType<INeoExternalNotificationSource>(new NeoFakeNotifications());
    }

    [TestMethod]
    public void AndroidPush_CreatedWithADispatcherReadsItsEvents()
    {
        // Without the plugin only CreateSystem binds the dispatcher: on Android it starts the loop that reports taps and tokens.
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "NeoAstra.Push"))) root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("The repository root was not found.");
        var push = File.ReadAllText(Path.Combine(root, "src", "NeoAstra.Push", "Push.cs"));
        StringAssert.Contains(push, "if (OperatingSystem.IsAndroid()) return AndroidPush.Create(dispatcher);");
        Assert.IsFalse(AndroidPush.Create(null).IsBound);
    }

    [TestMethod]
    public void ApplePush_CompletesARemoteNotificationItDoesNotReport()
    {
        // No message (or no push service yet): iOS gets its completion handler back at once, not after the grace of a silent push.
        var completed = 0;
        NeoApplePush.DidReceiveRemoteNotification(0, () => completed++);
        Assert.AreEqual(1, completed);
        Assert.ThrowsExactly<ArgumentNullException>(() => NeoApplePush.DidReceiveRemoteNotification(0, null!));
    }

    [TestMethod]
    public async Task Push_RegistersWithoutAskingTheUser()
    {
        // Silent messages need no permission: the registration then leaves the question of the system to the application.
        var registration = NeoDesktopResult<NeoPushRegistration>.Success(new("token", NeoPushService.Apns, false));
        Assert.AreEqual("token", (await new NeoFakePush { Registration = registration }.RegisterWithoutAskingAsync()).Value!.Token);
        Assert.AreEqual(NeoDesktopStatus.Unsupported, (await NeoPush.CreateSystem(new NeoFakeNotifications()).RegisterWithoutAskingAsync()).Status);
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "NeoAstra.Push", "Android"))) root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("The repository root was not found.");
        var ios = File.ReadAllText(Path.Combine(root, "src", "NeoAstra.Push", "IosPush.cs"));
        StringAssert.Contains(ios, "_canDisplay = ask ? await AuthorizeAsync(dispatcher, cancellationToken).ConfigureAwait(false) : await CanDisplayAsync(cancellationToken).ConfigureAwait(false);");
        var java = File.ReadAllText(Path.Combine(root, "src", "NeoAstra.Push", "Android", "com", "neoastra", "push", "NeoAstraPush.java"));
        StringAssert.Contains(java, "case \"register\": register(context, input.optBoolean(\"ask\", true), output); return;");
    }

    [TestMethod]
    public void Push_LetsTheApplicationDecideTheForegroundPresentation()
    {
        // iOS keeps the completion handler of willPresent until the decision; Java waits a few seconds for it in the foreground.
        Func<NeoPushMessage, CancellationToken, ValueTask<bool>> hide = static (_, _) => ValueTask.FromResult(false);
        var fake = new NeoFakePush { ForegroundPresentation = hide };
        Assert.AreSame(hide, fake.ForegroundPresentation);
        var notifications = new MacNotifications("dev.neoastra.tests", attachOnBind: true);
        Assert.IsNull(((INeoExternalNotificationSource)notifications).ExternalPresentation);
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "NeoAstra.Push", "Android"))) root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("The repository root was not found.");
        var mac = File.ReadAllText(Path.Combine(root, "src", "NeoAstra", "Desktop", "MacNotifications.cs"));
        StringAssert.Contains(mac, "var copied = Native._Block_copy(completion);");
        StringAssert.Contains(mac, "show ? 0x1Au : 0u");
        var java = File.ReadAllText(Path.Combine(root, "src", "NeoAstra.Push", "Android", "com", "neoastra", "push", "NeoAstraPush.java"));
        StringAssert.Contains(java, "String id = deciding && FOREGROUND.get() > 0");
        StringAssert.Contains(java, "show = decision.get(4, TimeUnit.SECONDS)");
    }

    [TestMethod]
    public void AndroidPush_ShowsADataMessageAsTheManifestNamesItsKeys()
    {
        // Firebase shows no data message: the service shows it from the keys that the manifest names, a string resource for a key.
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "NeoAstra.Push", "Android"))) root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("The repository root was not found.");
        var java = File.ReadAllText(Path.Combine(root, "src", "NeoAstra.Push", "Android", "com", "neoastra", "push", "NeoAstraPush.java"));
        StringAssert.Contains(java, "String[] shown = presentation(context, message.getData());");
        StringAssert.Contains(java, "meta.getString(\"com.neoastra.push.\" + name)");
        StringAssert.Contains(java, "key.replaceAll(\"[^A-Za-z0-9_]\", \"_\"), \"string\"");
        StringAssert.Contains(java, "if (body == null || body.isEmpty()) return null;");
    }

    [TestMethod]
    public async Task Renderer_RegistersWithoutAskingWhenThePageSaysSo()
    {
        var push = new RecordingPush();
        await using var session = Session(push, ["push:register"], out var frames);
        await InvokeAsync(session, frames, "1", "push.register");
        await InvokeAsync(session, frames, "2", "push.register", """{"ask":true}""");
        await InvokeAsync(session, frames, "3", "push.register", """{"ask":false}""");
        CollectionAssert.AreEqual(new[] { "ask", "ask", "silent" }, push.Registrations);
    }

    [TestMethod]
    public async Task Renderer_LetsThePagesDecideTheForegroundPresentation()
    {
        var push = new NeoFakePush();
        await using var session = Session(push, ["push:foreground"], out var frames, new NeoPushRendererOptions { PagesDecideForegroundPresentation = true });
        Assert.IsNotNull(push.ForegroundPresentation);
        // No page listens: the message is shown at once.
        Assert.IsTrue(await push.ForegroundPresentation!(new("m0", null, Payload), CancellationToken.None));

        await session.ReceiveAsync("""{"neoastra":1,"kind":"subscribe","id":"s1","event":"push.foreground-message"}""");
        await WaitUntilAsync(() => frames.Any(static json => json.Contains("\"s1\"", StringComparison.Ordinal)));
        var decision = push.ForegroundPresentation!(new("m1", null, Payload), CancellationToken.None).AsTask();
        await WaitUntilAsync(() => Events(frames).Length == 1);
        var message = Events(frames).Single().GetProperty("value");
        Assert.AreEqual("m1", message.GetProperty("message").GetProperty("messageId").GetString());
        Assert.AreEqual(42, message.GetProperty("message").GetProperty("payload").GetProperty("order").GetInt32());
        var answered = await InvokeAsync(session, frames, "1", "push.present", $$"""{"decisionId":"{{message.GetProperty("decisionId").GetString()}}","show":false}""");
        Assert.IsTrue(answered.GetProperty("accepted").GetBoolean());
        Assert.IsFalse(await decision);
        // A decision that nobody waits for any more is not accepted.
        Assert.IsFalse((await InvokeAsync(session, frames, "2", "push.present", """{"decisionId":"gone","show":false}""")).GetProperty("accepted").GetBoolean());
    }

    [TestMethod]
    public async Task Renderer_ShowsAForegroundMessageThatThePageDoesNotDecideInTime()
    {
        var push = new NeoFakePush();
        await using var session = Session(push, ["push:foreground"], out var frames, new NeoPushRendererOptions { PagesDecideForegroundPresentation = true, ForegroundDecisionTimeout = TimeSpan.FromMilliseconds(50) });
        await session.ReceiveAsync("""{"neoastra":1,"kind":"subscribe","id":"s1","event":"push.foreground-message"}""");
        await WaitUntilAsync(() => frames.Any(static json => json.Contains("\"s1\"", StringComparison.Ordinal)));
        Assert.IsTrue(await push.ForegroundPresentation!(new("m1", null, Payload), CancellationToken.None));
    }

    [TestMethod]
    public void Renderer_LeavesTheForegroundPresentationOfTheApplicationWithoutTheOption()
    {
        Func<NeoPushMessage, CancellationToken, ValueTask<bool>> application = static (_, _) => ValueTask.FromResult(false);
        var push = new NeoFakePush { ForegroundPresentation = application };
        new NeoRpcBuilder().AddNeoAstraPushHandlers(push);
        Assert.AreSame(application, push.ForegroundPresentation);
        Assert.Throws<ArgumentOutOfRangeException>(() => new NeoRpcBuilder().AddNeoAstraPushHandlers(push, new NeoPushRendererOptions { PagesDecideForegroundPresentation = true, ForegroundDecisionTimeout = TimeSpan.Zero }));
    }

    [TestMethod]
    public void ApplePush_TellsTheEnvironmentOfAPNsFromTheProvisioningProfile()
    {
        // A provisioning profile is a signed property list; a build for the App Store or TestFlight has none and is production.
        static byte[] Profile(string environment) => System.Text.Encoding.UTF8.GetBytes(
            "0\u0082\u0001\u00ff<?xml version=\"1.0\"?><plist><dict><key>Entitlements</key><dict><key>aps-environment</key>\n\t\t<string>" + environment + "</string></dict></dict></plist>\u0000\u0001");
        Assert.AreEqual(NeoApnsEnvironment.Development, IosPush.ApnsEnvironment(Profile("development"), simulator: false));
        Assert.AreEqual(NeoApnsEnvironment.Production, IosPush.ApnsEnvironment(Profile("production"), simulator: false));
        Assert.AreEqual(NeoApnsEnvironment.Production, IosPush.ApnsEnvironment(null, simulator: false));
        Assert.AreEqual(NeoApnsEnvironment.Development, IosPush.ApnsEnvironment(null, simulator: true));
        // A profile without the entitlement gives no token at all; it is taken for what a development profile is.
        Assert.AreEqual(NeoApnsEnvironment.Development, IosPush.ApnsEnvironment(System.Text.Encoding.UTF8.GetBytes("<plist><dict></dict></plist>"), simulator: false));
        Assert.IsNull(new NeoPushRegistration("t", NeoPushService.Firebase, true).ApnsEnvironment);
    }

    // Records which registration a page asked for.
    private sealed class RecordingPush : INeoPush
    {
        public List<string> Registrations { get; } = [];
        public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Emulated, 1, 0, "Recording push.");
        public NeoPushMessage? LaunchMessage => null;
        public event EventHandler<NeoPushRegistration>? TokenChanged { add { } remove { } }
        public event EventHandler<NeoPushMessage>? MessageReceived { add { } remove { } }
        public event EventHandler<NeoPushMessage>? MessageOpened { add { } remove { } }
        public Func<NeoPushMessage, CancellationToken, ValueTask<bool>>? ForegroundPresentation { get; set; }
        public ValueTask<NeoDesktopResult<NeoPushRegistration>> RegisterAsync(CancellationToken cancellationToken = default) { Registrations.Add("ask"); return ValueTask.FromResult(NeoDesktopResult<NeoPushRegistration>.Success(new("t", NeoPushService.Apns, true))); }
        public ValueTask<NeoDesktopResult<NeoPushRegistration>> RegisterWithoutAskingAsync(CancellationToken cancellationToken = default) { Registrations.Add("silent"); return ValueTask.FromResult(NeoDesktopResult<NeoPushRegistration>.Success(new("t", NeoPushService.Apns, false))); }
    }

    private static NeoRpcSession Session(INeoPush push, string[] permissions, out ConcurrentQueue<string> frames)
        => Session(push, permissions, out frames, null);

    private static NeoRpcSession Session(INeoPush push, string[] permissions, out ConcurrentQueue<string> frames, NeoPushRendererOptions? options)
    {
        var manifest = NeoCapabilityManifest.Resolve(System.Text.Encoding.UTF8.GetBytes($$"""
            {"$schema":"neoastra-capabilities-v1.schema.json","version":1,"capabilities":[{"id":"main","views":["main"],"permissions":[{{string.Join(",", permissions.Select(static item => $"\"{item}\""))}}]}]}
            """), new NeoPermissionCatalogBuilder().AddNeoAstraPushPermissions().Build(), new() { Platform = NeoCapabilityPlatform.IOS, Release = true, Profile = NeoSecurityProfile.ProductionLocalApp });
        var builder = new NeoRpcBuilder(new NeoRpcOptions { CapabilityManifest = manifest, SecurityProfile = manifest.Profile, AuthorizationService = new NeoCapabilityAuthorizationService(manifest) });
        if (options is null) builder.AddNeoAstraPushHandlers(push); else builder.AddNeoAstraPushHandlers(push, options);
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

    private static async Task<JsonElement> InvokeAsync(NeoRpcSession session, ConcurrentQueue<string> frames, string id, string command, string args = "{}")
    {
        var frame = await ResponseAsync(session, frames, id, command, args);
        Assert.IsTrue(frame.TryGetProperty("value", out var value), frame.ToString());
        return value;
    }

    private static async Task<string> ErrorAsync(NeoRpcSession session, ConcurrentQueue<string> frames, string id, string command)
    {
        var frame = await ResponseAsync(session, frames, id, command);
        Assert.IsTrue(frame.TryGetProperty("error", out var error), frame.ToString());
        return error.GetProperty("code").GetString()!;
    }

    private static async Task<JsonElement> ResponseAsync(NeoRpcSession session, ConcurrentQueue<string> frames, string id, string command, string args = "{}")
    {
        await session.ReceiveAsync($$$"""{"neoastra":1,"kind":"invoke","id":"{{{id}}}","command":"{{{command}}}","args":{{{args}}}}""");
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
