// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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
using global::NeoAstra.Desktop.WindowState;
using NeoAstra.Rpc;

namespace NeoAstra.Tests;

[TestClass]
public sealed class DesktopServicesTests
{
    [TestMethod]
    public async Task PluginGraphIsStaticDeterministicAndRejectsCyclesAndDuplicates()
    {
        var host = new NeoPluginBuilder()
            .AddNeoAstraPlugin(() => new StubPlugin("z.plugin", [new NeoPluginDependency("a.plugin", new Version(1, 0))]))
            .AddNeoAstraPlugin(() => new StubPlugin("a.plugin"))
            .Build();
        CollectionAssert.AreEqual(new[] { "a.plugin", "z.plugin" }, host.Plugins.Select(static item => item.Id).ToArray());
        await host.DisposeAsync();

        Assert.Throws<InvalidOperationException>(() => new NeoPluginBuilder().AddNeoAstraPlugin(() => new StubPlugin("same.plugin")).AddNeoAstraPlugin(() => new StubPlugin("same.plugin")).Build());
        Assert.Throws<InvalidOperationException>(() => new NeoPluginBuilder().AddNeoAstraPlugin(() => new StubPlugin("a.plugin", [new NeoPluginDependency("b.plugin", new Version(1, 0))])).AddNeoAstraPlugin(() => new StubPlugin("b.plugin", [new NeoPluginDependency("a.plugin", new Version(1, 0))])).Build());
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task PluginTeardownInitiatesEveryRemainingGroupAfterDeadline()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probes = Enumerable.Range(0, 6).Select(index => new DisposalProbe(index == 0 ? release.Task : Task.CompletedTask)).ToArray();
        using var canceled = new CancellationTokenSource(); canceled.Cancel();

        await NeoPluginHost.DisposeGroupsContainedAsync(
        [
            (probes.Take(2).Cast<IAsyncDisposable>(), "resource"),
            (probes.Skip(2).Take(2).Cast<IAsyncDisposable>(), "adapter"),
            (probes.Skip(4).Cast<IAsyncDisposable>(), "plugin"),
        ], null, canceled.Token);

        Assert.IsTrue(probes.All(static probe => probe.Started == 1), "Every remaining resource, adapter, and plugin disposal must be initiated after the deadline.");
        release.TrySetResult();
    }

    [TestMethod]
    public void OfficialPluginMetadataIsStaticScopedAndGrantFree()
    {
        var catalog = new NeoPermissionCatalogBuilder().AddNeoAstraDesktopPermissions().Build();
        var plugin = NeoDesktopEssentialsPlugin.PermissionCatalog;

        Assert.AreEqual(NeoDesktopEssentialsPlugin.Id, plugin.Id);
        Assert.IsTrue(plugin.Permissions.Count >= 20);
        Assert.IsTrue(plugin.Permissions.Where(static item => item.Risk == NeoPermissionRisk.High).All(static item => item.ScopeFamily == NeoScopeFamily.None || item.ScopeRequired));
        Assert.IsTrue(plugin.Permissions.SelectMany(static item => item.Commands).All(static command => command.StartsWith("desktop.", StringComparison.Ordinal)));
        Assert.IsTrue(plugin.Permissions.Single(static item => item.Id == "window:files").ScopeRequired);
        Assert.AreEqual(NeoScopeFamily.Filesystem, plugin.Permissions.Single(static item => item.Id == "window:files").ScopeFamily);
        Assert.IsFalse(plugin.Permissions.Single(static item => item.Id == "window:polish").ScopeRequired);
        Assert.AreEqual(1, catalog.Plugins.Count);
        Assert.IsTrue(catalog.Permissions.Count >= 20);
    }

    [TestMethod]
    public async Task WindowPolishReportsPerFeatureSemanticsAndValidatesBeforeNativeUse()
    {
        await using var service = new NeoWindowPolishService();
        Assert.IsFalse(string.IsNullOrWhiteSpace(service.IconSupport.Details));
        Assert.IsFalse(string.IsNullOrWhiteSpace(service.ContentProtectionSupport.Details));
        Assert.IsFalse(string.IsNullOrWhiteSpace(service.EnabledSupport.Details));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.SetProgressAsync(null!, (NeoWindowProgressState)99, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.SetProgressAsync(null!, NeoWindowProgressState.Normal, double.NaN));
        Assert.Throws<ArgumentException>(() => service.SetBadgeAsync(null!, new string('x', 17)));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.SetTitleBarThemeAsync(null!, (NeoWindowTitleBarTheme)99));

