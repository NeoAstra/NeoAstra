// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoAstra.Desktop.Menus;

/// <summary>Identifies standard menu roles whose behavior is implemented by the native presenter.</summary>
/// <remarks>Role behavior never executes application-provided JavaScript. A presenter uses an OS/framework label only when that platform exposes a reliable standard label; otherwise the application must provide a localized label with <see cref="NeoMenuItem.RoleItem(string, NeoMenuRole, string)"/>.</remarks>
public enum NeoMenuRole
{
    /// <summary>Copy.</summary>
    Copy,
    /// <summary>Cut.</summary>
    Cut,
    /// <summary>Paste.</summary>
    Paste,
    /// <summary>Select all.</summary>
    SelectAll,
    /// <summary>Undo.</summary>
    Undo,
    /// <summary>Redo.</summary>
    Redo,
    /// <summary>Minimize.</summary>
    Minimize,
    /// <summary>Close window.</summary>
    CloseWindow,
    /// <summary>Quit application.</summary>
    Quit,
}

/// <summary>Identifies a menu item kind.</summary>
public enum NeoMenuItemKind
{
    /// <summary>Backend command.</summary>
    Command,
    /// <summary>Submenu.</summary>
    Submenu,
    /// <summary>Separator.</summary>
    Separator,
    /// <summary>Native standard role.</summary>
    Role,
}

/// <summary>Describes one immutable, stable-ID native menu item.</summary>
public sealed record NeoMenuItem
{
    private NeoMenuItem(string id, NeoMenuItemKind kind, string? text, string? commandId, string? accelerator, bool enabled, bool visible, bool isChecked, NeoMenuRole? role, IReadOnlyList<NeoMenuItem> children)
    {
        ValidateId(id, nameof(id));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (text is { } display) ValidateText(display, nameof(text));
        Id = id; Kind = kind; Text = text; CommandId = commandId; Accelerator = accelerator; IsEnabled = enabled; IsVisible = visible; IsChecked = isChecked; Role = role; Children = children;
    }

    /// <summary>Gets the stable item ID.</summary>
    public string Id { get; }
    /// <summary>Gets the item kind.</summary>
    public NeoMenuItemKind Kind { get; }
    /// <summary>Gets application-provided text, including an explicit application-localized role label, or <see langword="null"/> when native label localization was requested.</summary>
    public string? Text { get; }
    /// <summary>Gets the shared backend command ID.</summary>
    public string? CommandId { get; }
    /// <summary>Gets the normalized accelerator.</summary>
    public string? Accelerator { get; }
    /// <summary>Gets whether the item is enabled.</summary>
    public bool IsEnabled { get; }
    /// <summary>Gets whether the item is visible.</summary>
    public bool IsVisible { get; }
    /// <summary>Gets whether the item is checked.</summary>
    public bool IsChecked { get; }
    /// <summary>Gets the native role.</summary>
    public NeoMenuRole? Role { get; }
    /// <summary>Gets immutable submenu children.</summary>
    public IReadOnlyList<NeoMenuItem> Children { get; }

    /// <summary>Creates a shared backend command item.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/>, <paramref name="text"/>, <paramref name="commandId"/>, or <paramref name="accelerator"/> is malformed.</exception>
    public static NeoMenuItem Command(string id, string text, string commandId, string? accelerator = null, bool enabled = true, bool isChecked = false, bool visible = true)
    {
        ValidateText(text, nameof(text));
        ValidateId(commandId, nameof(commandId));
        var normalized = accelerator is null ? null : NeoAccelerator.Normalize(accelerator);
        return new(id, NeoMenuItemKind.Command, text, commandId, normalized, enabled, visible, isChecked, null, Array.Empty<NeoMenuItem>());
    }

