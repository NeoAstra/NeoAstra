// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NeoAstra.Desktop.WindowState;

/// <summary>Identifies portable taskbar or dock progress states.</summary>
public enum NeoWindowProgressState
{
    /// <summary>Hide progress.</summary>
    None,
    /// <summary>Show ordinary progress.</summary>
    Normal,
    /// <summary>Show paused progress.</summary>
    Paused,
    /// <summary>Show error progress.</summary>
    Error,
    /// <summary>Show indeterminate progress.</summary>
    Indeterminate,
}

/// <summary>Identifies portable title-bar appearance intent.</summary>
public enum NeoWindowTitleBarTheme
{
    /// <summary>Use system appearance.</summary>
    System,
    /// <summary>Request light appearance.</summary>
    Light,
    /// <summary>Request dark appearance.</summary>
    Dark,
}

/// <summary>Provides capability-gated platform window polish using only borrowed native handles on the UI dispatcher.</summary>
public sealed partial class NeoWindowPolishService : IAsyncDisposable
{
    internal static readonly Guid TaskbarList3InterfaceId = new("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF");
    private readonly Dictionary<NeoWindow, WindowIconEntry> _windowsIcons = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<NeoWindow, EventHandler> _linuxAttention = new(ReferenceEqualityComparer.Instance);
    private readonly object _sync = new();
    private readonly string? _linuxApplicationId;
    private LinuxLauncherEntry? _launcher;
    private bool _disposed;

    internal string? LinuxApplicationId => _linuxApplicationId;

    /// <summary>Initializes the service. On Linux, the badge, progress, and attention go to the launcher entry of the desktop file named after the GLib program name, or else after the executable.</summary>
    public NeoWindowPolishService() : this(null) { }

    // On Linux the launcher entry is the one of the desktop file named after the GLib program name that the application set, or
    // else after the application ID, as the desktop-entry of its notifications (see LinuxDesktopEntry).
    internal NeoWindowPolishService(string? applicationId)
    {
        _linuxApplicationId = LinuxDesktopEntry.Name(null, applicationId) ?? LinuxDesktopEntry.Name(null, Path.GetFileNameWithoutExtension(Environment.ProcessPath));
        var docks = $"shown by the docks that read com.canonical.Unity.LauncherEntry (KDE Plasma, Ubuntu Dock and Dash to Dock, Plank, Latte; not stock GNOME) on the icon of the application, only when its desktop file is installed and matches its windows: the one named after the GLib program name that the application sets with g_set_prgname before GTK opens a window, else {_linuxApplicationId ?? "<application ID>"}.desktop";
        AttentionSupport = OperatingSystem.IsLinux()
            ? new(NeoSupportLevel.Limited, 1, 0, $"Application-scoped Unity launcher-entry urgency, {docks}. It lasts until the window that asked is activated or closed; nothing is asked for a focused window, and critical requests are not distinct. GTK4 removed per-window urgency hints.")
            : OperatingSystem.IsMacOS()
                ? new(NeoSupportLevel.Limited, 1, 0, "Cocoa user-attention requests are native but application-scoped rather than per-window.")
                : OperatingSystem.IsWindows()
                    ? new(NeoSupportLevel.Native, 1, 0, "Native Win32 taskbar and caption flashing.")
                    : new(NeoSupportLevel.None, 1, 0, "No supported desktop window backend.");
        ProgressSupport = OperatingSystem.IsWindows()
            ? new(NeoSupportLevel.Native, 1, 0, "Windows ITaskbarList3 progress state/value.")
            : OperatingSystem.IsLinux()
                ? new(NeoSupportLevel.Limited, 1, 0, $"Application-scoped Unity launcher-entry progress, {docks}. Paused and error progress look like normal progress, and indeterminate progress is unsupported.")
                : new(NeoSupportLevel.None, 1, 0, "The target desktop does not expose one reliable portable per-window taskbar/dock progress API.");
        BadgeSupport = OperatingSystem.IsMacOS()
            ? new(NeoSupportLevel.Native, 1, 0, "Native application dock-tile badge; macOS badges are application-scoped rather than window-scoped.")
            : OperatingSystem.IsWindows()
                ? new(NeoSupportLevel.Limited, 1, 0, "Overlay icon of the taskbar button of the window (ITaskbarList3::SetOverlayIcon): a red dot with at most two characters, so a number above 99 (or one that ends with a plus) shows 9+ and longer text keeps its first two characters. A taskbar button that groups several windows shows one overlay.")
                : OperatingSystem.IsLinux()
                    ? new(NeoSupportLevel.Limited, 1, 0, $"Application-scoped Unity launcher-entry count, {docks}. Numbers only: other text is unsupported, and 99+ shows 99.")
                    : new(NeoSupportLevel.None, 1, 0, "No reliable built-in per-window text badge semantic exists on this target.");
    }