        if (OperatingSystem.IsWindows())
        {
            Assert.AreEqual(NeoSupportLevel.Native, service.ProgressSupport.SupportLevel);
            Assert.AreEqual(NeoSupportLevel.Limited, service.ContentProtectionSupport.SupportLevel);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.AreEqual(NeoSupportLevel.Native, service.BadgeSupport.SupportLevel);
            Assert.AreEqual(NeoSupportLevel.Native, service.DocumentSupport.SupportLevel);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.AreEqual(NeoSupportLevel.None, service.ContentProtectionSupport.SupportLevel);
        }
    }

    [TestMethod]
    public async Task SystemGraphSelectsNativeInboundAndOutboundDragIntegration()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            await using var services = NeoDesktopServices.CreateSystem("test.drag", "Drag Test", "1.0", root, ["https://example.com"], [root], [root], [NeoOpenFileIntent.TextDocument]);
            Assert.AreEqual(NeoSupportLevel.Limited, services.DragDrop.Support.SupportLevel);
            StringAssert.Contains(services.DragDrop.Support.Details, "file, text, and URL drags");
            StringAssert.Contains(services.DragDrop.Support.Details, "source-bound one-shot gestures");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task RendererRegistrationExactlyMatchesStaticMetadataAndBackendOnlyRegistersNothing()
    {
        var root = CreateTemporaryDirectory();
        var commands = new NeoCommandService();
        var services = new NeoDesktopServices(new NeoFakeDialogs(), new NeoMenuService(commands), new NeoTrayService(), new NeoFakeClipboard(),
            new NeoFakeNotifications(), new NeoGlobalShortcutService(emulatedForTests: true), new NeoSystemInfoService("test.app", "Test App", "1.0"),
            new NeoExternalOpener(new global::NeoAstra.Desktop.Opener.NeoUrlScope(["https://example.com"]), new NeoFileScope([root]), new NeoFileScope([root]), new NeoOpenFilePolicy([NeoOpenFileIntent.TextDocument])),
            new NeoDragDropBroker(new NeoFileScope([root])), new UnsupportedSafeStorage("test"));
        try
        {
            var metadata = new NeoDesktopEssentialsPlugin(services).Metadata;
            CollectionAssert.AreEquivalent(NeoDesktopRendererContract.Commands, metadata.Commands.Select(static value => value.Name).ToArray());
            CollectionAssert.AreEquivalent(NeoDesktopRendererContract.Events, metadata.Events.Select(static value => value.Name).ToArray());
            var allOperations = metadata.PermissionCatalog!.Permissions.SelectMany(static value => value.Commands).ToArray();
            CollectionAssert.AreEquivalent(allOperations, metadata.Commands.Select(static value => value.Name).Concat(metadata.Events.Select(static value => value.Name)).ToArray());
            Assert.IsTrue(metadata.Commands.All(command => metadata.PermissionCatalog.Permissions.Single(permission => permission.Id == command.Permission).Commands.Contains(command.Name, StringComparer.Ordinal)));
            Assert.IsTrue(metadata.Events.All(pluginEvent => metadata.PermissionCatalog.Permissions.Single(permission => permission.Id == pluginEvent.Permission).Commands.Contains(pluginEvent.Name, StringComparer.Ordinal)));

            var backendOnly = new NeoRpcBuilder().Build();
            Assert.IsFalse(backendOnly.TryGetCommand(NeoDesktopRendererContract.Commands[0], out _));
            Assert.IsFalse(backendOnly.TryGetEvent(NeoDesktopRendererContract.Events[0], out _));
            await backendOnly.DisposeAsync();

            var rendererBuilder = new NeoRpcBuilder();
            rendererBuilder.AddNeoAstraDesktopHandlers(services, new NeoDesktopRendererOptions
            {
                FileRoots = new Dictionary<string, string> { ["root"] = root },
                AllowedMenuCommands = new HashSet<string> { "test.command" },
                AllowedTrayIds = new HashSet<string> { "main" },
                AllowedGlobalShortcuts = new HashSet<string> { "Ctrl+Shift+P" },
                AllowedSafeStorageKeys = new HashSet<string> { "test" },
            });
            var renderer = rendererBuilder.Build();
            Assert.IsTrue(metadata.Commands.All(command => renderer.TryGetCommand(command.Name, out var descriptor) && descriptor!.Options.Permission == command.Permission));
            Assert.IsTrue(metadata.Events.All(pluginEvent => renderer.TryGetEvent(pluginEvent.Name, out var descriptor) && descriptor!.Options.Permission == pluginEvent.Permission));
            await renderer.DisposeAsync();
        }
        finally { await services.DisposeAsync(); Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task EmbeddedDesktopMetadataAndScopeSchemasMatchManagedDeclarations()
    {
        var assembly = typeof(NeoDesktopEssentialsPlugin).Assembly;
        using var stream = assembly.GetManifestResourceStream("NeoAstra.Desktop.neoastra.plugin.json");
        Assert.IsNotNull(stream);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        Assert.AreEqual("none-by-default", root.GetProperty("rendererAuthority").GetString());
        var services = new NeoDesktopServices(new NeoFakeDialogs(), new NeoMenuService(new NeoCommandService()), new NeoTrayService(), new NeoFakeClipboard(), new NeoFakeNotifications(), new NeoGlobalShortcutService(true),
            new NeoSystemInfoService("test.app", "Test", "1"), new NeoExternalOpener(new global::NeoAstra.Desktop.Opener.NeoUrlScope(["https://example.com"]), new NeoFileScope([Path.GetTempPath()]), new NeoFileScope([Path.GetTempPath()]), new NeoOpenFilePolicy([NeoOpenFileIntent.TextDocument])),
            new NeoDragDropBroker(new NeoFileScope([Path.GetTempPath()])), new UnsupportedSafeStorage("test"));
        var plugin = new NeoDesktopEssentialsPlugin(services).Metadata;
        var commands = root.GetProperty("commands").EnumerateArray().ToDictionary(static item => item.GetProperty("name").GetString()!, static item => item.GetProperty("permission").GetString()!, StringComparer.Ordinal);
        var events = root.GetProperty("events").EnumerateArray().ToDictionary(static item => item.GetProperty("name").GetString()!, static item => item.GetProperty("permission").GetString()!, StringComparer.Ordinal);
        CollectionAssert.AreEquivalent(plugin.Commands.Select(static item => item.Name).ToArray(), commands.Keys.ToArray());
        CollectionAssert.AreEquivalent(plugin.Events.Select(static item => item.Name).ToArray(), events.Keys.ToArray());
        Assert.IsTrue(plugin.Commands.All(item => commands[item.Name] == item.Permission));
        Assert.IsTrue(plugin.Events.All(item => events[item.Name] == item.Permission));
        foreach (var schema in plugin.Commands.Select(static item => item.ScopeSchema).Where(static value => value is not null).Distinct(StringComparer.Ordinal))
            Assert.IsNotNull(assembly.GetManifestResourceNames().SingleOrDefault(name => name.EndsWith(schema!.Replace('/', '.'), StringComparison.Ordinal)));
        await services.DisposeAsync();
    }

    [TestMethod]
    public async Task CommandActivationIsSerialAndDisposalRejectsQueuedWork()
    {
        var commands = new NeoCommandService();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        commands.Register("test.run", async _ => { Interlocked.Increment(ref calls); entered.TrySetResult(); await release.Task.ConfigureAwait(false); });

        var first = commands.ActivateAsync("test.run").AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = commands.ActivateAsync("test.run").AsTask();
        var dispose = commands.DisposeAsync().AsTask();
        release.TrySetResult();

        Assert.AreEqual(NeoDesktopStatus.Success, await first);
        Assert.AreEqual(NeoDesktopStatus.Canceled, await second);
        await dispose;
        Assert.AreEqual(1, calls);
        Assert.AreEqual(NeoDesktopStatus.Canceled, await commands.ActivateAsync("test.run"));
    }

    [TestMethod]
    public void MenuTrayAndShortcutDescriptorsAreBoundedAndDeterministic()
    {
        var command = NeoMenuItem.Command("open", "Open", "document.open", "shift+ctrl+p");
        Assert.AreEqual("Ctrl+Shift+P", command.Accelerator);
        Assert.AreEqual("text", Assert.Throws<ArgumentNullException>(() => NeoMenuItem.Command("null-command", null!, "document.null")).ParamName);
        Assert.AreEqual(typeof(ArgumentException), Assert.Throws<ArgumentException>(() => NeoMenuItem.Command("empty-command", "", "document.empty")).GetType());
        Assert.AreEqual("text", Assert.Throws<ArgumentNullException>(() => NeoMenuItem.Submenu("null-submenu", null!, [command])).ParamName);
        Assert.AreEqual(typeof(ArgumentException), Assert.Throws<ArgumentException>(() => NeoMenuItem.Submenu("empty-submenu", "", [command])).GetType());
        Assert.AreEqual("localizedText", Assert.Throws<ArgumentNullException>(() => NeoMenuItem.RoleItem("null-role", NeoMenuRole.Copy, null!)).ParamName);
        Assert.AreEqual(typeof(ArgumentException), Assert.Throws<ArgumentException>(() => NeoMenuItem.RoleItem("empty-role", NeoMenuRole.Copy, "")).GetType());
        Assert.Throws<ArgumentException>(() => NeoMenuItem.Command("close", "Close", "document.close", "Alt+F4"));
        Assert.Throws<ArgumentException>(() => NeoMenuItem.Submenu("duplicate", "Bad", [command, command]));

        var tray = new NeoTrayService();
        var sequences = new List<ulong>();
        tray.Activated += (_, activation) => sequences.Add(activation.Sequence);
        tray.Set(new NeoTrayItemOptions { Id = "main", ToolTip = "NeoAstra", Menu = [command] });
        tray.Activate("main"); tray.Activate("main", secondary: true);
        CollectionAssert.AreEqual(new ulong[] { 1, 2 }, sequences);

        var shortcuts = new NeoGlobalShortcutService(emulatedForTests: true);
        Assert.AreEqual(NeoDesktopStatus.Success, shortcuts.Register("palette", "Ctrl+Shift+P"));
        Assert.AreEqual(NeoDesktopStatus.Conflict, shortcuts.Register("other", "Shift+Ctrl+P"));
    }

    [TestMethod]
    public async Task MenuPresenterReplacementActivationRollbackAndTeardownAreTransactional()
    {
        var commands = new NeoCommandService(); var activated = new List<string>();
        commands.Register("first.run", _ => { activated.Add("first"); return ValueTask.CompletedTask; });
        commands.Register("second.run", _ => { activated.Add("second"); return ValueTask.CompletedTask; });
        var presenter = new RecordingMenuPresenter(commands); var service = new NeoMenuService(commands, presenter: presenter);
        var first = NeoMenuItem.Command("first", "Localized Héllo", "first.run", "Ctrl+1");
        var second = NeoMenuItem.Command("second", "更新", "second.run", "Ctrl+2", isChecked: true);

        await service.SetMenuAsync("context:view", [first]);
        presenter.ActivateDuringSet = "second.run";
        await service.SetMenuAsync("context:view", [second]);
        CollectionAssert.AreEqual(new[] { "second.run" }, presenter.VisibleCommands.ToArray());
        CollectionAssert.AreEqual(new[] { "second" }, activated);
        Assert.AreEqual("更新", service.GetMenu("context:view")[0].Text);

        presenter.FailNextSet = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetMenuAsync("context:view", [first]).AsTask());
        Assert.AreEqual("更新", service.GetMenu("context:view")[0].Text);
        Assert.AreEqual(NeoDesktopStatus.Success, await commands.ActivateAsync("second.run"));
        Assert.IsTrue(await service.RemoveMenuAsync("context:view"));
        await service.DisposeAsync();
        CollectionAssert.AreEqual(new[] { "set:context:view", "set:context:view", "set:context:view", "remove:context:view", "dispose" }, presenter.Operations.ToArray());
        Assert.Throws<ObjectDisposedException>(() => presenter.SetMenu("context:view", [first]));
        await commands.DisposeAsync();
    }

    [TestMethod]
    public async Task TrayPresenterUpdatesActivateInArrivalOrderAndCannotCallbackAfterTeardown()
    {
        var presenter = new RecordingTrayPresenter(); var tray = new NeoTrayService(presenter: presenter); var activations = new List<NeoTrayActivation>(); tray.Activated += (_, value) => activations.Add(value);
        var first = new NeoTrayItemOptions { Id = "main", ToolTip = "Hello", Menu = [NeoMenuItem.RoleItem("quit", NeoMenuRole.Quit)] };
        tray.Set(first); presenter.Emit("main", false); presenter.Emit("main", false); presenter.Emit("main", true);
        await tray.SetAsync(first with { ToolTip = "更新", IsTemplateImage = true });
        Assert.AreEqual("更新", presenter.Items["main"].ToolTip);
        CollectionAssert.AreEqual(new ulong[] { 1, 2, 3 }, activations.Select(static value => value.Sequence).ToArray());
        CollectionAssert.AreEqual(new[] { false, false, true }, activations.Select(static value => value.Secondary).ToArray());
        await tray.DisposeAsync(); presenter.Emit("main", false);
        Assert.AreEqual(3, activations.Count); Assert.AreEqual(1, presenter.RemoveCount); Assert.IsTrue(presenter.Disposed);
    }

    [TestMethod]
    public async Task AccessibilityAndLocalizationSnapshotsRemainDeterministic()
    {
        using var systemInfo = new NeoSystemInfoService("test.accessibility", "Localized Héllo 更新", "1.0", dispatcher: null, monitorPlatform: false);
        var changed = new TaskCompletionSource<NeoThemeSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        systemInfo.ThemeChanged += (_, snapshot) => changed.TrySetResult(snapshot);
        var expected = new NeoThemeSnapshot(NeoSystemTheme.HighContrast, "#12ABEF", ReducedMotion: true, ReducedTransparency: true);
        systemInfo.PublishTheme(expected);
        Assert.AreEqual(expected, await changed.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(expected, systemInfo.Theme);

        var implicitRole = NeoMenuItem.RoleItem("copy", NeoMenuRole.Copy);
        Assert.IsNull(implicitRole.Text);
        Assert.Throws<NotSupportedException>(() => NeoMenuRolePresentation.RequireExplicitLabel(implicitRole, "test presenter"), "A presenter without reliable framework role labels must not silently claim OS localization.");
        var role = NeoMenuItem.RoleItem("copy-localized", NeoMenuRole.Copy, "複製 Héllo 🌍");
        Assert.AreEqual("複製 Héllo 🌍", role.Text);
        var presenter = new RecordingMenuPresenter(new NeoCommandService());
        presenter.SetMenu("application", [role]);
        Assert.AreEqual("複製 Héllo 🌍", presenter.Snapshots[^1][0].Text, "The application-localized Unicode role label must reach the presenter snapshot unchanged.");
        var commands = new NeoCommandService();
        var windowsSupport = new WindowsMenuPresenter(commands, null).Support; var macSupport = new MacMenuPresenter(commands, null).Support; var linuxSupport = new LinuxMenuPresenter(commands, null).Support;
        Assert.AreEqual(NeoSupportLevel.Limited, windowsSupport.SupportLevel); StringAssert.Contains(windowsSupport.Details, "role labels must be supplied by the application");
        Assert.AreEqual(NeoSupportLevel.Limited, macSupport.SupportLevel); StringAssert.Contains(macSupport.Details, "role labels must be supplied by the application");
        StringAssert.Contains(linuxSupport.Details, "GTK4 removed stock menu-item labels");
        Assert.AreEqual("Ctrl+Alt+Shift+P", NeoAccelerator.Normalize("shift+ctrl+alt+p"));
        Assert.AreEqual("Localized Héllo 更新", systemInfo.Metadata.ApplicationName);
    }

    [TestMethod]
    public void WindowsTaskDialogInteropUsesNativePackedLayouts()
    {
        var dialogs = typeof(NeoDesktopServices).Assembly.GetType("NeoAstra.Desktop.Dialogs.WindowsDialogs", throwOnError: true)!;
        var config = dialogs.GetNestedType("TaskDialogConfig", System.Reflection.BindingFlags.NonPublic)!;
        var button = dialogs.GetNestedType("TaskDialogButton", System.Reflection.BindingFlags.NonPublic)!;

        Assert.AreEqual(IntPtr.Size == 8 ? 160 : 96, Marshal.SizeOf(config));
        Assert.AreEqual(IntPtr.Size == 8 ? 12 : 8, Marshal.SizeOf(button));
    }

    [TestMethod]
    public void MacDialogScriptsUseLiteralMultipleSelectionClause()
    {
        var scope = new NeoFileScope([Path.GetTempPath()]);
        var multiple = new NeoFileDialogOptions { AllowMultiple = true, Scope = scope };
        var single = new NeoFileDialogOptions { Scope = scope };

        var multipleFileScript = ProcessDialogs.MacArguments(multiple, folders: false, save: false)[1];
        var multipleFolderScript = ProcessDialogs.MacArguments(multiple, folders: true, save: false)[1];
        var singleFileScript = ProcessDialogs.MacArguments(single, folders: false, save: false)[1];

        StringAssert.Contains(multipleFileScript, "choose file with prompt (item 1 of argv) with multiple selections allowed");
        StringAssert.Contains(multipleFolderScript, "choose folder with prompt (item 1 of argv) with multiple selections allowed");
        Assert.IsFalse(singleFileScript.Contains("multiple selections allowed", StringComparison.Ordinal));
        Assert.IsFalse(multipleFileScript.Contains("multiple selections allowed (", StringComparison.Ordinal));
    }

    [TestMethod]
    public void LinuxDialogsAuthorizeCanonicalSelectionsWithinScope()
    {
        var root = Path.Combine(Path.GetTempPath(), "neoastra-linux-dialog-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "selected.txt");
            File.WriteAllText(file, "selected");
            var options = new NeoFileDialogOptions { Scope = new NeoFileScope([root]) };
            var dialogs = new LinuxDialogs(dispatcher: null);

            Assert.AreEqual(File.Exists("/usr/bin/zenity") ? NeoSupportLevel.Limited : NeoSupportLevel.None, dialogs.Support.SupportLevel);
            StringAssert.Contains(dialogs.Support.Details, File.Exists("/usr/bin/zenity") ? "GTK4-compatible dialogs" : "zenity is unavailable");
            var allowed = LinuxDialogs.AuthorizeSelections(options, [file], save: false, folders: false);
            Assert.AreEqual(NeoDesktopStatus.Success, allowed.Status);
            Assert.AreEqual(Path.GetFullPath(file), allowed.Value![0]);

            var outside = LinuxDialogs.AuthorizeSelections(options, [Path.GetTempPath()], save: false, folders: true);
            Assert.AreEqual(NeoDesktopStatus.Denied, outside.Status);
            Assert.AreEqual("path_scope", outside.Code);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void LinuxRoleTargetSelectionUsesOwnedWindowAndStableLiveViewOrdering()
    {
        var owner = new object(); var otherOwner = new object();
        var wrongLabelMatch = new RoleTargetCandidate(otherOwner, "window-label", (nint)11);
        var stale = new RoleTargetCandidate(owner, "aaa-stale", 0);
        var later = new RoleTargetCandidate(owner, "z-view", (nint)33);
        var selected = new RoleTargetCandidate(owner, "a-view", (nint)22);

        var actual = LinuxRoleTargetSelection.Select([wrongLabelMatch, later, stale, selected], owner, static value => value.Owner, static value => value.Label, static value => value.Widget);

        Assert.AreSame(selected, actual, "Selection must use owner identity, ignore zero/stale handles, and choose the ordinally first live view label.");
        Assert.IsNull(LinuxRoleTargetSelection.Select([wrongLabelMatch, stale], owner, static value => value.Owner, static value => value.Label, static value => value.Widget));
        Assert.IsTrue(LinuxRoleTargetSelection.IsCurrentWidget((nint)22, (nint)22));
        Assert.IsFalse(LinuxRoleTargetSelection.IsCurrentWidget((nint)22, (nint)23), "A replaced native widget is stale and must not be invoked.");
        Assert.IsFalse(LinuxRoleTargetSelection.IsCurrentWidget((nint)22, 0));
        var unlabeledHigh = new RoleTargetCandidate(owner, null, (nint)44); var unlabeledLow = new RoleTargetCandidate(owner, null, (nint)12);
        Assert.AreSame(unlabeledLow, LinuxRoleTargetSelection.Select([unlabeledHigh, unlabeledLow], owner, static value => value.Owner, static value => value.Label, static value => value.Widget));
    }

    [TestMethod]
    public async Task ClipboardFormatsAreExplicitAndContentIsCapped()
    {
        var clipboard = new NeoFakeClipboard();
        var text = Encoding.UTF8.GetBytes("hello");
        Assert.AreEqual(NeoDesktopStatus.Success, await clipboard.WriteAsync(NeoClipboardFormat.Text, text));
        CollectionAssert.AreEqual(text, (await clipboard.ReadAsync(NeoClipboardFormat.Text)).Value!);
        var unicode = Encoding.UTF8.GetBytes("héllo 🌍\r\n"); Assert.AreEqual(NeoDesktopStatus.Success, await clipboard.WriteAsync(NeoClipboardFormat.Html, unicode)); CollectionAssert.AreEqual(unicode, (await clipboard.ReadAsync(NeoClipboardFormat.Html)).Value!);
        await Assert.ThrowsAsync<ArgumentException>(() => clipboard.WriteAsync(NeoClipboardFormat.Text, "a\0b"u8.ToArray()).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => clipboard.WriteAsync(NeoClipboardFormat.Html, "<b>a\0b</b>"u8.ToArray()).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => clipboard.WriteAsync(NeoClipboardFormat.Png, new byte[NeoDesktopLimits.MaximumClipboardBytes + 1]).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => clipboard.WriteAsync(NeoClipboardFormat.FileList, "[\"../relative\"]"u8.ToArray()).AsTask());
        Assert.AreEqual(NeoDesktopStatus.NotFound, (await clipboard.ReadAsync(NeoClipboardFormat.Png)).Status);
        var linux = new LinuxClipboard(dispatcher: null);
        Assert.AreEqual(NeoSupportLevel.Native, linux.Support.SupportLevel);
        CollectionAssert.AreEqual(new[] { "text/plain;charset=utf-8", "text/plain" }, LinuxClipboard.Targets(NeoClipboardFormat.Text).ToArray());
        CollectionAssert.AreEqual(new[] { "image/png" }, LinuxClipboard.Targets(NeoClipboardFormat.Png).ToArray());
    }

    [TestMethod]
    public void RendererPolicyIsValidatedFrozenAndCanonicalizesExistingChildren()
    {
        var root = CreateTemporaryDirectory(); var outside = CreateTemporaryDirectory();
        try
        {
            var roots = new Dictionary<string, string> { ["root"] = root }; var menus = new HashSet<string> { "allowed.command" }; var tray = new HashSet<string> { "main" }; var shortcuts = new HashSet<string> { "Ctrl+Shift+P" }; var secrets = new HashSet<string> { "token" };
            var options = new NeoDesktopRendererOptions { FileRoots = roots, AllowedMenuCommands = menus, AllowedTrayIds = tray, AllowedGlobalShortcuts = shortcuts, AllowedSafeStorageKeys = secrets };
            roots["root"] = outside; menus.Add("later.command"); tray.Add("later"); shortcuts.Add("Ctrl+Shift+Q"); secrets.Add("later");
            Assert.AreEqual(NeoFileScope.Canonicalize(root, requireExisting: true), options.FileRoots["root"]); Assert.IsFalse(options.AllowedMenuCommands.Contains("later.command")); Assert.IsFalse(options.AllowedTrayIds.Contains("later")); Assert.IsFalse(options.AllowedGlobalShortcuts.Contains("Ctrl+Shift+Q")); Assert.IsFalse(options.AllowedSafeStorageKeys.Contains("later"));
            Assert.Throws<ArgumentException>(() => new NeoDesktopRendererOptions { AllowedGlobalShortcuts = new HashSet<string> { "shift+ctrl+p" } });
            var file = Path.Combine(root, "inside.txt"); File.WriteAllText(file, "inside"); Assert.AreEqual(NeoFileScope.Canonicalize(file, requireExisting: true), options.ResolveExisting("root", "inside.txt"));
            var outsideFile = Path.Combine(outside, "outside.txt"); File.WriteAllText(outsideFile, "outside"); var link = Path.Combine(root, "link.txt");
            try { File.CreateSymbolicLink(link, outsideFile); Assert.Throws<NeoRpcException>(() => options.ResolveExisting("root", "link.txt")); }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { }
        }
        finally { Directory.Delete(root, recursive: true); Directory.Delete(outside, recursive: true); }
    }

    [TestMethod]
    public void MacShortcutNativeIdsAreProcessUniqueAcrossPresenters()
    {
        var values = Enumerable.Range(0, 256).Select(static _ => MacGlobalShortcutPresenter.AllocateNativeId()).ToArray();
        Assert.AreEqual(values.Length, values.Distinct().Count()); Assert.IsFalse(values.Contains(0u));
    }

    [TestMethod]
    public void LinuxNotificationProtocolArgumentsAreBoundedAndParsedWithoutShellEvaluation()
    {
        Assert.AreEqual("['default','Open','safe.id','It\\'s \\\\ safe']", LinuxNotifications.VariantArray(["default", "Open", "safe.id", "It's \\ safe"]));
        Assert.AreEqual("'org.example.It\\'s\\\\Safe'", LinuxNotifications.VariantString("org.example.It's\\Safe"));
        Assert.IsTrue(LinuxNotifications.TryParseNativeId("(uint32 4294967295,)", out var id)); Assert.AreEqual(uint.MaxValue, id);
        Assert.IsTrue(LinuxNotifications.TryParseNativeId("/org/freedesktop/Notifications: org.freedesktop.Notifications.ActionInvoked (uint32 42, 'default')", out id)); Assert.AreEqual(42u, id);
        Assert.IsFalse(LinuxNotifications.TryParseNativeId("(uint32 0,)", out _)); Assert.IsFalse(LinuxNotifications.TryParseNativeId("not a protocol reply", out _));
    }

    [TestMethod]
    public void WindowsNotificationReplacementCommitsOrRollsBackOneGeneration()
    {
        var operations = new List<string>();
        Assert.AreEqual(WindowsNotifications.NativeReplacementResult.Unchanged, WindowsNotifications.CompleteReplacement(7, 8, false, id => { operations.Add($"delete:{id}"); return true; }, id => { operations.Add($"restore:{id}"); return true; }));
        Assert.AreEqual(0, operations.Count);
        Assert.AreEqual(WindowsNotifications.NativeReplacementResult.Committed, WindowsNotifications.CompleteReplacement(null, 8, true, id => { operations.Add($"delete:{id}"); return true; }, id => { operations.Add($"restore:{id}"); return true; }));
        Assert.AreEqual(0, operations.Count);
        Assert.AreEqual(WindowsNotifications.NativeReplacementResult.Committed, WindowsNotifications.CompleteReplacement(7, 8, true, id => { operations.Add($"delete:{id}"); return true; }, id => { operations.Add($"restore:{id}"); return true; }));
        CollectionAssert.AreEqual(new[] { "delete:7" }, operations);
        operations.Clear();
        Assert.AreEqual(WindowsNotifications.NativeReplacementResult.Unchanged, WindowsNotifications.CompleteReplacement(7, 8, true, id => { operations.Add($"delete:{id}"); return id == 8; }, id => { operations.Add($"restore:{id}"); return true; }));
        CollectionAssert.AreEqual(new[] { "delete:7", "delete:8", "restore:7" }, operations);
        operations.Clear();
        Assert.AreEqual(WindowsNotifications.NativeReplacementResult.Indeterminate, WindowsNotifications.CompleteReplacement(7, 8, true, id => { operations.Add($"delete:{id}"); return false; }, id => { operations.Add($"restore:{id}"); return false; }));
        CollectionAssert.AreEqual(new[] { "delete:7", "delete:8", "restore:7" }, operations);
        operations.Clear();
        Assert.AreEqual(WindowsNotifications.NativeReplacementResult.Indeterminate, WindowsNotifications.CompleteReplacement(7, 8, true, id => { operations.Add($"delete:{id}"); return false; }, id => { operations.Add($"restore:{id}"); return true; }));
        CollectionAssert.AreEqual(new[] { "delete:7", "delete:8", "restore:7" }, operations);
        operations.Clear();
        Assert.AreEqual(WindowsNotifications.NativeReplacementResult.Indeterminate, WindowsNotifications.CompleteReplacement(7, 8, true, id => { operations.Add($"delete:{id}"); return id == 8; }, id => { operations.Add($"restore:{id}"); return false; }));
        CollectionAssert.AreEqual(new[] { "delete:7", "delete:8", "restore:7" }, operations);
    }

    [TestMethod]
    public async Task LimitedWindowsNotificationsRejectActionsInsteadOfDiscardingThem()
    {
        await using var notifications = new WindowsNotifications(null);
        var request = new NeoNotificationRequest
        {
            Id = "action-test",
            Title = "Action test",
            Body = "The action must not be silently discarded.",
            Actions = [new NeoNotificationAction("open", "Open")],
        };

        Assert.AreEqual(NeoDesktopStatus.Unsupported, await notifications.ShowAsync(request));
        Assert.AreEqual(NeoSupportLevel.Limited, notifications.Support.SupportLevel);
        StringAssert.Contains(notifications.Support.Details, "report unsupported");
    }

    [TestMethod]
    public async Task OpenerRejectsTargetsOutsideExactScopesBeforeLaunch()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var file = Path.Combine(root, "name with spaces.txt"); await File.WriteAllTextAsync(file, "data");
            var opener = new NeoExternalOpener(new global::NeoAstra.Desktop.Opener.NeoUrlScope(["https://example.com"]), new NeoFileScope([root]), new NeoFileScope([root]), new NeoOpenFilePolicy([NeoOpenFileIntent.TextDocument]));

            Assert.AreEqual(NeoDesktopStatus.Denied, await opener.OpenUrlAsync(new Uri("file:///secret")));
            Assert.AreEqual(NeoDesktopStatus.Denied, await opener.OpenUrlAsync(new Uri("https://other.example/path")));
            Assert.AreEqual(NeoDesktopStatus.NotFound, await opener.OpenFileAsync(Path.Combine(root, "missing.txt"), NeoOpenFileIntent.TextDocument));
            Assert.AreEqual(NeoDesktopStatus.Denied, await opener.OpenFileAsync(Path.GetFullPath(Path.Combine(root, "..", "outside.txt")), NeoOpenFileIntent.TextDocument));
            foreach (var extension in new[] { ".lnk", ".url", ".scr", ".ps1", ".bat", ".cmd", ".js", ".vbs", ".exe", ".com", ".msi", ".desktop" })
            {
                var unsafeFile = Path.Combine(root, "unsafe" + extension); await File.WriteAllTextAsync(unsafeFile, "not executable");
                Assert.AreEqual(NeoDesktopStatus.Denied, await opener.OpenFileAsync(unsafeFile, NeoOpenFileIntent.TextDocument), extension);
            }
            Assert.AreEqual(NeoDesktopStatus.Denied, await opener.OpenFileAsync(file, NeoOpenFileIntent.PdfDocument));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task DragDropFileTokensAreScopedAndGestureTokensAreOneShot()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var file = Path.Combine(root, "drop.txt"); await File.WriteAllTextAsync(file, "drop");
            await using var broker = new NeoDragDropBroker(new NeoFileScope([root]));
            var owner = NeoPluginOwner.DocumentSession("session-a");
            var thief = NeoPluginOwner.DocumentSession("session-b");
            Assert.Throws<ArgumentException>(() => broker.BrokerInbound("view", null, default, Array.Empty<(NeoDragDataKind, string)>(), default));
            var drop = broker.BrokerInbound("view", "window", new NeoPoint(1, 2), [(NeoDragDataKind.File, file), (NeoDragDataKind.Text, "hello")], owner);
            Assert.AreEqual(NeoDesktopStatus.Success, drop.Status);
            var token = drop.Value!.Items[0].FileToken!;
            Assert.IsFalse(broker.TryResolveFile(token, thief, out _));
            Assert.IsTrue(broker.TryResolveFile(token, owner, out var canonical));
            Assert.AreEqual(NeoFileScope.Canonicalize(file, requireExisting: true), canonical);
            broker.ReleaseOwner(owner);
            Assert.IsFalse(broker.TryResolveFile(token, owner, out _));
            var gesture = broker.IssueUserGesture(owner, TimeSpan.FromSeconds(1));
            Assert.IsFalse(broker.TryConsumeUserGesture(gesture, thief));
            Assert.IsTrue(broker.TryConsumeUserGesture(gesture, owner));
            Assert.IsFalse(broker.TryConsumeUserGesture(gesture, owner));
            var expired = broker.IssueUserGesture(owner, TimeSpan.FromMilliseconds(1));
            await Task.Delay(20);
            Assert.IsFalse(broker.TryConsumeUserGesture(expired, owner));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task OutboundDragConsumesOnlyTheExactOwnersGestureAndContainsPresenterFailures()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var file = Path.Combine(root, "drag.txt"); await File.WriteAllTextAsync(file, "drag");
            var presenter = new RecordingDragPresenter();
            await using var broker = new NeoDragDropBroker(new NeoFileScope([root]), presenter);
            var owner = NeoPluginOwner.DocumentSession("session-a");
            var thief = NeoPluginOwner.DocumentSession("session-b");
            var request = new NeoOutboundDragRequest { ViewLabel = "view", Items = [new(NeoDragDataKind.File, file)] };
            var token = broker.IssueUserGesture(owner, TimeSpan.FromSeconds(1));

            Assert.AreEqual(NeoDesktopStatus.Denied, await broker.StartOutboundAsync(token, thief, request));
            Assert.AreEqual(NeoDesktopStatus.Success, await broker.StartOutboundAsync(token, owner, request));
            Assert.AreEqual(NeoDesktopStatus.Denied, await broker.StartOutboundAsync(token, owner, request));
            Assert.AreEqual(NeoFileScope.Canonicalize(file, requireExisting: true), presenter.LastRequest!.Items[0].Value);

            presenter.Throw = true;
            Assert.AreEqual(NeoDesktopStatus.Failed, await broker.StartOutboundAsync(broker.IssueUserGesture(owner, TimeSpan.FromSeconds(1)), owner, request));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [Timeout(15000)]
    public async Task NativeDropDispatchReachesOnlyTheActiveRendererDocumentSession()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            await RunStaAsync(() => NeoApplication.Run(new NeoApplicationOptions { ApplicationName = "NeoAstra drop routing test", QueueInitialLaunchEvent = false, ShutdownMode = NeoApplicationShutdownMode.Explicit }, async application =>
            {
                var root = CreateTemporaryDirectory();
                try
                {
                    var file = Path.Combine(root, "drop.txt"); await File.WriteAllTextAsync(file, "drop");
                    var broker = new NeoDragDropBroker(new NeoFileScope([root]));
                    await using var services = CreateRendererServices(root, broker);
                    ((INeoApplicationBoundDesktopService)broker).BindApplication(application);
                    var builder = new NeoRpcBuilder(new NeoRpcOptions { AuthorizationService = AllowDesktopAuthorization.Instance });
                    await using var registration = new DesktopRendererRegistration(services, new NeoDesktopRendererOptions { FileRoots = new Dictionary<string, string> { ["root"] = root } });
                    registration.Register(builder);
                    await using var rpc = builder.Build();
                    var window = application.CreateWindow(new NeoWindowOptions { Label = "drop-window", Title = "Drop", Width = 320, Height = 200 });
                    await using var environment = await application.CreateEnvironmentAsync(new NeoEnvironmentOptions { CustomSchemes = [NeoCustomScheme.Application("app", new DragResourceProvider())] });
                    await using var view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), new NeoAstraOptions { ViewLabel = "drop-view", BridgePolicy = NeoBridgePolicy.TrustedOrigins, BridgeOrigins = ["app://neoastra"] });
                    var parent = window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value;
                    var webViewWindow = FindWindowEx(parent, 0, "Chrome_WidgetWin_0", null);
                    Assert.AreNotEqual(0, webViewWindow, "The windowed WebView2 child HWND was not created.");
                    Assert.AreEqual(0, GetProp(parent, "NeoAstra.DropTarget"), "The host window must not replace an unrelated OLE target.");
                    Assert.AreNotEqual(0, GetProp(webViewWindow, "NeoAstra.DropTarget"), "The WebView controller child must own a brokered OLE drop target.");
                    var oldSessionId = await NavigateAndGetSessionAsync(view, new Uri("app://neoastra/first.html"));
                    var rendererWindows = GetDescendantWindows(webViewWindow);
                    Assert.IsNotEmpty(rendererWindows, "The WebView2 renderer child-window tree was not created.");
                    Assert.IsTrue(rendererWindows.All(windowHandle => GetProp(windowHandle, "NeoAstra.DropTarget") != 0), "Every descendant renderer window must own a brokered OLE drop target.");
                    var matchingFrames = new ConcurrentQueue<string>(); var otherFrames = new ConcurrentQueue<string>(); var replacementFrames = new ConcurrentQueue<string>();
                    await using var matching = rpc.OpenSession(new NeoRpcSessionIdentity("drop-view", oldSessionId), Capture(matchingFrames));
                    await using var other = rpc.OpenSession(new NeoRpcSessionIdentity("other-view", "session-other"), Capture(otherFrames));
                    await matching.ReceiveAsync(Subscribe("matching-drop")); await other.ReceiveAsync(Subscribe("other-drop"));
                    view.DispatchNativeDrop((int)NeoDragDataKind.File, [file], new NeoPoint(12, 18));
                    await WaitUntilAsync(() => EventFrames(matchingFrames).Count == 1);
                    Assert.AreEqual(0, EventFrames(otherFrames).Count);
                    var firstDrop = EventFrames(matchingFrames).Single().GetProperty("value").GetProperty("drop");
                    Assert.AreEqual("drop-view", firstDrop.GetProperty("viewLabel").GetString());
                    Assert.AreEqual("drop-window", firstDrop.GetProperty("windowLabel").GetString());
                    var firstToken = firstDrop.GetProperty("items")[0].GetProperty("fileToken").GetString()!;
                    Assert.AreEqual("Success", await ResolveStatusAsync(matching, matchingFrames, "match-resolve", firstToken));
                    Assert.AreEqual("NotFound", await ResolveStatusAsync(other, otherFrames, "other-resolve", firstToken));

                    var replacementSessionId = await NavigateAndGetSessionAsync(view, new Uri("app://neoastra/second.html"), oldSessionId);
                    Assert.AreEqual("NotFound", await ResolveStatusAsync(matching, matchingFrames, "stale-resolve", firstToken));
                    await using var replacement = rpc.OpenSession(new NeoRpcSessionIdentity("drop-view", replacementSessionId), Capture(replacementFrames));
                    await replacement.ReceiveAsync(Subscribe("replacement-drop"));
                    view.DispatchNativeDrop((int)NeoDragDataKind.File, [file], new NeoPoint(20, 24));
                    await WaitUntilAsync(() => EventFrames(replacementFrames).Count == 1);
                    Assert.AreEqual(1, EventFrames(matchingFrames).Count);
                    Assert.AreEqual(0, EventFrames(otherFrames).Count);
                    var secondToken = EventFrames(replacementFrames).Single().GetProperty("value").GetProperty("drop").GetProperty("items")[0].GetProperty("fileToken").GetString()!;
                    Assert.AreEqual("Success", await ResolveStatusAsync(replacement, replacementFrames, "replacement-resolve", secondToken));
                    Assert.AreEqual("NotFound", await ResolveStatusAsync(matching, matchingFrames, "old-cannot-resolve", secondToken));
                    var thirdSessionId = await NavigateAndGetSessionAsync(view, new Uri("app://neoastra/third.html"), replacementSessionId);
                    Assert.AreEqual("NotFound", await ResolveStatusAsync(replacement, replacementFrames, "navigation-resolve", secondToken));
                    var thirdFrames = new ConcurrentQueue<string>(); await using var third = rpc.OpenSession(new NeoRpcSessionIdentity("drop-view", thirdSessionId), Capture(thirdFrames)); await third.ReceiveAsync(Subscribe("third-drop"));
                    view.DispatchNativeDrop((int)NeoDragDataKind.File, [file], new NeoPoint(28, 30));
                    await WaitUntilAsync(() => EventFrames(thirdFrames).Count == 1);
                    var closeToken = EventFrames(thirdFrames)[0].GetProperty("value").GetProperty("drop").GetProperty("items")[0].GetProperty("fileToken").GetString()!;
                    var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); window.Closed += (_, _) => closed.TrySetResult();
                    window.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    Assert.AreEqual("NotFound", await ResolveStatusAsync(third, thirdFrames, "closed-resolve", closeToken));
                    var teardownOwner = NeoPluginOwner.DocumentSession(thirdSessionId); var teardownDrop = broker.BrokerInbound("drop-view", "drop-window", default, [(NeoDragDataKind.File, file)], teardownOwner);
                    var teardownToken = teardownDrop.Value!.Items[0].FileToken!;
                    Assert.IsTrue(broker.TryResolveFile(teardownToken, teardownOwner, out _));
                    await registration.DisposeAsync();
                    Assert.IsFalse(broker.TryResolveFile(teardownToken, teardownOwner, out _));
                }
                finally { application.ForceShutdown(); try { Directory.Delete(root, recursive: true); } catch { } }
            }));
        }
        catch (NeoAstraNativeLibraryException)
        {
            // Managed-only CI does not stage native assets; native-enabled verification executes this path.
        }
    }

    [TestMethod]
    [Timeout(15000)]
    public async Task RendererOutboundDragRequiresMatchingCurrentNativeGesture()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            await RunStaAsync(() => NeoApplication.Run(new NeoApplicationOptions { ApplicationName = "NeoAstra renderer drag test", QueueInitialLaunchEvent = false, ShutdownMode = NeoApplicationShutdownMode.Explicit }, async application =>
            {
                var root = CreateTemporaryDirectory();
                try
                {
                    var presenter = new SourceBoundGesturePresenter();
                    var broker = new NeoDragDropBroker(new NeoFileScope([root]), presenter);
                    await using var services = CreateRendererServices(root, broker);
                    ((INeoApplicationBoundDesktopService)broker).BindApplication(application);
                    var builder = new NeoRpcBuilder(new NeoRpcOptions { AuthorizationService = AllowDesktopAuthorization.Instance });
                    builder.AddNeoAstraDesktopHandlers(services, new NeoDesktopRendererOptions());
                    await using var rpc = builder.Build();
                    var window = application.CreateWindow(new NeoWindowOptions { Label = "drag-window", Title = "Drag", Width = 320, Height = 200 });
                    await using var environment = await application.CreateEnvironmentAsync(new NeoEnvironmentOptions { CustomSchemes = [NeoCustomScheme.Application("app", new DragResourceProvider())] });
                    await using var view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), new NeoAstraOptions { ViewLabel = "drag-view", BridgePolicy = NeoBridgePolicy.TrustedOrigins, BridgeOrigins = ["app://neoastra"] });
                    presenter.ObserveNavigation(view);
                    var documentSessionId = await NavigateAndGetSessionAsync(view, new Uri("app://neoastra/drag-first.html"));
                    var frames = new ConcurrentQueue<string>();
                    await using var session = rpc.OpenSession(new NeoRpcSessionIdentity("drag-view", documentSessionId), Capture(frames));

                    Assert.AreEqual("Denied", await OutboundStatusAsync(session, frames, "absent"));
                    presenter.Observe("other-view", documentSessionId, TimeSpan.FromSeconds(1));
                    Assert.AreEqual("Denied", await OutboundStatusAsync(session, frames, "wrong-view"));
                    presenter.Observe("drag-view", "other-session", TimeSpan.FromSeconds(1));
                    Assert.AreEqual("Denied", await OutboundStatusAsync(session, frames, "wrong-session"));
                    presenter.Observe("drag-view", documentSessionId, TimeSpan.FromMilliseconds(1)); await Task.Delay(20);
                    Assert.AreEqual("Denied", await OutboundStatusAsync(session, frames, "expired"));
                    presenter.Observe("drag-view", documentSessionId, TimeSpan.FromSeconds(1));
                    Assert.AreEqual("Success", await OutboundStatusAsync(session, frames, "matching"));
                    Assert.AreEqual("Denied", await OutboundStatusAsync(session, frames, "reused"));
                    presenter.Observe("drag-view", documentSessionId, TimeSpan.FromSeconds(1));
                    var rotatedSessionId = await NavigateAndGetSessionAsync(view, new Uri("app://neoastra/drag-second.html"), documentSessionId);
                    Assert.AreEqual("Denied", await OutboundStatusAsync(session, frames, "rotated-old-session"));
                    var rotatedFrames = new ConcurrentQueue<string>();
                    await using var rotated = rpc.OpenSession(new NeoRpcSessionIdentity("drag-view", rotatedSessionId), Capture(rotatedFrames));
                    Assert.AreEqual("Denied", await OutboundStatusAsync(rotated, rotatedFrames, "navigation-invalidated"));
                }
                finally { application.ForceShutdown(); try { Directory.Delete(root, recursive: true); } catch { } }
            }));
        }
        catch (NeoAstraNativeLibraryException)
        {
            // Managed-only CI does not stage native assets; native-enabled verification executes this path.
        }
    }

    [TestMethod]
    public async Task WindowStateIsAtomicRejectsMalformedInputAndClampsOffscreenState()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new NeoJsonWindowStateStore(root);
            var saved = new NeoWindowPlacement(new NeoRect(9000, 9000, 900, 700), NeoWindowState.Minimized, "gone", 2, true);
            await store.SaveAsync("main", saved);
            Assert.AreEqual(saved, await store.LoadAsync("main"));
            await File.WriteAllTextAsync(Path.Combine(root, "corrupt.json"), "{not-json");
            Assert.IsNull(await store.LoadAsync("corrupt"));
            Assert.Throws<ArgumentException>(() => NeoJsonWindowStateStore.ValidatePlacement(saved with { DisplayScaleFactor = double.NaN }));
            Assert.Throws<ArgumentException>(() => NeoWindowStateRestore.Clamp(saved, [new NeoDisplaySnapshot("bad", new NeoRect(0, 0, 100, 100), new NeoRect(0, 0, 100, 100), double.NaN, true, null, null)]));
            var restored = NeoWindowStateRestore.Clamp(saved, [new NeoDisplaySnapshot("primary", new NeoRect(0, 0, 1920, 1080), new NeoRect(0, 0, 1920, 1040), 1, true, null, null)]);
            Assert.AreEqual(NeoWindowState.Normal, restored.State);
            Assert.IsTrue(restored.NormalBounds.X + restored.NormalBounds.Width <= 1920);
            Assert.IsTrue(restored.NormalBounds.Y + restored.NormalBounds.Height <= 1040);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task WindowsSafeStorageRoundTripsWithoutPlaintextOnDisk()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows DPAPI verification requires Windows.");
        var root = CreateTemporaryDirectory();
        try
        {
            var storage = NeoSafeStorage.CreateSystem("test-service", root);
            var secret = Encoding.UTF8.GetBytes("neoastra-secret-value");
            Assert.AreEqual(NeoDesktopStatus.Success, await storage.StoreAsync("account", secret));
            var loaded = await storage.RetrieveAsync("account");
            CollectionAssert.AreEqual(secret, loaded.Value!);
            var bytes = await File.ReadAllBytesAsync(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Single());
            Assert.IsFalse(bytes.AsSpan().IndexOf(secret) >= 0);
            Assert.AreEqual(NeoDesktopStatus.Success, await storage.DeleteAsync("account"));
            Assert.AreEqual(NeoDesktopStatus.NotFound, (await storage.RetrieveAsync("account")).Status);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [Timeout(15000)]
    public async Task DesktopProcessCapturesBoundedDeterministicOutput()
    {
        var executableName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var executable = Environment.GetEnvironmentVariable("PATH")!.Split(Path.PathSeparator).Select(path => Path.Combine(path, executableName)).First(File.Exists);
        var result = await DesktopProcess.RunAsync(executable, ["--version"], default, TimeSpan.FromSeconds(10), captureOutput: true, CancellationToken.None);

        Assert.AreEqual(0, result.ExitCode);
        Assert.IsTrue(result.Output.Length is > 0 and <= NeoDesktopLimits.MaximumProcessOutputBytes);
        Assert.AreEqual(0, result.Error.Length);
    }

    private static NeoDesktopServices CreateRendererServices(string root, NeoDragDropBroker broker)
        => new(new NeoFakeDialogs(), new NeoMenuService(new NeoCommandService()), new NeoTrayService(), new NeoFakeClipboard(),
            new NeoFakeNotifications(), new NeoGlobalShortcutService(emulatedForTests: true), new NeoSystemInfoService("test.drag", "Drag Test", "1.0"),
            new NeoExternalOpener(new global::NeoAstra.Desktop.Opener.NeoUrlScope(["https://example.com"]), new NeoFileScope([root]), new NeoFileScope([root]), new NeoOpenFilePolicy([NeoOpenFileIntent.TextDocument])),
            broker, new UnsupportedSafeStorage("test"));

    private static NeoRpcSendFrame Capture(ConcurrentQueue<string> frames) => (json, _) => { frames.Enqueue(json); return ValueTask.CompletedTask; };
    private static string Subscribe(string id) => $"{{\"neoastra\":1,\"kind\":\"subscribe\",\"id\":\"{id}\",\"event\":\"desktop.drag-drop.inbound\"}}";

#pragma warning disable SYSLIB1054 // Test-only Win32 inspection does not need source-generated interop.
    [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string? className, string? windowName);

    [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)]
    private static extern nint GetProp(nint window, string name);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(nint parent, EnumWindow callback, nint parameter);

    private delegate bool EnumWindow(nint window, nint parameter);
