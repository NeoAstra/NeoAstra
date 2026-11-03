using System.Collections.Concurrent;
using System.Text.Json;
using global::NeoAstra.Desktop;
using global::NeoAstra.Desktop.Clipboard;
using global::NeoAstra.Desktop.DragDrop;
using global::NeoAstra.Desktop.Dialogs;
using global::NeoAstra.Desktop.GlobalShortcuts;
using global::NeoAstra.Desktop.Menus;
using global::NeoAstra.Desktop.Opener;
using global::NeoAstra.Desktop.Notifications;
using global::NeoAstra.Desktop.SafeStorage;
using global::NeoAstra.Desktop.SystemInfo;
using global::NeoAstra.Desktop.Tray;
using NeoAstra.Rpc;

namespace NeoAstra.Tests;

[TestClass]
public sealed class DesktopScheduledNotificationTests
{
    private const string Reminder = """{"appIdentity":"test.app","category":"reminders","urgency":"normal","persistent":false,"payload":"open:list/1","id":"reminder.1","title":"Milk","body":"Buy milk","actions":[],"deliverAt":"2099-01-01T08:00:00+01:00"}""";
    private const string AllPermissions = """
        {"id":"notifications:schedule","scope":{"appIdentity":"test.app","categories":["reminders"],"maximumPayloadBytes":1024,"persistent":false,"urgencies":["normal"]}},
        "notifications:unschedule","notifications:request","notifications:launch-activation","notifications:activation"
        """;

    [TestMethod]
    public async Task ScheduledNotification_OutlivesItsSessionAndItsActivationWaitsForTheNextPage()
    {
        var notifications = new NeoFakeNotifications();
        await using var services = Services(notifications);
        await using var rpc = Host(services, AllPermissions);
        await using (var first = new Page(rpc))
        {
            var scheduled = await first.InvokeAsync("desktop.notifications.schedule", Reminder);
            Assert.AreEqual("Success", scheduled.GetProperty("status").GetString());
        }

        // The page that scheduled it is gone, and no page listens: the tap waits.
        Assert.IsTrue(notifications.Activate(DesktopRendererRegistration.ScheduledNotificationPrefix + "reminder.1"));
        await using var second = new Page(rpc);
        var waiting = await second.InvokeAsync("desktop.notifications.launch-activation", "{}");
        Assert.AreEqual("reminder.1", waiting.GetProperty("activation").GetProperty("id").GetString());
        Assert.AreEqual("open:list/1", waiting.GetProperty("activation").GetProperty("payload").GetString());
        // It is taken once.
        var taken = await second.InvokeAsync("desktop.notifications.launch-activation", "{}");
        Assert.AreEqual(JsonValueKind.Null, taken.GetProperty("activation").ValueKind);
    }

    [TestMethod]
    public async Task ScheduledNotification_ActivationReachesAListeningPageAndDoesNotWait()
    {
        var notifications = new NeoFakeNotifications();
        await using var services = Services(notifications);
        await using var rpc = Host(services, AllPermissions);
        await using var page = new Page(rpc);
        await page.InvokeAsync("desktop.notifications.schedule", Reminder);
        await page.SubscribeAsync("desktop.notifications.activated");

        Assert.IsTrue(notifications.Activate(DesktopRendererRegistration.ScheduledNotificationPrefix + "reminder.1"));
        await page.WaitUntilAsync(() => page.Events().Length == 1);
        var activation = page.Events().Single().GetProperty("value");
        Assert.AreEqual("reminder.1", activation.GetProperty("id").GetString());
        Assert.AreEqual("open:list/1", activation.GetProperty("payload").GetString());
        var waiting = await page.InvokeAsync("desktop.notifications.launch-activation", "{}");
        Assert.AreEqual(JsonValueKind.Null, waiting.GetProperty("activation").ValueKind);
    }

