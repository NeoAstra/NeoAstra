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
    private readonly Dictionary<NeoWindow, WindowIconEntry> _windowsIcons = new(ReferenceEqualityComparer.Instance);
    private readonly object _sync = new();
    private bool _disposed;

    /// <summary>Gets icon support.</summary>
    public NeoCapabilityInfo IconSupport { get; } = OperatingSystem.IsWindows()
        ? new(NeoSupportLevel.Native, 1, 0, "Native Win32 window icons use copied application-controlled files.")
        : OperatingSystem.IsMacOS()
            ? new(NeoSupportLevel.Limited, 1, 0, "Cocoa exposes an application-scoped icon rather than a per-window icon.")
            : OperatingSystem.IsLinux()
                ? new(NeoSupportLevel.None, 1, 0, "GTK4 removed per-window file icons; Linux applications should provide desktop-entry icon metadata.")
                : new(NeoSupportLevel.None, 1, 0, "No supported desktop window backend.");
    /// <summary>Gets attention support.</summary>
    public NeoCapabilityInfo AttentionSupport { get; } = OperatingSystem.IsLinux()
        ? new(NeoSupportLevel.None, 1, 0, "GTK4 removed per-window urgency hints and no portable compositor-neutral replacement is available.")
        : OperatingSystem.IsMacOS()
            ? new(NeoSupportLevel.Limited, 1, 0, "Cocoa user-attention requests are native but application-scoped rather than per-window.")
            : OperatingSystem.IsWindows()
                ? new(NeoSupportLevel.Native, 1, 0, "Native Win32 taskbar and caption flashing.")
                : new(NeoSupportLevel.None, 1, 0, "No supported desktop window backend.");
    /// <summary>Gets explicit window-enabled support.</summary>
    public NeoCapabilityInfo EnabledSupport { get; } = OperatingSystem.IsWindows() || OperatingSystem.IsLinux() ? new(NeoSupportLevel.Native, 1, 0, "Native window input enablement without a nested application loop.") : new(NeoSupportLevel.None, 1, 0, "Cocoa does not expose a correct general disabled-window semantic; owned modal sheets are provided by the core window owner relationship.");
    /// <summary>Gets taskbar/dock progress support.</summary>
    public NeoCapabilityInfo ProgressSupport { get; } = OperatingSystem.IsWindows() ? new(NeoSupportLevel.Native, 1, 0, "Windows ITaskbarList3 progress state/value.") : new(NeoSupportLevel.None, 1, 0, "The target desktop does not expose one reliable portable per-window taskbar/dock progress API.");
    /// <summary>Gets app badge support.</summary>
    public NeoCapabilityInfo BadgeSupport { get; } = OperatingSystem.IsMacOS() ? new(NeoSupportLevel.Native, 1, 0, "Native application dock-tile badge; macOS badges are application-scoped rather than window-scoped.") : new(NeoSupportLevel.None, 1, 0, "No reliable built-in per-window text badge semantic exists on this target.");
    /// <summary>Gets represented-file/document-edited support.</summary>
    public NeoCapabilityInfo DocumentSupport { get; } = OperatingSystem.IsMacOS() ? new(NeoSupportLevel.Native, 1, 0, "Native NSWindow represented filename and document-edited indicator.") : new(NeoSupportLevel.None, 1, 0, "The target does not expose macOS represented-document title-bar semantics.");
    /// <summary>Gets capture-protection support.</summary>
    public NeoCapabilityInfo ContentProtectionSupport { get; } = OperatingSystem.IsWindows() ? new(NeoSupportLevel.Limited, 1, 0, "SetWindowDisplayAffinity excludes supported desktop capture paths but cannot prevent cameras, privileged software, or every capture stack.") : OperatingSystem.IsMacOS() ? new(NeoSupportLevel.Limited, 1, 0, "NSWindow sharing type restricts ordinary window sharing but cannot prevent cameras, privileged software, or every capture stack.") : new(NeoSupportLevel.None, 1, 0, "GTK/Wayland has no portable meaningful per-window capture exclusion contract.");
    /// <summary>Gets title-bar theme support.</summary>
    public NeoCapabilityInfo TitleBarThemeSupport { get; } = OperatingSystem.IsWindows() ? new(NeoSupportLevel.Limited, 1, 0, "DWM immersive dark-mode title-bar intent on supported Windows versions.") : new(NeoSupportLevel.None, 1, 0, "No safe portable title-bar-only theme override is exposed on this target.");

    /// <summary>Sets a copied native window icon from an existing absolute .ico/.png path.</summary>
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
    public ValueTask<NeoDesktopStatus> SetBadgeAsync(NeoWindow window, string? badge, CancellationToken cancellationToken = default)
    {
        if (badge is { } value && (value.Length > 16 || value.Any(char.IsControl))) throw new ArgumentException("A badge must be at most 16 non-control characters.", nameof(badge));
        return Dispatch(window, () => SetBadge(badge), cancellationToken);
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
        KeyValuePair<NeoWindow, WindowIconEntry>[] entries;
        lock (_sync) { if (_disposed) return; _disposed = true; entries = _windowsIcons.ToArray(); _windowsIcons.Clear(); }
        var cleanups = new List<Task>(entries.Length);
        foreach (var pair in entries)
        {
            pair.Key.Closed -= pair.Value.ClosedHandler;
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
            var icon = Native.LoadImage(0, path, 1, 0, 0, 0x0010); if (icon == 0) return NeoDesktopStatus.Failed;
            var handle = window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value;
            if (!Native.IsWindow(handle)) { _ = Native.DestroyIcon(icon); return NeoDesktopStatus.NotFound; }
            _ = Native.SendMessage(handle, 0x0080, 0, icon); _ = Native.SendMessage(handle, 0x0080, 1, icon);
            lock (_sync)
            {
                if (_windowsIcons.Remove(window, out var previous))
                {
                    window.Closed -= previous.ClosedHandler;
                    if (previous.Icon != 0) _ = Native.DestroyIcon(previous.Icon);
                }
                EventHandler handler = (_, _) => ReleaseWindowIcon(window);
                _windowsIcons[window] = new(handle, icon, handler); window.Closed += handler;
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

    private static NeoDesktopStatus RequestAttention(NeoWindow window, bool critical)
    {
        if (OperatingSystem.IsWindows()) { var handle=window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value;if(!Native.IsWindow(handle))return NeoDesktopStatus.NotFound;var info = new FlashWindowInfo { Size = (uint)Marshal.SizeOf<FlashWindowInfo>(), Window = handle, Flags = 3 | 12, Count = critical ? 5u : 3u, Timeout = 0 }; _=Native.FlashWindowEx(ref info);return NeoDesktopStatus.Success; }
        if (OperatingSystem.IsMacOS()) { var app = ObjC.Send(ObjC.Class("NSApplication"), ObjC.Selector("sharedApplication")); _ = ObjC.SendInt(app, ObjC.Selector("requestUserAttention:"), critical ? 0 : 10); return NeoDesktopStatus.Success; }
        if (OperatingSystem.IsLinux()) return NeoDesktopStatus.Unsupported;
        return NeoDesktopStatus.Unsupported;
    }

    private static NeoDesktopStatus SetEnabled(NeoWindow window, bool enabled)
    {
        if (OperatingSystem.IsWindows()) { var handle = window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value; if (!Native.IsWindow(handle)) return NeoDesktopStatus.NotFound; _ = Native.EnableWindow(handle, enabled); return NeoDesktopStatus.Success; }
        if (OperatingSystem.IsLinux()) { Native.GtkWidgetSetSensitive(window.GetNativeHandle(NeoNativeHandleKind.GtkWindow).Value, enabled); return NeoDesktopStatus.Success; }
        return NeoDesktopStatus.Unsupported;
    }

    private static unsafe NeoDesktopStatus SetProgress(NeoWindow window, NeoWindowProgressState state, double value)
    {
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

    private static NeoDesktopStatus SetBadge(string? badge)
    {
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

    private static unsafe nint CreateTaskbar()
    {
        var clsid = new Guid("56FDF344-FD6D-11d0-958A-006097C9A090"); var iid = new Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEA84"); nint value = 0;
        if (Native.CoCreateInstance(ref clsid, 0, 1, ref iid, &value) < 0 || value == 0) return 0; var table = *(nint**)value; if (((delegate* unmanaged[Stdcall]<nint, int>)table[3])(value) >= 0) return value; Release(value); return 0;
    }
    private static unsafe void Release(nint value) { var table = *(nint**)value; _ = ((delegate* unmanaged[Stdcall]<nint, uint>)table[2])(value); }
    private void ReleaseWindowIcon(NeoWindow window)
    {
        WindowIconEntry? entry;
        lock (_sync) if (!_windowsIcons.Remove(window, out entry) || entry is null) return;
        window.Closed -= entry.ClosedHandler; ClearAndDestroyIcon(entry);
    }
    private static void ClearAndDestroyIcon(WindowIconEntry entry) { if (Native.IsWindow(entry.WindowHandle)) { _ = Native.SendMessage(entry.WindowHandle, 0x0080, 0, 0); _ = Native.SendMessage(entry.WindowHandle, 0x0080, 1, 0); } if (entry.Icon != 0) _ = Native.DestroyIcon(entry.Icon); }
    private sealed record WindowIconEntry(nint WindowHandle, nint Icon, EventHandler ClosedHandler);
    [StructLayout(LayoutKind.Sequential)] private struct FlashWindowInfo { internal uint Size; internal nint Window; internal uint Flags; internal uint Count; internal uint Timeout; }
    private static unsafe partial class Native
    {
        [LibraryImport("user32.dll", EntryPoint = "LoadImageW", StringMarshalling = StringMarshalling.Utf16)] internal static partial nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);
        [LibraryImport("user32.dll", EntryPoint = "SendMessageW")] internal static partial nint SendMessage(nint window, uint message, nuint wParam, nint lParam);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool DestroyIcon(nint icon);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool FlashWindowEx(ref FlashWindowInfo info);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool EnableWindow(nint window, [MarshalAs(UnmanagedType.Bool)] bool enabled);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool IsWindow(nint window);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool SetWindowDisplayAffinity(nint window, uint affinity);
        [LibraryImport("dwmapi.dll")] internal static partial int DwmSetWindowAttribute(nint window, uint attribute, ref int value, int size);
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
