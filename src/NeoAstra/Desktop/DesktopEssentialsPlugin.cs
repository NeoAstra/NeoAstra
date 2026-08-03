// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using NeoAstra.Desktop.Clipboard;
using NeoAstra.Desktop.Dialogs;
using NeoAstra.Desktop.DragDrop;
using NeoAstra.Desktop.GlobalShortcuts;
using NeoAstra.Desktop.Menus;
using NeoAstra.Desktop.Notifications;
using NeoAstra.Desktop.Opener;
using NeoAstra.Desktop.SafeStorage;
using NeoAstra.Desktop.SystemInfo;
using NeoAstra.Desktop.Tray;
using NeoAstra.Desktop.WindowState;
using NeoAstra.Rpc;

namespace NeoAstra.Desktop;

/// <summary>Contains the first official backend desktop-service contracts and selected adapters.</summary>
public sealed class NeoDesktopServices : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly List<IAsyncDisposable> _rendererRegistrations = [];
    private bool _disposed;
    /// <summary>Initializes an explicit service composition. Referencing it grants no renderer authority.</summary>
    public NeoDesktopServices(INeoDialogs dialogs, NeoMenuService menus, NeoTrayService tray, INeoClipboard clipboard,
        INeoNotifications notifications, NeoGlobalShortcutService globalShortcuts, NeoSystemInfoService systemInfo,
        NeoExternalOpener opener, NeoDragDropBroker dragDrop, INeoSafeStorage safeStorage)
    {
        Dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        Menus = menus ?? throw new ArgumentNullException(nameof(menus));
        Tray = tray ?? throw new ArgumentNullException(nameof(tray));
        Clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        Notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        GlobalShortcuts = globalShortcuts ?? throw new ArgumentNullException(nameof(globalShortcuts));
        SystemInfo = systemInfo ?? throw new ArgumentNullException(nameof(systemInfo));
        Opener = opener ?? throw new ArgumentNullException(nameof(opener));
        DragDrop = dragDrop ?? throw new ArgumentNullException(nameof(dragDrop));
        SafeStorage = safeStorage ?? throw new ArgumentNullException(nameof(safeStorage));
        WindowPolish = new NeoWindowPolishService();
    }

    /// <summary>Creates the statically selected system adapters with explicit application identity and opener/file policy.</summary>
    /// <param name="applicationId">Stable application/plugin namespace.</param>
    /// <param name="applicationName">Localized application display name.</param>
    /// <param name="applicationVersion">Application version displayed by system metadata.</param>
    /// <param name="privateDataDirectory">Absolute private directory for encrypted records.</param>
    /// <param name="allowedUrlOrigins">Exact URL origins accepted by the opener.</param>
    /// <param name="openFileRoots">Canonical roots from which existing non-executable files may be opened or used in outbound drags.</param>
    /// <param name="revealFileRoots">Canonical roots whose files/folders may be revealed.</param>
    /// <param name="openFileIntents">Explicit non-executable content intents accepted by the opener.</param>
    /// <param name="dispatcher">Optional UI dispatcher used for coalesced metadata/menu delivery.</param>
    /// <returns>An owned desktop service graph.</returns>
    public static NeoDesktopServices CreateSystem(string applicationId, string applicationName, string applicationVersion,
        string privateDataDirectory, IEnumerable<string> allowedUrlOrigins, IEnumerable<string> openFileRoots,
        IEnumerable<string> revealFileRoots, IEnumerable<NeoOpenFileIntent> openFileIntents, NeoDispatcher? dispatcher = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationVersion);
        var openFiles = new NeoFileScope(openFileRoots);
        var revealFiles = new NeoFileScope(revealFileRoots);
        var commands = new NeoCommandService();
        INeoMenuPresenter? menuPresenter = OperatingSystem.IsWindows() ? new WindowsMenuPresenter(commands, dispatcher) : OperatingSystem.IsMacOS() ? new MacMenuPresenter(commands, dispatcher) : OperatingSystem.IsLinux() ? new LinuxMenuPresenter(commands, dispatcher) : null;
        INeoTrayPresenter? trayPresenter = OperatingSystem.IsWindows() ? new WindowsTrayPresenter(commands, dispatcher) : OperatingSystem.IsMacOS() ? new MacTrayPresenter(commands, dispatcher) : OperatingSystem.IsLinux() ? new LinuxTrayPresenter(commands, dispatcher) : null;
        return new(
            NeoDialogs.CreateSystem(dispatcher),
            new NeoMenuService(commands, dispatcher, menuPresenter),
            new NeoTrayService(dispatcher, trayPresenter),
            NeoClipboard.CreateSystem(dispatcher),
            NeoNotifications.CreateSystem(applicationId, applicationName, dispatcher),
            NeoGlobalShortcutService.CreateSystem(dispatcher),
            new NeoSystemInfoService(applicationId, applicationName, applicationVersion, dispatcher),
            new NeoExternalOpener(new Opener.NeoUrlScope(allowedUrlOrigins), openFiles, revealFiles, new NeoOpenFilePolicy(openFileIntents)),
            new NeoDragDropBroker(openFiles, new NativeOutboundDragPresenter()),
            NeoSafeStorage.CreateSystem(applicationId, privateDataDirectory));
    }

    /// <summary>Gets dialogs.</summary>
    public INeoDialogs Dialogs { get; }
    /// <summary>Gets menus and shared command routing.</summary>
    public NeoMenuService Menus { get; }
    /// <summary>Gets tray/status item ownership.</summary>
    public NeoTrayService Tray { get; }
    /// <summary>Gets the format-specific clipboard.</summary>
    public INeoClipboard Clipboard { get; }
    /// <summary>Gets notifications.</summary>
    public INeoNotifications Notifications { get; }
    /// <summary>Gets global shortcuts.</summary>
    public NeoGlobalShortcutService GlobalShortcuts { get; }
    /// <summary>Gets theme/display/app metadata.</summary>
    public NeoSystemInfoService SystemInfo { get; }
    /// <summary>Gets the scoped external opener.</summary>
    public NeoExternalOpener Opener { get; }
    /// <summary>Gets drag/drop authority brokering.</summary>
    public NeoDragDropBroker DragDrop { get; }
    /// <summary>Gets OS-backed safe storage.</summary>
    public INeoSafeStorage SafeStorage { get; }
    /// <summary>Gets native window polish operations.</summary>
    public NeoWindowPolishService WindowPolish { get; }

    internal void BindApplication(NeoApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (Dialogs is INeoApplicationBoundDesktopService dialogs) dialogs.BindApplication(application);
        if (Menus is INeoApplicationBoundDesktopService menus) menus.BindApplication(application);
        if (Tray is INeoApplicationBoundDesktopService tray) tray.BindApplication(application);
        if (Clipboard is WindowsClipboard windowsClipboard) windowsClipboard.BindDispatcher(application.Dispatcher);
        if (Clipboard is INeoApplicationBoundDesktopService clipboard) clipboard.BindApplication(application);
        if (Notifications is INeoApplicationBoundDesktopService notifications) notifications.BindApplication(application);
        if (GlobalShortcuts is INeoApplicationBoundDesktopService shortcuts) shortcuts.BindApplication(application);
        if (DragDrop is INeoApplicationBoundDesktopService dragDrop) dragDrop.BindApplication(application);
    }

    internal void TrackRendererRegistration(IAsyncDisposable registration)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _rendererRegistrations.Add(registration);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        IAsyncDisposable[] registrations;
        lock (_sync) { if (_disposed) return; _disposed = true; registrations = _rendererRegistrations.AsEnumerable().Reverse().ToArray(); _rendererRegistrations.Clear(); }
        foreach (var registration in registrations) try { await registration.DisposeAsync().ConfigureAwait(false); } catch { }
        SystemInfo.Dispose();
        if (Notifications is IAsyncDisposable notificationLifetime) await notificationLifetime.DisposeAsync().ConfigureAwait(false);
        await GlobalShortcuts.DisposeAsync().ConfigureAwait(false);
        await DragDrop.DisposeAsync().ConfigureAwait(false);
        await WindowPolish.DisposeAsync().ConfigureAwait(false);
        await Tray.DisposeAsync().ConfigureAwait(false);
        await Menus.DisposeAsync().ConfigureAwait(false);
        await Menus.Commands.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Official statically composed desktop essentials plugin. Renderer command declarations are not handlers or grants.</summary>
