// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace NeoAstra.Desktop.Menus;

internal sealed unsafe partial class MacMenuPresenter(NeoCommandService commands, NeoDispatcher? dispatcher) : INeoMenuPresenter, INeoApplicationBoundDesktopService
{
    private static readonly object ClassLock = new();
    private static readonly ConcurrentDictionary<nint, MacMenuPresenter> Owners = new();
    private static nint s_delegateClass;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<nint, Activation> _items = [];
    private readonly HashSet<nint> _observedWindows = [];
    private NeoDispatcher? _dispatcher = dispatcher;
    private NeoApplication? _application;
    private nint _delegate;
    private nint _notificationCenter;
    private long _generation;
    private bool _disposed;

    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Limited, 1, 0, "Native AppKit application menu bar, key-window menu switching, context menus, responder-chain roles, key equivalents, and deterministic notification/delegate teardown. AppKit selectors provide native behavior but not a reliable complete localized title set, so role labels must be supplied by the application.");

    void INeoApplicationBoundDesktopService.BindApplication(NeoApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (_application is not null && !ReferenceEquals(_application, application)) throw new InvalidOperationException("The menu presenter is already bound to an application.");
        if (_dispatcher is not null && !ReferenceEquals(_dispatcher, application.Dispatcher)) throw new InvalidOperationException("The menu presenter is already bound to another dispatcher.");
        _application = application; _dispatcher = application.Dispatcher;
    }

    public void SetMenu(string targetId, IReadOnlyList<NeoMenuItem> items)
    {
        EnsureAccess(); ObjectDisposedException.ThrowIf(_disposed, this); EnsureDelegate();
        var target = ResolveTarget(targetId); var generation = checked(++_generation); var itemPointers = new List<nint>(); nint menu = 0;
        try
        {
            menu = BuildMenu(items, generation, itemPointers);
            var replacement = new Entry(target, menu, itemPointers, generation);
            Attach(replacement);
            if (_entries.Remove(targetId, out var old)) Destroy(old, detach: false);
            _entries[targetId] = replacement;
            if(target.Kind!=TargetKind.Context){var app=Native.Send(Native.GetClass("NSApplication"),Native.GetSelector("sharedApplication"));Native.SendVoidArg(app,Native.GetSelector("setMainMenu:"),PreferredMainMenu(0));}
        }
        catch { foreach (var item in itemPointers) _items.Remove(item); if (menu != 0) Native.SendVoid(menu, Native.GetSelector("release")); throw; }
    }

    public void RemoveMenu(string targetId) { EnsureAccess(); if (_entries.Remove(targetId, out var entry)) Destroy(entry, detach: true); }

    public NeoDesktopStatus ShowContextMenu(string targetId, NeoPoint position)
    {
        EnsureAccess(); ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_entries.TryGetValue(targetId, out var entry) || entry.Target.Kind != TargetKind.Context) return NeoDesktopStatus.NotFound;
        if (entry.Target.Native == 0) return NeoDesktopStatus.NotFound;
        Native.SendPopup(entry.Menu, Native.GetSelector("popUpMenuPositioningItem:atLocation:inView:"), 0, new(position.X, position.Y), entry.Target.Native);
        return NeoDesktopStatus.Success;
    }

    public ValueTask DisposeAsync()
    {
        var value = _dispatcher; if (value is not null && !value.CheckAccess()) return value.InvokeAsync(DisposeOnDispatcher); DisposeOnDispatcher(); return ValueTask.CompletedTask;
    }

    private nint BuildMenu(IReadOnlyList<NeoMenuItem> items, long generation, List<nint> itemPointers)
    {
        var menu = Allocate("NSMenu"); Native.SendVoidBool(menu, Native.GetSelector("setAutoenablesItems:"), false);
        try
        {
            foreach (var item in items)
            {
                if (!item.IsVisible) continue;
                nint nativeItem;
                if (item.Kind == NeoMenuItemKind.Separator) nativeItem = Native.Send(Native.GetClass("NSMenuItem"), Native.GetSelector("separatorItem"));
                else
                {
                    using var title = NativeString.Create(item.Kind == NeoMenuItemKind.Role ? NeoMenuRolePresentation.RequireExplicitLabel(item, "AppKit") : item.Text!); using var key = NativeString.Create(KeyEquivalent(item.Accelerator));
                    var action = item.Kind == NeoMenuItemKind.Role ? Native.GetSelector(RoleSelector(item.Role!.Value)) : Native.GetSelector("neoastraMenuActivated:");
                    nativeItem = Native.Send3(Native.Send(Native.GetClass("NSMenuItem"), Native.GetSelector("alloc")), Native.GetSelector("initWithTitle:action:keyEquivalent:"), title.Value, action, key.Value);
                    if (nativeItem == 0) throw new InvalidOperationException("Unable to allocate an AppKit menu item.");
                    Native.SendVoidBool(nativeItem, Native.GetSelector("setEnabled:"), item.IsEnabled); Native.SendVoidLong(nativeItem, Native.GetSelector("setState:"), item.IsChecked ? 1 : 0);
                    if (item.Accelerator is { } accelerator) Native.SendVoidULong(nativeItem, Native.GetSelector("setKeyEquivalentModifierMask:"), ModifierMask(accelerator));
                    if (item.Kind == NeoMenuItemKind.Submenu) { var child = BuildMenu(item.Children, generation, itemPointers); Native.SendVoidArg(nativeItem, Native.GetSelector("setSubmenu:"), child); Native.SendVoid(child, Native.GetSelector("release")); }
                    else if (item.Kind == NeoMenuItemKind.Command) { Native.SendVoidArg(nativeItem, Native.GetSelector("setTarget:"), _delegate); _items[nativeItem] = new(item.CommandId!, generation); itemPointers.Add(nativeItem); }
                    else Native.SendVoidArg(nativeItem, Native.GetSelector("setTarget:"), 0);
                }
                Native.SendVoidArg(menu, Native.GetSelector("addItem:"), nativeItem);
                if (item.Kind != NeoMenuItemKind.Separator) Native.SendVoid(nativeItem, Native.GetSelector("release"));
            }
            return menu;
        }
        catch { Native.SendVoid(menu, Native.GetSelector("release")); throw; }
    }

    private void Attach(Entry entry)
    {
        var app = Native.Send(Native.GetClass("NSApplication"), Native.GetSelector("sharedApplication"));
        if (entry.Target.Kind == TargetKind.Application) Native.SendVoidArg(app, Native.GetSelector("setMainMenu:"), entry.Menu);
        else if (entry.Target.Kind == TargetKind.Window)
        {
            ObserveWindow(entry.Target.Native); if (Native.SendBool(entry.Target.Native, Native.GetSelector("isKeyWindow"))) Native.SendVoidArg(app, Native.GetSelector("setMainMenu:"), entry.Menu);
        }
    }

    private void ObserveWindow(nint window)
    {
        if (!_observedWindows.Add(window)) return;
        using var key = NativeString.Create("NSWindowDidBecomeKeyNotification"); using var close = NativeString.Create("NSWindowWillCloseNotification");
        Native.SendObserver(_notificationCenter, Native.GetSelector("addObserver:selector:name:object:"), _delegate, Native.GetSelector("neoastraWindowBecameKey:"), key.Value, window);
        Native.SendObserver(_notificationCenter, Native.GetSelector("addObserver:selector:name:object:"), _delegate, Native.GetSelector("neoastraWindowWillClose:"), close.Value, window);
    }

    private void Destroy(Entry entry, bool detach)
    {
        foreach (var item in entry.Items) _items.Remove(item);
        if (detach && entry.Target.Kind != TargetKind.Context)
        {
            var app = Native.Send(Native.GetClass("NSApplication"), Native.GetSelector("sharedApplication")); var current = Native.Send(app, Native.GetSelector("mainMenu")); if (current == entry.Menu) Native.SendVoidArg(app, Native.GetSelector("setMainMenu:"), PreferredMainMenu(entry.Target.Native));
            if (entry.Target.Kind == TargetKind.Window && _observedWindows.Remove(entry.Target.Native)) Native.SendRemoveObserver(_notificationCenter, Native.GetSelector("removeObserver:name:object:"), _delegate, 0, entry.Target.Native);
        }
        Native.SendVoid(entry.Menu, Native.GetSelector("release"));
    }

    private void Activated(nint sender)
    {
        if (!_items.TryGetValue(sender, out var activation) || !_entries.Values.Any(entry => entry.Generation == activation.Generation)) return;
        try { _ = commands.ActivateAsync(activation.CommandId); } catch { }
    }

    private void WindowBecameKey(nint notification)
    {
        var window = Native.Send(notification, Native.GetSelector("object")); var entry = _entries.Values.LastOrDefault(value => value.Target.Kind == TargetKind.Window && value.Target.Native == window); if (entry is null) return;
        var app = Native.Send(Native.GetClass("NSApplication"), Native.GetSelector("sharedApplication")); Native.SendVoidArg(app, Native.GetSelector("setMainMenu:"), entry.Menu);
    }

    private void WindowWillClose(nint notification)
    {
        var window = Native.Send(notification, Native.GetSelector("object")); _observedWindows.Remove(window); Native.SendRemoveObserver(_notificationCenter, Native.GetSelector("removeObserver:name:object:"), _delegate, 0, window); foreach (var id in _entries.Where(pair => pair.Value.Target.Native == window).Select(static pair => pair.Key).ToArray()) if (_entries.Remove(id, out var entry)) Destroy(entry, false); var app = Native.Send(Native.GetClass("NSApplication"), Native.GetSelector("sharedApplication")); Native.SendVoidArg(app, Native.GetSelector("setMainMenu:"), PreferredMainMenu(window));
    }

    private nint PreferredMainMenu(nint excludedWindow)
    {
        foreach (var entry in _entries.Values)
            if (entry.Target.Kind == TargetKind.Window && entry.Target.Native != excludedWindow && Native.SendBool(entry.Target.Native, Native.GetSelector("isKeyWindow"))) return entry.Menu;
        return _entries.TryGetValue("application", out var application) ? application.Menu : 0;
    }

    private Target ResolveTarget(string id)
    {
        var app = _application ?? throw new InvalidOperationException("The menu presenter must be bound to an application.");
        if (id == "application") return new(id, TargetKind.Application, 0);
        if (id.StartsWith("window:", StringComparison.Ordinal)) { if (!app.TryGetWindow(id[7..], out var window) || window is null) throw new ArgumentException("The window menu target does not exist.", nameof(id)); return new(id, TargetKind.Window, window.GetNativeHandle(NeoNativeHandleKind.CocoaNSWindow).Value); }
        if (id.StartsWith("context:", StringComparison.Ordinal)) { if (!app.TryGetView(id[8..], out var view) || view is null) throw new ArgumentException("The context menu target does not exist.", nameof(id)); return new(id, TargetKind.Context, view.GetNativeHandle(NeoNativeHandleKind.WkWebView).Value); }
        throw new ArgumentException("A native menu target must be 'application', 'window:<label>', or 'context:<view-label>'.", nameof(id));
    }

    private void EnsureDelegate()
    {
        if (_delegate != 0) return; EnsureDelegateClass(); _delegate = Native.Send(Native.Send(s_delegateClass, Native.GetSelector("alloc")), Native.GetSelector("init")); _notificationCenter = Native.Send(Native.GetClass("NSNotificationCenter"), Native.GetSelector("defaultCenter")); if (_delegate == 0 || _notificationCenter == 0 || !Owners.TryAdd(_delegate, this)) throw new InvalidOperationException("Unable to initialize the AppKit menu callback target.");
    }

    private static void EnsureDelegateClass()
    {
        if (s_delegateClass != 0) return; lock (ClassLock)
        {
            if (s_delegateClass != 0) return; var name = "NeoAstraMenuTarget_v1"u8; fixed (byte* namePointer = name)
            {
                var value = Native.objc_lookUpClass(namePointer); if (value == 0)
                {
                    value = Native.objc_allocateClassPair(Native.GetClass("NSObject"), namePointer, 0); if (value == 0) throw new InvalidOperationException("Unable to allocate the AppKit menu callback class.");
                    Add(value, "neoastraMenuActivated:", &MenuActivated); Add(value, "neoastraWindowBecameKey:", &WindowBecameKeyCallback); Add(value, "neoastraWindowWillClose:", &WindowWillCloseCallback); Native.objc_registerClassPair(value);
                }
                s_delegateClass = value;
            }
        }
        static void Add(nint type, string selector, delegate* unmanaged[Cdecl]<nint, nint, nint, void> callback) { var encoding = "v@:@"u8; fixed (byte* pointer = encoding) if (!Native.class_addMethod(type, Native.GetSelector(selector), (nint)callback, pointer)) throw new InvalidOperationException("Unable to define an AppKit menu callback."); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static void MenuActivated(nint self, nint selector, nint sender) { try { if (Owners.TryGetValue(self, out var owner)) owner.Activated(sender); } catch { } }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static void WindowBecameKeyCallback(nint self, nint selector, nint notification) { try { if (Owners.TryGetValue(self, out var owner)) owner.WindowBecameKey(notification); } catch { } }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static void WindowWillCloseCallback(nint self, nint selector, nint notification) { try { if (Owners.TryGetValue(self, out var owner)) owner.WindowWillClose(notification); } catch { } }

    private void DisposeOnDispatcher()
    {
        if (_disposed) return; _disposed = true; foreach (var entry in _entries.Values.ToArray()) Destroy(entry, true); _entries.Clear(); _items.Clear(); _observedWindows.Clear(); if (_delegate != 0) { Owners.TryRemove(_delegate, out _); if (_notificationCenter != 0) Native.SendVoidArg(_notificationCenter, Native.GetSelector("removeObserver:"), _delegate); Native.SendVoid(_delegate, Native.GetSelector("release")); } _delegate = 0; _notificationCenter = 0;
    }
    private void EnsureAccess() { var value = _dispatcher ?? throw new InvalidOperationException("The native menu presenter is not bound to a UI dispatcher."); if (!value.CheckAccess()) throw new InvalidOperationException("Native menu presenter mutations require the NeoAstra UI dispatcher."); }
    private static nint Allocate(string className) { var value = Native.Send(Native.Send(Native.GetClass(className), Native.GetSelector("alloc")), Native.GetSelector("init")); return value != 0 ? value : throw new InvalidOperationException($"Unable to allocate {className}."); }
    private static string RoleSelector(NeoMenuRole role) => role switch { NeoMenuRole.Copy => "copy:", NeoMenuRole.Cut => "cut:", NeoMenuRole.Paste => "paste:", NeoMenuRole.SelectAll => "selectAll:", NeoMenuRole.Undo => "undo:", NeoMenuRole.Redo => "redo:", NeoMenuRole.Minimize => "performMiniaturize:", NeoMenuRole.CloseWindow => "performClose:", NeoMenuRole.Quit => "terminate:", _ => throw new ArgumentOutOfRangeException(nameof(role)) };
    private static string KeyEquivalent(string? accelerator) { if (accelerator is null) return string.Empty; var key = accelerator.Split('+')[^1]; if (key.Length == 1) return key.ToLowerInvariant(); return key switch { "Enter" => "\r", "Tab" => "\t", "Escape" => "\u001b", "Space" => " ", "Delete" => "\u007f", "Left" => "\uf702", "Right" => "\uf703", "Down" => "\uf701", "Up" => "\uf700", _ when key.StartsWith('F') && int.TryParse(key.AsSpan(1), out var value) && value is >= 1 and <= 24 => char.ConvertFromUtf32(0xF703 + value), _ => string.Empty }; }
    private static nuint ModifierMask(string accelerator) { nuint mask = 0; foreach (var part in accelerator.Split('+')[..^1]) mask |= part switch { "Ctrl" => 1u << 18, "Alt" => 1u << 19, "Shift" => 1u << 17, "Meta" => 1u << 20, _ => 0 }; return mask; }

    private enum TargetKind { Application, Window, Context }
    private sealed record Target(string Id, TargetKind Kind, nint Native);
    private sealed record Entry(Target Target, nint Menu, IReadOnlyList<nint> Items, long Generation);
    private sealed record Activation(string CommandId, long Generation);
    [StructLayout(LayoutKind.Sequential)] private readonly struct Point(double x, double y) { private readonly double X = x, Y = y; }
    private readonly struct NativeString(nint value) : IDisposable { internal nint Value { get; } = value; internal static NativeString Create(string value) { var bytes = Encoding.UTF8.GetBytes(value + '\0'); fixed (byte* pointer = bytes) { var result = Native.SendUtf8(Native.GetClass("NSString"), Native.GetSelector("stringWithUTF8String:"), pointer); Native.SendVoid(result, Native.GetSelector("retain")); return new(result); } } public void Dispose() { if (Value != 0) Native.SendVoid(Value, Native.GetSelector("release")); } }

    private static partial class Native
    {
        [LibraryImport("/usr/lib/libobjc.A.dylib")] internal static partial nint objc_lookUpClass(byte* name);
        [LibraryImport("/usr/lib/libobjc.A.dylib")] internal static partial nint objc_allocateClassPair(nint superclass, byte* name, nuint extraBytes);
        [LibraryImport("/usr/lib/libobjc.A.dylib")] internal static partial void objc_registerClassPair(nint value);
        [LibraryImport("/usr/lib/libobjc.A.dylib")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool class_addMethod(nint type, nint selector, nint implementation, byte* types);
        [LibraryImport("/usr/lib/libobjc.A.dylib")] internal static partial nint objc_getClass(byte* name);
        [LibraryImport("/usr/lib/libobjc.A.dylib")] internal static partial nint sel_registerName(byte* name);
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial nint Send(nint target, nint selector);
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial nint Send3(nint target, nint selector, nint first, nint second, nint third);
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial void SendVoid(nint target, nint selector);
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial void SendVoidArg(nint target, nint selector, nint value);
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial void SendVoidBool(nint target, nint selector, [MarshalAs(UnmanagedType.I1)] bool value);
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial void SendVoidLong(nint target, nint selector, nint value);
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial void SendVoidULong(nint target, nint selector, nuint value);
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] internal static partial bool SendBool(nint target, nint selector);
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial nint SendUtf8(nint target, nint selector, byte* value);
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial void SendPopup(nint target, nint selector, nint item, Point point, nint view);
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial void SendObserver(nint target, nint selector, nint observer, nint callback, nint name, nint value);
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial void SendRemoveObserver(nint target, nint selector, nint observer, nint name, nint value);
        internal static nint GetClass(string name) { var bytes = Encoding.UTF8.GetBytes(name + '\0'); fixed (byte* pointer = bytes) return objc_getClass(pointer); }
        internal static nint GetSelector(string name) { var bytes = Encoding.UTF8.GetBytes(name + '\0'); fixed (byte* pointer = bytes) return sel_registerName(pointer); }
    }
}