    /// <summary>Creates a submenu.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> or <paramref name="children"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/>, <paramref name="text"/>, or <paramref name="children"/> is malformed.</exception>
    public static NeoMenuItem Submenu(string id, string text, IEnumerable<NeoMenuItem> children)
    {
        ValidateText(text, nameof(text));
        ArgumentNullException.ThrowIfNull(children);
        var values = children.Take(257).ToArray();
        if (values.Length is < 1 or > 256 || values.Any(static child => child is null)) throw new ArgumentException("A submenu requires 1 to 256 children.", nameof(children));
        ValidateTree(values);
        return new(id, NeoMenuItemKind.Submenu, text, null, null, true, true, false, null, Array.AsReadOnly(values));
    }

    /// <summary>Creates a separator.</summary>
    public static NeoMenuItem Separator(string id) => new(id, NeoMenuItemKind.Separator, null, null, null, false, true, false, null, Array.Empty<NeoMenuItem>());

    /// <summary>Creates a standard role that requests a label from reliable OS/framework resources.</summary>
    /// <remarks>A platform presenter that has no reliable standard label for <paramref name="role"/> rejects the item with <see cref="NotSupportedException"/>. Use <see cref="RoleItem(string, NeoMenuRole, string)"/> for portable localization.</remarks>
    public static NeoMenuItem RoleItem(string id, NeoMenuRole role)
    {
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        return new(id, NeoMenuItemKind.Role, null, null, null, true, true, false, role, Array.Empty<NeoMenuItem>());
    }

    /// <summary>Creates a standard native role with an application-localized display label.</summary>
    /// <param name="id">The stable item ID.</param>
    /// <param name="role">The native behavior to invoke.</param>
    /// <param name="localizedText">The localized Unicode label supplied by the application.</param>
    /// <returns>The immutable role item.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="localizedText"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> or <paramref name="localizedText"/> is malformed.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="role"/> is unknown.</exception>
    public static NeoMenuItem RoleItem(string id, NeoMenuRole role, string localizedText)
    {
        ValidateText(localizedText, nameof(localizedText));
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        return new(id, NeoMenuItemKind.Role, localizedText, null, null, true, true, false, role, Array.Empty<NeoMenuItem>());
    }

    internal static void ValidateTree(IReadOnlyList<NeoMenuItem> items)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var accelerators = new Dictionary<string, string>(StringComparer.Ordinal);
        var count = 0;
        Visit(items, 1);
        return;
        void Visit(IReadOnlyList<NeoMenuItem> values, int depth)
        {
            if (depth > 16) throw new ArgumentException("A menu may be at most 16 levels deep.", nameof(items));
            foreach (var item in values)
            {
                if (++count > 4096) throw new ArgumentException("A menu may contain at most 4096 items.", nameof(items));
                if (!ids.Add(item.Id)) throw new ArgumentException($"Menu item ID '{item.Id}' is duplicated.", nameof(items));
                if (item.Accelerator is { } accelerator && !accelerators.TryAdd(accelerator, item.Id)) throw new ArgumentException($"Accelerator '{accelerator}' conflicts between '{accelerators[accelerator]}' and '{item.Id}'.", nameof(items));
                Visit(item.Children, depth + 1);
            }
        }
    }

    internal static void ValidateId(string value, string parameterName)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || value.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':'))) throw new ArgumentException("A menu or command ID is malformed.", parameterName);
    }

    private static void ValidateText(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length is < 1 or > 512 || value.Any(static character => character == '\0')) throw new ArgumentException("Menu text is malformed.", parameterName);
    }
}

