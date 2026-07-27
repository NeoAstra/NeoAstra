// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NeoAstra.Desktop.Menus;

internal sealed unsafe partial class WindowsMenuPresenter(NeoCommandService commands, NeoDispatcher? dispatcher) : INeoMenuPresenter, INeoApplicationBoundDesktopService
{
    private const uint WmCommand = 0x0111, WmKeyDown = 0x0100, WmNcDestroy = 0x0082, WmClose = 0x0010;
    private const uint MfString = 0, MfPopup = 0x10, MfSeparator = 0x800, MfDisabled = 0x2, MfGray = 0x1, MfChecked = 0x8;
    private const uint TpmReturnCommand = 0x100, TpmRightButton = 0x2;
    private static readonly ConcurrentDictionary<nuint, WindowsMenuPresenter> Presenters = new();
    private static readonly object HookLock = new();
    private static readonly Dictionary<uint, List<WindowsMenuPresenter>> HookPresenters = [];
    private static readonly Dictionary<uint, nint> ThreadHooks = [];
    private static long s_nextSubclassId;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<nint, AttachedWindow> _windows = [];
    private NeoDispatcher? _dispatcher = dispatcher;
    private NeoApplication? _application;
    private nuint _subclassId;
    private uint _hookThread;
    private long _generation;
    private bool _disposed;

    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Limited, 1, 0, "Native Win32 application/window menu bars and context menus, focused-control edit/window roles, local key accelerators, generation-safe callbacks, deterministic HWND teardown, and window-scope override/fallback when Win32's single menu bar also has an application menu. Win32 exposes no reliable complete localized label set for these roles, so role labels must be supplied by the application.");

    void INeoApplicationBoundDesktopService.BindApplication(NeoApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (_application is not null && !ReferenceEquals(_application, application)) throw new InvalidOperationException("The menu presenter is already bound to an application.");
        if (_dispatcher is not null && !ReferenceEquals(_dispatcher, application.Dispatcher)) throw new InvalidOperationException("The menu presenter is already bound to another dispatcher.");
        _application = application; _dispatcher = application.Dispatcher;
    }

    public void SetMenu(string targetId, IReadOnlyList<NeoMenuItem> items)
    {
        EnsureAccess(); ObjectDisposedException.ThrowIf(_disposed, this);
        var target = ResolveTarget(targetId);
        var generation = checked(++_generation); var created = Build(items, generation, target.Window, target.Kind == TargetKind.Context);
        try
        {
            if (target.Kind != TargetKind.Context) Attach(target, created.Menu);
            var replacement = new Entry(target, created.Menu, created.Actions, created.Accelerators, generation);
            if (_entries.Remove(targetId, out var previous)) Destroy(previous, detach: false);
            _entries[targetId] = replacement;
            if(target.Kind!=TargetKind.Context)ApplyPreferred(target.Window);
        }
        catch { Native.DestroyMenu(created.Menu); throw; }
    }

    public void RemoveMenu(string targetId)
    {
        EnsureAccess(); if (!_entries.Remove(targetId, out var entry)) return; Destroy(entry, detach: true);
    }

    public NeoDesktopStatus ShowContextMenu(string targetId, NeoPoint position)
    {
        EnsureAccess(); ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_entries.TryGetValue(targetId, out var entry) || entry.Target.Kind != TargetKind.Context) return NeoDesktopStatus.NotFound;
        var window = entry.Target.Window; if (window == 0 || !Native.IsWindow(window)) return NeoDesktopStatus.NotFound;
        var point = new Point { X = position.X, Y = position.Y }; if (!Native.ClientToScreen(window, &point)) return NeoDesktopStatus.Failed;
        var command = Native.TrackPopupMenu(entry.Menu, TpmReturnCommand | TpmRightButton, point.X, point.Y, 0, window, 0);
        if (command != 0) Activate(entry, command, window);
        return NeoDesktopStatus.Success;
    }

    public ValueTask DisposeAsync()
    {
        var value = _dispatcher;
        if (value is not null && !value.CheckAccess()) return value.InvokeAsync(DisposeOnDispatcher);
        DisposeOnDispatcher(); return ValueTask.CompletedTask;
    }

    private void Attach(Target target, nint menu)
    {
        var window = target.Window; if (window == 0 || !Native.IsWindow(window)) throw new InvalidOperationException("The native menu target window is unavailable.");
        if (!_windows.ContainsKey(window))
        {
            var first=_windows.Count==0;if(first){EnsureKeyboardHook();_subclassId=checked((nuint)Interlocked.Increment(ref s_nextSubclassId));if(!Presenters.TryAdd(_subclassId,this)){_subclassId=0;ReleaseKeyboardHook();throw new InvalidOperationException("Unable to register the native menu presenter.");}}
            if (!Native.SetWindowSubclass(window, &WindowSubclass, _subclassId, _subclassId)) { if(first){Presenters.TryRemove(_subclassId,out _);_subclassId=0;ReleaseKeyboardHook();} throw new InvalidOperationException("Unable to observe the native menu owner window."); }
            _windows.Add(window, new(target.Id, 1));
        }
        else { var attached = _windows[window]; _windows[window] = attached with { References = attached.References + 1 }; }
        if (!Native.SetMenu(window, menu)) { ReleaseWindow(window); throw new InvalidOperationException("Unable to attach the native window menu."); }
        _ = Native.DrawMenuBar(window);
    }

    private void Destroy(Entry entry, bool detach)
    {
        if (entry.Target.Kind != TargetKind.Context)
        {
            if (detach && Native.IsWindow(entry.Target.Window)) ApplyPreferred(entry.Target.Window);
            ReleaseWindow(entry.Target.Window);
        }
        if (entry.Menu != 0) _ = Native.DestroyMenu(entry.Menu);
    }

    private void ReleaseWindow(nint window)
    {
        if (!_windows.TryGetValue(window, out var attached)) return;
        if (attached.References > 1) { _windows[window] = attached with { References = attached.References - 1 }; return; }
        _windows.Remove(window); if (Native.IsWindow(window)) _ = Native.RemoveWindowSubclass(window, &WindowSubclass, _subclassId);
        if (_windows.Count == 0 && _subclassId != 0) { Presenters.TryRemove(_subclassId, out _); _subclassId = 0; ReleaseKeyboardHook(); }
    }

    private Entry? Preferred(nint window)=>_entries.Values.LastOrDefault(value=>value.Target.Window==window&&value.Target.Kind==TargetKind.Window)??_entries.Values.LastOrDefault(value=>value.Target.Window==window&&value.Target.Kind==TargetKind.Application);
    private void ApplyPreferred(nint window){if(!Native.IsWindow(window))return;_ = Native.SetMenu(window,Preferred(window)?.Menu??0);_ = Native.DrawMenuBar(window);}

    private CreatedMenu Build(IReadOnlyList<NeoMenuItem> items, long generation, nint owner, bool rootPopup)
    {
        var actions = new Dictionary<uint, ActionItem>(); var accelerators = new Dictionary<Accelerator, uint>(); uint next = 100;
        nint BuildLevel(IReadOnlyList<NeoMenuItem> values, bool popup)
        {
            var menu = popup ? Native.CreatePopupMenu() : Native.CreateMenu(); if (menu == 0) throw new InvalidOperationException("Unable to allocate a native menu.");
            try
            {
                foreach (var item in values)
                {
                    if (!item.IsVisible) continue;
                    if (item.Kind == NeoMenuItemKind.Separator) { if (!Native.AppendMenu(menu, MfSeparator, 0, null)) throw new InvalidOperationException("Unable to add a native menu separator."); continue; }
                    if (item.Kind == NeoMenuItemKind.Submenu)
                    {
                        var child = BuildLevel(item.Children, true); if (!Native.AppendMenu(menu, MfPopup | Flags(item), (nuint)child, item.Text)) { Native.DestroyMenu(child); throw new InvalidOperationException("Unable to add a native submenu."); } continue;
                    }
                    var id = next++; var text = item.Kind == NeoMenuItemKind.Role ? NeoMenuRolePresentation.RequireExplicitLabel(item, "Win32") : item.Text!; if (item.Accelerator is { } accelerator) text += "\t" + accelerator;
                    if (!Native.AppendMenu(menu, MfString | Flags(item), id, text)) throw new InvalidOperationException("Unable to add a native menu item.");
                    actions.Add(id, new(item.CommandId, item.Role, generation));
                    if (item.Accelerator is { } normalized)
                    {
                        if (!TryAccelerator(normalized, out var key)) throw new NotSupportedException($"Win32 cannot represent accelerator '{normalized}'.");
                        accelerators.Add(key, id);
                    }
                }
                return menu;
            }
            catch { Native.DestroyMenu(menu); throw; }
        }
        return new(BuildLevel(items, rootPopup), actions, accelerators);
    }

    private static uint Flags(NeoMenuItem item) => (item.IsEnabled ? 0u : MfDisabled | MfGray) | (item.IsChecked ? MfChecked : 0u);
    private void Receive(nint window, uint message, nuint wParam)
    {
        try
        {
            var entry = Preferred(window); if (entry is null) return;
            if (message == WmCommand) Activate(entry, unchecked((uint)(wParam & 0xffff)), window);
            else if (message == WmKeyDown && TryKeyMessage(unchecked((uint)wParam), out var accelerator) && entry.Accelerators.TryGetValue(accelerator, out var id)) Activate(entry, id, window);
        }
        catch { }
    }

    private bool ReceiveKey(nint source, uint key)
    {
        var window = Native.GetAncestor(source, 2); var entry = Preferred(window);
        if (entry is null || !TryKeyMessage(key, out var accelerator) || !entry.Accelerators.TryGetValue(accelerator, out var id)) return false;
        Activate(entry, id, window); return true;
    }

    private void Activate(Entry entry, uint nativeId, nint window)
    {
        if (!_entries.TryGetValue(entry.Target.Id, out var current) || current.Generation != entry.Generation || !entry.Actions.TryGetValue(nativeId, out var action)) return;
        if (action.CommandId is { } command) { try { _ = commands.ActivateAsync(command); } catch { } }
        else if (action.Role is { } role) ActivateRole(role, window);
    }

    private void ActivateRole(NeoMenuRole role, nint window)
    {
        try
        {
            var focus = Native.GetFocus();
            switch (role)
            {
                case NeoMenuRole.Copy: _ = Native.SendMessage(focus, 0x0301, 0, 0); break;
                case NeoMenuRole.Cut: _ = Native.SendMessage(focus, 0x0300, 0, 0); break;
                case NeoMenuRole.Paste: _ = Native.SendMessage(focus, 0x0302, 0, 0); break;
                case NeoMenuRole.SelectAll: _ = Native.SendMessage(focus, 0x00B1, 0, -1); break;
                case NeoMenuRole.Undo: _ = Native.SendMessage(focus, 0x0304, 0, 0); break;
                case NeoMenuRole.Redo: _ = Native.SendMessage(focus, 0x0454, 0, 0); break;
                case NeoMenuRole.Minimize: _ = Native.ShowWindow(window, 6); break;
                case NeoMenuRole.CloseWindow: _ = Native.PostMessage(window, WmClose, 0, 0); break;
                case NeoMenuRole.Quit: if (_application is { } app) _ = app.RequestQuitAsync(); break;
            }
        }
        catch { }
    }

    private Target ResolveTarget(string id)
    {
        var app = _application ?? throw new InvalidOperationException("The native menu presenter must be bound to an application.");
        if (id == "application") return new(id, TargetKind.Application, WindowHandle(app.MainWindow ?? throw new InvalidOperationException("An application menu requires a main window.")));
        if (id.StartsWith("window:", StringComparison.Ordinal)) { if (!app.TryGetWindow(id[7..], out var window) || window is null) throw new ArgumentException("The window menu target does not exist.", nameof(id)); return new(id, TargetKind.Window, WindowHandle(window)); }
        if (id.StartsWith("context:", StringComparison.Ordinal)) { if (!app.TryGetView(id[8..], out var view) || view?.OwnedWindow is null) throw new ArgumentException("The context menu target does not exist.", nameof(id)); return new(id, TargetKind.Context, WindowHandle(view.OwnedWindow)); }
        throw new ArgumentException("A native menu target must be 'application', 'window:<label>', or 'context:<view-label>'.", nameof(id));
    }

    private static nint WindowHandle(NeoWindow window) => window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value;
    private void EnsureAccess() { var value = _dispatcher ?? throw new InvalidOperationException("The native menu presenter is not bound to a UI dispatcher."); if (!value.CheckAccess()) throw new InvalidOperationException("Native menu presenter mutations require the NeoAstra UI dispatcher."); }
    private void OwnerDestroyed(nint window) { _windows.Remove(window); foreach (var id in _entries.Where(pair => pair.Value.Target.Window == window).Select(static pair => pair.Key).ToArray()) { if (_entries.Remove(id, out var entry) && entry.Menu != 0) _ = Native.DestroyMenu(entry.Menu); } if (_windows.Count == 0 && _subclassId != 0) { Presenters.TryRemove(_subclassId, out _); _subclassId = 0; ReleaseKeyboardHook(); } }
    private void DisposeOnDispatcher() { if (_disposed) return; _disposed = true; foreach (var entry in _entries.Values.ToArray()) Destroy(entry, true); _entries.Clear(); foreach (var window in _windows.Keys.ToArray()) ReleaseWindow(window); if (_subclassId != 0) Presenters.TryRemove(_subclassId, out _); _subclassId = 0; ReleaseKeyboardHook(); }

    private void EnsureKeyboardHook()
    {
        if (_hookThread != 0) return; var thread = Native.GetCurrentThreadId(); lock (HookLock)
        {
            if (!HookPresenters.TryGetValue(thread, out var presenters))
            {
                var hook = Native.SetWindowsHookEx(3, &GetMessageHook, 0, thread); if (hook == 0) throw new InvalidOperationException("Unable to install the local menu accelerator hook.");
                ThreadHooks.Add(thread, hook); HookPresenters.Add(thread, presenters = []);
            }
            presenters.Add(this); _hookThread = thread;
        }
    }
    private void ReleaseKeyboardHook()
    {
        if (_hookThread == 0) return; lock (HookLock)
        {
            if (HookPresenters.TryGetValue(_hookThread, out var presenters)) { presenters.Remove(this); if (presenters.Count == 0) { HookPresenters.Remove(_hookThread); if (ThreadHooks.Remove(_hookThread, out var hook)) _ = Native.UnhookWindowsHookEx(hook); } }
            _hookThread = 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowSubclass(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        try { if (Presenters.TryGetValue(data, out var presenter)) { if (message == WmNcDestroy) presenter.OwnerDestroyed(window); else presenter.Receive(window, message, wParam); } } catch { }
        return Native.DefSubclassProc(window, message, wParam, lParam);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint GetMessageHook(int code, nuint wParam, nint lParam)
    {
        try
        {
            if (code >= 0 && wParam != 0 && lParam != 0) { var message = (NativeMessage*)lParam; if (message->Message is WmKeyDown or 0x0104) { List<WindowsMenuPresenter>? values; lock (HookLock) values = HookPresenters.GetValueOrDefault(Native.GetCurrentThreadId())?.ToList(); if (values is not null && values.Any(value => value.ReceiveKey(message->Window, unchecked((uint)message->WParam)))) message->Message = 0; } }
        }
        catch { }
        return Native.CallNextHookEx(0, code, wParam, lParam);
    }

    private static bool TryKeyMessage(uint key, out Accelerator value)
    {
        var modifiers = (Native.GetKeyState(0x11) < 0 ? 1 : 0) | (Native.GetKeyState(0x12) < 0 ? 2 : 0) | (Native.GetKeyState(0x10) < 0 ? 4 : 0) | ((Native.GetKeyState(0x5B) < 0 || Native.GetKeyState(0x5C) < 0) ? 8 : 0); value = new(modifiers, key); return modifiers != 0;
    }
    private static bool TryAccelerator(string text, out Accelerator value)
    {
        var parts = text.Split('+'); var modifiers = 0; foreach (var part in parts[..^1]) modifiers |= part switch { "Ctrl" => 1, "Alt" => 2, "Shift" => 4, "Meta" => 8, _ => 0 }; var key = parts[^1];
        uint code = key.Length == 1 ? char.ToUpperInvariant(key[0]) : key.StartsWith('F') && int.TryParse(key.AsSpan(1), out var number) && number is >= 1 and <= 24 ? (uint)(0x6F + number) : key switch { "Escape" => 0x1B, "Space" => 0x20, "Enter" => 0x0D, "Tab" => 0x09, "Delete" => 0x2E, "Left" => 0x25, "Up" => 0x26, "Right" => 0x27, "Down" => 0x28, _ => 0 }; value = new(modifiers, code); return code != 0;
    }
    private enum TargetKind { Application, Window, Context }
    private sealed record Target(string Id, TargetKind Kind, nint Window);
    private sealed record Entry(Target Target, nint Menu, IReadOnlyDictionary<uint, ActionItem> Actions, IReadOnlyDictionary<Accelerator, uint> Accelerators, long Generation);
    private sealed record ActionItem(string? CommandId, NeoMenuRole? Role, long Generation);
    private readonly record struct Accelerator(int Modifiers, uint Key);
    private readonly record struct CreatedMenu(nint Menu, IReadOnlyDictionary<uint, ActionItem> Actions, IReadOnlyDictionary<Accelerator, uint> Accelerators);
    private readonly record struct AttachedWindow(string TargetId, int References);
    [StructLayout(LayoutKind.Sequential)] private struct Point { internal int X; internal int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMessage { internal nint Window; internal uint Message; internal nuint WParam; internal nint LParam; internal uint Time; internal Point Point; internal uint Private; }

    private static partial class Native
    {
        [LibraryImport("user32.dll")] internal static partial nint CreateMenu();
        [LibraryImport("user32.dll")] internal static partial nint CreatePopupMenu();
        [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool AppendMenu(nint menu, uint flags, nuint id, string? text);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool DestroyMenu(nint menu);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool SetMenu(nint window, nint menu);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool DrawMenuBar(nint window);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool IsWindow(nint window);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool ClientToScreen(nint window, Point* point);
        [LibraryImport("user32.dll", EntryPoint = "TrackPopupMenu")] internal static partial uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rectangle);
        [LibraryImport("user32.dll")] internal static partial nint GetFocus();
        [LibraryImport("user32.dll", EntryPoint = "SendMessageW")] internal static partial nint SendMessage(nint window, uint message, nuint wParam, nint lParam);
        [LibraryImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool ShowWindow(nint window, int command);
        [LibraryImport("user32.dll")] internal static partial short GetKeyState(int key);
        [LibraryImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool SetWindowSubclass(nint window, delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nuint, nint> callback, nuint id, nuint data);
        [LibraryImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool RemoveWindowSubclass(nint window, delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nuint, nint> callback, nuint id);
        [LibraryImport("comctl32.dll")] internal static partial nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
        [LibraryImport("kernel32.dll")] internal static partial uint GetCurrentThreadId();
        [LibraryImport("user32.dll")] internal static partial nint GetAncestor(nint window, uint flags);
        [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW")] internal static partial nint SetWindowsHookEx(int id, delegate* unmanaged[Stdcall]<int, nuint, nint, nint> callback, nint module, uint thread);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool UnhookWindowsHookEx(nint hook);
        [LibraryImport("user32.dll")] internal static partial nint CallNextHookEx(nint hook, int code, nuint wParam, nint lParam);
    }
}