    /// <summary>Gets icon support.</summary>
    public NeoCapabilityInfo IconSupport { get; } = OperatingSystem.IsWindows()
        ? new(NeoSupportLevel.Native, 1, 0, "Native Win32 window icons use copied application-controlled .ico files at the sizes of the scale of the window's display.")
        : OperatingSystem.IsMacOS()
            ? new(NeoSupportLevel.Limited, 1, 0, "Cocoa exposes an application-scoped icon rather than a per-window icon.")
            : OperatingSystem.IsLinux()
                ? new(NeoSupportLevel.None, 1, 0, "GTK4 removed per-window file icons; Linux applications should provide desktop-entry icon metadata.")
                : new(NeoSupportLevel.None, 1, 0, "No supported desktop window backend.");
    /// <summary>Gets attention support.</summary>
    public NeoCapabilityInfo AttentionSupport { get; }
    /// <summary>Gets explicit window-enabled support.</summary>
    public NeoCapabilityInfo EnabledSupport { get; } = OperatingSystem.IsWindows() || OperatingSystem.IsLinux() ? new(NeoSupportLevel.Native, 1, 0, "Native window input enablement without a nested application loop.") : new(NeoSupportLevel.None, 1, 0, "Cocoa does not expose a correct general disabled-window semantic; owned modal sheets are provided by the core window owner relationship.");
    /// <summary>Gets taskbar/dock progress support.</summary>
    public NeoCapabilityInfo ProgressSupport { get; }
    /// <summary>Gets app badge support.</summary>
    public NeoCapabilityInfo BadgeSupport { get; }
    /// <summary>Gets represented-file/document-edited support.</summary>
    public NeoCapabilityInfo DocumentSupport { get; } = OperatingSystem.IsMacOS() ? new(NeoSupportLevel.Native, 1, 0, "Native NSWindow represented filename and document-edited indicator.") : new(NeoSupportLevel.None, 1, 0, "The target does not expose macOS represented-document title-bar semantics.");
    /// <summary>Gets capture-protection support.</summary>
    public NeoCapabilityInfo ContentProtectionSupport { get; } = OperatingSystem.IsWindows() ? new(NeoSupportLevel.Limited, 1, 0, "SetWindowDisplayAffinity excludes supported desktop capture paths but cannot prevent cameras, privileged software, or every capture stack.") : OperatingSystem.IsMacOS() ? new(NeoSupportLevel.Limited, 1, 0, "NSWindow sharing type restricts ordinary window sharing but cannot prevent cameras, privileged software, or every capture stack.") : new(NeoSupportLevel.None, 1, 0, "GTK/Wayland has no portable meaningful per-window capture exclusion contract.");
    /// <summary>Gets title-bar theme support.</summary>
    public NeoCapabilityInfo TitleBarThemeSupport { get; } = OperatingSystem.IsWindows() ? new(NeoSupportLevel.Limited, 1, 0, "DWM immersive dark-mode title-bar intent on supported Windows versions.") : new(NeoSupportLevel.None, 1, 0, "No safe portable title-bar-only theme override is exposed on this target.");