    [TestMethod]
    public async Task ScheduledNotification_IsCanceledByAnyPageOfTheApplication()
    {
        var notifications = new NeoFakeNotifications();
        await using var services = Services(notifications);
        await using var rpc = Host(services, AllPermissions);
        await using (var first = new Page(rpc)) await first.InvokeAsync("desktop.notifications.schedule", Reminder);
        await using var second = new Page(rpc);
        var removed = await second.InvokeAsync("desktop.notifications.unschedule", """{"id":"reminder.1"}""");
        Assert.AreEqual("Success", removed.GetProperty("status").GetString());
        Assert.IsFalse(notifications.Activate(DesktopRendererRegistration.ScheduledNotificationPrefix + "reminder.1"));
    }

    [TestMethod]
    public async Task ScheduledNotification_RefusesAMomentWithoutOffsetAnIdTooLongAndWhatIsNotGranted()
    {
        await using var services = Services(new NeoFakeNotifications());
        await using var rpc = Host(services, AllPermissions);
        await using var page = new Page(rpc);
        Assert.IsNotNull(await page.ErrorAsync("desktop.notifications.schedule", Reminder.Replace("2099-01-01T08:00:00+01:00", "2099-01-01T08:00:00", StringComparison.Ordinal)));
        Assert.IsNotNull(await page.ErrorAsync("desktop.notifications.schedule", Reminder.Replace("2099-01-01T08:00:00+01:00", "tomorrow", StringComparison.Ordinal)));
        Assert.IsNotNull(await page.ErrorAsync("desktop.notifications.schedule", Reminder.Replace("reminder.1", new string('r', 120), StringComparison.Ordinal)));
        Assert.AreEqual("scope_denied", await page.ErrorAsync("desktop.notifications.schedule", Reminder.Replace("\"reminders\"", "\"other\"", StringComparison.Ordinal)));

        // The prefix of the scheduled notifications is not a page's to show with.
        await using var displayRpc = Host(services, AllPermissions.Replace("notifications:schedule", "notifications:display", StringComparison.Ordinal));
        await using var display = new Page(displayRpc);
        Assert.IsNotNull(await display.ErrorAsync("desktop.notifications.show", Reminder.Replace("\"reminder.1\"", $"\"{DesktopRendererRegistration.ScheduledNotificationPrefix}x\"", StringComparison.Ordinal).Replace(",\"deliverAt\":\"2099-01-01T08:00:00+01:00\"", "", StringComparison.Ordinal)));
        Assert.AreEqual("Success", (await display.InvokeAsync("desktop.notifications.show", Reminder.Replace(",\"deliverAt\":\"2099-01-01T08:00:00+01:00\"", "", StringComparison.Ordinal))).GetProperty("status").GetString());

        await using var statusOnlyRpc = Host(services, "\"notifications:status\"");
        await using var displayOnly = new Page(statusOnlyRpc);
        Assert.AreEqual("permission_denied", await displayOnly.ErrorAsync("desktop.notifications.schedule", Reminder));
        Assert.AreEqual("permission_denied", await displayOnly.ErrorAsync("desktop.notifications.unschedule", """{"id":"reminder.1"}"""));
        Assert.AreEqual("permission_denied", await displayOnly.ErrorAsync("desktop.notifications.request-permission", "{}"));
        Assert.AreEqual("permission_denied", await displayOnly.ErrorAsync("desktop.notifications.launch-activation", "{}"));
    }

    [TestMethod]
    public async Task RequestPermission_AnswersTheStatusThatTheUserChose()
    {
        await using var services = Services(new NeoFakeNotifications());
        await using var rpc = Host(services, AllPermissions);
        await using var page = new Page(rpc);
        var status = await page.InvokeAsync("desktop.notifications.request-permission", "{}");
        Assert.AreEqual("Granted", status.GetProperty("status").GetString());
    }

    private static NeoDesktopServices Services(INeoNotifications notifications)
    {
        var root = Path.GetTempPath();
        return new(new NeoFakeDialogs(), new NeoMenuService(new NeoCommandService()), new NeoTrayService(), new NeoFakeClipboard(),
            notifications, new NeoGlobalShortcutService(emulatedForTests: true), new NeoSystemInfoService("test.app", "Test App", "1.0"),
            new NeoExternalOpener(new global::NeoAstra.Desktop.Opener.NeoUrlScope(["https://example.com"]), new NeoFileScope([root]), new NeoFileScope([root]), new NeoOpenFilePolicy([NeoOpenFileIntent.TextDocument])),
            new NeoDragDropBroker(new NeoFileScope([root])), new UnsupportedSafeStorage("test"));
    }