/// <summary>Validates and normalizes portable keyboard accelerators.</summary>
public static class NeoAccelerator
{
    /// <summary>Normalizes an accelerator such as <c>Ctrl+Shift+P</c>.</summary>
    /// <param name="accelerator">The portable accelerator.</param>
    /// <returns>The normalized accelerator.</returns>
    /// <exception cref="ArgumentException">The accelerator is malformed, duplicated, or reserved.</exception>
    public static string Normalize(string accelerator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accelerator);
        if (accelerator.Length > 64 || accelerator.Any(char.IsControl)) throw new ArgumentException("An accelerator is malformed.", nameof(accelerator));
        var parts = accelerator.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 5) throw new ArgumentException("An accelerator must contain one key and at most four modifiers.", nameof(accelerator));
        var modifiers = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < parts.Length - 1; index++)
        {
            var modifier = parts[index].ToLowerInvariant() switch { "ctrl" or "control" => "Ctrl", "alt" or "option" => "Alt", "shift" => "Shift", "meta" or "cmd" or "command" or "win" => "Meta", _ => throw new ArgumentException("An accelerator modifier is unknown.", nameof(accelerator)) };
            if (!modifiers.Add(modifier)) throw new ArgumentException("An accelerator modifier is duplicated.", nameof(accelerator));
        }
        var key = parts[^1];
        if (key.Length > 16 || key.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ))) throw new ArgumentException("An accelerator key is invalid.", nameof(accelerator));
        key = key.Length == 1 ? key.ToUpperInvariant() : key.ToUpperInvariant() switch { "ESC" => "Escape", "DEL" => "Delete", var value => char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant() };
        var ordered = new[] { "Ctrl", "Alt", "Shift", "Meta" }.Where(modifiers.Contains).Append(key);
        var result = string.Join('+', ordered);
        if (result is "Alt+F4" or "Ctrl+Alt+Delete" or "Meta+L" or "Meta+Tab") throw new ArgumentException("The accelerator is reserved by the operating system.", nameof(accelerator));
        return result;
    }
}

/// <summary>Routes native command activation to explicit backend handlers in arrival order.</summary>
public sealed class NeoCommandService : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Func<CancellationToken, ValueTask>> _handlers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _activation = new(1, 1);
    private bool _disposed;

    /// <summary>Registers one exact backend command.</summary>
    public void Register(string commandId, Func<CancellationToken, ValueTask> handler)
    {
        NeoMenuItem.ValidateId(commandId, nameof(commandId));
        ArgumentNullException.ThrowIfNull(handler);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_handlers.Count >= 4096) throw new InvalidOperationException("The command limit was reached.");
            if (!_handlers.TryAdd(commandId, handler)) throw new ArgumentException($"Command '{commandId}' is already registered.", nameof(commandId));
        }
    }

    /// <summary>Invokes one exact backend command without evaluating JavaScript.</summary>
    public async ValueTask<NeoDesktopStatus> ActivateAsync(string commandId, CancellationToken cancellationToken = default)
    {
        NeoMenuItem.ValidateId(commandId, nameof(commandId));
        Func<CancellationToken, ValueTask>? handler;
        lock (_sync) { if (_disposed) return NeoDesktopStatus.Canceled; _handlers.TryGetValue(commandId, out handler); }
        if (handler is null) return NeoDesktopStatus.NotFound;
        await _activation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync) { if (_disposed) return NeoDesktopStatus.Canceled; }
            await handler(cancellationToken).ConfigureAwait(false);
            return NeoDesktopStatus.Success;
        }
        finally { _activation.Release(); }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; }
        await _activation.WaitAsync().ConfigureAwait(false);
        try { lock (_sync) _handlers.Clear(); }
        finally { _activation.Release(); }
    }
}

/// <summary>Presents immutable menu snapshots. Every method is invoked on the configured UI dispatcher.</summary>
public interface INeoMenuPresenter : IAsyncDisposable
{
    /// <summary>Gets truthful native support.</summary>
    NeoCapabilityInfo Support { get; }
    /// <summary>Creates or updates one application, window, or context menu.</summary>
    void SetMenu(string targetId, IReadOnlyList<NeoMenuItem> items);
    /// <summary>Removes one menu.</summary>
    void RemoveMenu(string targetId);
    /// <summary>Displays one context menu at a client-coordinate point.</summary>
    NeoDesktopStatus ShowContextMenu(string targetId, NeoPoint position);
}