    /// <summary>Sets a copied native window icon from an existing absolute .ico/.png path.</summary>
    /// <param name="window">The window whose icon is set.</param>
    /// <param name="path">The full path of an existing .ico or .png file.</param>
    /// <param name="cancellationToken">Cancels the request before it reaches the UI thread.</param>
    /// <returns>The status of the request: <see cref="NeoDesktopStatus.Denied"/> for a path that is not a full path of an existing .ico/.png file, <see cref="NeoDesktopStatus.Unsupported"/> for a format or platform without that semantic.</returns>
    /// <remarks>
    /// On Windows only .ico files are accepted. The window gets a big icon, for the taskbar and Alt+Tab, at the <c>SM_CXICON</c> size of the
    /// scale of its display, and a small one, for its title bar, at the <c>SM_CXSMICON</c> size: each is the picture of the file that suits
    /// that size best. Both are loaded again from the file when the window moves to a display of another scale, and destroyed when the window
    /// closes or the service is disposed. Without a call, a Windows window shows the icon of its executable (for a .NET application,
    /// its <c>ApplicationIcon</c>) at the same sizes, and shows it again when the service takes its icons away.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="window"/> or <paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or white space.</exception>
    /// <exception cref="ObjectDisposedException">The service is disposed.</exception>
    public ValueTask<NeoDesktopStatus> SetIconAsync(NeoWindow window, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window); ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || path.Any(char.IsControl) || !new[] { ".ico", ".png" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) return ValueTask.FromResult(NeoDesktopStatus.Denied);
        return Dispatch(window, () => SetIcon(window, Path.GetFullPath(path)), cancellationToken);
    }

    /// <summary>Requests bounded user attention without activating arbitrary applications.</summary>
    public ValueTask<NeoDesktopStatus> RequestAttentionAsync(NeoWindow window, bool critical = false, CancellationToken cancellationToken = default)
        => Dispatch(window, () => RequestAttention(window, critical), cancellationToken);

    /// <summary>Enables or disables native window input where the platform provides that semantic.</summary>
    public ValueTask<NeoDesktopStatus> SetEnabledAsync(NeoWindow window, bool enabled, CancellationToken cancellationToken = default)
        => Dispatch(window, () => SetEnabled(window, enabled), cancellationToken);

    /// <summary>Sets native taskbar progress. Value must be finite in [0,1].</summary>
    public ValueTask<NeoDesktopStatus> SetProgressAsync(NeoWindow window, NeoWindowProgressState state, double value, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(state) || !double.IsFinite(value) || value is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(value));
        return Dispatch(window, () => SetProgress(window, state, value), cancellationToken);
    }

    /// <summary>Sets or clears the application dock badge where the OS provides that semantic.</summary>
    /// <param name="window">The window whose badge is set: on Windows the badge is the one of its taskbar button, elsewhere the one of the application.</param>
    /// <param name="badge">The text of the badge, or <see langword="null"/> (or empty) to clear it.</param>
    /// <param name="cancellationToken">Cancels the request before it reaches the UI thread.</param>
    /// <returns>The status of the request: <see cref="NeoDesktopStatus.Unsupported"/> for a platform without a badge, or on Linux for text that is not a number.</returns>
    /// <remarks>
    /// macOS shows the text in the badge of the Dock tile. Windows draws it on a red dot in the overlay icon of the taskbar button of
    /// the window, which holds two characters: a number of one or two digits as it is, a larger one (or one that ends with a plus, as
    /// <c>99+</c>) as <c>9+</c>, and other text cut to its first two characters. Linux sends the number as the count of the Unity
    /// launcher entry of the application (<c>99+</c> gives 99), which only some docks show; see <see cref="BadgeSupport"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="window"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="badge"/> is longer than 16 characters or has control characters.</exception>
    /// <exception cref="ObjectDisposedException">The service is disposed.</exception>
    public ValueTask<NeoDesktopStatus> SetBadgeAsync(NeoWindow window, string? badge, CancellationToken cancellationToken = default)
    {
        if (badge is { } value && (value.Length > 16 || value.Any(char.IsControl))) throw new ArgumentException("A badge must be at most 16 non-control characters.", nameof(badge));
        return Dispatch(window, () => SetBadge(window, badge), cancellationToken);
    }

    /// <summary>Sets native represented-document edited state where supported.</summary>
    public ValueTask<NeoDesktopStatus> SetDocumentEditedAsync(NeoWindow window, bool edited, CancellationToken cancellationToken = default)
        => Dispatch(window, () => SetDocumentEdited(window, edited), cancellationToken);

    /// <summary>Sets an existing represented file where supported.</summary>
    public ValueTask<NeoDesktopStatus> SetRepresentedFileAsync(NeoWindow window, string? path, CancellationToken cancellationToken = default)
    {
        if (path is not null && (!Path.IsPathFullyQualified(path) || !File.Exists(path) || path.Any(char.IsControl))) return ValueTask.FromResult(NeoDesktopStatus.Denied);
        return Dispatch(window, () => SetRepresentedFile(window, path), cancellationToken);
    }

    /// <summary>Enables or disables meaningful OS capture exclusion where supported.</summary>
    public ValueTask<NeoDesktopStatus> SetContentProtectionAsync(NeoWindow window, bool enabled, CancellationToken cancellationToken = default)
        => Dispatch(window, () => SetContentProtection(window, enabled), cancellationToken);

    /// <summary>Sets title-bar appearance intent without exposing arbitrary draggable regions.</summary>
    public ValueTask<NeoDesktopStatus> SetTitleBarThemeAsync(NeoWindow window, NeoWindowTitleBarTheme theme, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(theme)) throw new ArgumentOutOfRangeException(nameof(theme));
        return Dispatch(window, () => SetTitleBarTheme(window, theme), cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        KeyValuePair<NeoWindow, WindowIconEntry>[] entries; KeyValuePair<NeoWindow, EventHandler>[] attention; LinuxLauncherEntry? launcher;
        lock (_sync) { if (_disposed) return; _disposed = true; entries = _windowsIcons.ToArray(); _windowsIcons.Clear(); attention = _linuxAttention.ToArray(); _linuxAttention.Clear(); launcher = _launcher; _launcher = null; }
        foreach (var pair in attention) { pair.Key.Activated -= pair.Value; pair.Key.Closed -= pair.Value; }
        if (launcher is not null)
        {
            try { if (attention.Length != 0) _ = launcher.Update(LinuxLauncherEntry.UrgentProperties(false)); } catch { }
            launcher.Dispose();
        }
        var cleanups = new List<Task>(entries.Length);
        foreach (var pair in entries)
        {
            pair.Key.Closed -= pair.Value.ClosedHandler; pair.Key.ScaleFactorChanged -= pair.Value.ScaleFactorChangedHandler;
            try
            {
                if (pair.Key.Application.Dispatcher.CheckAccess()) ClearAndDestroyIcon(pair.Value);
                else cleanups.Add(pair.Key.Application.Dispatcher.InvokeAsync(() => ClearAndDestroyIcon(pair.Value)).AsTask());
            }
            catch { /* A stopped owner dispatcher makes the borrowed HWND unsafe to touch; bounded icon leakage is safer than a dangling native handle. */ }
        }
        if (cleanups.Count == 0) return;
        var all = Task.WhenAll(cleanups); if (await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false) == all) try { await all.ConfigureAwait(false); } catch { }
        else foreach (var cleanup in cleanups) _ = cleanup.ContinueWith(static task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private ValueTask<NeoDesktopStatus> Dispatch(NeoWindow window, Func<NeoDesktopStatus> action, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(window); ArgumentNullException.ThrowIfNull(action); token.ThrowIfCancellationRequested();
        lock (_sync) ObjectDisposedException.ThrowIf(_disposed, this);
        return window.Application.Dispatcher.CheckAccess() ? ValueTask.FromResult(Contained(action)) : window.Application.Dispatcher.InvokeAsync(() => Contained(action), token);
    }

    private static NeoDesktopStatus Contained(Func<NeoDesktopStatus> action) { try { return action(); } catch (OperationCanceledException) { throw; } catch { return NeoDesktopStatus.Failed; } }
    private NeoDesktopStatus SetIcon(NeoWindow window, string path)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!string.Equals(Path.GetExtension(path), ".ico", StringComparison.OrdinalIgnoreCase)) return NeoDesktopStatus.Unsupported;
            lock (_sync) if (!_windowsIcons.ContainsKey(window) && _windowsIcons.Count >= 128) return NeoDesktopStatus.LimitExceeded;
            var handle = window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value;
            if (!Native.IsWindow(handle)) return NeoDesktopStatus.NotFound;
            if (!LoadWindowsIcons(path, WindowDpi(window), out var big, out var small)) return NeoDesktopStatus.Failed;
            _ = Native.SendMessage(handle, 0x0080, 1, big); _ = Native.SendMessage(handle, 0x0080, 0, small);
            lock (_sync)
            {
                if (_windowsIcons.Remove(window, out var previous))
                {
                    window.Closed -= previous.ClosedHandler; window.ScaleFactorChanged -= previous.ScaleFactorChangedHandler;
                    DestroyIcons(previous);
                }
                var entry = new WindowIconEntry(handle, path, (_, _) => ReleaseWindowIcon(window), (_, _) => ReloadWindowIcon(window)) { Big = big, Small = small };
                _windowsIcons[window] = entry; window.Closed += entry.ClosedHandler; window.ScaleFactorChanged += entry.ScaleFactorChangedHandler;
            }
            return NeoDesktopStatus.Success;
        }
        if (OperatingSystem.IsLinux()) return NeoDesktopStatus.Unsupported;
        if (OperatingSystem.IsMacOS())
        {
            var name = ObjC.String(path); var image = ObjC.SendInt(ObjC.Send(ObjC.Class("NSImage"), ObjC.Selector("alloc")), ObjC.Selector("initWithContentsOfFile:"), name);
            try { if (image == 0) return NeoDesktopStatus.Failed; var app = ObjC.Send(ObjC.Class("NSApplication"), ObjC.Selector("sharedApplication")); ObjC.SendVoid(app, ObjC.Selector("setApplicationIconImage:"), image); return NeoDesktopStatus.Success; }
            finally { if (image != 0) ObjC.SendVoid(image, ObjC.Selector("release")); if (name != 0) ObjC.SendVoid(name, ObjC.Selector("release")); }
        }
        return NeoDesktopStatus.Unsupported;
    }

    private NeoDesktopStatus RequestAttention(NeoWindow window, bool critical)
    {
        if (OperatingSystem.IsWindows()) { var handle=window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value;if(!Native.IsWindow(handle))return NeoDesktopStatus.NotFound;var info = new FlashWindowInfo { Size = (uint)Marshal.SizeOf<FlashWindowInfo>(), Window = handle, Flags = 3 | 12, Count = critical ? 5u : 3u, Timeout = 0 }; _=Native.FlashWindowEx(ref info);return NeoDesktopStatus.Success; }
        if (OperatingSystem.IsMacOS()) { var app = ObjC.Send(ObjC.Class("NSApplication"), ObjC.Selector("sharedApplication")); _ = ObjC.SendInt(app, ObjC.Selector("requestUserAttention:"), critical ? 0 : 10); return NeoDesktopStatus.Success; }
        if (OperatingSystem.IsLinux()) return RequestLinuxAttention(window);
        return NeoDesktopStatus.Unsupported;
    }

    // Urgency is of the application: it stays until each window that asked for attention is activated or closed. A focused window
    // asks for nothing, as an active application on macOS.
    private NeoDesktopStatus RequestLinuxAttention(NeoWindow window)
    {
        if (window.IsClosed) return NeoDesktopStatus.NotFound;
        if (window.IsFocused) return NeoDesktopStatus.Success;
        var launcher = Launcher(); if (launcher is null) return NeoDesktopStatus.Unsupported;
        var status = launcher.Update(LinuxLauncherEntry.UrgentProperties(true)); if (status != NeoDesktopStatus.Success) return status;
        lock (_sync)
        {
            if (_disposed || _linuxAttention.ContainsKey(window)) return status;
            EventHandler handler = (_, _) => EndLinuxAttention(window);
            _linuxAttention[window] = handler; window.Activated += handler; window.Closed += handler;
        }
        return status;
    }

    private void EndLinuxAttention(NeoWindow window)
    {
        LinuxLauncherEntry? launcher;
        lock (_sync)
        {
            if (!_linuxAttention.Remove(window, out var handler)) return;
            window.Activated -= handler; window.Closed -= handler;
            if (_linuxAttention.Count != 0) return;
            launcher = _launcher;
        }
        try { _ = launcher?.Update(LinuxLauncherEntry.UrgentProperties(false)); } catch { }
    }

    private LinuxLauncherEntry? Launcher()
    {
        lock (_sync)
        {
            if (_disposed) return null;
            // The program name is read at the first use, after the application named itself.
            if (_launcher is null && LinuxDesktopEntry.Resolve(_linuxApplicationId) is { } entry) _launcher = new LinuxLauncherEntry(entry);
            return _launcher;
        }
    }

    private static NeoDesktopStatus SetEnabled(NeoWindow window, bool enabled)
    {
        if (OperatingSystem.IsWindows()) { var handle = window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value; if (!Native.IsWindow(handle)) return NeoDesktopStatus.NotFound; _ = Native.EnableWindow(handle, enabled); return NeoDesktopStatus.Success; }
        if (OperatingSystem.IsLinux()) { Native.GtkWidgetSetSensitive(window.GetNativeHandle(NeoNativeHandleKind.GtkWindow).Value, enabled); return NeoDesktopStatus.Success; }
        return NeoDesktopStatus.Unsupported;
    }

    private unsafe NeoDesktopStatus SetProgress(NeoWindow window, NeoWindowProgressState state, double value)
    {
        if (OperatingSystem.IsLinux())
            return state == NeoWindowProgressState.Indeterminate ? NeoDesktopStatus.Unsupported
                : Launcher()?.Update(LinuxLauncherEntry.ProgressProperties(state == NeoWindowProgressState.None ? null : value)) ?? NeoDesktopStatus.Unsupported;
        if (!OperatingSystem.IsWindows()) return NeoDesktopStatus.Unsupported;
        var taskbar = CreateTaskbar(); if (taskbar == 0) return NeoDesktopStatus.Failed;
        try
        {
            var vtable = *(nint**)taskbar; var hr = ((delegate* unmanaged[Stdcall]<nint, nint, uint, int>)vtable[10])(taskbar, window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value, state switch { NeoWindowProgressState.None => 0u, NeoWindowProgressState.Indeterminate => 1u, NeoWindowProgressState.Normal => 2u, NeoWindowProgressState.Error => 4u, NeoWindowProgressState.Paused => 8u, _ => 0u });
            if (hr < 0) return NeoDesktopStatus.Failed;
            if (state is NeoWindowProgressState.Normal or NeoWindowProgressState.Error or NeoWindowProgressState.Paused) hr = ((delegate* unmanaged[Stdcall]<nint, nint, ulong, ulong, int>)vtable[9])(taskbar, window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value, (ulong)Math.Round(value * 10_000), 10_000);
            return hr >= 0 ? NeoDesktopStatus.Success : NeoDesktopStatus.Failed;
        }
        finally { Release(taskbar); }
    }

    private NeoDesktopStatus SetBadge(NeoWindow window, string? badge)
    {
        if (OperatingSystem.IsWindows()) return SetOverlayIcon(window, badge);
        if (OperatingSystem.IsLinux())
        {
            long? count = null;
            if (!string.IsNullOrWhiteSpace(badge)) { if (!NeoWindowBadge.TryLauncherCount(badge, out var value)) return NeoDesktopStatus.Unsupported; count = value; }
            return Launcher()?.Update(LinuxLauncherEntry.CountProperties(count)) ?? NeoDesktopStatus.Unsupported;
        }
        if (!OperatingSystem.IsMacOS()) return NeoDesktopStatus.Unsupported;
        var app = ObjC.Send(ObjC.Class("NSApplication"), ObjC.Selector("sharedApplication")); var dock = ObjC.Send(app, ObjC.Selector("dockTile")); var text = ObjC.String(badge);
        try { ObjC.SendVoid(dock, ObjC.Selector("setBadgeLabel:"), text); return NeoDesktopStatus.Success; } finally { if (text != 0) ObjC.SendVoid(text, ObjC.Selector("release")); }
    }

    private static NeoDesktopStatus SetDocumentEdited(NeoWindow window, bool edited)
    { if (!OperatingSystem.IsMacOS()) return NeoDesktopStatus.Unsupported; ObjC.SendBool(window.GetNativeHandle(NeoNativeHandleKind.CocoaNSWindow).Value, ObjC.Selector("setDocumentEdited:"), edited); return NeoDesktopStatus.Success; }
    private static NeoDesktopStatus SetRepresentedFile(NeoWindow window, string? path)
    { if (!OperatingSystem.IsMacOS()) return NeoDesktopStatus.Unsupported; var text = ObjC.String(path); try { ObjC.SendVoid(window.GetNativeHandle(NeoNativeHandleKind.CocoaNSWindow).Value, ObjC.Selector("setRepresentedFilename:"), text); return NeoDesktopStatus.Success; } finally { if (text != 0) ObjC.SendVoid(text, ObjC.Selector("release")); } }
    private static NeoDesktopStatus SetContentProtection(NeoWindow window, bool enabled)
    { if (OperatingSystem.IsWindows()) return Native.SetWindowDisplayAffinity(window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value, enabled ? 0x11u : 0u) ? NeoDesktopStatus.Success : NeoDesktopStatus.Failed; if (OperatingSystem.IsMacOS()) { ObjC.SendInt(window.GetNativeHandle(NeoNativeHandleKind.CocoaNSWindow).Value, ObjC.Selector("setSharingType:"), enabled ? 0 : 1); return NeoDesktopStatus.Success; } return NeoDesktopStatus.Unsupported; }
    private static NeoDesktopStatus SetTitleBarTheme(NeoWindow window, NeoWindowTitleBarTheme theme)
    {
        if (!OperatingSystem.IsWindows()) return NeoDesktopStatus.Unsupported;
        var dark = theme switch { NeoWindowTitleBarTheme.Dark => 1, NeoWindowTitleBarTheme.Light => 0, _ => Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int light && light == 0 ? 1 : 0 };
        return Native.DwmSetWindowAttribute(window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value, 20, ref dark, sizeof(int)) >= 0 ? NeoDesktopStatus.Success : NeoDesktopStatus.Unsupported;
    }

    // The taskbar keeps a copy of the overlay icon it is given (ITaskbarList3::SetOverlayIcon), so the icon is destroyed at once and
    // the service holds none: a later badge replaces the overlay, a null icon clears it, and the button goes with its window.
    private static unsafe NeoDesktopStatus SetOverlayIcon(NeoWindow window, string? badge)
    {
        var handle = window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value;
        if (!Native.IsWindow(handle)) return NeoDesktopStatus.NotFound;
        var text = NeoWindowBadge.OverlayText(badge); nint icon = 0;
        if (text is not null)
        {
            var size = Native.GetSystemMetricsForDpi(49, WindowDpi(window)); // SM_CXSMICON
            icon = CreateOverlayIcon(text, Math.Clamp(size, 16, 256)); if (icon == 0) return NeoDesktopStatus.Failed;
        }
        var taskbar = CreateTaskbar();
        try
        {
            if (taskbar == 0) return NeoDesktopStatus.Failed;
            var vtable = *(nint**)taskbar;
            // The description of the overlay, for accessibility, is the whole badge.
            fixed (char* description = text is null ? null : badge!.Trim())
                return ((delegate* unmanaged[Stdcall]<nint, nint, nint, char*, int>)vtable[18])(taskbar, handle, icon, description) >= 0 ? NeoDesktopStatus.Success : NeoDesktopStatus.Failed;
        }
        finally { if (taskbar != 0) Release(taskbar); if (icon != 0) _ = Native.DestroyIcon(icon); }
    }

    internal static unsafe nint CreateOverlayIcon(string text, int size)
    {
        var coverage = DrawOverlayText(text, size); if (coverage is null) return 0;
        var pixels = NeoWindowBadge.RenderOverlay(size, coverage);
        var header = BitmapHeader(size); void* bits;
        var color = Native.CreateDIBSection(0, &header, 0, &bits, 0, 0); if (color == 0 || bits is null) { if (color != 0) _ = Native.DeleteObject(color); return 0; }
        nint mask = 0;
        try
        {
            pixels.CopyTo(new Span<byte>(bits, pixels.Length));
            // The alpha of the color bitmap gives the shape; the monochrome mask, rows of whole words, stays empty.
            var empty = new byte[(size + 15) / 16 * 2 * size];
            fixed (byte* maskBits = empty) mask = Native.CreateBitmap(size, size, 1, 1, maskBits);
            if (mask == 0) return 0;
            // CreateIconIndirect copies the bitmaps, which are deleted here.
            var info = new IconInfo { Icon = 1, Mask = mask, Color = color };
            return Native.CreateIconIndirect(&info);
        }
        finally { if (mask != 0) _ = Native.DeleteObject(mask); _ = Native.DeleteObject(color); }
    }

    // The coverage of the text, drawn white on black by GDI with grayscale smoothing in the bold Segoe UI font at the largest
    // height whose text fits in the dot.
    internal static unsafe byte[]? DrawOverlayText(string text, int size)
    {
        var dc = Native.CreateCompatibleDC(0); if (dc == 0) return null;
        var header = BitmapHeader(size); void* bits; nint bitmap = 0, font = 0, previousBitmap = 0, previousFont = 0;
        try
        {
            bitmap = Native.CreateDIBSection(dc, &header, 0, &bits, 0, 0); if (bitmap == 0 || bits is null) return null;
            new Span<byte>(bits, size * size * 4).Clear();
            previousBitmap = Native.SelectObject(dc, bitmap);
            var room = size - Math.Max(2, size / 8);
            for (var height = size * 4 / 5; ; height--)
            {
                font = Native.CreateFont(-height, 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI"); if (font == 0) return null; // FW_BOLD, DEFAULT_CHARSET, ANTIALIASED_QUALITY
                var previous = Native.SelectObject(dc, font); if (previousFont == 0) previousFont = previous;
                TextSize extent;
                if (height <= size / 2 || (Native.GetTextExtentPoint32(dc, text, text.Length, &extent) && extent.Width <= room)) break;
                _ = Native.SelectObject(dc, previousFont); _ = Native.DeleteObject(font); font = 0;
            }
            _ = Native.SetTextColor(dc, 0x00FFFFFF); _ = Native.SetBkMode(dc, 1); // TRANSPARENT
            var rect = new TextRect { Right = size, Bottom = size };
            _ = Native.DrawText(dc, text, text.Length, &rect, 0x1 | 0x4 | 0x20 | 0x100 | 0x800); // DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOCLIP | DT_NOPREFIX
            _ = Native.GdiFlush();
            var coverage = new byte[size * size]; var source = (byte*)bits;
            for (var index = 0; index < coverage.Length; index++) coverage[index] = Math.Max(source[index * 4], Math.Max(source[index * 4 + 1], source[index * 4 + 2]));
            return coverage;
        }
        finally
        {
            if (previousFont != 0) _ = Native.SelectObject(dc, previousFont); if (font != 0) _ = Native.DeleteObject(font);
            if (previousBitmap != 0) _ = Native.SelectObject(dc, previousBitmap); if (bitmap != 0) _ = Native.DeleteObject(bitmap);
            _ = Native.DeleteDC(dc);
        }
    }

    // A top-down 32-bit bitmap.
    private static unsafe BitmapInfoHeader BitmapHeader(int size) => new() { Size = (uint)sizeof(BitmapInfoHeader), Width = size, Height = -size, Planes = 1, BitCount = 32 };

    private static unsafe nint CreateTaskbar()
    {
        var clsid = new Guid("56FDF344-FD6D-11d0-958A-006097C9A090"); var iid = TaskbarList3InterfaceId; nint value = 0;
        if (Native.CoCreateInstance(ref clsid, 0, 1, ref iid, &value) < 0 || value == 0) return 0; var table = *(nint**)value; if (((delegate* unmanaged[Stdcall]<nint, int>)table[3])(value) >= 0) return value; Release(value); return 0;
    }
    private static unsafe void Release(nint value) { var table = *(nint**)value; _ = ((delegate* unmanaged[Stdcall]<nint, uint>)table[2])(value); }
    private void ReleaseWindowIcon(NeoWindow window)
    {
        WindowIconEntry? entry;
        lock (_sync) if (!_windowsIcons.Remove(window, out entry) || entry is null) return;
        window.Closed -= entry.ClosedHandler; window.ScaleFactorChanged -= entry.ScaleFactorChangedHandler; ClearAndDestroyIcon(entry);
    }
    // Runs on the UI thread, which alone gives, replaces, and destroys the icons of an entry.
    private void ReloadWindowIcon(NeoWindow window)
    {
        WindowIconEntry? entry;
        lock (_sync) if (!_windowsIcons.TryGetValue(window, out entry)) return;
        // When the file cannot be loaded again, the icons of the previous scale stay.
        if (!Native.IsWindow(entry.WindowHandle) || !LoadWindowsIcons(entry.Path, WindowDpi(window), out var big, out var small)) return;
        _ = Native.SendMessage(entry.WindowHandle, 0x0080, 1, big); _ = Native.SendMessage(entry.WindowHandle, 0x0080, 0, small);
        DestroyIcons(entry); entry.Big = big; entry.Small = small;
    }
    // The system draws a big icon in the taskbar and Alt+Tab and a small one in the title bar, at sizes that follow the scale of
    // the display. An .ico loaded without a size gives its first picture, often 16 pixels, which the taskbar enlarges.
    private static bool LoadWindowsIcons(string path, uint dpi, out nint big, out nint small)
    {
        var bigSize = Native.GetSystemMetricsForDpi(11, dpi); if (bigSize <= 0) bigSize = (int)(32 * dpi / 96); // SM_CXICON
        var smallSize = Native.GetSystemMetricsForDpi(49, dpi); if (smallSize <= 0) smallSize = (int)(16 * dpi / 96); // SM_CXSMICON
        big = Native.LoadImage(0, path, 1, bigSize, bigSize, 0x0010); small = Native.LoadImage(0, path, 1, smallSize, smallSize, 0x0010);
        if (big != 0 && small != 0) return true;
        if (big != 0) _ = Native.DestroyIcon(big); if (small != 0) _ = Native.DestroyIcon(small);
        big = small = 0; return false;
    }
    // The scale of a window is the one of its display in a process aware of it, and 1 otherwise, as GetDpiForWindow tells.
    private static uint WindowDpi(NeoWindow window) { var dpi = Math.Round(window.ScaleFactor * 96); return dpi is >= 48 and <= 96 * 8 ? (uint)dpi : 96u; }
    private static void DestroyIcons(WindowIconEntry entry) { if (entry.Big != 0) _ = Native.DestroyIcon(entry.Big); if (entry.Small != 0) _ = Native.DestroyIcon(entry.Small); entry.Big = entry.Small = 0; }
    private static void ClearAndDestroyIcon(WindowIconEntry entry) { if (Native.IsWindow(entry.WindowHandle)) { _ = Native.SendMessage(entry.WindowHandle, 0x0080, 0, 0); _ = Native.SendMessage(entry.WindowHandle, 0x0080, 1, 0); } DestroyIcons(entry); }
    private sealed class WindowIconEntry(nint windowHandle, string path, EventHandler closedHandler, EventHandler<NeoWindowScaleFactorChangedEventArgs> scaleFactorChangedHandler)
    {
        internal nint WindowHandle { get; } = windowHandle;
        internal string Path { get; } = path;
        internal EventHandler ClosedHandler { get; } = closedHandler;
        internal EventHandler<NeoWindowScaleFactorChangedEventArgs> ScaleFactorChangedHandler { get; } = scaleFactorChangedHandler;
        internal nint Big { get; set; }
        internal nint Small { get; set; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader { internal uint Size; internal int Width; internal int Height; internal ushort Planes; internal ushort BitCount; internal uint Compression; internal uint SizeImage; internal int XPelsPerMeter; internal int YPelsPerMeter; internal uint ColorsUsed; internal uint ColorsImportant; }
    [StructLayout(LayoutKind.Sequential)] private struct IconInfo { internal int Icon; internal uint HotspotX; internal uint HotspotY; internal nint Mask; internal nint Color; }
    [StructLayout(LayoutKind.Sequential)] private struct TextRect { internal int Left; internal int Top; internal int Right; internal int Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct TextSize { internal int Width; internal int Height; }
    [StructLayout(LayoutKind.Sequential)] private struct FlashWindowInfo { internal uint Size; internal nint Window; internal uint Flags; internal uint Count; internal uint Timeout; }
    private static unsafe partial class Native
    {
        [LibraryImport("user32.dll", EntryPoint = "LoadImageW", StringMarshalling = StringMarshalling.Utf16)] internal static partial nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);
        [LibraryImport("user32.dll", EntryPoint = "SendMessageW")] internal static partial nint SendMessage(nint window, uint message, nuint wParam, nint lParam);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool DestroyIcon(nint icon);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool FlashWindowEx(ref FlashWindowInfo info);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool EnableWindow(nint window, [MarshalAs(UnmanagedType.Bool)] bool enabled);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool IsWindow(nint window);
        [LibraryImport("user32.dll")] internal static partial int GetSystemMetricsForDpi(int index, uint dpi);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool SetWindowDisplayAffinity(nint window, uint affinity);
        [LibraryImport("dwmapi.dll")] internal static partial int DwmSetWindowAttribute(nint window, uint attribute, ref int value, int size);
        [LibraryImport("user32.dll")] internal static partial nint CreateIconIndirect(IconInfo* info);
        [LibraryImport("user32.dll", EntryPoint = "DrawTextW", StringMarshalling = StringMarshalling.Utf16)] internal static partial int DrawText(nint dc, string text, int length, TextRect* rect, uint format);
        [LibraryImport("gdi32.dll")] internal static partial nint CreateDIBSection(nint dc, BitmapInfoHeader* info, uint usage, void** bits, nint section, uint offset);
        [LibraryImport("gdi32.dll")] internal static partial nint CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, void* bits);
        [LibraryImport("gdi32.dll")] internal static partial nint CreateCompatibleDC(nint dc);
        [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool DeleteDC(nint dc);
        [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool DeleteObject(nint value);
        [LibraryImport("gdi32.dll")] internal static partial nint SelectObject(nint dc, nint value);
        [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)] internal static partial nint CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outputPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
        [LibraryImport("gdi32.dll", EntryPoint = "GetTextExtentPoint32W", StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool GetTextExtentPoint32(nint dc, string text, int length, TextSize* size);
        [LibraryImport("gdi32.dll")] internal static partial uint SetTextColor(nint dc, uint color);
        [LibraryImport("gdi32.dll")] internal static partial int SetBkMode(nint dc, int mode);
        [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool GdiFlush();
        [LibraryImport("ole32.dll")] internal static partial int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, nint* value);
        [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_widget_set_sensitive")] internal static partial void GtkWidgetSetSensitive(nint widget, [MarshalAs(UnmanagedType.Bool)] bool sensitive);
    }

    private static partial class ObjC
    {
        internal static nint Class(string name) => GetClass(name);
        internal static nint Selector(string name) => RegisterSelector(name);
        internal static nint String(string? value) { if (value is null) return 0; var item = Send(Class("NSString"), Selector("alloc")); return InitString(item, Selector("initWithUTF8String:"), value); }
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)] private static partial nint GetClass(string name);
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)] private static partial nint RegisterSelector(string name);
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial nint Send(nint receiver, nint selector);
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial nint SendInt(nint receiver, nint selector, nint value);
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial void SendVoid(nint receiver, nint selector);
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial void SendVoid(nint receiver, nint selector, nint value);
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial void SendBool(nint receiver, nint selector, [MarshalAs(UnmanagedType.I1)] bool value);
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)] private static partial nint InitString(nint receiver, nint selector, string value);
    }
}
