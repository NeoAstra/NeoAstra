// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using global::NeoAstra.Desktop.Menus;

namespace NeoAstra.Desktop.Tray;

/// <summary>Describes one trusted application-owned tray/status item.</summary>
public sealed record NeoTrayItemOptions
{
    /// <summary>Gets the stable item ID.</summary>
    public required string Id { get; init; }
    /// <summary>Gets bounded localized tooltip text.</summary>
    public string? ToolTip { get; init; }
    /// <summary>Gets an application-controlled absolute icon path.</summary>
    public string? IconPath { get; init; }
    /// <summary>Gets whether macOS may treat the icon as a template image.</summary>
    public bool IsTemplateImage { get; init; }
    /// <summary>Gets the native menu descriptor.</summary>
    public IReadOnlyList<NeoMenuItem> Menu { get; init; } = Array.Empty<NeoMenuItem>();

    internal void Validate()
    {
        NeoMenuItem.ValidateId(Id, nameof(Id));
        if (ToolTip is { } tooltip && (tooltip.Length > 512 || tooltip.Any(static c => c == '\0'))) throw new ArgumentException("A tray tooltip is malformed.", nameof(ToolTip));
        if (IconPath is { } icon && (!Path.IsPathFullyQualified(icon) || icon.Length > 32_768 || icon.Any(char.IsControl))) throw new ArgumentException("A tray icon path must be a bounded absolute path.", nameof(IconPath));
        if (Menu.Count != 0) NeoMenuItem.ValidateTree(Menu);
    }
}

/// <summary>Represents one ordered tray activation.</summary>
/// <param name="ItemId">Stable tray item ID.</param>
/// <param name="Secondary">Whether the secondary activation occurred.</param>
/// <param name="Sequence">Monotonic process-local sequence.</param>
public sealed record NeoTrayActivation(string ItemId, bool Secondary, ulong Sequence);

/// <summary>Presents application-owned native tray or status items.</summary>
public interface INeoTrayPresenter : IAsyncDisposable
{
    /// <summary>Gets truthful native support.</summary>
    NeoCapabilityInfo Support { get; }
    /// <summary>Occurs when native primary or secondary activation arrives.</summary>
    event Action<string, bool>? Activated;
    /// <summary>Creates or atomically updates an item.</summary>
    void Set(NeoTrayItemOptions options);
    /// <summary>Removes an item.</summary>
    bool Remove(string id);
}

/// <summary>Owns bounded tray/status descriptors and deterministic activation ordering.</summary>
public sealed class NeoTrayService : IAsyncDisposable, INeoApplicationBoundDesktopService
{
    private readonly object _sync = new();
    private readonly Dictionary<string, NeoTrayItemOptions> _items = new(StringComparer.Ordinal);
    private readonly INeoTrayPresenter? _presenter;
    private NeoDispatcher? _dispatcher;
    private ulong _sequence;
    private bool _disposed;

    /// <summary>Initializes a tray service with an optional native presenter.</summary>
    public NeoTrayService(NeoDispatcher? dispatcher = null, INeoTrayPresenter? presenter = null)
    {
        _dispatcher = dispatcher; _presenter = presenter; if (_presenter is not null) _presenter.Activated += OnPresenterActivated;
    }

    /// <summary>Gets truthful support.</summary>
    public NeoCapabilityInfo Support => _presenter?.Support ?? new(NeoSupportLevel.Emulated, 1, 0, "Trusted descriptor/fake service; no native presenter attached.");

    /// <summary>Occurs in native arrival order for primary/secondary activation.</summary>
    public event EventHandler<NeoTrayActivation>? Activated;

    /// <summary>Creates or updates one trusted item.</summary>
    /// <exception cref="NotSupportedException">The selected presenter cannot honor a requested platform-specific policy such as template-image rendering.</exception>
    public void Set(NeoTrayItemOptions options)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        var snapshot = options with { Menu = Array.AsReadOnly(options.Menu.ToArray()) };
        void Update()
        {
            NeoTrayItemOptions? previous; lock (_sync) { ObjectDisposedException.ThrowIf(_disposed, this); if (!_items.TryGetValue(options.Id, out previous) && _items.Count >= 64) throw new InvalidOperationException("At most 64 tray items may exist."); _items[options.Id] = snapshot; }
            try { _presenter?.Set(snapshot); }
            catch { lock (_sync) { if (previous is null) _items.Remove(options.Id); else _items[options.Id] = previous; } throw; }
        }
        if (_dispatcher is not null && !_dispatcher.CheckAccess()) throw new InvalidOperationException("Synchronous tray mutation requires the NeoAstra UI dispatcher. Use SetAsync from background code."); Update();
    }

    /// <summary>Creates or updates one item through the UI dispatcher.</summary>
    /// <exception cref="NotSupportedException">The selected presenter cannot honor a requested platform-specific policy such as template-image rendering.</exception>
    public ValueTask SetAsync(NeoTrayItemOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        if (_dispatcher is not null && !_dispatcher.CheckAccess()) return _dispatcher.InvokeAsync(() => Set(options), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested(); Set(options); return ValueTask.CompletedTask;
    }

    /// <summary>Removes one item.</summary>
    public bool Remove(string id)
    {
        NeoMenuItem.ValidateId(id, nameof(id)); if (_dispatcher is not null && !_dispatcher.CheckAccess()) throw new InvalidOperationException("Synchronous tray removal requires the NeoAstra UI dispatcher. Use RemoveAsync from background code.");
        NeoTrayItemOptions? previous; lock (_sync) if (!_items.TryGetValue(id, out previous)) return false;
        try { if (_presenter is not null && !_presenter.Remove(id)) return false; lock (_sync) _items.Remove(id); return true; }
        catch { throw; }
    }

    /// <summary>Removes one item through the UI dispatcher.</summary>
    public ValueTask<bool> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        NeoMenuItem.ValidateId(id, nameof(id)); if (_dispatcher is not null && !_dispatcher.CheckAccess()) return _dispatcher.InvokeAsync(() => Remove(id), cancellationToken); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Remove(id));
    }

    /// <summary>Raises a trusted adapter activation for contract tests/platform presenters.</summary>
    public void Activate(string id, bool secondary = false)
    {
        NeoMenuActivation(id);
        NeoTrayActivation activation;
        lock (_sync) { if (_disposed || !_items.ContainsKey(id)) return; activation = new(id, secondary, ++_sequence); }
        try { Activated?.Invoke(this, activation); } catch { }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        void Clear() { lock (_sync) { if (_disposed) return; _disposed = true; } foreach (var id in _items.Keys.ToArray()) try { _presenter?.Remove(id); } catch { } lock (_sync) { _items.Clear(); Activated = null; } }
        if (_dispatcher is not null && !_dispatcher.CheckAccess()) await _dispatcher.InvokeAsync(Clear).ConfigureAwait(false); else Clear();
        if (_presenter is not null) { _presenter.Activated -= OnPresenterActivated; await _presenter.DisposeAsync().ConfigureAwait(false); }
    }

    void INeoApplicationBoundDesktopService.BindApplication(NeoApplication application)
    {
        ArgumentNullException.ThrowIfNull(application); if (_dispatcher is not null && !ReferenceEquals(_dispatcher, application.Dispatcher)) throw new InvalidOperationException("The tray service is already bound to another dispatcher."); _dispatcher = application.Dispatcher; if (_presenter is INeoApplicationBoundDesktopService bound) bound.BindApplication(application);
    }

    private void OnPresenterActivated(string id, bool secondary) => Activate(id, secondary);

    private static void NeoMenuActivation(string id) => NeoMenuItem.ValidateId(id, nameof(id));
}
