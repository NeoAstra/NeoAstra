// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using global::NeoAstra.Desktop.Menus;

namespace NeoAstra.Desktop.Tray;

internal sealed unsafe partial class MacTrayPresenter(NeoCommandService commands, NeoDispatcher? dispatcher) : INeoTrayPresenter, INeoApplicationBoundDesktopService
{
    private static readonly object ClassLock = new();
    private static readonly ConcurrentDictionary<nint, MacTrayPresenter> Owners = new();
    private static nint s_targetClass;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<nint, MenuAction> _menuActions = [];
    private readonly Dictionary<nint, string> _buttons = [];
    private NeoDispatcher? _dispatcher = dispatcher;
    private NeoApplication? _application;
    private nint _target, _statusBar;
    private bool _disposed;

    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Limited, 1, 0, "Native macOS NSStatusItem lifecycle, template images, tooltip, native menus, and ordered primary/secondary activation. AppKit role selectors provide native behavior but not a reliable complete localized title set, so role labels must be supplied by the application. AppKit exposes status-item bounds indirectly through its button window; NeoAstra does not promise stable cross-Space coordinates, and status items have no OS attention request semantic.");
    public event Action<string, bool>? Activated;

    void INeoApplicationBoundDesktopService.BindApplication(NeoApplication application) { ArgumentNullException.ThrowIfNull(application); if (_application is not null && !ReferenceEquals(_application, application)) throw new InvalidOperationException("The status presenter is already bound to an application."); if (_dispatcher is not null && !ReferenceEquals(_dispatcher, application.Dispatcher)) throw new InvalidOperationException("The status presenter is already bound to another dispatcher."); _application = application; _dispatcher = application.Dispatcher; }

    public void Set(NeoTrayItemOptions options)
    {
        EnsureAccess(); ObjectDisposedException.ThrowIf(_disposed, this); EnsureTarget();
        var item = Native.SendDouble(_statusBar, Native.GetSelector("statusItemWithLength:"), -1); if (item == 0) throw new InvalidOperationException("Unable to allocate a macOS status item."); var button = Native.Send(item, Native.GetSelector("button")); if (button == 0) { Native.SendVoidArg(_statusBar, Native.GetSelector("removeStatusItem:"), item); throw new InvalidOperationException("The macOS status item has no button."); }
        var menuItems = new List<nint>(); nint menu = 0, image = 0;
        try
        {
            Native.SendVoidArg(button, Native.GetSelector("setTarget:"), _target); Native.SendVoidArg(button, Native.GetSelector("setAction:"), Native.GetSelector("neoastraStatusActivated:")); Native.SendVoidULong(button, Native.GetSelector("sendActionOn:"), (1u << 1) | (1u << 3));
            if (options.ToolTip is { } tooltip) SetString(button, "setToolTip:", tooltip);
            if (options.IconPath is { } path)
            {
                if (!File.Exists(path)) throw new FileNotFoundException("The status-item image does not exist.", path); using var value = NativeString.Create(path); image = Native.SendArg(Native.Send(Native.GetClass("NSImage"), Native.GetSelector("alloc")), Native.GetSelector("initWithContentsOfFile:"), value.Value); if (image == 0) throw new ArgumentException("AppKit could not decode the status-item image.", nameof(options)); Native.SendVoidBool(image, Native.GetSelector("setTemplate:"), options.IsTemplateImage); Native.SendVoidArg(button, Native.GetSelector("setImage:"), image);
            }
            else SetString(button, "setTitle:", "●");
            if (options.Menu.Count != 0) menu = BuildMenu(options.Menu, menuItems);
            var replacement = new Entry(item, button, image, menu, menuItems, options); _buttons[button] = options.Id;
            _entries.TryGetValue(options.Id, out var previous); _entries[options.Id] = replacement; if (previous is not null) Destroy(previous);
            item = 0; image = 0; menu = 0;
        }
        catch { if (menu != 0) Native.SendVoid(menu, Native.GetSelector("release")); if (image != 0) Native.SendVoid(image, Native.GetSelector("release")); Native.SendVoidArg(_statusBar, Native.GetSelector("removeStatusItem:"), item); foreach (var native in menuItems) _menuActions.Remove(native); throw; }
    }

    public bool Remove(string id) { EnsureAccess(); if (!_entries.Remove(id, out var entry)) return false; Destroy(entry); return true; }
    public ValueTask DisposeAsync() { var value = _dispatcher; if (value is not null && !value.CheckAccess()) return value.InvokeAsync(DisposeOnDispatcher); DisposeOnDispatcher(); return ValueTask.CompletedTask; }

    private nint BuildMenu(IReadOnlyList<NeoMenuItem> items, List<nint> actionItems)
    {
        var menu = Allocate("NSMenu"); Native.SendVoidBool(menu, Native.GetSelector("setAutoenablesItems:"), false);
        try
        {
            foreach (var item in items)
            {
                if (!item.IsVisible) continue; nint native;
                if (item.Kind == NeoMenuItemKind.Separator) native = Native.Send(Native.GetClass("NSMenuItem"), Native.GetSelector("separatorItem"));
                else
                {
                    using var title = NativeString.Create(item.Kind == NeoMenuItemKind.Role ? NeoMenuRolePresentation.RequireExplicitLabel(item, "AppKit") : item.Text!); using var key = NativeString.Create(string.Empty); var selector = item.Kind == NeoMenuItemKind.Role ? Native.GetSelector(RoleSelector(item.Role!.Value)) : Native.GetSelector("neoastraTrayMenuActivated:");
                    native = Native.Send3(Native.Send(Native.GetClass("NSMenuItem"), Native.GetSelector("alloc")), Native.GetSelector("initWithTitle:action:keyEquivalent:"), title.Value, selector, key.Value); Native.SendVoidBool(native, Native.GetSelector("setEnabled:"), item.IsEnabled); Native.SendVoidLong(native, Native.GetSelector("setState:"), item.IsChecked ? 1 : 0);
                    if (item.Kind == NeoMenuItemKind.Submenu) { var child = BuildMenu(item.Children, actionItems); Native.SendVoidArg(native, Native.GetSelector("setSubmenu:"), child); Native.SendVoid(child, Native.GetSelector("release")); }
                    else if (item.Kind == NeoMenuItemKind.Command) { Native.SendVoidArg(native, Native.GetSelector("setTarget:"), _target); _menuActions[native] = new(item.CommandId, item.Role); actionItems.Add(native); }
                    else Native.SendVoidArg(native, Native.GetSelector("setTarget:"), 0);
                }
                Native.SendVoidArg(menu, Native.GetSelector("addItem:"), native); if (item.Kind != NeoMenuItemKind.Separator) Native.SendVoid(native, Native.GetSelector("release"));
            }
            return menu;
        }
        catch { Native.SendVoid(menu, Native.GetSelector("release")); throw; }
    }

    private void StatusActivated(nint sender)
    {
        if (!_buttons.TryGetValue(sender, out var id) || !_entries.TryGetValue(id, out var entry)) return; var app = Native.Send(Native.GetClass("NSApplication"), Native.GetSelector("sharedApplication")); var currentEvent = Native.Send(app, Native.GetSelector("currentEvent")); var secondary = currentEvent != 0 && Native.SendLong(currentEvent, Native.GetSelector("buttonNumber")) == 1; try { Activated?.Invoke(id, secondary); } catch { } if (entry.Menu != 0) Native.SendVoidArg(entry.Item, Native.GetSelector("popUpStatusItemMenu:"), entry.Menu);
    }
    private void MenuActivated(nint sender) { if (!_menuActions.TryGetValue(sender, out var action) || action.CommandId is null) return; try { _ = commands.ActivateAsync(action.CommandId); } catch { } }

    private void Destroy(Entry entry) { _buttons.Remove(entry.Button); foreach (var item in entry.ActionItems) _menuActions.Remove(item); Native.SendVoidArg(_statusBar, Native.GetSelector("removeStatusItem:"), entry.Item); if (entry.Menu != 0) Native.SendVoid(entry.Menu, Native.GetSelector("release")); if (entry.Image != 0) Native.SendVoid(entry.Image, Native.GetSelector("release")); }
    private void EnsureTarget()
    {
        if (_target != 0) return; EnsureClass(); _target = Native.Send(Native.Send(s_targetClass, Native.GetSelector("alloc")), Native.GetSelector("init")); _statusBar = Native.Send(Native.GetClass("NSStatusBar"), Native.GetSelector("systemStatusBar")); if (_target == 0 || _statusBar == 0 || !Owners.TryAdd(_target, this)) throw new InvalidOperationException("Unable to initialize the macOS status callback target.");
    }
    private static void EnsureClass()
    {
        if (s_targetClass != 0) return; lock (ClassLock) { if (s_targetClass != 0) return; var name = "NeoAstraStatusTarget_v1"u8; fixed (byte* pointer = name) { var value = Native.objc_lookUpClass(pointer); if (value == 0) { value = Native.objc_allocateClassPair(Native.GetClass("NSObject"), pointer, 0); Add(value, "neoastraStatusActivated:", &StatusCallback); Add(value, "neoastraTrayMenuActivated:", &MenuCallback); Native.objc_registerClassPair(value); } s_targetClass = value; } }
        static void Add(nint type, string selector, delegate* unmanaged[Cdecl]<nint, nint, nint, void> callback) { var encoding = "v@:@"u8; fixed (byte* pointer = encoding) if (!Native.class_addMethod(type, Native.GetSelector(selector), (nint)callback, pointer)) throw new InvalidOperationException("Unable to define a status callback."); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static void StatusCallback(nint self, nint selector, nint sender) { try { if (Owners.TryGetValue(self, out var owner)) owner.StatusActivated(sender); } catch { } }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static void MenuCallback(nint self, nint selector, nint sender) { try { if (Owners.TryGetValue(self, out var owner)) owner.MenuActivated(sender); } catch { } }
    private void DisposeOnDispatcher() { if (_disposed) return; _disposed = true; Activated = null; foreach (var entry in _entries.Values) Destroy(entry); _entries.Clear(); _buttons.Clear(); _menuActions.Clear(); if (_target != 0) { Owners.TryRemove(_target, out _); Native.SendVoid(_target, Native.GetSelector("release")); } _target = 0; _statusBar = 0; }
    private void EnsureAccess() { var value = _dispatcher ?? throw new InvalidOperationException("The status presenter is not bound to a UI dispatcher."); if (!value.CheckAccess()) throw new InvalidOperationException("Native status-item mutations require the NeoAstra UI dispatcher."); }
    private static string RoleSelector(NeoMenuRole role) => role switch { NeoMenuRole.Copy => "copy:", NeoMenuRole.Cut => "cut:", NeoMenuRole.Paste => "paste:", NeoMenuRole.SelectAll => "selectAll:", NeoMenuRole.Undo => "undo:", NeoMenuRole.Redo => "redo:", NeoMenuRole.Minimize => "performMiniaturize:", NeoMenuRole.CloseWindow => "performClose:", NeoMenuRole.Quit => "terminate:", _ => throw new ArgumentOutOfRangeException(nameof(role)) };
    private static nint Allocate(string className) { var result = Native.Send(Native.Send(Native.GetClass(className), Native.GetSelector("alloc")), Native.GetSelector("init")); return result != 0 ? result : throw new InvalidOperationException($"Unable to allocate {className}."); }
    private static void SetString(nint target, string selector, string value) { using var text = NativeString.Create(value); Native.SendVoidArg(target, Native.GetSelector(selector), text.Value); }

    private sealed record Entry(nint Item, nint Button, nint Image, nint Menu, IReadOnlyList<nint> ActionItems, NeoTrayItemOptions Options);
    private sealed record MenuAction(string? CommandId, NeoMenuRole? Role);
    private readonly struct NativeString(nint value) : IDisposable { internal nint Value { get; } = value; internal static NativeString Create(string value) { var bytes = Encoding.UTF8.GetBytes(value + '\0'); fixed (byte* pointer = bytes) { var result = Native.SendUtf8(Native.GetClass("NSString"), Native.GetSelector("stringWithUTF8String:"), pointer); Native.SendVoid(result, Native.GetSelector("retain")); return new(result); } } public void Dispose() { if (Value != 0) Native.SendVoid(Value, Native.GetSelector("release")); } }
    private static partial class Native
    {
        [LibraryImport("/usr/lib/libobjc.A.dylib")] internal static partial nint objc_lookUpClass(byte* name); [LibraryImport("/usr/lib/libobjc.A.dylib")] internal static partial nint objc_allocateClassPair(nint superclass, byte* name, nuint extra); [LibraryImport("/usr/lib/libobjc.A.dylib")] internal static partial void objc_registerClassPair(nint value); [LibraryImport("/usr/lib/libobjc.A.dylib")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool class_addMethod(nint type, nint selector, nint implementation, byte* encoding); [LibraryImport("/usr/lib/libobjc.A.dylib")] internal static partial nint objc_getClass(byte* name); [LibraryImport("/usr/lib/libobjc.A.dylib")] internal static partial nint sel_registerName(byte* name);
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial nint Send(nint target, nint selector); [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial nint SendArg(nint target, nint selector, nint value); [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial nint Send3(nint target, nint selector, nint first, nint second, nint third); [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial nint SendDouble(nint target, nint selector, double value); [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial nint SendLong(nint target, nint selector); [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial void SendVoid(nint target, nint selector); [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial void SendVoidArg(nint target, nint selector, nint value); [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial void SendVoidBool(nint target, nint selector, [MarshalAs(UnmanagedType.I1)] bool value); [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial void SendVoidLong(nint target, nint selector, nint value); [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial void SendVoidULong(nint target, nint selector, nuint value); [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial nint SendUtf8(nint target, nint selector, byte* value);
        internal static nint GetClass(string name) { var bytes = Encoding.UTF8.GetBytes(name + '\0'); fixed (byte* pointer = bytes) return objc_getClass(pointer); } internal static nint GetSelector(string name) { var bytes = Encoding.UTF8.GetBytes(name + '\0'); fixed (byte* pointer = bytes) return sel_registerName(pointer); }
    }
}
