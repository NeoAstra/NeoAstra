// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
using System.Text.Json;
using NeoAstra.Desktop;
using NeoAstra.Location;
using NeoAstra.Rpc;

namespace NeoAstra.Tests;

// The location plugin reaches Core Location and the location manager of Android only on a device; its contract, its permissions,
// its renderer commands, and how it reads the answers of Android are checked on every host.
[TestClass]
public sealed class LocationPluginTests
{
    private static readonly NeoLocationPosition Vienna = new(48.2082, 16.3738, 12.5, DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000)) { Altitude = 171, SpeedMetersPerSecond = 1.5 };

    [TestMethod]
    public async Task Metadata_MatchesTheEmbeddedManifestAndThePermissionPolicy()
    {
        await using var plugin = new NeoLocationPlugin(new NeoFakeLocation());
        var metadata = plugin.Metadata;
        Assert.AreEqual(NeoLocationPlugin.Id, metadata.Id);
        using var stream = typeof(NeoLocationPlugin).Assembly.GetManifestResourceStream("NeoAstra.Location.neoastra.plugin.json");
        Assert.IsNotNull(stream);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        Assert.AreEqual(NeoLocationPlugin.Id, root.GetProperty("id").GetString());
        Assert.AreEqual("none-by-default", root.GetProperty("rendererAuthority").GetString());
        var commands = root.GetProperty("commands").EnumerateArray().ToDictionary(static item => item.GetProperty("name").GetString()!, static item => item.GetProperty("permission").GetString()!, StringComparer.Ordinal);
        CollectionAssert.AreEquivalent(metadata.Commands.Select(static item => item.Name).ToArray(), commands.Keys.ToArray());
        Assert.IsTrue(metadata.Commands.All(item => commands[item.Name] == item.Permission));
        var permissions = root.GetProperty("permissions").EnumerateArray().ToDictionary(static item => item.GetProperty("id").GetString()!, static item => item.GetProperty("operations").EnumerateArray().Select(static value => value.GetString()!).ToArray(), StringComparer.Ordinal);
        foreach (var permission in NeoLocationPlugin.PermissionCatalog.Permissions) CollectionAssert.AreEquivalent(permission.Commands.ToArray(), permissions[permission.Id]);
        Assert.AreEqual(NeoPermissionRisk.Low, NeoLocationPlugin.PermissionCatalog.Permissions.Single(static item => item.Id == "location:status").Risk);
        Assert.AreEqual(NeoPermissionRisk.Sensitive, NeoLocationPlugin.PermissionCatalog.Permissions.Single(static item => item.Id == "location:read").Risk);

        // Registering the handlers grants nothing, and no handler waits longer or runs more often than its permission allows.
        var builder = new NeoRpcBuilder();
        builder.AddNeoAstraLocationHandlers(new NeoFakeLocation());
        await using var rpc = builder.Build();
        foreach (var command in metadata.Commands)
        {
            Assert.IsTrue(rpc.TryGetCommand(command.Name, out var descriptor));
            Assert.IsNotNull(descriptor);
            var permission = NeoLocationPlugin.PermissionCatalog.Permissions.Single(item => item.Id == command.Permission);
            Assert.AreEqual(command.Permission, descriptor.Options.Permission);
            Assert.IsTrue(descriptor.Options.Timeout <= permission.DefaultTimeout);
            Assert.IsTrue(descriptor.Options.MaximumConcurrency <= permission.MaximumConcurrency);
        }
    }

    [TestMethod]
    public async Task Renderer_ReadsThePositionOnlyWithTheGrantedPermission()
    {
        var location = new NeoFakeLocation { AnswerOfTheUser = new(NeoLocationPermission.Granted, true, true), Position = Vienna };
        await using var granted = Session(location, ["location:status", "location:read"], out var frames);
        Assert.AreEqual("NotRequested", (await InvokeAsync(granted, frames, "1", "location.authorization", "{}")).GetProperty("permission").GetString());
        var answer = await InvokeAsync(granted, frames, "2", "location.request-authorization", "{}");
        Assert.AreEqual("Granted", answer.GetProperty("permission").GetString());
        Assert.IsTrue(answer.GetProperty("isPrecise").GetBoolean());
        var position = await InvokeAsync(granted, frames, "3", "location.current-position", """{"accuracy":"High","timeoutMilliseconds":5000}""");
        Assert.AreEqual("Success", position.GetProperty("status").GetString());
        var value = position.GetProperty("value");
        Assert.AreEqual(48.2082, value.GetProperty("latitude").GetDouble());
        Assert.AreEqual(16.3738, value.GetProperty("longitude").GetDouble());
        Assert.AreEqual(12.5, value.GetProperty("accuracy").GetDouble());
        Assert.AreEqual(1_760_000_000_000, value.GetProperty("timestamp").GetInt64());
        Assert.AreEqual(171, value.GetProperty("altitude").GetDouble());
        Assert.AreEqual(JsonValueKind.Null, value.GetProperty("course").ValueKind);

        await using var statusOnly = Session(location, ["location:status"], out var statusFrames);
        Assert.AreEqual("Granted", (await InvokeAsync(statusOnly, statusFrames, "4", "location.authorization", "{}")).GetProperty("permission").GetString());
        Assert.IsTrue((await ErrorAsync(statusOnly, statusFrames, "5", "location.current-position", "{}")).Length > 0);
        Assert.IsTrue((await ErrorAsync(statusOnly, statusFrames, "6", "location.request-authorization", "{}")).Length > 0);
    }

    [TestMethod]
    public async Task Renderer_RejectsARequestOutOfItsRange()
    {
        var location = new NeoFakeLocation { Authorization = new(NeoLocationPermission.Granted, true, true), Position = Vienna };
        await using var session = Session(location, ["location:read"], out var frames);
        Assert.AreEqual("location_invalid", await ErrorAsync(session, frames, "1", "location.current-position", """{"timeoutMilliseconds":10}"""));
        Assert.AreEqual("location_invalid", await ErrorAsync(session, frames, "2", "location.current-position", """{"maximumAgeMilliseconds":-1}"""));
    }

    [TestMethod]
    public async Task Fake_AnswersWhatTheUserAndTheDeviceAllow()
    {
        var location = new NeoFakeLocation();
        var request = new NeoLocationRequest();
        Assert.AreEqual(NeoLocationPermission.NotRequested, (await location.GetAuthorizationAsync()).Permission);
        Assert.AreEqual(NeoLocation.PermissionCode, (await location.GetCurrentPositionAsync(request)).Code);
        Assert.AreEqual(NeoLocationPermission.Denied, (await location.RequestAuthorizationAsync()).Permission);
        location.Authorization = new(NeoLocationPermission.Granted, false, true);
        Assert.AreEqual(NeoLocation.TimeoutCode, (await location.GetCurrentPositionAsync(request)).Code);
        location.Position = Vienna;
        Assert.AreEqual(Vienna, (await location.GetCurrentPositionAsync(request)).Value);
        location.Authorization = new(NeoLocationPermission.Granted, false, false);
        var disabled = await location.GetCurrentPositionAsync(request);
        Assert.AreEqual(NeoDesktopStatus.Denied, disabled.Status);
        Assert.AreEqual(NeoLocation.ServiceDisabledCode, disabled.Code);
    }

    [TestMethod]
    public async Task System_AnswersUnsupportedOnADesktopAndValidatesTheRequest()
    {
        var location = NeoLocation.CreateSystem();
        Assert.AreEqual(NeoSupportLevel.None, location.Support.SupportLevel);
        Assert.AreEqual(new NeoLocationAuthorization(NeoLocationPermission.Denied, false, false), await location.RequestAuthorizationAsync());
        Assert.AreEqual(NeoDesktopStatus.Unsupported, (await location.GetCurrentPositionAsync(new NeoLocationRequest())).Status);
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await location.GetCurrentPositionAsync(null!));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await location.GetCurrentPositionAsync(new NeoLocationRequest { Timeout = TimeSpan.FromMinutes(3) }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await location.GetCurrentPositionAsync(new NeoLocationRequest { MaximumAge = TimeSpan.FromDays(2) }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await location.GetCurrentPositionAsync(new NeoLocationRequest { Accuracy = (NeoLocationAccuracy)7 }));
        Assert.AreEqual(NeoSupportLevel.Native, new AndroidLocation().Support.SupportLevel);
    }

    [TestMethod]
    public void Android_ReadsTheAnswersOfTheJavaService()
    {
        Assert.AreEqual(new NeoLocationAuthorization(NeoLocationPermission.Granted, false, true),
            AndroidLocation.Authorization(JsonDocument.Parse("""{"status":"Success","permission":"Granted","precise":false,"enabled":true}""")));
        Assert.AreEqual(new NeoLocationAuthorization(NeoLocationPermission.Denied, false, false), AndroidLocation.Authorization(JsonDocument.Parse("""{"status":"Unsupported"}""")));

        var fix = AndroidLocation.Position(JsonDocument.Parse("""{"status":"Success","latitude":48.2082,"longitude":16.3738,"accuracy":12.5,"timestamp":1760000000000,"altitude":171,"speed":1.5}"""));
        Assert.AreEqual(Vienna, fix.Value);
        var timeout = AndroidLocation.Position(JsonDocument.Parse("""{"status":"NotFound","code":"timeout"}"""));
        Assert.AreEqual(NeoDesktopStatus.NotFound, timeout.Status);
        Assert.AreEqual(NeoLocation.TimeoutCode, timeout.Code);
        Assert.AreEqual(NeoDesktopStatus.Failed, AndroidLocation.Position(JsonDocument.Parse("{}")).Status);
        // A desktop backend has no platform services to reach the Java service with.
        DesktopPlatformCall.AssertNotSupported(() => AndroidLocation.Call("authorization", null));
    }

    [TestMethod]
    public void Permissions_ResolveForEveryPlatform()
    {
        var catalog = new NeoPermissionCatalogBuilder().AddNeoAstraLocationPermissions().Build();
        foreach (var platform in Enum.GetValues<NeoCapabilityPlatform>())
        {
            var manifest = Manifest(catalog, platform, ["location:status", "location:read"]);
            Assert.IsNotNull(manifest);
        }
    }

    private static NeoRpcSession Session(INeoLocation location, string[] permissions, out ConcurrentQueue<string> frames)
    {
        var manifest = Manifest(new NeoPermissionCatalogBuilder().AddNeoAstraLocationPermissions().Build(), NeoCapabilityPlatform.Android, permissions);
        var builder = new NeoRpcBuilder(new NeoRpcOptions { CapabilityManifest = manifest, SecurityProfile = manifest.Profile, AuthorizationService = new NeoCapabilityAuthorizationService(manifest) });
        builder.AddNeoAstraLocationHandlers(location);
        var rpc = builder.Build();
        var queue = new ConcurrentQueue<string>();
        frames = queue;
        return rpc.OpenSession(new NeoRpcSessionIdentity("main", "document-1") { Platform = NeoCapabilityPlatform.Android, IsMainFrame = true }, (json, _) => { queue.Enqueue(json); return ValueTask.CompletedTask; });
    }

    private static NeoCapabilityManifest Manifest(NeoPermissionCatalog catalog, NeoCapabilityPlatform platform, string[] permissions)
        => NeoCapabilityManifest.Resolve(System.Text.Encoding.UTF8.GetBytes($$"""
            {"$schema":"neoastra-capabilities-v1.schema.json","version":1,"capabilities":[{"id":"main","views":["main"],"permissions":[{{string.Join(",", permissions.Select(static item => $"\"{item}\""))}}]}]}
            """), catalog, new() { Platform = platform, Release = true, Profile = NeoSecurityProfile.ProductionLocalApp });

    private static async Task<JsonElement> InvokeAsync(NeoRpcSession session, ConcurrentQueue<string> frames, string id, string command, string arguments)
    {
        var frame = await ResponseAsync(session, frames, id, command, arguments);
        Assert.IsTrue(frame.TryGetProperty("value", out var value), frame.ToString());
        return value;
    }

    private static async Task<string> ErrorAsync(NeoRpcSession session, ConcurrentQueue<string> frames, string id, string command, string arguments)
    {
        var frame = await ResponseAsync(session, frames, id, command, arguments);
        Assert.IsTrue(frame.TryGetProperty("error", out var error), frame.ToString());
        return error.GetProperty("code").GetString()!;
    }

    private static async Task<JsonElement> ResponseAsync(NeoRpcSession session, ConcurrentQueue<string> frames, string id, string command, string arguments)
    {
        await session.ReceiveAsync($$"""{"neoastra":1,"kind":"invoke","id":"{{id}}","command":"{{command}}","args":{{arguments}}}""");
        for (var index = 0; index < 400; index++)
        {
            foreach (var json in frames)
            {
                var frame = JsonDocument.Parse(json).RootElement.Clone();
                if (frame.TryGetProperty("id", out var value) && value.GetString() == id) return frame;
            }
            await Task.Delay(5);
        }
        Assert.Fail($"No response to {command}.");
        return default;
    }
}
