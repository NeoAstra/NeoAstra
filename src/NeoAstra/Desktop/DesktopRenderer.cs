// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Security.Cryptography;
using System.Collections.Frozen;
using System.Text;
using System.Text.Json.Serialization;
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

/// <summary>Defines application-side bounds applied after renderer capability authorization.</summary>
public sealed class NeoDesktopRendererOptions
{
    private IReadOnlyDictionary<string, string> _fileRoots = FrozenDictionary<string, string>.Empty;
    private IReadOnlySet<string> _allowedMenuCommands = FrozenSet<string>.Empty;
    private IReadOnlySet<string> _allowedTrayIds = FrozenSet<string>.Empty;
    private IReadOnlySet<string> _allowedGlobalShortcuts = FrozenSet<string>.Empty;
    private IReadOnlySet<string> _allowedSafeStorageKeys = FrozenSet<string>.Empty;

    /// <summary>Gets or sets opaque root tokens used to resolve scoped relative paths.</summary>
    public IReadOnlyDictionary<string, string> FileRoots
    {
        get => _fileRoots;
        set => _fileRoots = ValidateRoots(value);
    }

    /// <summary>Gets or sets exact menu command IDs renderer activation may request.</summary>
    public IReadOnlySet<string> AllowedMenuCommands { get => _allowedMenuCommands; set => _allowedMenuCommands = ValidateSet(value, "menu command", NeoMenuItem.ValidateId); }
    /// <summary>Gets or sets exact tray IDs renderer sessions may create or control.</summary>
    public IReadOnlySet<string> AllowedTrayIds { get => _allowedTrayIds; set => _allowedTrayIds = ValidateSet(value, "tray ID", NeoMenuItem.ValidateId); }
    /// <summary>Gets or sets exact normalized global accelerators renderer sessions may register.</summary>
    public IReadOnlySet<string> AllowedGlobalShortcuts { get => _allowedGlobalShortcuts; set => _allowedGlobalShortcuts = ValidateSet(value, "shortcut", static (item, name) => { if (!string.Equals(item, NeoAccelerator.Normalize(item), StringComparison.Ordinal)) throw new ArgumentException("Allowed shortcuts must already be normalized.", name); }); }
    /// <summary>Gets or sets exact safe-storage keys exposed to renderer commands.</summary>
    public IReadOnlySet<string> AllowedSafeStorageKeys { get => _allowedSafeStorageKeys; set => _allowedSafeStorageKeys = ValidateSet(value, "safe-storage key", static (item, _) => NeoSafeStorage.ValidateKey(item)); }

    internal string Resolve(string root, string relativePath)
    {
        if (!_fileRoots.TryGetValue(root, out var path) || string.IsNullOrEmpty(relativePath) || Path.IsPathFullyQualified(relativePath) || relativePath.Length > 4096 || relativePath.Any(char.IsControl)) throw Denied("The requested path is outside configured renderer roots.");
        if (relativePath == ".") return path;
        var normalized = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Any(static part => part is "." or "..")) throw Denied("The requested path is outside configured renderer roots.");
        var candidate = Path.GetFullPath(Path.Combine(path, normalized));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar, comparison) && !string.Equals(candidate, path, comparison)) throw Denied("The requested path is outside configured renderer roots.");
        return candidate;
    }

    internal string ResolveExisting(string root, string relativePath)
    {
        var lexical = Resolve(root, relativePath);
        if (!_fileRoots.TryGetValue(root, out var authorizedRoot) || !new NeoFileScope([authorizedRoot]).TryAuthorize(lexical, requireExisting: true, out var canonical)) throw Denied("The requested existing path escaped its configured renderer root.");
        return canonical!;
    }

    internal NeoDesktopRendererOptions Snapshot() => new()
    {
        FileRoots = _fileRoots,
        AllowedMenuCommands = _allowedMenuCommands,
        AllowedTrayIds = _allowedTrayIds,
        AllowedGlobalShortcuts = _allowedGlobalShortcuts,
        AllowedSafeStorageKeys = _allowedSafeStorageKeys,
    };

    private static IReadOnlyDictionary<string, string> ValidateRoots(IReadOnlyDictionary<string, string> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Count > 128) throw new ArgumentException("Renderer roots exceed the supported bound.", nameof(value));
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in value)
        {
            NeoMenuItem.ValidateId(pair.Key, nameof(value));
            var full = NeoFileScope.Canonicalize(pair.Value, requireExisting: true);
            if (!Directory.Exists(full) || !output.TryAdd(pair.Key, full)) throw new ArgumentException("Renderer roots must be unique existing directories.", nameof(value));
        }
        return output.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static IReadOnlySet<string> ValidateSet(IReadOnlySet<string> value, string kind, Action<string, string> validate)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Count > 4096) throw new ArgumentException($"Allowed {kind} values exceed the supported bound.", nameof(value));
        var output = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value) { if (item is null) throw new ArgumentException($"An allowed {kind} is null.", nameof(value)); validate(item, nameof(value)); if (!output.Add(item)) throw new ArgumentException($"An allowed {kind} is duplicated.", nameof(value)); }
        return output.ToFrozenSet(StringComparer.Ordinal);
    }

    internal static NeoRpcException Denied(string message) => new("desktop_denied", message);
}

/// <summary>Registers official desktop renderer handlers explicitly without granting any capability.</summary>
public static class NeoDesktopRendererExtensions
{
    /// <summary>Adds all declared desktop handlers with source-generated JSON metadata. Calling this method grants nothing.</summary>
    /// <param name="builder">The explicit RPC builder.</param>
    /// <param name="services">Backend desktop services.</param>
    /// <param name="options">Application-side renderer bounds.</param>
    /// <returns>The builder.</returns>
    public static NeoRpcBuilder AddNeoAstraDesktopHandlers(this NeoRpcBuilder builder, NeoDesktopServices services, NeoDesktopRendererOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder); ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(options);
        var registration = new DesktopRendererRegistration(services, options.Snapshot());
        registration.Register(builder);
        services.TrackRendererRegistration(registration);
        return builder;
    }
}

internal static class NeoDesktopRendererContract
{
    internal static readonly string[] Commands =
    [
        "desktop.dialogs.open-file", "desktop.dialogs.open-folder", "desktop.dialogs.save-file", "desktop.dialogs.message",
        "desktop.menus.activate", "desktop.menus.set", "desktop.menus.popup", "desktop.tray.create", "desktop.tray.update", "desktop.tray.remove",
        "desktop.clipboard.read-text", "desktop.clipboard.write-text", "desktop.clipboard.read-rich", "desktop.clipboard.write-rich", "desktop.clipboard.clear",
        "desktop.notifications.status", "desktop.notifications.show", "desktop.notifications.remove",
        "desktop.shortcuts.register", "desktop.shortcuts.unregister", "desktop.system.theme", "desktop.system.displays", "desktop.system.metadata",
        "desktop.opener.url", "desktop.opener.file", "desktop.opener.reveal", "desktop.drag-drop.outbound", "desktop.drag-drop.resolve-file",
        "desktop.window.set-icon", "desktop.window.set-represented-file", "desktop.window.request-attention", "desktop.window.set-progress", "desktop.window.set-badge", "desktop.window.set-document-edited", "desktop.window.set-content-protection", "desktop.window.set-titlebar-theme",
        "desktop.safe-storage.store", "desktop.safe-storage.retrieve", "desktop.safe-storage.delete", "desktop.safe-storage.contains",
    ];
    internal static readonly string[] Events =
    [
        "desktop.tray.activated", "desktop.notifications.activated", "desktop.shortcuts.activated",
        "desktop.system.theme-changed", "desktop.system.displays-changed", "desktop.drag-drop.inbound",
    ];
}