#pragma warning restore SYSLIB1054
    private static IReadOnlyList<JsonElement> EventFrames(ConcurrentQueue<string> frames)
        => frames.Select(ParseFrame).Where(static frame => frame.GetProperty("kind").GetString() == "event").ToArray();
    private static JsonElement ParseFrame(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static async Task<string?> ResolveStatusAsync(NeoRpcSession session, ConcurrentQueue<string> frames, string id, string token)
    {
        await session.ReceiveAsync($"{{\"neoastra\":1,\"kind\":\"invoke\",\"id\":\"{id}\",\"command\":\"desktop.drag-drop.resolve-file\",\"args\":{{\"token\":\"{token}\"}}}}");
        await WaitUntilAsync(() => frames.Select(ParseFrame).Any(frame => frame.TryGetProperty("id", out var value) && value.GetString() == id));
        return frames.Select(ParseFrame).Single(frame => frame.TryGetProperty("id", out var value) && value.GetString() == id).GetProperty("value").GetProperty("status").GetString();
    }

    private static async Task<string?> OutboundStatusAsync(NeoRpcSession session, ConcurrentQueue<string> frames, string id)
    {
        await session.ReceiveAsync($"{{\"neoastra\":1,\"kind\":\"invoke\",\"id\":\"{id}\",\"command\":\"desktop.drag-drop.outbound\",\"args\":{{\"viewLabel\":\"drag-view\",\"items\":[{{\"kind\":\"Text\",\"value\":\"drag\"}}]}}}}");
        await WaitUntilAsync(() => frames.Select(ParseFrame).Any(frame => frame.TryGetProperty("id", out var value) && value.GetString() == id));
        return frames.Select(ParseFrame).Single(frame => frame.TryGetProperty("id", out var value) && value.GetString() == id).GetProperty("value").GetProperty("status").GetString();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (var index = 0; index < 200 && !predicate(); index++) await Task.Delay(5);
        Assert.IsTrue(predicate());
    }

    private static async Task<string> NavigateAndGetSessionAsync(global::NeoAstra.NeoAstra view, Uri target, string? previousSessionId = null)
    {
        var connected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void SessionChanged(NeoTransportSessionSnapshot? snapshot)
        {
            if (snapshot is { } value && !string.Equals(value.DocumentSessionId, previousSessionId, StringComparison.Ordinal)) connected.TrySetResult(value.DocumentSessionId);
        }
        view.TransportSessionChanged += SessionChanged;
        try
        {
            await view.NavigateAsync(target);
            return await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { view.TransportSessionChanged -= SessionChanged; }
    }

    private static IReadOnlyList<nint> GetDescendantWindows(nint parent)
    {
        var windows = new List<nint>();
        EnumChildWindows(parent, (window, _) => { windows.Add(window); return true; }, 0);
        return windows;
    }

    private static Task<T> RunStaAsync<T>(Func<T> action)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { completion.TrySetResult(action()); } catch (Exception exception) { completion.TrySetException(exception); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "neoastra-desktop-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }

    private sealed class StubPlugin(string id, IReadOnlyList<NeoPluginDependency>? dependencies = null) : INeoAstraPlugin
    {
        public NeoPluginMetadata Metadata { get; } = new(id, new Version(1, 0), 1, new Version(0, 1), dependencies: dependencies, hasStaticJsonMetadata: true);
        public INeoPluginAdapter CreateAdapter() => new StubAdapter();
        public ValueTask ConfigureAsync(NeoPluginContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask ReadyAsync(NeoPluginContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask StoppingAsync(NeoPluginContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubAdapter : INeoPluginAdapter
    {
        public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Emulated, 1, 0, "test");
        public ValueTask AttachAsync(NeoApplication application, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingDragPresenter : INeoOutboundDragPresenter
    {
        public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Emulated, 1, 1, "test");
        public NeoOutboundDragRequest? LastRequest { get; private set; }
        public bool Throw { get; set; }
        public ValueTask<NeoDesktopStatus> StartAsync(NeoOutboundDragRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            if (Throw) throw new InvalidOperationException("contained");
            return ValueTask.FromResult(NeoDesktopStatus.Success);
        }
    }

    private sealed class SourceBoundGesturePresenter : INeoOutboundDragPresenter, INeoRendererOutboundDragPresenter
    {
        private readonly object _sync = new();
        private (string ViewLabel, string DocumentSessionId, DateTimeOffset Expires)? _gesture;

        public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Emulated, 1, 0, "source-bound native gesture contract test");

        internal void ObserveNavigation(global::NeoAstra.NeoAstra view)
        {
            view.NativeNavigationStarted += () =>
            {
                lock (_sync) if (_gesture is { } gesture && string.Equals(gesture.ViewLabel, view.ViewLabel, StringComparison.Ordinal)) _gesture = null;
            };
        }

        internal void Observe(string viewLabel, string documentSessionId, TimeSpan lifetime)
        {
            lock (_sync) _gesture = (viewLabel, documentSessionId, DateTimeOffset.UtcNow + lifetime);
        }

        public ValueTask<NeoDesktopStatus> StartAsync(NeoOutboundDragRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(NeoDesktopStatus.Success);
        }

        ValueTask<NeoDesktopStatus> INeoRendererOutboundDragPresenter.StartRendererAsync(string documentSessionId, NeoOutboundDragRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (_gesture is not { } gesture || gesture.Expires <= DateTimeOffset.UtcNow || !string.Equals(gesture.ViewLabel, request.ViewLabel, StringComparison.Ordinal) || !string.Equals(gesture.DocumentSessionId, documentSessionId, StringComparison.Ordinal))
                {
                    if (_gesture is { } expired && expired.Expires <= DateTimeOffset.UtcNow) _gesture = null;
                    return ValueTask.FromResult(NeoDesktopStatus.Denied);
                }
                _gesture = null;
                return ValueTask.FromResult(NeoDesktopStatus.Success);
            }
        }
    }

    private sealed class AllowDesktopAuthorization : INeoRpcAuthorizationService
    {
        internal static AllowDesktopAuthorization Instance { get; } = new();
        public ValueTask<NeoRpcAuthorizationResult> AuthorizeAsync(NeoRpcAuthorizationRequest request, CancellationToken cancellationToken)
            => ValueTask.FromResult(NeoRpcAuthorizationResult.Allow());
    }

    private sealed class DragResourceProvider : INeoResourceProvider
    {
        public NeoResourceResponse GetResponse(NeoResourceRequest request)
            => NeoResourceResponse.FromBytes("<!doctype html><title>drag integration</title><script>globalThis[Symbol.for('@neoastra/client/transport/v1')].send({neoastra:1,kind:'hello',protocol:{major:1,minor:0},features:['invoke','events'],client:{name:'drag-test',version:'1.0'}})</script>"u8.ToArray(), "text/html; charset=utf-8");
    }

    private sealed class RecordingMenuPresenter(NeoCommandService commands) : INeoMenuPresenter
    {
        private readonly Dictionary<string, IReadOnlyList<NeoMenuItem>> _menus = new(StringComparer.Ordinal);
        public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Emulated, 1, 0, "presenter contract test");
        public List<string> Operations { get; } = [];
        public List<IReadOnlyList<NeoMenuItem>> Snapshots { get; } = [];
        public IEnumerable<string> VisibleCommands => _menus.Values.SelectMany(static menu => menu).Where(static item => item.CommandId is not null).Select(static item => item.CommandId!);
        public string? ActivateDuringSet { get; set; }
        public bool FailNextSet { get; set; }
        private bool _disposed;
        public void SetMenu(string targetId, IReadOnlyList<NeoMenuItem> items)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); Operations.Add("set:" + targetId);
            if (FailNextSet) { FailNextSet = false; throw new InvalidOperationException("native replacement failed"); }
            _menus[targetId] = items; Snapshots.Add(Array.AsReadOnly(items.ToArray()));
            if (ActivateDuringSet is { } command) { ActivateDuringSet = null; commands.ActivateAsync(command).AsTask().GetAwaiter().GetResult(); }
        }
        public void RemoveMenu(string targetId) { ObjectDisposedException.ThrowIf(_disposed, this); Operations.Add("remove:" + targetId); _menus.Remove(targetId); }
        public NeoDesktopStatus ShowContextMenu(string targetId, NeoPoint position) => _menus.ContainsKey(targetId) ? NeoDesktopStatus.Success : NeoDesktopStatus.NotFound;
        public ValueTask DisposeAsync() { _disposed = true; Operations.Add("dispose"); _menus.Clear(); return ValueTask.CompletedTask; }
    }

    private sealed class RecordingTrayPresenter : INeoTrayPresenter
    {
        public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Emulated, 1, 0, "presenter contract test");
        public event Action<string, bool>? Activated;
        public Dictionary<string, NeoTrayItemOptions> Items { get; } = new(StringComparer.Ordinal);
        public int RemoveCount { get; private set; }
        public bool Disposed { get; private set; }
        public void Set(NeoTrayItemOptions options) { ObjectDisposedException.ThrowIf(Disposed, this); Items[options.Id] = options; }
        public bool Remove(string id) { ObjectDisposedException.ThrowIf(Disposed, this); if (!Items.Remove(id)) return false; RemoveCount++; return true; }
        public void Emit(string id, bool secondary) { if (!Disposed) Activated?.Invoke(id, secondary); }
        public ValueTask DisposeAsync() { Disposed = true; Items.Clear(); Activated = null; return ValueTask.CompletedTask; }
    }

    private sealed class DisposalProbe(Task completion) : IAsyncDisposable
    {
        private int _started;
        public int Started => Volatile.Read(ref _started);
        public async ValueTask DisposeAsync() { Interlocked.Increment(ref _started); await completion.ConfigureAwait(false); }
    }

    private sealed record RoleTargetCandidate(object Owner, string? Label, nint Widget);

}
