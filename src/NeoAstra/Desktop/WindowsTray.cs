// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using global::NeoAstra.Desktop.Menus;

namespace NeoAstra.Desktop.Tray;

internal sealed unsafe partial class WindowsTrayPresenter(NeoCommandService commands, NeoDispatcher? dispatcher) : INeoTrayPresenter, INeoApplicationBoundDesktopService
{
    private const uint CallbackMessage = 0x8000 + 41, NimAdd = 0, NimModify = 1, NimDelete = 2;
    private static readonly ConcurrentDictionary<nint, WindowsTrayPresenter> Presenters = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> _ids = [];
    private NeoDispatcher? _dispatcher = dispatcher;
    private NeoApplication? _application;
    private nint _window, _previousProcedure;
    private uint _nextId;
    private bool _disposed;

    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Limited, 1, 0, "Native Windows notification-area icons with atomic update, tooltips up to the native 127 UTF-16-unit limit, ICO policy, popup menus, ordered primary/secondary activation, and deterministic teardown. Role behavior is native, but Win32 has no reliable complete localized role-label set, so role labels must be supplied by the application. Template-image intent is rejected because Windows has no equivalent policy; reliable bounds and a distinct attention semantic are unavailable through Shell_NotifyIcon.");
    public event Action<string, bool>? Activated;

    void INeoApplicationBoundDesktopService.BindApplication(NeoApplication application) { ArgumentNullException.ThrowIfNull(application); if (_application is not null && !ReferenceEquals(_application, application)) throw new InvalidOperationException("The tray presenter is already bound to an application."); if (_dispatcher is not null && !ReferenceEquals(_dispatcher, application.Dispatcher)) throw new InvalidOperationException("The tray presenter is already bound to another dispatcher."); _application = application; _dispatcher = application.Dispatcher; }

    public void Set(NeoTrayItemOptions options)
    {
        EnsureAccess(); ObjectDisposedException.ThrowIf(_disposed, this);
        if(options.IsTemplateImage)throw new NotSupportedException("Windows notification-area icons do not support macOS template-image rendering.");
        EnsureWindow();
        if (options.ToolTip is { Length: > 127 }) throw new ArgumentException("Windows tray tooltips are limited to 127 UTF-16 code units.", nameof(options));
        if (options.IconPath is { } path && !string.Equals(Path.GetExtension(path), ".ico", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Windows tray icons require an existing .ico file.", nameof(options));
        var existing = _entries.GetValueOrDefault(options.Id); var nativeId = existing?.NativeId ?? NextId(); var icon = LoadIcon(options.IconPath); var ownedIcon = options.IconPath is not null;
        nint menu = 0;
        try
        {
            menu = BuildMenu(options.Menu, out var actions);
            var data = CreateData(nativeId, options, icon);
            if (!Native.ShellNotifyIcon(existing is null ? NimAdd : NimModify, &data)) throw new InvalidOperationException("Shell_NotifyIcon rejected the tray item update.");
            var replacement = new Entry(nativeId, icon, ownedIcon, menu, actions, options);
            _entries[options.Id] = replacement; _ids[nativeId] = options.Id;
            if (existing is not null) DestroyResources(existing);
            icon = 0; menu = 0;
        }
        finally { if (menu != 0) Native.DestroyMenu(menu); if (ownedIcon && icon != 0) Native.DestroyIcon(icon); }
    }

    public bool Remove(string id)
    {
        EnsureAccess(); if (!_entries.TryGetValue(id, out var entry)) return false; var data = new NotifyIconData { Size = (uint)sizeof(NotifyIconData), Window = _window, Id = entry.NativeId }; if (!Native.ShellNotifyIcon(NimDelete, &data)) return false; _entries.Remove(id); _ids.Remove(entry.NativeId); DestroyResources(entry); return true;
    }

    public ValueTask DisposeAsync() { var value = _dispatcher; if (value is not null && !value.CheckAccess()) return value.InvokeAsync(DisposeOnDispatcher); DisposeOnDispatcher(); return ValueTask.CompletedTask; }

    private void Receive(uint nativeId, uint message)
    {
        if (!_ids.TryGetValue(nativeId, out var id) || !_entries.TryGetValue(id, out var entry)) return;
        if (message is 0x0202 or 0x0203 or 0x0400 or 0x0401) Raise(id, false);
        else if (message is 0x0205 or 0x007B)
        {
            Raise(id, true); if (entry.Menu != 0) { Native.GetCursorPos(out var point); Native.SetForegroundWindow(_window); var command = Native.TrackPopupMenu(entry.Menu, 0x100 | 0x2, point.X, point.Y, 0, _window, 0); if (command != 0 && entry.Actions.TryGetValue(command, out var action)) Activate(action); }
        }
    }

    private void Activate(ActionItem action)
    {
        if (action.CommandId is { } command) { try { _ = commands.ActivateAsync(command); } catch { } return; }
        if (action.Role is not { } role || _application is not { } app) return;
        try
        {
            var window = app.MainWindow?.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value ?? 0; var focus = Native.GetFocus();
            switch (role) { case NeoMenuRole.Copy: _ = Native.SendMessage(focus, 0x0301, 0, 0); break; case NeoMenuRole.Cut: _ = Native.SendMessage(focus, 0x0300, 0, 0); break; case NeoMenuRole.Paste: _ = Native.SendMessage(focus, 0x0302, 0, 0); break; case NeoMenuRole.SelectAll: _ = Native.SendMessage(focus, 0x00B1, 0, -1); break; case NeoMenuRole.Undo: _ = Native.SendMessage(focus, 0x0304, 0, 0); break; case NeoMenuRole.Redo: _ = Native.SendMessage(focus, 0x0454, 0, 0); break; case NeoMenuRole.Minimize: _ = Native.ShowWindow(window, 6); break; case NeoMenuRole.CloseWindow: _ = Native.PostMessage(window, 0x0010, 0, 0); break; case NeoMenuRole.Quit: _ = app.RequestQuitAsync(); break; }
        }
        catch { }
    }
    private void Raise(string id, bool secondary) { try { Activated?.Invoke(id, secondary); } catch { } }

    private nint BuildMenu(IReadOnlyList<NeoMenuItem> items, out IReadOnlyDictionary<uint, ActionItem> actions)
    {
        var values = new Dictionary<uint, ActionItem>(); uint next = 100;
        nint Level(IReadOnlyList<NeoMenuItem> level)
        {
            if (level.Count == 0) return 0; var menu = Native.CreatePopupMenu(); if (menu == 0) throw new InvalidOperationException("Unable to allocate a tray popup menu.");
            try
            {
                foreach (var item in level)
                {
                    if (!item.IsVisible) continue; if (item.Kind == NeoMenuItemKind.Separator) { if (!Native.AppendMenu(menu, 0x800, 0, null)) throw new InvalidOperationException("Unable to append a tray menu separator."); continue; }
                    if (item.Kind == NeoMenuItemKind.Submenu) { var child = Level(item.Children); if (!Native.AppendMenu(menu, 0x10, (nuint)child, item.Text)) { Native.DestroyMenu(child); throw new InvalidOperationException("Unable to append a tray submenu."); } continue; }
                    var native = next++; var flags = (item.IsEnabled ? 0u : 0x3u) | (item.IsChecked ? 0x8u : 0u); var text = item.Kind == NeoMenuItemKind.Role ? NeoMenuRolePresentation.RequireExplicitLabel(item, "Win32") : item.Text!; if (!Native.AppendMenu(menu, flags, native, text)) throw new InvalidOperationException("Unable to append a tray menu item."); values.Add(native, new(item.CommandId, item.Role));
                }
                return menu;
            }
            catch { Native.DestroyMenu(menu); throw; }
        }
        var result = Level(items); actions = values; return result;
    }

    private nint LoadIcon(string? path)
    {
        if (path is null) return Native.LoadIcon(0, 32512); if (!File.Exists(path)) throw new FileNotFoundException("The tray icon does not exist.", path); var icon = Native.LoadImage(0, path, 1, 0, 0, 0x10 | 0x40); return icon != 0 ? icon : throw new ArgumentException("The tray ICO could not be decoded.", nameof(path));
    }
    private NotifyIconData CreateData(uint id, NeoTrayItemOptions options, nint icon) { var data = new NotifyIconData { Size = (uint)sizeof(NotifyIconData), Window = _window, Id = id, Flags = 0x1 | 0x2 | 0x4, CallbackMessage = CallbackMessage, Icon = icon }; Copy(options.ToolTip ?? string.Empty, data.Tip, 128); return data; }
    private static void Copy(string value, char* output, int capacity) { var length = Math.Min(value.Length, capacity - 1); value.AsSpan(0, length).CopyTo(new Span<char>(output, capacity)); output[length] = '\0'; }
    private void DestroyResources(Entry entry) { if (entry.Menu != 0) Native.DestroyMenu(entry.Menu); if (entry.OwnedIcon && entry.Icon != 0) Native.DestroyIcon(entry.Icon); }
    private uint NextId() { do _nextId = unchecked(_nextId + 1); while (_nextId == 0 || _ids.ContainsKey(_nextId)); return _nextId; }

    private void EnsureWindow()
    {
        if (_window != 0) return; _window = Native.CreateWindowEx(0, "STATIC", string.Empty, 0, 0, 0, 0, 0, -3, 0, 0, 0); if (_window == 0) throw new InvalidOperationException("Unable to create a tray callback window."); _previousProcedure = Native.SetWindowLongPtr(_window, -4, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WindowProcedure); if (_previousProcedure == 0) { Native.DestroyWindow(_window); _window = 0; throw new InvalidOperationException("Unable to attach the tray callback window procedure."); } Presenters[_window] = this;
    }
    private void DisposeOnDispatcher() { if (_disposed) return; _disposed = true; Activated = null; foreach (var entry in _entries.Values) { var data = new NotifyIconData { Size = (uint)sizeof(NotifyIconData), Window = _window, Id = entry.NativeId }; Native.ShellNotifyIcon(NimDelete, &data); DestroyResources(entry); } _entries.Clear(); _ids.Clear(); if (_window != 0) { Presenters.TryRemove(_window, out _); if (_previousProcedure != 0) Native.SetWindowLongPtr(_window, -4, _previousProcedure); Native.DestroyWindow(_window); _window = 0; _previousProcedure = 0; } }
    private void EnsureAccess() { var value = _dispatcher ?? throw new InvalidOperationException("The tray presenter is not bound to the UI dispatcher."); if (!value.CheckAccess()) throw new InvalidOperationException("Native tray mutations require the NeoAstra UI dispatcher."); }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam) { try { if (Presenters.TryGetValue(window, out var presenter)) { if (message == CallbackMessage) { presenter.Receive((uint)wParam, unchecked((uint)lParam)); return 0; } if (presenter._previousProcedure != 0) return Native.CallWindowProc(presenter._previousProcedure, window, message, wParam, lParam); } } catch { } return Native.DefWindowProc(window, message, wParam, lParam); }

    private sealed record Entry(uint NativeId, nint Icon, bool OwnedIcon, nint Menu, IReadOnlyDictionary<uint, ActionItem> Actions, NeoTrayItemOptions Options);
    private sealed record ActionItem(string? CommandId, NeoMenuRole? Role);
    [StructLayout(LayoutKind.Sequential)] private struct Point { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyIconData { internal uint Size; internal nint Window; internal uint Id, Flags, CallbackMessage; internal nint Icon; internal fixed char Tip[128]; internal uint State, StateMask; internal fixed char Info[256]; internal uint VersionOrTimeout; internal fixed char InfoTitle[64]; internal uint InfoFlags; internal Guid ItemGuid; internal nint BalloonIcon; }
    private static partial class Native
    {
        [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool ShellNotifyIcon(uint operation, NotifyIconData* data);
        [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)] internal static partial nint CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] internal static partial nint SetWindowLongPtr(nint window, int index, nint value);
        [LibraryImport("user32.dll", EntryPoint = "CallWindowProcW")] internal static partial nint CallWindowProc(nint procedure, nint window, uint message, nuint wParam, nint lParam);
        [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")] internal static partial nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool DestroyWindow(nint window);
        [LibraryImport("user32.dll", EntryPoint = "LoadIconW")] internal static partial nint LoadIcon(nint instance, nint name);
        [LibraryImport("user32.dll", EntryPoint = "LoadImageW", StringMarshalling = StringMarshalling.Utf16)] internal static partial nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool DestroyIcon(nint icon);
        [LibraryImport("user32.dll")] internal static partial nint CreatePopupMenu();
        [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool AppendMenu(nint menu, uint flags, nuint id, string? text);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool DestroyMenu(nint menu);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool GetCursorPos(out Point point);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool SetForegroundWindow(nint window);
        [LibraryImport("user32.dll")] internal static partial uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rectangle);
        [LibraryImport("user32.dll")] internal static partial nint GetFocus();
        [LibraryImport("user32.dll", EntryPoint = "SendMessageW")] internal static partial nint SendMessage(nint window, uint message, nuint wParam, nint lParam);
        [LibraryImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool ShowWindow(nint window, int command);
    }
}