internal sealed class DesktopRendererRegistration(NeoDesktopServices services, NeoDesktopRendererOptions applicationOptions) : INeoRpcServiceRegistration, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _ownershipGate = new(1, 1);
    private readonly Dictionary<(string Session, string Id), long> _ownedTray = [];
    private readonly Dictionary<(string Session, string Id), long> _ownedShortcuts = [];
    private readonly Dictionary<(string Session, string Id), long> _ownedNotifications = [];
    private readonly Dictionary<string, (string Session, long Generation)> _ownedMenus = new(StringComparer.Ordinal);
    private readonly HashSet<string> _trackedDropSessions = new(StringComparer.Ordinal);
    private long _nextGeneration;
    private NeoRpcEvent<DesktopIdEvent>? _trayActivated;
    private NeoRpcEvent<DesktopNotificationActivatedEvent>? _notificationActivated;
    private NeoRpcEvent<DesktopIdEvent>? _shortcutActivated;
    private NeoRpcEvent<DesktopThemeChangedEvent>? _themeChanged;
    private NeoRpcEvent<DesktopDisplaysChangedEvent>? _displaysChanged;
    private NeoRpcEvent<DesktopDropEvent>? _dropInbound;
    private bool _dragDropRegistered;
    private bool _disposed;

    public void Register(NeoRpcBuilder builder)
    {
        if (_dragDropRegistered) throw new InvalidOperationException("The desktop renderer registration is already registered.");
        var json = DesktopRendererJsonContext.Default;
        Add(builder, "desktop.dialogs.open-file", "dialogs:open-file", OpenFilesAsync, json.DesktopDialogRequest, json.DesktopPathsResult);
        Add(builder, "desktop.dialogs.open-folder", "dialogs:open-folder", OpenFoldersAsync, json.DesktopDialogRequest, json.DesktopPathsResult);
        Add(builder, "desktop.dialogs.save-file", "dialogs:save-file", SaveFileAsync, json.DesktopDialogRequest, json.DesktopPathResult);
        Add(builder, "desktop.dialogs.message", "dialogs:message", MessageAsync, json.DesktopMessageRequest, json.DesktopMessageResult);
        Add(builder, "desktop.menus.activate", "menus:activate", ActivateMenuAsync, json.DesktopIdRequest, json.DesktopStatusResult);
        Add(builder, "desktop.menus.set", "menus:control", SetMenuAsync, json.DesktopMenuRequest, json.DesktopStatusResult);
        Add(builder, "desktop.menus.popup", "menus:control", ShowContextMenuAsync, json.DesktopContextMenuRequest, json.DesktopStatusResult);
        Add(builder, "desktop.tray.create", "tray:create", CreateTrayAsync, json.DesktopTrayRequest, json.DesktopStatusResult);
        Add(builder, "desktop.tray.update", "tray:control", UpdateTrayAsync, json.DesktopTrayRequest, json.DesktopStatusResult);
        Add(builder, "desktop.tray.remove", "tray:control", RemoveTrayAsync, json.DesktopIdRequest, json.DesktopStatusResult);
        Add(builder, "desktop.clipboard.read-text", "clipboard:read-text", ReadClipboardAsync, json.DesktopClipboardReadRequest, json.DesktopBytesResult);
        Add(builder, "desktop.clipboard.write-text", "clipboard:write-text", WriteClipboardAsync, json.DesktopClipboardWriteRequest, json.DesktopStatusResult);
        Add(builder, "desktop.clipboard.read-rich", "clipboard:read-rich", ReadClipboardAsync, json.DesktopClipboardReadRequest, json.DesktopBytesResult);
        Add(builder, "desktop.clipboard.write-rich", "clipboard:write-rich", WriteClipboardAsync, json.DesktopClipboardWriteRequest, json.DesktopStatusResult);
        Add(builder, "desktop.clipboard.clear", "clipboard:clear", ClearClipboardAsync, json.DesktopClipboardClearRequest, json.DesktopStatusResult);
        Add(builder, "desktop.notifications.status", "notifications:status", NotificationStatusAsync, json.DesktopEmptyRequest, json.DesktopNotificationStatusResult);
        Add(builder, "desktop.notifications.show", "notifications:display", ShowNotificationAsync, json.DesktopNotificationRequestDto, json.DesktopStatusResult);
        Add(builder, "desktop.notifications.remove", "notifications:remove", RemoveNotificationAsync, json.DesktopIdRequest, json.DesktopStatusResult);
        Add(builder, "desktop.shortcuts.register", "shortcuts:register", RegisterShortcutAsync, json.DesktopShortcutRequest, json.DesktopStatusResult);
        Add(builder, "desktop.shortcuts.unregister", "shortcuts:control", UnregisterShortcutAsync, json.DesktopIdRequest, json.DesktopStatusResult);
        Add(builder, "desktop.system.theme", "system-info:theme", ThemeAsync, json.DesktopEmptyRequest, json.DesktopThemeResult);
        Add(builder, "desktop.system.displays", "system-info:displays", DisplaysAsync, json.DesktopEmptyRequest, json.DesktopDisplaysResult);
        Add(builder, "desktop.system.metadata", "system-info:metadata", MetadataAsync, json.DesktopEmptyRequest, json.DesktopMetadataResult);
        Add(builder, "desktop.opener.url", "opener:url", OpenUrlAsync, json.DesktopUrlRequest, json.DesktopStatusResult);
        Add(builder, "desktop.opener.file", "opener:file", OpenFileAsync, json.DesktopFileRequest, json.DesktopStatusResult);
        Add(builder, "desktop.opener.reveal", "opener:reveal", RevealAsync, json.DesktopScopedPathRequest, json.DesktopStatusResult);
        Add(builder, "desktop.drag-drop.outbound", "drag-drop:outbound", OutboundDragAsync, json.DesktopOutboundDragRequest, json.DesktopStatusResult);
        Add(builder, "desktop.drag-drop.resolve-file", "drag-drop:receive-files", ResolveDropFileAsync, json.DesktopDropFileRequest, json.DesktopPathResult);
        Add(builder, "desktop.window.set-icon", "window:files", SetWindowIconAsync, json.DesktopScopedPathRequest, json.DesktopStatusResult);
        Add(builder, "desktop.window.set-represented-file", "window:files", SetRepresentedFileAsync, json.DesktopOptionalScopedPathRequest, json.DesktopStatusResult);
        Add(builder, "desktop.window.request-attention", "window:polish", RequestWindowAttentionAsync, json.DesktopBoolRequest, json.DesktopStatusResult);
        Add(builder, "desktop.window.set-progress", "window:polish", SetWindowProgressAsync, json.DesktopWindowProgressRequest, json.DesktopStatusResult);
        Add(builder, "desktop.window.set-badge", "window:polish", SetWindowBadgeAsync, json.DesktopOptionalTextRequest, json.DesktopStatusResult);
        Add(builder, "desktop.window.set-document-edited", "window:polish", SetWindowDocumentEditedAsync, json.DesktopBoolRequest, json.DesktopStatusResult);
        Add(builder, "desktop.window.set-content-protection", "window:polish", SetWindowContentProtectionAsync, json.DesktopBoolRequest, json.DesktopStatusResult);
        Add(builder, "desktop.window.set-titlebar-theme", "window:polish", SetWindowTitleBarThemeAsync, json.DesktopWindowThemeRequest, json.DesktopStatusResult);
        Add(builder, "desktop.safe-storage.store", "safe-storage:store", StoreSecretAsync, json.DesktopSecretWriteRequest, json.DesktopStatusResult);
        Add(builder, "desktop.safe-storage.retrieve", "safe-storage:retrieve", RetrieveSecretAsync, json.DesktopIdRequest, json.DesktopBytesResult);
        Add(builder, "desktop.safe-storage.delete", "safe-storage:delete", DeleteSecretAsync, json.DesktopIdRequest, json.DesktopStatusResult);
        Add(builder, "desktop.safe-storage.contains", "safe-storage:retrieve", ContainsSecretAsync, json.DesktopIdRequest, json.DesktopBoolResult);

        _trayActivated = builder.AddEvent("desktop.tray.activated", json.DesktopIdEvent, new() { Permission = "tray:control" });
        _notificationActivated = builder.AddEvent("desktop.notifications.activated", json.DesktopNotificationActivatedEvent, new() { Permission = "notifications:activation" });
        _shortcutActivated = builder.AddEvent("desktop.shortcuts.activated", json.DesktopIdEvent, new() { Permission = "shortcuts:control" });
        _themeChanged = builder.AddEvent("desktop.system.theme-changed", json.DesktopThemeChangedEvent, new() { Permission = "system-info:theme" });
        _displaysChanged = builder.AddEvent("desktop.system.displays-changed", json.DesktopDisplaysChangedEvent, new() { Permission = "system-info:displays" });
        _dropInbound = builder.AddEvent("desktop.drag-drop.inbound", json.DesktopDropEvent, new() { Permission = "drag-drop:receive-files" });
        services.Tray.Activated += OnTrayActivated;
        services.Notifications.Activated += OnNotificationActivated;
        services.GlobalShortcuts.Activated += OnShortcutActivated;
        services.SystemInfo.ThemeChanged += OnThemeChanged;
        services.SystemInfo.DisplaysChanged += OnDisplaysChanged;
        services.DragDrop.Inbound += OnDropInbound;
        services.DragDrop.RegisterRenderer();
        _dragDropRegistered = true;
    }

    private static void Add<TRequest, TResult>(NeoRpcBuilder builder, string command, string permission, NeoRpcCommandHandler<TRequest, TResult> handler,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TRequest> request, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> result)
        => builder.AddCommand(command, handler, request, result, new NeoRpcCommandOptions { Permission = permission, Dispatch = permission.StartsWith("shortcuts:", StringComparison.Ordinal) ? NeoRpcDispatchMode.UiThread : NeoRpcDispatchMode.Background, MaximumConcurrency = permission.StartsWith("system-info:", StringComparison.Ordinal) ? 4 : 1, Timeout = TimeSpan.FromSeconds(30) });

    private NeoFileDialogOptions DialogOptions(DesktopDialogRequest request, NeoRpcContext context, bool save)
    {
        var initial = applicationOptions.ResolveExisting(request.InitialLocation, request.InitialRelativePath ?? ".");
        if (request.Extensions is null || request.Filters is null || request.Extensions.Count > 64 || request.Filters.Count > 64) throw new ArgumentException("Dialog filters exceed their bound.");
        var scopedExtensions = request.Extensions.ToHashSet(StringComparer.Ordinal);
        if (request.Filters.Any(filter => filter is null || filter.MimeTypes is null || filter.MimeTypes.Count != 0 || filter.Extensions is null || filter.Extensions.Any(extension => !scopedExtensions.Contains(extension)))) throw NeoDesktopRendererOptions.Denied("Dialog filters must use only capability-scoped extensions; renderer MIME filters are not accepted.");
        var filters = request.Filters.Select(static filter => new NeoFileDialogFilter(filter.Name, filter.Extensions, filter.MimeTypes)).ToArray();
        return new NeoFileDialogOptions { Owner = Window(context), Title = request.Title, InitialDirectory = initial, SuggestedFileName = save ? request.SuggestedFileName : null, Filters = filters, AllowMultiple = !save && request.AllowMultiple, Scope = new NeoFileScope(applicationOptions.FileRoots.Values) };
    }

    private async ValueTask<DesktopPathsResult> OpenFilesAsync(DesktopDialogRequest request, NeoRpcContext context, CancellationToken token)
    {
        if (request.Kind != "openFile") throw NeoDesktopRendererOptions.Denied("The dialog kind does not match the command.");
        var result = await services.Dialogs.OpenFilesAsync(DialogOptions(request, context, false), token).ConfigureAwait(false); return new(result.Status, result.Value, result.Code);
    }
    private async ValueTask<DesktopPathsResult> OpenFoldersAsync(DesktopDialogRequest request, NeoRpcContext context, CancellationToken token)
    {
        if (request.Kind != "openFolder") throw NeoDesktopRendererOptions.Denied("The dialog kind does not match the command.");
        var result = await services.Dialogs.OpenFoldersAsync(DialogOptions(request, context, false), token).ConfigureAwait(false); return new(result.Status, result.Value, result.Code);
    }
    private async ValueTask<DesktopPathResult> SaveFileAsync(DesktopDialogRequest request, NeoRpcContext context, CancellationToken token)
    {
        if (request.Kind != "saveFile") throw NeoDesktopRendererOptions.Denied("The dialog kind does not match the command.");
        var result = await services.Dialogs.SaveFileAsync(DialogOptions(request, context, true), token).ConfigureAwait(false); return new(result.Status, result.Value, result.Code);
    }
    private async ValueTask<DesktopMessageResult> MessageAsync(DesktopMessageRequest request, NeoRpcContext context, CancellationToken token)
    {
        if (request.Kind != "message") throw NeoDesktopRendererOptions.Denied("The dialog kind does not match the command.");
        var result = await services.Dialogs.ShowMessageAsync(new NeoMessageDialogOptions { Owner = Window(context), Title = request.Title, Message = request.Message, Detail = request.Detail, Icon = request.Icon, Buttons = request.Buttons }, token).ConfigureAwait(false); return new(result.Status, result.Value, result.Code);
    }
    private async ValueTask<DesktopStatusResult> ActivateMenuAsync(DesktopIdRequest request, NeoRpcContext context, CancellationToken token)
    {
        if (!applicationOptions.AllowedMenuCommands.Contains(request.Id)) throw NeoDesktopRendererOptions.Denied("The menu command is not application-allowed.");
        return Status(await services.Menus.Commands.ActivateAsync(request.Id, token).ConfigureAwait(false));
    }
    private async ValueTask<DesktopStatusResult> SetMenuAsync(DesktopMenuRequest request, NeoRpcContext context, CancellationToken token)
    {
        if (request.TargetId != context.ViewLabel) throw NeoDesktopRendererOptions.Denied("Renderer menus may target only their originating view.");
        var nativeTarget = "context:" + context.ViewLabel;
        ValidateMenuCommands(request.Items);
        await _ownershipGate.WaitAsync(token).ConfigureAwait(false);
        var added = false; var generation = 0L;
        try
        {
            lock (_sync)
            {
                if (_disposed) return Status(NeoDesktopStatus.Canceled);
                if (_ownedMenus.TryGetValue(nativeTarget, out var owner) && owner.Session != context.DocumentSessionId) return Status(NeoDesktopStatus.Conflict);
                if (owner.Session is null) { generation = checked(++_nextGeneration); _ownedMenus.Add(nativeTarget, (context.DocumentSessionId, generation)); added = true; }
                else generation = owner.Generation;
            }
            await services.Menus.SetMenuAsync(nativeTarget, request.Items.Select(MenuItem), token).ConfigureAwait(false);
            if (added) context.Resources.Add(new AsyncAction(() => RemoveOwnedMenuAsync(nativeTarget, context.DocumentSessionId, generation)));
            return Status(NeoDesktopStatus.Success);
        }
        catch { if (added) { lock (_sync) _ownedMenus.Remove(nativeTarget); await services.Menus.RemoveMenuAsync(nativeTarget).ConfigureAwait(false); } throw; }
        finally { _ownershipGate.Release(); }
    }
    private async ValueTask<DesktopStatusResult> ShowContextMenuAsync(DesktopContextMenuRequest request, NeoRpcContext context, CancellationToken token)
    {
        if (request.TargetId != context.ViewLabel) throw NeoDesktopRendererOptions.Denied("Renderer context menus may target only their originating view.");
        if (request.X is < -1_000_000 or > 1_000_000 || request.Y is < -1_000_000 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(request));
        var target = "context:" + context.ViewLabel; lock (_sync) if (!_ownedMenus.TryGetValue(target, out var owner) || owner.Session != context.DocumentSessionId) return Status(NeoDesktopStatus.NotFound);
        return Status(await services.Menus.ShowContextMenuAsync(target, new(request.X, request.Y), token).ConfigureAwait(false));
    }
    private ValueTask<DesktopStatusResult> CreateTrayAsync(DesktopTrayRequest request, NeoRpcContext context, CancellationToken token) => SetTrayAsync(request, context, create: true, token);
    private ValueTask<DesktopStatusResult> UpdateTrayAsync(DesktopTrayRequest request, NeoRpcContext context, CancellationToken token) => SetTrayAsync(request, context, create: false, token);
    private async ValueTask<DesktopStatusResult> SetTrayAsync(DesktopTrayRequest request, NeoRpcContext context, bool create, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); EnsureAllowed(applicationOptions.AllowedTrayIds, request.Id, "tray");
        ValidateMenuCommands(request.Menu);
        var key = (context.DocumentSessionId, request.Id);
        await _ownershipGate.WaitAsync(token).ConfigureAwait(false);
        var added = false; var generation = 0L;
        try
        {
            lock (_sync)
            {
                if (_disposed) return Status(NeoDesktopStatus.Canceled);
                var exists = _ownedTray.TryGetValue(key, out generation);
                if (_ownedTray.Keys.Any(owner => owner.Id == request.Id && owner.Session != context.DocumentSessionId)) return Status(NeoDesktopStatus.Conflict);
                if (create == exists) return Status(exists ? NeoDesktopStatus.Conflict : NeoDesktopStatus.NotFound);
                if (!exists) { generation = checked(++_nextGeneration); _ownedTray.Add(key, generation); added = true; }
            }
            await services.Tray.SetAsync(new NeoTrayItemOptions { Id = request.Id, ToolTip = request.ToolTip, IconPath = null, IsTemplateImage = request.IsTemplateImage, Menu = request.Menu.Select(MenuItem).ToArray() }, token).ConfigureAwait(false);
            if (added) context.Resources.Add(new AsyncAction(() => RemoveOwnedTrayAsync(key, generation)));
        }
        catch { if (added) { lock (_sync) _ownedTray.Remove(key); await services.Tray.RemoveAsync(request.Id).ConfigureAwait(false); } throw; }
        finally { _ownershipGate.Release(); }
        return Status(NeoDesktopStatus.Success);
    }
    private async ValueTask<DesktopStatusResult> RemoveTrayAsync(DesktopIdRequest request, NeoRpcContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var key = (context.DocumentSessionId, request.Id);
        await _ownershipGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            long generation; lock (_sync) if (!_ownedTray.TryGetValue(key, out generation)) return Status(NeoDesktopStatus.NotFound);
            var removed = await services.Tray.RemoveAsync(request.Id, token).ConfigureAwait(false);
            lock (_sync) if (_ownedTray.TryGetValue(key, out var current) && current == generation) _ownedTray.Remove(key);
            return Status(removed ? NeoDesktopStatus.Success : NeoDesktopStatus.NotFound);
        }
        finally { _ownershipGate.Release(); }
    }
    private async ValueTask<DesktopBytesResult> ReadClipboardAsync(DesktopClipboardReadRequest request, NeoRpcContext context, CancellationToken token)
    {
        if (request.Operation != "read") throw NeoDesktopRendererOptions.Denied("The clipboard operation is invalid.");
        var format = ClipboardFormat(request.Format); var result = await services.Clipboard.ReadAsync(format, token).ConfigureAwait(false); return new(result.Status, result.Value is null ? null : Convert.ToBase64String(result.Value), result.Code);
    }
    private async ValueTask<DesktopStatusResult> WriteClipboardAsync(DesktopClipboardWriteRequest request, NeoRpcContext context, CancellationToken token)
    {
        if (request.Operation != "write") throw NeoDesktopRendererOptions.Denied("The clipboard operation is invalid.");
        try { return Status(await services.Clipboard.WriteAsync(ClipboardFormat(request.Format), request.Bytes, token).ConfigureAwait(false)); }
        finally { CryptographicOperations.ZeroMemory(request.Bytes); }
    }
    private async ValueTask<DesktopStatusResult> ClearClipboardAsync(DesktopClipboardClearRequest request, NeoRpcContext context, CancellationToken token)
    {
        if (request.Operation != "write" || request.Format != "all") throw NeoDesktopRendererOptions.Denied("The clipboard clear operation is invalid.");
        return Status(await services.Clipboard.ClearAsync(token).ConfigureAwait(false));
    }
    private async ValueTask<DesktopNotificationStatusResult> NotificationStatusAsync(DesktopEmptyRequest request, NeoRpcContext context, CancellationToken token) => new(await services.Notifications.GetPermissionStatusAsync(token).ConfigureAwait(false));
    private async ValueTask<DesktopStatusResult> ShowNotificationAsync(DesktopNotificationRequestDto request, NeoRpcContext context, CancellationToken token)
    {
        if (request.AppIdentity != services.SystemInfo.Metadata.ApplicationId) throw NeoDesktopRendererOptions.Denied("The notification identity is invalid.");
        var key = (context.DocumentSessionId, request.Id);
        await _ownershipGate.WaitAsync(token).ConfigureAwait(false);
        var added = false; var generation = 0L;
        try
        {
            lock (_sync)
            {
                if (_disposed) return Status(NeoDesktopStatus.Canceled);
                if (_ownedNotifications.Keys.Any(owner => owner.Id == request.Id && owner.Session != context.DocumentSessionId)) return Status(NeoDesktopStatus.Conflict);
                if (!_ownedNotifications.TryGetValue(key, out generation)) { generation = checked(++_nextGeneration); _ownedNotifications.Add(key, generation); added = true; }
            }
        var value = new NeoNotificationRequest { Id = request.Id, Title = request.Title, Body = request.Body, ActivationData = request.Payload, Actions = request.Actions.Select(static action => new NeoNotificationAction(action.Id, action.Title)).ToArray() };
        var status = await services.Notifications.ShowAsync(value, token).ConfigureAwait(false);
        if (status != NeoDesktopStatus.Success) { if (added) lock (_sync) _ownedNotifications.Remove(key); return Status(status); }
        if (added)
        {
            try { context.Resources.Add(new AsyncAction(() => RemoveOwnedNotificationAsync(key, generation))); }
            catch { lock (_sync) _ownedNotifications.Remove(key); await services.Notifications.RemoveAsync(request.Id, CancellationToken.None).ConfigureAwait(false); throw; }
        }
        return Status(status);
        }
        finally { _ownershipGate.Release(); }
    }
    private async ValueTask<DesktopStatusResult> RemoveNotificationAsync(DesktopIdRequest request, NeoRpcContext context, CancellationToken token)
    {
        var key = (context.DocumentSessionId, request.Id); await _ownershipGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            long generation; lock (_sync) if (!_ownedNotifications.TryGetValue(key, out generation)) return Status(NeoDesktopStatus.NotFound);
            var status = await services.Notifications.RemoveAsync(request.Id, token).ConfigureAwait(false);
            if (status is NeoDesktopStatus.Success or NeoDesktopStatus.NotFound) lock (_sync) if (_ownedNotifications.TryGetValue(key, out var current) && current == generation) _ownedNotifications.Remove(key);
            return Status(status);
        }
        finally { _ownershipGate.Release(); }
    }
    private async ValueTask<DesktopStatusResult> RegisterShortcutAsync(DesktopShortcutRequest request, NeoRpcContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var normalized = NeoAccelerator.Normalize(request.Accelerator); EnsureAllowed(applicationOptions.AllowedGlobalShortcuts, normalized, "shortcut");
        var key = (context.DocumentSessionId, request.Id);
        await _ownershipGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            long generation;
            lock (_sync) { if (_disposed) return Status(NeoDesktopStatus.Canceled); if (_ownedShortcuts.Keys.Any(owner => owner.Id == request.Id)) return Status(NeoDesktopStatus.Conflict); generation = checked(++_nextGeneration); _ownedShortcuts.Add(key, generation); }
            var status = await services.GlobalShortcuts.RegisterAsync(request.Id, normalized, token).ConfigureAwait(false);
            if (status != NeoDesktopStatus.Success) { lock (_sync) _ownedShortcuts.Remove(key); return Status(status); }
            try { context.Resources.Add(new AsyncAction(() => RemoveOwnedShortcutAsync(key, generation))); }
            catch { lock (_sync) _ownedShortcuts.Remove(key); await services.GlobalShortcuts.UnregisterAsync(request.Id, CancellationToken.None).ConfigureAwait(false); throw; }
            return Status(status);
        }
        finally { _ownershipGate.Release(); }
    }
    private async ValueTask<DesktopStatusResult> UnregisterShortcutAsync(DesktopIdRequest request, NeoRpcContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var key = (context.DocumentSessionId, request.Id); await _ownershipGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            long generation; lock (_sync) if (!_ownedShortcuts.TryGetValue(key, out generation)) return Status(NeoDesktopStatus.NotFound);
            var removed = await services.GlobalShortcuts.UnregisterAsync(request.Id, token).ConfigureAwait(false);
            lock (_sync) if (_ownedShortcuts.TryGetValue(key, out var current) && current == generation) _ownedShortcuts.Remove(key);
            return Status(removed ? NeoDesktopStatus.Success : NeoDesktopStatus.NotFound);
        }
        finally { _ownershipGate.Release(); }
    }
    private ValueTask<DesktopThemeResult> ThemeAsync(DesktopEmptyRequest request, NeoRpcContext context, CancellationToken token) { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new DesktopThemeResult(services.SystemInfo.Theme)); }
    private ValueTask<DesktopDisplaysResult> DisplaysAsync(DesktopEmptyRequest request, NeoRpcContext context, CancellationToken token) { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new DesktopDisplaysResult(services.SystemInfo.Displays)); }
    private ValueTask<DesktopMetadataResult> MetadataAsync(DesktopEmptyRequest request, NeoRpcContext context, CancellationToken token) { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new DesktopMetadataResult(services.SystemInfo.Metadata)); }
    private async ValueTask<DesktopStatusResult> OpenUrlAsync(DesktopUrlRequest request, NeoRpcContext context, CancellationToken token)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)) throw new NeoRpcException("invalid_url", "The URL is malformed."); return Status(await services.Opener.OpenUrlAsync(uri, token).ConfigureAwait(false));
    }
    private async ValueTask<DesktopStatusResult> OpenFileAsync(DesktopFileRequest request, NeoRpcContext context, CancellationToken token)
    {
        if (request.Operation != "open") throw NeoDesktopRendererOptions.Denied("The file operation is invalid.");
        return Status(await services.Opener.OpenFileAsync(applicationOptions.ResolveExisting(request.Root, request.RelativePath), request.Intent, token).ConfigureAwait(false));
    }
    private async ValueTask<DesktopStatusResult> RevealAsync(DesktopScopedPathRequest request, NeoRpcContext context, CancellationToken token)
    {
        if (request.Operation != "reveal") throw NeoDesktopRendererOptions.Denied("The file operation is invalid.");
        return Status(await services.Opener.RevealAsync(applicationOptions.ResolveExisting(request.Root, request.RelativePath), token).ConfigureAwait(false));
    }
    private async ValueTask<DesktopStatusResult> OutboundDragAsync(DesktopOutboundDragRequest request, NeoRpcContext context, CancellationToken token)
    {
        if (request.ViewLabel != context.ViewLabel) throw NeoDesktopRendererOptions.Denied("The outbound drag source view is invalid.");
        var items = request.Items.Select(item => new NeoOutboundDragItem(item.Kind, item.Kind == NeoDragDataKind.File ? applicationOptions.ResolveExisting(item.Root ?? string.Empty, item.RelativePath ?? string.Empty) : item.Value ?? string.Empty)).ToArray();
        TrackDropOwner(context);
        var owner = NeoPluginOwner.DocumentSession(context.DocumentSessionId); return Status(await services.DragDrop.StartRendererOutboundAsync(owner, new NeoOutboundDragRequest { ViewLabel = request.ViewLabel, Items = items }, token).ConfigureAwait(false));
    }
    private ValueTask<DesktopPathResult> ResolveDropFileAsync(DesktopDropFileRequest request, NeoRpcContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        TrackDropOwner(context);
        if (!services.DragDrop.TryResolveFile(request.Token, NeoPluginOwner.DocumentSession(context.DocumentSessionId), out var path)) return ValueTask.FromResult(new DesktopPathResult(NeoDesktopStatus.NotFound, null, "drop_token_unavailable"));
        return ValueTask.FromResult(new DesktopPathResult(NeoDesktopStatus.Success, path));
    }
    private async ValueTask<DesktopStatusResult> SetWindowIconAsync(DesktopScopedPathRequest request, NeoRpcContext context, CancellationToken token)
    {
        if (request.Operation != "read") throw NeoDesktopRendererOptions.Denied("The window icon operation is invalid.");
        return Status(await services.WindowPolish.SetIconAsync(RequiredWindow(context), applicationOptions.ResolveExisting(request.Root, request.RelativePath), token).ConfigureAwait(false));
    }
    private async ValueTask<DesktopStatusResult> SetRepresentedFileAsync(DesktopOptionalScopedPathRequest request, NeoRpcContext context, CancellationToken token)
    {
        if (request.Operation != "read") throw NeoDesktopRendererOptions.Denied("The represented-file operation is invalid.");
        if ((request.Root is null) != (request.RelativePath is null)) throw NeoDesktopRendererOptions.Denied("Both represented-file path components are required.");
        var path = request.Root is null ? null : applicationOptions.ResolveExisting(request.Root, request.RelativePath!);
        return Status(await services.WindowPolish.SetRepresentedFileAsync(RequiredWindow(context), path, token).ConfigureAwait(false));
    }
    private async ValueTask<DesktopStatusResult> RequestWindowAttentionAsync(DesktopBoolRequest request, NeoRpcContext context, CancellationToken token)
        => Status(await services.WindowPolish.RequestAttentionAsync(RequiredWindow(context), request.Value, token).ConfigureAwait(false));
    private async ValueTask<DesktopStatusResult> SetWindowProgressAsync(DesktopWindowProgressRequest request, NeoRpcContext context, CancellationToken token)
        => Status(await services.WindowPolish.SetProgressAsync(RequiredWindow(context), request.State, request.Value, token).ConfigureAwait(false));
    private async ValueTask<DesktopStatusResult> SetWindowBadgeAsync(DesktopOptionalTextRequest request, NeoRpcContext context, CancellationToken token)
        => Status(await services.WindowPolish.SetBadgeAsync(RequiredWindow(context), request.Value, token).ConfigureAwait(false));
    private async ValueTask<DesktopStatusResult> SetWindowDocumentEditedAsync(DesktopBoolRequest request, NeoRpcContext context, CancellationToken token)
        => Status(await services.WindowPolish.SetDocumentEditedAsync(RequiredWindow(context), request.Value, token).ConfigureAwait(false));
    private async ValueTask<DesktopStatusResult> SetWindowContentProtectionAsync(DesktopBoolRequest request, NeoRpcContext context, CancellationToken token)
        => Status(await services.WindowPolish.SetContentProtectionAsync(RequiredWindow(context), request.Value, token).ConfigureAwait(false));
    private async ValueTask<DesktopStatusResult> SetWindowTitleBarThemeAsync(DesktopWindowThemeRequest request, NeoRpcContext context, CancellationToken token)
        => Status(await services.WindowPolish.SetTitleBarThemeAsync(RequiredWindow(context), request.Theme, token).ConfigureAwait(false));
    private async ValueTask<DesktopStatusResult> StoreSecretAsync(DesktopSecretWriteRequest request, NeoRpcContext context, CancellationToken token)
    {
        EnsureAllowed(applicationOptions.AllowedSafeStorageKeys, request.Id, "safe-storage key");
        try { return Status(await services.SafeStorage.StoreAsync(request.Id, request.Secret, token).ConfigureAwait(false)); } finally { CryptographicOperations.ZeroMemory(request.Secret); }
    }
    private async ValueTask<DesktopBytesResult> RetrieveSecretAsync(DesktopIdRequest request, NeoRpcContext context, CancellationToken token)
    {
        EnsureAllowed(applicationOptions.AllowedSafeStorageKeys, request.Id, "safe-storage key"); var result = await services.SafeStorage.RetrieveAsync(request.Id, token).ConfigureAwait(false); if (result.Value is null) return new(result.Status, null, result.Code);
        try { return new(result.Status, Convert.ToBase64String(result.Value), result.Code); } finally { CryptographicOperations.ZeroMemory(result.Value); }
    }
    private async ValueTask<DesktopStatusResult> DeleteSecretAsync(DesktopIdRequest request, NeoRpcContext context, CancellationToken token) { EnsureAllowed(applicationOptions.AllowedSafeStorageKeys, request.Id, "safe-storage key"); return Status(await services.SafeStorage.DeleteAsync(request.Id, token).ConfigureAwait(false)); }
    private async ValueTask<DesktopBoolResult> ContainsSecretAsync(DesktopIdRequest request, NeoRpcContext context, CancellationToken token) { EnsureAllowed(applicationOptions.AllowedSafeStorageKeys, request.Id, "safe-storage key"); var result = await services.SafeStorage.ContainsAsync(request.Id, token).ConfigureAwait(false); return new(result.Status, result.Value, result.Code); }

    private async ValueTask RemoveOwnedTrayAsync((string Session, string Id) key, long generation)
    {
        await _ownershipGate.WaitAsync().ConfigureAwait(false);
        try { lock (_sync) if (!_ownedTray.TryGetValue(key, out var current) || current != generation) return; try { await services.Tray.RemoveAsync(key.Id).ConfigureAwait(false); } catch { } lock (_sync) if (_ownedTray.TryGetValue(key, out var current) && current == generation) _ownedTray.Remove(key); services.DragDrop.ReleaseOwner(NeoPluginOwner.DocumentSession(key.Session)); }
        finally { _ownershipGate.Release(); }
    }
    private async ValueTask RemoveOwnedMenuAsync(string targetId, string session, long generation)
    {
        await _ownershipGate.WaitAsync().ConfigureAwait(false);
        try { lock (_sync) if (!_ownedMenus.TryGetValue(targetId, out var owner) || owner != (session, generation)) return; try { await services.Menus.RemoveMenuAsync(targetId).ConfigureAwait(false); } catch { } lock (_sync) if (_ownedMenus.TryGetValue(targetId, out var current) && current == (session, generation)) _ownedMenus.Remove(targetId); services.DragDrop.ReleaseOwner(NeoPluginOwner.DocumentSession(session)); }
        finally { _ownershipGate.Release(); }
    }
    private async ValueTask RemoveOwnedShortcutAsync((string Session, string Id) key, long generation)
    {
        await _ownershipGate.WaitAsync().ConfigureAwait(false);
        try { lock (_sync) if (!_ownedShortcuts.TryGetValue(key, out var current) || current != generation) return; try { await services.GlobalShortcuts.UnregisterAsync(key.Id).ConfigureAwait(false); } catch { } lock (_sync) if (_ownedShortcuts.TryGetValue(key, out var current) && current == generation) _ownedShortcuts.Remove(key); services.DragDrop.ReleaseOwner(NeoPluginOwner.DocumentSession(key.Session)); }
        finally { _ownershipGate.Release(); }
    }
    private async ValueTask RemoveOwnedNotificationAsync((string Session, string Id) key, long generation)
    {
        await _ownershipGate.WaitAsync().ConfigureAwait(false);
        try { lock (_sync) if (!_ownedNotifications.TryGetValue(key, out var current) || current != generation) return; try { await services.Notifications.RemoveAsync(key.Id).ConfigureAwait(false); } catch { } lock (_sync) if (_ownedNotifications.TryGetValue(key, out var current) && current == generation) _ownedNotifications.Remove(key); services.DragDrop.ReleaseOwner(NeoPluginOwner.DocumentSession(key.Session)); }
        finally { _ownershipGate.Release(); }
    }
    private void OnTrayActivated(object? sender, NeoTrayActivation e) { if (!IsDisposed()) _ = PublishContainedAsync(_trayActivated, new(e.ItemId), context => IsOwned(_ownedTray, context, e.ItemId)); }
    private void OnNotificationActivated(object? sender, NeoNotificationActivation e) { if (!IsDisposed()) _ = PublishContainedAsync(_notificationActivated, new(e.NotificationId, e.ActionId, e.ActivationData), context => IsOwned(_ownedNotifications, context, e.NotificationId)); }
    private void OnShortcutActivated(object? sender, string e) { if (!IsDisposed()) _ = PublishContainedAsync(_shortcutActivated, new(e), context => IsOwned(_ownedShortcuts, context, e)); }
    private void OnThemeChanged(object? sender, NeoThemeSnapshot e) { if (!IsDisposed()) _ = PublishContainedAsync(_themeChanged, new(e), null); }
    private void OnDisplaysChanged(object? sender, IReadOnlyList<NeoDisplaySnapshot> e) { if (!IsDisposed()) _ = PublishContainedAsync(_displaysChanged, new(e), null); }
    private void OnDropInbound(object? sender, NeoOwnedDropEvent e) { if (!IsDisposed()) _ = PublishContainedAsync(_dropInbound, new(e.Drop), context => e.Owner.Kind == NeoPluginOwnerKind.DocumentSession && e.Owner.Id == context.DocumentSessionId && TrackDropOwner(context)); }
    private bool TrackDropOwner(NeoRpcContext context)
    {
        var added = false; lock (_sync) { if (_disposed) return false; added = _trackedDropSessions.Add(context.DocumentSessionId); }
        if (!added) return true;
        services.DragDrop.RegisterRendererSession(context.DocumentSessionId);
        try { context.Resources.Add(new AsyncAction(() => { services.DragDrop.ReleaseOwner(NeoPluginOwner.DocumentSession(context.DocumentSessionId)); services.DragDrop.UnregisterRendererSession(context.DocumentSessionId); lock (_sync) _trackedDropSessions.Remove(context.DocumentSessionId); return ValueTask.CompletedTask; })); return true; }
        catch { services.DragDrop.UnregisterRendererSession(context.DocumentSessionId); lock (_sync) _trackedDropSessions.Remove(context.DocumentSessionId); throw; }
    }
    private bool IsDisposed() { lock (_sync) return _disposed; }
    private bool IsOwned(Dictionary<(string Session, string Id), long> values, NeoRpcContext context, string id) { lock (_sync) return values.ContainsKey((context.DocumentSessionId, id)); }
    private static async Task PublishContainedAsync<T>(NeoRpcEvent<T>? publisher, T value, Func<NeoRpcContext, bool>? recipient) where T : class
    { try { if (publisher is not null) await publisher.PublishAsync(value, recipient ?? (static _ => true)).ConfigureAwait(false); } catch { } }
    public async ValueTask DisposeAsync()
    {
        await _ownershipGate.WaitAsync().ConfigureAwait(false);
        try
        {
            (string Target, (string Session, long Generation) Owner)[] menus; ((string Session, string Id) Key, long Generation)[] tray, shortcuts, notifications; string[] sessions, trackedSessions;
            lock (_sync)
            {
                if (_disposed) return; _disposed = true;
                menus = _ownedMenus.Select(static pair => (pair.Key, pair.Value)).ToArray(); tray = _ownedTray.Select(static pair => (pair.Key, pair.Value)).ToArray(); shortcuts = _ownedShortcuts.Select(static pair => (pair.Key, pair.Value)).ToArray(); notifications = _ownedNotifications.Select(static pair => (pair.Key, pair.Value)).ToArray();
                trackedSessions = _trackedDropSessions.ToArray();
                sessions = menus.Select(static item => item.Owner.Session).Concat(tray.Select(static item => item.Key.Session)).Concat(shortcuts.Select(static item => item.Key.Session)).Concat(notifications.Select(static item => item.Key.Session)).Concat(trackedSessions).Distinct(StringComparer.Ordinal).ToArray();
            }
            foreach (var menu in menus) try { await services.Menus.RemoveMenuAsync(menu.Target).ConfigureAwait(false); } catch { }
            foreach (var item in tray) try { await services.Tray.RemoveAsync(item.Key.Id).ConfigureAwait(false); } catch { }
            foreach (var item in shortcuts) try { await services.GlobalShortcuts.UnregisterAsync(item.Key.Id).ConfigureAwait(false); } catch { }
            foreach (var item in notifications) try { await services.Notifications.RemoveAsync(item.Key.Id).ConfigureAwait(false); } catch { }
            foreach (var session in sessions) services.DragDrop.ReleaseOwner(NeoPluginOwner.DocumentSession(session));
            foreach (var session in trackedSessions) services.DragDrop.UnregisterRendererSession(session);
            lock (_sync) { _ownedMenus.Clear(); _ownedTray.Clear(); _ownedShortcuts.Clear(); _ownedNotifications.Clear(); _trackedDropSessions.Clear(); }
        }
        finally { _ownershipGate.Release(); }
        services.Tray.Activated -= OnTrayActivated;
        services.Notifications.Activated -= OnNotificationActivated;
        services.GlobalShortcuts.Activated -= OnShortcutActivated;
        services.SystemInfo.ThemeChanged -= OnThemeChanged;
        services.SystemInfo.DisplaysChanged -= OnDisplaysChanged;
        services.DragDrop.Inbound -= OnDropInbound;
        if (_dragDropRegistered) { _dragDropRegistered = false; services.DragDrop.UnregisterRenderer(); }
    }
    private static NeoWindow? Window(NeoRpcContext context) { context.TryGetWindow(out var value); return value; }
    private static NeoWindow RequiredWindow(NeoRpcContext context) => Window(context) ?? throw NeoDesktopRendererOptions.Denied("The command requires a live owner window.");
    private static void EnsureAllowed(IReadOnlySet<string> values, string value, string kind) { if (!values.Contains(value)) throw NeoDesktopRendererOptions.Denied($"The {kind} is not application-allowed."); }
    private static DesktopStatusResult Status(NeoDesktopStatus status, string? code = null) => new(status, code);
    private static NeoClipboardFormat ClipboardFormat(string value) => value switch { "text" => NeoClipboardFormat.Text, "html" => NeoClipboardFormat.Html, "image" => NeoClipboardFormat.Png, "files" => NeoClipboardFormat.FileList, _ => throw NeoDesktopRendererOptions.Denied("The clipboard format is invalid.") };
    private static NeoMenuItem MenuItem(DesktopMenuItemDto item) => item.Kind switch
    {
        NeoMenuItemKind.Command => NeoMenuItem.Command(item.Id, item.Text ?? throw new ArgumentException("Command text is required."), item.CommandId ?? throw new ArgumentException("Command ID is required."), item.Accelerator, item.Enabled, item.Checked, item.Visible),
        NeoMenuItemKind.Submenu => NeoMenuItem.Submenu(item.Id, item.Text ?? throw new ArgumentException("Submenu text is required."), item.Children.Select(MenuItem)),
        NeoMenuItemKind.Separator => NeoMenuItem.Separator(item.Id),
        NeoMenuItemKind.Role when item.Role is { } role => item.Text is { } label ? NeoMenuItem.RoleItem(item.Id, role, label) : NeoMenuItem.RoleItem(item.Id, role),
        _ => throw new ArgumentException("The menu item is malformed."),
    };
    private void ValidateMenuCommands(IReadOnlyList<DesktopMenuItemDto> items)
    {
        if (items is null) throw new ArgumentException("Menu items are required.");
        foreach (var item in items)
        {
            if (item is null) throw new ArgumentException("A menu item is required.");
            if (item.CommandId is { } command && !applicationOptions.AllowedMenuCommands.Contains(command)) throw NeoDesktopRendererOptions.Denied("A menu command is not application-allowed.");
            ValidateMenuCommands(item.Children);
        }
    }

    private sealed class AsyncAction(Func<ValueTask> action) : IAsyncDisposable { private Func<ValueTask>? _action = action; public ValueTask DisposeAsync() => Interlocked.Exchange(ref _action, null)?.Invoke() ?? ValueTask.CompletedTask; }
}