    // The RPC host of the application, which outlives the documents of its view, as with NeoApp.
    private static NeoRpcHost Host(NeoDesktopServices services, string permissions)
    {
        var manifest = NeoCapabilityManifest.Resolve(System.Text.Encoding.UTF8.GetBytes($$"""
            {"$schema":"neoastra-capabilities-v1.schema.json","version":1,"capabilities":[{"id":"main","views":["main"],"permissions":[{{permissions}}]}]}
            """), new NeoPermissionCatalogBuilder().AddNeoAstraDesktopPermissions().Build(), new() { Platform = NeoCapabilityPlatform.IOS, Release = true, Profile = NeoSecurityProfile.ProductionLocalApp });
        var builder = new NeoRpcBuilder(new NeoRpcOptions { CapabilityManifest = manifest, SecurityProfile = manifest.Profile, AuthorizationService = new NeoCapabilityAuthorizationService(manifest) });
        builder.AddNeoAstraDesktopHandlers(services, new NeoDesktopRendererOptions());
        return builder.Build();
    }

    // One document of the main view over the in-memory transport.
    private sealed class Page : IAsyncDisposable
    {
        private static int s_document;
        private readonly NeoRpcSession _session;
        private readonly ConcurrentQueue<string> _frames = new();
        private int _next;

        public Page(NeoRpcHost rpc)
        {
            var document = $"document-{Interlocked.Increment(ref s_document)}";
            _session = rpc.OpenSession(new NeoRpcSessionIdentity("main", document) { Platform = NeoCapabilityPlatform.IOS, IsMainFrame = true }, (json, _) => { _frames.Enqueue(json); return ValueTask.CompletedTask; });
        }

        public async Task<JsonElement> InvokeAsync(string command, string args)
        {
            var frame = await ResponseAsync(command, args);
            Assert.IsTrue(frame.TryGetProperty("value", out var value), frame.ToString());
            return value;
        }

        public async Task<string?> ErrorAsync(string command, string args)
        {
            var frame = await ResponseAsync(command, args);
            Assert.IsTrue(frame.TryGetProperty("error", out var error), frame.ToString());
            return error.GetProperty("code").GetString();
        }

        public async Task SubscribeAsync(string name)
        {
            var id = $"s{Interlocked.Increment(ref _next)}";
            await _session.ReceiveAsync($$$"""{"neoastra":1,"kind":"subscribe","id":"{{{id}}}","event":"{{{name}}}"}""");
            await WaitUntilAsync(() => _frames.Any(json => json.Contains($"\"{id}\"", StringComparison.Ordinal)));
        }

        public JsonElement[] Events()
            => _frames.Select(static json => JsonDocument.Parse(json).RootElement.Clone()).Where(static frame => frame.GetProperty("kind").GetString() == "event").ToArray();

        public async Task WaitUntilAsync(Func<bool> predicate)
        {
            for (var index = 0; index < 400 && !predicate(); index++) await Task.Delay(5);
            Assert.IsTrue(predicate());
        }

        private async Task<JsonElement> ResponseAsync(string command, string args)
        {
            var id = $"c{Interlocked.Increment(ref _next)}";
            await _session.ReceiveAsync($$$"""{"neoastra":1,"kind":"invoke","id":"{{{id}}}","command":"{{{command}}}","args":{{{args}}}}""");
            JsonElement? found = null;
            await WaitUntilAsync(() =>
            {
                foreach (var json in _frames)
                {
                    var frame = JsonDocument.Parse(json).RootElement.Clone();
                    if (frame.GetProperty("kind").GetString() == "result" && frame.TryGetProperty("id", out var value) && value.GetString() == id) { found = frame; return true; }
                }
                return false;
            });
            return found!.Value;
        }

        public ValueTask DisposeAsync() => _session.DisposeAsync();
    }
}