/// <summary>Stores application, window, and context menu descriptors through one UI dispatcher.</summary>
public sealed class NeoMenuService : IAsyncDisposable, INeoApplicationBoundDesktopService
{
    private readonly object _sync = new();
    private NeoDispatcher? _dispatcher;
    private NeoApplication? _application;
    private readonly INeoMenuPresenter? _presenter;
    private readonly Dictionary<string, IReadOnlyList<NeoMenuItem>> _menus = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Action> _ownerDetach = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Initializes a fake/backend-only service, optionally marshaled to a UI dispatcher.</summary>
    public NeoMenuService(NeoCommandService commands, NeoDispatcher? dispatcher = null, INeoMenuPresenter? presenter = null)
    {
        ArgumentNullException.ThrowIfNull(commands);
        Commands = commands;
        _dispatcher = dispatcher;
        _presenter = presenter;
    }

    /// <summary>Gets shared command routing.</summary>
    public NeoCommandService Commands { get; }

    void INeoApplicationBoundDesktopService.BindApplication(NeoApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (_dispatcher is not null && !ReferenceEquals(_dispatcher, application.Dispatcher)) throw new InvalidOperationException("The menu service is already bound to another dispatcher.");
        if (_application is not null && !ReferenceEquals(_application, application)) throw new InvalidOperationException("The menu service is already bound to another application.");
        _application = application; _dispatcher = application.Dispatcher;
        if (_presenter is INeoApplicationBoundDesktopService bound) bound.BindApplication(application);
    }

    /// <summary>Gets truthful adapter support. Descriptor/fake behavior does not claim native menu support.</summary>
    public NeoCapabilityInfo Support => _presenter?.Support ?? new(_dispatcher is null ? NeoSupportLevel.Emulated : NeoSupportLevel.Limited, 1, 0, _dispatcher is null ? "Descriptor/test service; no native menu is created." : "Updates are UI-dispatched; no native menu presenter is attached.");

    /// <summary>Sets an application, window, or context menu by stable target ID.</summary>
    public ValueTask SetMenuAsync(string targetId, IEnumerable<NeoMenuItem> items, CancellationToken cancellationToken = default)
    {
        NeoMenuItem.ValidateId(targetId, nameof(targetId));
        ArgumentNullException.ThrowIfNull(items);
        var values = items.Take(4097).ToArray();
        if (values.Length is < 1 or > 4096) throw new ArgumentException("A menu requires 1 to 4096 items.", nameof(items));
        NeoMenuItem.ValidateTree(values);
        void Update()
        {
            var snapshot = Array.AsReadOnly(values);
            IReadOnlyList<NeoMenuItem>? previous;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _menus.TryGetValue(targetId, out previous);
                _menus[targetId] = snapshot;
                if (previous is null) AttachOwner(targetId);
            }
            try { _presenter?.SetMenu(targetId, snapshot); }
            catch { lock (_sync) { if (previous is null) { _menus.Remove(targetId); DetachOwner(targetId); } else _menus[targetId] = previous; } throw; }
        }
        if (_dispatcher is not null && !_dispatcher.CheckAccess()) return _dispatcher.InvokeAsync(Update, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested(); Update(); return ValueTask.CompletedTask;
    }

    /// <summary>Gets an immutable snapshot for diagnostics or diffing.</summary>
    public IReadOnlyList<NeoMenuItem> GetMenu(string targetId) { NeoMenuItem.ValidateId(targetId, nameof(targetId)); lock (_sync) return _menus.TryGetValue(targetId, out var value) ? value : Array.Empty<NeoMenuItem>(); }