public sealed class NeoDesktopEssentialsPlugin : INeoAstraPlugin
{
    /// <summary>Stable official plugin ID.</summary>
    public const string Id = "neoastra.desktop.essentials";
    private readonly NeoDesktopServices _services;
    private int _disposed;

    /// <summary>Initializes the plugin and takes ownership of the explicit backend service graph.</summary>
    public NeoDesktopEssentialsPlugin(NeoDesktopServices services) => _services = services ?? throw new ArgumentNullException(nameof(services));

    /// <summary>Gets the official static permission catalog. Adding it grants nothing.</summary>
    public static NeoPluginPermissionCatalog PermissionCatalog { get; } = CreatePermissionCatalog();

    /// <inheritdoc />
    public NeoPluginMetadata Metadata { get; } = CreateMetadata();

    /// <inheritdoc />
    public INeoPluginAdapter CreateAdapter() => new DesktopAdapter(_services);

    /// <inheritdoc />
    public ValueTask ConfigureAsync(NeoPluginContext context, CancellationToken cancellationToken) { ArgumentNullException.ThrowIfNull(context); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }

    /// <inheritdoc />
    public ValueTask ReadyAsync(NeoPluginContext context, CancellationToken cancellationToken) { ArgumentNullException.ThrowIfNull(context); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }

    /// <inheritdoc />
    public ValueTask StoppingAsync(NeoPluginContext context, CancellationToken cancellationToken) { ArgumentNullException.ThrowIfNull(context); return ValueTask.CompletedTask; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() { if (Interlocked.Exchange(ref _disposed, 1) == 0) await _services.DisposeAsync().ConfigureAwait(false); }

    private static NeoPluginMetadata CreateMetadata()
    {
        NeoPermissionDeclaration Find(string operation) => PermissionCatalog.Permissions.Single(permission => permission.Commands.Contains(operation, StringComparer.Ordinal));
        var commands = NeoDesktopRendererContract.Commands.Select(command =>
        {
            var permission = Find(command);
            return new NeoPluginCommandDeclaration(command,
            permission.Id,
            permission.ScopeFamily switch
            {
                NeoScopeFamily.Dialogs => "schemas/dialogs.scope.schema.json",
                NeoScopeFamily.Clipboard => "schemas/clipboard.scope.schema.json",
                NeoScopeFamily.Notifications => "schemas/notifications.scope.schema.json",
                NeoScopeFamily.Url => "schemas/url.scope.schema.json",
                NeoScopeFamily.Filesystem => "schemas/filesystem.scope.schema.json",
                NeoScopeFamily.Shortcuts => "schemas/shortcuts.scope.schema.json",
                _ => null,
            },
            permission.Risk,
            Audited: true);
        }).ToArray();
        var events = NeoDesktopRendererContract.Events.Select(pluginEvent => { var permission = Find(pluginEvent); return new NeoPluginEventDeclaration(pluginEvent, permission.Id, permission.Risk, Audited: true); }).ToArray();
        return new(Id, new Version(1, 0, 0), 1, new Version(0, 1, 0), commands: commands, events: events, permissionCatalog: PermissionCatalog, hasStaticJsonMetadata: true);
    }

    private static NeoPluginPermissionCatalog CreatePermissionCatalog()
    {
        var declarations = new[]
        {
            Permission("dialogs:open-file", ["desktop.dialogs.open-file"], NeoPermissionRisk.Sensitive, NeoScopeFamily.Dialogs),
            Permission("dialogs:save-file", ["desktop.dialogs.save-file"], NeoPermissionRisk.High, NeoScopeFamily.Dialogs),
            Permission("dialogs:open-folder", ["desktop.dialogs.open-folder"], NeoPermissionRisk.Sensitive, NeoScopeFamily.Dialogs),
            Permission("dialogs:message", ["desktop.dialogs.message"], NeoPermissionRisk.Low),
            Permission("menus:activate", ["desktop.menus.activate"], NeoPermissionRisk.Low),
            Permission("menus:control", ["desktop.menus.set", "desktop.menus.popup"], NeoPermissionRisk.High),
            Permission("tray:create", ["desktop.tray.create"], NeoPermissionRisk.High),
            Permission("tray:control", ["desktop.tray.update", "desktop.tray.remove", "desktop.tray.activated"], NeoPermissionRisk.High),
            Permission("clipboard:read-text", ["desktop.clipboard.read-text"], NeoPermissionRisk.High, NeoScopeFamily.Clipboard),
            Permission("clipboard:write-text", ["desktop.clipboard.write-text"], NeoPermissionRisk.Sensitive, NeoScopeFamily.Clipboard),
            Permission("clipboard:read-rich", ["desktop.clipboard.read-rich"], NeoPermissionRisk.High, NeoScopeFamily.Clipboard),
            Permission("clipboard:write-rich", ["desktop.clipboard.write-rich"], NeoPermissionRisk.High, NeoScopeFamily.Clipboard),
            Permission("clipboard:clear", ["desktop.clipboard.clear"], NeoPermissionRisk.High),
            Permission("notifications:status", ["desktop.notifications.status"], NeoPermissionRisk.Low),
            Permission("notifications:display", ["desktop.notifications.show"], NeoPermissionRisk.Sensitive, NeoScopeFamily.Notifications),
            Permission("notifications:remove", ["desktop.notifications.remove"], NeoPermissionRisk.Sensitive),
            Permission("notifications:activation", ["desktop.notifications.activated"], NeoPermissionRisk.Sensitive),
            Permission("shortcuts:register", ["desktop.shortcuts.register"], NeoPermissionRisk.High, NeoScopeFamily.Shortcuts),
            Permission("shortcuts:control", ["desktop.shortcuts.unregister", "desktop.shortcuts.activated"], NeoPermissionRisk.High),
            Permission("system-info:theme", ["desktop.system.theme", "desktop.system.theme-changed"], NeoPermissionRisk.Low),
            Permission("system-info:displays", ["desktop.system.displays", "desktop.system.displays-changed"], NeoPermissionRisk.Sensitive),
            Permission("system-info:metadata", ["desktop.system.metadata"], NeoPermissionRisk.Low),
            Permission("opener:url", ["desktop.opener.url"], NeoPermissionRisk.High, NeoScopeFamily.Url),
            Permission("opener:file", ["desktop.opener.file"], NeoPermissionRisk.High, NeoScopeFamily.Filesystem),
            Permission("opener:reveal", ["desktop.opener.reveal"], NeoPermissionRisk.Sensitive, NeoScopeFamily.Filesystem),
            Permission("drag-drop:outbound", ["desktop.drag-drop.outbound"], NeoPermissionRisk.High),
            Permission("drag-drop:receive-files", ["desktop.drag-drop.inbound", "desktop.drag-drop.resolve-file"], NeoPermissionRisk.High),
            Permission("window:files", ["desktop.window.set-icon", "desktop.window.set-represented-file"], NeoPermissionRisk.High, NeoScopeFamily.Filesystem),
            Permission("window:polish", ["desktop.window.request-attention", "desktop.window.set-progress", "desktop.window.set-badge", "desktop.window.set-document-edited", "desktop.window.set-content-protection", "desktop.window.set-titlebar-theme"], NeoPermissionRisk.High),
            Permission("safe-storage:store", ["desktop.safe-storage.store"], NeoPermissionRisk.High),
            Permission("safe-storage:retrieve", ["desktop.safe-storage.retrieve", "desktop.safe-storage.contains"], NeoPermissionRisk.High),
            Permission("safe-storage:delete", ["desktop.safe-storage.delete"], NeoPermissionRisk.High),
        };
        return new(Id, "1.0.0", "0.1.0", declarations, new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["read-system-info"] = ["system-info:theme", "system-info:metadata"],
        });
    }