internal sealed record DesktopEmptyRequest;
internal sealed record DesktopIdRequest(string Id);
internal sealed record DesktopStatusResult(NeoDesktopStatus Status, string? Code = null);
internal sealed record DesktopBoolResult(NeoDesktopStatus Status, bool? Value, string? Code = null);
internal sealed record DesktopBytesResult(NeoDesktopStatus Status, string? Base64, string? Code = null);
internal sealed record DesktopPathResult(NeoDesktopStatus Status, string? Path, string? Code = null);
internal sealed record DesktopPathsResult(NeoDesktopStatus Status, IReadOnlyList<string>? Paths, string? Code = null);
internal sealed record DesktopMessageResult(NeoDesktopStatus Status, NeoDialogButtonRole? Button, string? Code = null);
internal sealed record DesktopThemeResult(NeoThemeSnapshot Theme);
internal sealed record DesktopDisplaysResult(IReadOnlyList<NeoDisplaySnapshot> Displays);
internal sealed record DesktopMetadataResult(NeoApplicationMetadata Metadata);
internal sealed record DesktopNotificationStatusResult(NeoNotificationPermissionStatus Status);
internal sealed record DesktopBoolValue(bool Value);
internal sealed record DesktopBoolRequest(bool Value);
internal sealed record DesktopOptionalTextRequest(string? Value);
internal sealed record DesktopFileFilterDto(string Name, IReadOnlyList<string> Extensions, IReadOnlyList<string> MimeTypes);
internal sealed record DesktopDialogRequest(string Kind, string InitialLocation, string? InitialRelativePath, IReadOnlyList<string> Extensions, string? Title, string? SuggestedFileName, bool AllowMultiple, IReadOnlyList<DesktopFileFilterDto> Filters);
internal sealed record DesktopMessageRequest(string Kind, string? Title, string Message, string? Detail, NeoDialogIcon Icon, IReadOnlyList<NeoDialogButtonRole> Buttons);
internal sealed record DesktopMenuItemDto(string Id, NeoMenuItemKind Kind, string? Text, string? CommandId, string? Accelerator, bool Enabled, bool Visible, bool Checked, NeoMenuRole? Role, IReadOnlyList<DesktopMenuItemDto> Children);
internal sealed record DesktopMenuRequest(string TargetId, IReadOnlyList<DesktopMenuItemDto> Items);
internal sealed record DesktopContextMenuRequest(string TargetId, int X, int Y);
internal sealed record DesktopTrayRequest(string Id, string? ToolTip, bool IsTemplateImage, IReadOnlyList<DesktopMenuItemDto> Menu);
internal sealed record DesktopClipboardReadRequest(string Format, string Operation);
internal sealed record DesktopClipboardWriteRequest(string Format, string Operation, [property: JsonPropertyName("base64")] byte[] Bytes);
internal sealed record DesktopClipboardClearRequest(string Format, string Operation);
internal sealed record DesktopNotificationActionDto(string Id, string Title);
internal sealed record DesktopNotificationRequestDto(string AppIdentity, string Category, string Urgency, bool Persistent, string? Payload, string Id, string Title, string Body, IReadOnlyList<DesktopNotificationActionDto> Actions);
internal sealed record DesktopShortcutRequest(string Id, string Accelerator);
internal sealed record DesktopUrlRequest(string Url);
internal record DesktopScopedPathRequest(string Root, string RelativePath, string Operation);
internal sealed record DesktopOptionalScopedPathRequest(string? Root, string? RelativePath, string Operation);
internal sealed record DesktopFileRequest(string Root, string RelativePath, string Operation, NeoOpenFileIntent Intent) : DesktopScopedPathRequest(Root, RelativePath, Operation);
internal sealed record DesktopOutboundDragItemDto(NeoDragDataKind Kind, string? Value, string? Root, string? RelativePath);
internal sealed record DesktopOutboundDragRequest(string ViewLabel, IReadOnlyList<DesktopOutboundDragItemDto> Items);
internal sealed record DesktopSecretWriteRequest(string Id, [property: JsonPropertyName("base64")] byte[] Secret);
internal sealed record DesktopDropFileRequest(string Token);
internal sealed record DesktopWindowProgressRequest(NeoWindowProgressState State, double Value);
internal sealed record DesktopWindowThemeRequest(NeoWindowTitleBarTheme Theme);
internal sealed record DesktopIdEvent(string Id);
internal sealed record DesktopNotificationActivatedEvent(string Id, string? ActionId, string? Payload);
internal sealed record DesktopThemeChangedEvent(NeoThemeSnapshot Theme);
internal sealed record DesktopDisplaysChangedEvent(IReadOnlyList<NeoDisplaySnapshot> Displays);
internal sealed record DesktopDropEvent(NeoDropEvent Drop);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(DesktopEmptyRequest))]
[JsonSerializable(typeof(DesktopIdRequest))]
[JsonSerializable(typeof(DesktopStatusResult))]
[JsonSerializable(typeof(DesktopBoolResult))]
[JsonSerializable(typeof(DesktopBytesResult))]
[JsonSerializable(typeof(DesktopPathResult))]
[JsonSerializable(typeof(DesktopPathsResult))]
[JsonSerializable(typeof(DesktopMessageResult))]
[JsonSerializable(typeof(DesktopThemeResult))]
[JsonSerializable(typeof(DesktopDisplaysResult))]
[JsonSerializable(typeof(DesktopMetadataResult))]
[JsonSerializable(typeof(DesktopNotificationStatusResult))]
[JsonSerializable(typeof(DesktopBoolRequest))]
[JsonSerializable(typeof(DesktopOptionalTextRequest))]
[JsonSerializable(typeof(DesktopDialogRequest))]
[JsonSerializable(typeof(DesktopMessageRequest))]
[JsonSerializable(typeof(DesktopMenuRequest))]
[JsonSerializable(typeof(DesktopContextMenuRequest))]
[JsonSerializable(typeof(DesktopTrayRequest))]
[JsonSerializable(typeof(DesktopClipboardReadRequest))]
[JsonSerializable(typeof(DesktopClipboardWriteRequest))]
[JsonSerializable(typeof(DesktopClipboardClearRequest))]
[JsonSerializable(typeof(DesktopNotificationRequestDto))]
[JsonSerializable(typeof(DesktopShortcutRequest))]
[JsonSerializable(typeof(DesktopUrlRequest))]
[JsonSerializable(typeof(DesktopScopedPathRequest))]
[JsonSerializable(typeof(DesktopOptionalScopedPathRequest))]
[JsonSerializable(typeof(DesktopFileRequest))]
[JsonSerializable(typeof(DesktopOutboundDragRequest))]
[JsonSerializable(typeof(DesktopSecretWriteRequest))]
[JsonSerializable(typeof(DesktopDropFileRequest))]
[JsonSerializable(typeof(DesktopWindowProgressRequest))]
[JsonSerializable(typeof(DesktopWindowThemeRequest))]
[JsonSerializable(typeof(DesktopIdEvent))]
[JsonSerializable(typeof(DesktopNotificationActivatedEvent))]
[JsonSerializable(typeof(DesktopThemeChangedEvent))]
[JsonSerializable(typeof(DesktopDisplaysChangedEvent))]
[JsonSerializable(typeof(DesktopDropEvent))]
internal sealed partial class DesktopRendererJsonContext : JsonSerializerContext;