    /// <summary>Removes one application, window, or context menu descriptor.</summary>
    public bool RemoveMenu(string targetId)
    {
        NeoMenuItem.ValidateId(targetId, nameof(targetId));
        bool Remove()
        {
            IReadOnlyList<NeoMenuItem>? previous;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_menus.Remove(targetId, out previous)) return false;
                DetachOwner(targetId);
            }
            try { _presenter?.RemoveMenu(targetId); return true; }
            catch { lock (_sync) if (!_disposed) { _menus[targetId] = previous; AttachOwner(targetId); } throw; }
        }
        if (_dispatcher is not null && !_dispatcher.CheckAccess()) throw new InvalidOperationException("Synchronous menu removal requires the configured UI dispatcher. Use RemoveMenuAsync from background code.");
        return Remove();
    }

    /// <summary>Removes one menu without blocking the caller while it is queued to the UI dispatcher.</summary>
    public ValueTask<bool> RemoveMenuAsync(string targetId, CancellationToken cancellationToken = default)
    {
        NeoMenuItem.ValidateId(targetId, nameof(targetId));
        bool Remove()
        {
            IReadOnlyList<NeoMenuItem>? previous;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_menus.Remove(targetId, out previous)) return false;
                DetachOwner(targetId);
            }
            try { _presenter?.RemoveMenu(targetId); return true; }
            catch { lock (_sync) if (!_disposed) { _menus[targetId] = previous; AttachOwner(targetId); } throw; }
        }
        if (_dispatcher is not null && !_dispatcher.CheckAccess()) return _dispatcher.InvokeAsync(Remove, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Remove());
    }

    /// <summary>Displays a previously configured <c>context:&lt;view-label&gt;</c> menu at client coordinates.</summary>
    public ValueTask<NeoDesktopStatus> ShowContextMenuAsync(string targetId, NeoPoint position, CancellationToken cancellationToken = default)
    {
        NeoMenuItem.ValidateId(targetId, nameof(targetId));
        if (_presenter is null) return ValueTask.FromResult(NeoDesktopStatus.Unsupported);
        NeoDesktopStatus Show() { lock (_sync) { ObjectDisposedException.ThrowIf(_disposed, this); if (!_menus.ContainsKey(targetId)) return NeoDesktopStatus.NotFound; } return _presenter.ShowContextMenu(targetId, position); }
        if (_dispatcher is not null && !_dispatcher.CheckAccess()) return _dispatcher.InvokeAsync(Show, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Show());
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        string[] targets;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            targets = _menus.Keys.ToArray();
            foreach (var detach in _ownerDetach.Values) detach();
            _ownerDetach.Clear();
        }

        void RemoveAll()
        {
            foreach (var target in targets)
            {
                try { _presenter?.RemoveMenu(target); } catch { }
            }
            lock (_sync) _menus.Clear();
        }

        if (_dispatcher is not null && !_dispatcher.CheckAccess()) await _dispatcher.InvokeAsync(RemoveAll).ConfigureAwait(false);
        else RemoveAll();
        if (_presenter is not null) await _presenter.DisposeAsync().ConfigureAwait(false);
    }

    private void AttachOwner(string targetId)
    {
        if (_application is null || _ownerDetach.ContainsKey(targetId)) return;
        if (targetId.StartsWith("window:", StringComparison.Ordinal) && _application.TryGetWindow(targetId[7..], out var window) && window is not null)
        {
            EventHandler handler = (_, _) => OwnerDestroyed(targetId); window.Closed += handler; _ownerDetach[targetId] = () => window.Closed -= handler;
        }
        else if (targetId.StartsWith("context:", StringComparison.Ordinal) && _application.TryGetView(targetId[8..], out var view) && view is not null)
        {
            void Handler() => OwnerDestroyed(targetId); EventHandler windowClosed = (_, _) => OwnerDestroyed(targetId); view.Disposing += Handler; if (view.OwnedWindow is not null) view.OwnedWindow.Closed += windowClosed; _ownerDetach[targetId] = () => { view.Disposing -= Handler; if (view.OwnedWindow is not null) view.OwnedWindow.Closed -= windowClosed; };
        }
    }

    private void DetachOwner(string targetId) { if (_ownerDetach.Remove(targetId, out var detach)) detach(); }

    private void OwnerDestroyed(string targetId)
    {
        lock (_sync) { if (!_menus.Remove(targetId)) return; DetachOwner(targetId); }
        try { _presenter?.RemoveMenu(targetId); } catch { }
    }
}