    private static NeoPermissionDeclaration Permission(string id, string[] commands, NeoPermissionRisk risk, NeoScopeFamily scope = NeoScopeFamily.None) => new(id, 1, commands, risk, scope)
    {
        ScopeRequired = scope != NeoScopeFamily.None,
        UnionSafe = scope != NeoScopeFamily.None,
        MaximumConcurrency = risk == NeoPermissionRisk.High ? 1 : 4,
        DefaultTimeout = TimeSpan.FromSeconds(30),
        Redaction = NeoAuditRedaction.Full,
        Documentation = "Official NeoAstra desktop essentials renderer permission. Registration and grants remain explicit.",
    };

    private sealed class DesktopAdapter(NeoDesktopServices services) : INeoPluginAdapter
    {
        public NeoCapabilityInfo Support { get; } = AggregateSupport(services);
        public ValueTask AttachAsync(NeoApplication application, CancellationToken cancellationToken) { ArgumentNullException.ThrowIfNull(application); cancellationToken.ThrowIfCancellationRequested(); if (!application.Dispatcher.CheckAccess()) throw new InvalidOperationException("Desktop adapters must attach on the UI dispatcher."); services.BindApplication(application); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static NeoCapabilityInfo AggregateSupport(NeoDesktopServices value)
        {
            var levels = new[] { value.Dialogs.Support.SupportLevel, value.Menus.Support.SupportLevel, value.Tray.Support.SupportLevel, value.Clipboard.Support.SupportLevel, value.Notifications.Support.SupportLevel, value.GlobalShortcuts.Support.SupportLevel, value.SystemInfo.ThemeSupport.SupportLevel, value.Opener.Support.SupportLevel, value.DragDrop.Support.SupportLevel, value.SafeStorage.Support.SupportLevel, value.WindowPolish.IconSupport.SupportLevel };
            var support = levels.All(static level => level == NeoSupportLevel.None) ? NeoSupportLevel.None : levels.All(static level => level == NeoSupportLevel.Native) ? NeoSupportLevel.Native : NeoSupportLevel.Limited;
            return new(support, 1, 0, "Per-service support details are authoritative; unsupported features never silently emulate OS security guarantees.");
        }
    }
}

/// <summary>Contains static desktop plugin registration helpers.</summary>
public static class NeoDesktopPluginExtensions
{
    /// <summary>Adds official desktop services through an explicit AOT-safe factory and transfers their ownership to the plugin host. This does not register renderer handlers or grants.</summary>
    public static NeoPluginBuilder AddNeoAstraDesktop(this NeoPluginBuilder builder, NeoDesktopServices services)
    {
        ArgumentNullException.ThrowIfNull(builder); ArgumentNullException.ThrowIfNull(services);
        return builder.AddNeoAstraPlugin(() => new NeoDesktopEssentialsPlugin(services));
    }

    /// <summary>Adds official permission declarations to application capability tooling. This grants no renderer authority.</summary>
    public static NeoPermissionCatalogBuilder AddNeoAstraDesktopPermissions(this NeoPermissionCatalogBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddPlugin(NeoDesktopEssentialsPlugin.PermissionCatalog);
    }
}
