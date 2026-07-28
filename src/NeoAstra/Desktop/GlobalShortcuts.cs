// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoAstra.Desktop.GlobalShortcuts;

using global::NeoAstra.Desktop.Menus;

/// <summary>Presents app-owned global shortcut registrations through a platform API.</summary>
public interface INeoGlobalShortcutPresenter : IAsyncDisposable
{
    /// <summary>Gets truthful support.</summary>
    NeoCapabilityInfo Support { get; }
    /// <summary>Occurs for one native activation.</summary>
    event EventHandler<string>? Activated;
    /// <summary>Registers one exact normalized accelerator.</summary>
    NeoDesktopStatus Register(string id, string normalizedAccelerator);
    /// <summary>Unregisters one exact ID.</summary>
    bool Unregister(string id);
}

/// <summary>Owns normalized app-scoped global shortcut registrations.</summary>
public sealed class NeoGlobalShortcutService : IAsyncDisposable, INeoApplicationBoundDesktopService
{
    private readonly object _sync = new();
    private readonly Dictionary<string, string> _registrations = new(StringComparer.Ordinal);
    private readonly INeoGlobalShortcutPresenter? _presenter;
    private NeoDispatcher? _dispatcher;
    private bool _disposed;

    /// <summary>Initializes a deterministic test registry or a truthful unavailable facade.</summary>
    /// <param name="emulatedForTests">Whether activations are enabled for deterministic tests.</param>
    public NeoGlobalShortcutService(bool emulatedForTests = false)
    {
        EmulatedForTests = emulatedForTests;
        Support = emulatedForTests ? new(NeoSupportLevel.Emulated, 1, 0, "Deterministic test adapter.") : new(NeoSupportLevel.None, 1, 0, "No native global-shortcut presenter is attached.");
    }

    private NeoGlobalShortcutService(INeoGlobalShortcutPresenter presenter)
    {
        _presenter = presenter;
        Support = presenter.Support;
        presenter.Activated += OnPresenterActivated;
    }

    /// <summary>Creates the statically selected system presenter.</summary>
    public static NeoGlobalShortcutService CreateSystem(NeoDispatcher? dispatcher = null)
        => OperatingSystem.IsWindows() ? new(new WindowsGlobalShortcutPresenter(dispatcher))
            : OperatingSystem.IsMacOS() ? new(new MacGlobalShortcutPresenter(dispatcher))
            : OperatingSystem.IsLinux() ? new(new LinuxX11GlobalShortcutPresenter(dispatcher))
            : new();

    /// <summary>Gets whether deterministic fake activation is enabled.</summary>
    public bool EmulatedForTests { get; }
    /// <summary>Gets truthful support.</summary>
    public NeoCapabilityInfo Support { get; }
    /// <summary>Occurs when a trusted native/fake adapter activates a registered shortcut.</summary>
    public event EventHandler<string>? Activated;

    /// <summary>Registers one normalized accelerator, rejecting conflicts and reserved values.</summary>
    public NeoDesktopStatus Register(string id, string accelerator)
    {
        NeoMenuItem.ValidateId(id, nameof(id)); var normalized = NeoAccelerator.Normalize(accelerator);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_registrations.Count >= 128) return NeoDesktopStatus.LimitExceeded;
            if (_registrations.ContainsKey(id) || _registrations.Values.Contains(normalized, StringComparer.Ordinal)) return NeoDesktopStatus.Conflict;
            _registrations.Add(id, normalized);
        }
        NeoDesktopStatus status;
        try { status = _presenter?.Register(id, normalized) ?? (EmulatedForTests ? NeoDesktopStatus.Success : NeoDesktopStatus.Unsupported); }
        catch { lock (_sync) _registrations.Remove(id); throw; }
        if (status != NeoDesktopStatus.Success) lock (_sync) _registrations.Remove(id);
        return status;
    }

    /// <summary>Registers through the bound UI dispatcher without synchronously blocking a background caller.</summary>
    public ValueTask<NeoDesktopStatus> RegisterAsync(string id, string accelerator, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dispatcher = _dispatcher;
        if (_presenter is null || dispatcher is null || dispatcher.CheckAccess()) return ValueTask.FromResult(Register(id, accelerator));
        return dispatcher.InvokeAsync(() => Register(id, accelerator), cancellationToken);
    }

    /// <summary>Unregisters one ID.</summary>
    public bool Unregister(string id)
    {
        NeoMenuItem.ValidateId(id, nameof(id));
        string accelerator;
        lock (_sync) { if (!_registrations.Remove(id, out accelerator!)) return false; }
        if (_presenter is null) return true;
        bool removed;
        try { removed = _presenter.Unregister(id); }
        catch { lock (_sync) if (!_disposed) _registrations[id] = accelerator; throw; }
        if (!removed) lock (_sync) if (!_disposed) _registrations[id] = accelerator;
        return removed;
    }

    /// <summary>Unregisters through the bound UI dispatcher without synchronously blocking a background caller.</summary>
    public ValueTask<bool> UnregisterAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dispatcher = _dispatcher;
        if (_presenter is null || dispatcher is null || dispatcher.CheckAccess()) return ValueTask.FromResult(Unregister(id));
        return dispatcher.InvokeAsync(() => Unregister(id), cancellationToken);
    }

    /// <summary>Gets the normalized registered accelerator.</summary>
    public bool TryGet(string id, out string? accelerator) { NeoMenuItem.ValidateId(id, nameof(id)); lock (_sync) return _registrations.TryGetValue(id, out accelerator); }

    /// <summary>Activates a fake registration for tests.</summary>
    public void Activate(string id)
    {
        if (!EmulatedForTests) return;
        RaiseActivation(id);
    }

    void INeoApplicationBoundDesktopService.BindApplication(NeoApplication application)
    {
        _dispatcher = application.Dispatcher;
        if (_presenter is INeoApplicationBoundDesktopService bound) bound.BindApplication(application);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; _registrations.Clear(); }
        if (_presenter is not null)
        {
            _presenter.Activated -= OnPresenterActivated;
            await _presenter.DisposeAsync().ConfigureAwait(false);
        }
        Activated = null;
    }

    private void OnPresenterActivated(object? sender, string id) => RaiseActivation(id);

    private void RaiseActivation(string id)
    {
        NeoMenuItem.ValidateId(id, nameof(id));
        EventHandler<string>? handler;
        lock (_sync) { if (_disposed || !_registrations.ContainsKey(id)) return; handler = Activated; }
        try { handler?.Invoke(this, id); } catch { }
    }
}
