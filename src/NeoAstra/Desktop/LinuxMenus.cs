// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NeoAstra.Desktop.Menus;

internal sealed unsafe partial class LinuxMenuPresenter(NeoCommandService commands, NeoDispatcher? dispatcher) : INeoMenuPresenter, INeoApplicationBoundDesktopService
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<nint, Host> _hosts = [];
    private NeoDispatcher? _dispatcher = dispatcher;
    private NeoApplication? _application;
    private long _generation;
    private bool _disposed;

    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Limited, 1, 0, "Native GTK3 application/window menu bars and context menus with window-scope override/fallback, GTK accelerators, WebKit edit roles, generation-safe callbacks, and deterministic widget teardown. GTK3 stock resources localize Copy, Cut, Paste, Select All, Undo, Redo, Close, and Quit; Minimize requires an application-localized label. Wayland compositors choose context-menu positioning and do not expose global menu-bar export uniformly.");

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
        var generation = checked(++_generation); var callbacks = new List<GCHandle>(); nint accel = 0, menu = 0;
        try
        {
            menu = Build(items, target.Kind == TargetKind.Context, target, generation, callbacks, ref accel);
            if (target.Kind != TargetKind.Context) Attach(target, menu, accel);
            var replacement = new Entry(target, menu, accel, callbacks, generation);
            if (_entries.Remove(targetId, out var old)) Destroy(old, detach: true);
            _entries[targetId] = replacement;
            if(target.Kind!=TargetKind.Context)ApplyPreferred(target.Window);
        }
        catch { if (menu != 0) Native.gtk_widget_destroy(menu); foreach (var callback in callbacks) if (callback.IsAllocated) callback.Free(); if (accel != 0) Native.g_object_unref(accel); throw; }
    }

    public void RemoveMenu(string targetId) { EnsureAccess(); if (_entries.Remove(targetId, out var entry)) { Destroy(entry, true); if(entry.Target.Kind!=TargetKind.Context)ApplyPreferred(entry.Target.Window); } }

    public NeoDesktopStatus ShowContextMenu(string targetId, NeoPoint position)
    {
        EnsureAccess(); ObjectDisposedException.ThrowIf(_disposed, this); if (!_entries.TryGetValue(targetId, out var entry) || entry.Target.Kind != TargetKind.Context) return NeoDesktopStatus.NotFound;
        try
        {
            if (!TryRoleWidget(entry.Target, out var widget)) return NeoDesktopStatus.NotFound;
            var nativeWindow = Native.gtk_widget_get_window(widget); if (nativeWindow == 0) return NeoDesktopStatus.NotFound;
            var rectangle = new Rectangle { X = position.X, Y = position.Y, Width = 1, Height = 1 }; Native.gtk_menu_popup_at_rect(entry.Menu, nativeWindow, &rectangle, 1, 1, 0); return NeoDesktopStatus.Success;
        }
        catch (EntryPointNotFoundException) { Native.gtk_menu_popup_at_pointer(entry.Menu, 0); return NeoDesktopStatus.Success; }
    }

    public ValueTask DisposeAsync() { var value = _dispatcher; if (value is not null && !value.CheckAccess()) return value.InvokeAsync(DisposeOnDispatcher); DisposeOnDispatcher(); return ValueTask.CompletedTask; }

    private nint Build(IReadOnlyList<NeoMenuItem> items, bool popup, Target target, long generation, List<GCHandle> callbacks, ref nint acceleratorGroup)
    {
        var menu = popup ? Native.gtk_menu_new() : Native.gtk_menu_bar_new(); if (menu == 0) throw new InvalidOperationException("Unable to allocate a GTK menu.");
        try
        {
            foreach (var item in items)
            {
                if (!item.IsVisible) continue; nint nativeItem;
                if (item.Kind == NeoMenuItemKind.Separator) nativeItem = Native.gtk_separator_menu_item_new();
                else
                {
                    nativeItem = item.Kind == NeoMenuItemKind.Role && item.Text is null
                        ? CreateStockRoleItem(item.Role!.Value)
                        : item.IsChecked ? Native.gtk_check_menu_item_new_with_label(item.Text!) : Native.gtk_menu_item_new_with_label(item.Text!);
                    if (nativeItem == 0) throw new InvalidOperationException("Unable to allocate a GTK menu item."); Native.gtk_widget_set_sensitive(nativeItem, item.IsEnabled && RoleTargetAvailable(item.Role, target));
                    if (item.IsChecked) Native.gtk_check_menu_item_set_active(nativeItem, true);
                    if (item.Kind == NeoMenuItemKind.Submenu) { var child = Build(item.Children, true, target, generation, callbacks, ref acceleratorGroup); Native.gtk_menu_item_set_submenu(nativeItem, child); }
                    else
                    {
                        var context = new ActivationContext(this, target.Id, generation, item.CommandId, item.Role); var handle = GCHandle.Alloc(context); callbacks.Add(handle);
                        if (Native.g_signal_connect_data(nativeItem, "activate", (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&Activated, GCHandle.ToIntPtr(handle), 0, 0) == 0) throw new InvalidOperationException("Unable to attach a GTK menu callback.");
                        if (item.Accelerator is { } accelerator && target.Window != 0)
                        {
                            acceleratorGroup = acceleratorGroup == 0 ? Native.gtk_accel_group_new() : acceleratorGroup; Native.gtk_accelerator_parse(accelerator.Replace("Ctrl+", "<Control>", StringComparison.Ordinal).Replace("Alt+", "<Alt>", StringComparison.Ordinal).Replace("Shift+", "<Shift>", StringComparison.Ordinal).Replace("Meta+", "<Super>", StringComparison.Ordinal), out var key, out var modifiers);
                            if (key == 0) throw new ArgumentException("The GTK backend cannot represent the normalized accelerator.", nameof(items));
                            Native.gtk_widget_add_accelerator(nativeItem, "activate", acceleratorGroup, key, modifiers, 1);
                        }
                    }
                }
                Native.gtk_menu_shell_append(menu, nativeItem); Native.gtk_widget_show(nativeItem);
            }
            return menu;
        }
        catch { Native.gtk_widget_destroy(menu); throw; }
    }

    private void Attach(Target target, nint menu, nint accelerator)
    {
        if (!_hosts.TryGetValue(target.Window, out var host))
        {
            var child = Native.gtk_bin_get_child(target.Window); if (child == 0) throw new InvalidOperationException("The GTK window content is unavailable."); Native.g_object_ref(child); Native.gtk_container_remove(target.Window, child);
            var box = Native.gtk_box_new(1, 0); if (box == 0) { Native.gtk_container_add(target.Window, child); Native.g_object_unref(child); throw new InvalidOperationException("Unable to allocate a GTK menu host."); }
            Native.gtk_container_add(target.Window, box); Native.gtk_box_pack_end(box, child, true, true, 0); Native.g_object_unref(child);
            var ownerHandle = GCHandle.Alloc(this); if (Native.g_signal_connect_data(target.Window, "destroy", (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OwnerDestroyed, GCHandle.ToIntPtr(ownerHandle), (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&ReleaseHandle, 0) == 0) { ownerHandle.Free(); Native.g_object_ref(child); Native.gtk_container_remove(box, child); Native.gtk_container_remove(target.Window, box); Native.gtk_container_add(target.Window, child); Native.g_object_unref(child); throw new InvalidOperationException("Unable to observe GTK menu-owner teardown."); }
            host = new(box, child, 0, null); _hosts.Add(target.Window, host);
        }
        Native.gtk_box_pack_start(host.Box, menu, false, false, 0);Native.gtk_widget_hide(menu);_hosts[target.Window] = host with { References = host.References + 1 }; Native.gtk_widget_show_all(target.Window);Native.gtk_widget_hide(menu);
    }

    private void Destroy(Entry entry, bool detach)
    {
        var attached = Native.gtk_widget_get_parent(entry.Menu) != 0;
        if (entry.Target.Kind != TargetKind.Context && _hosts.TryGetValue(entry.Target.Window, out var host))
        {
            if (host.ActiveId==entry.Target.Id&&entry.AcceleratorGroup != 0) Native.gtk_window_remove_accel_group(entry.Target.Window, entry.AcceleratorGroup); if (detach && Native.gtk_widget_get_parent(entry.Menu) == host.Box) Native.gtk_container_remove(host.Box, entry.Menu);
            var references = host.References - 1; if (references <= 0) RestoreHost(entry.Target.Window, host); else _hosts[entry.Target.Window] = host with { References = references };
        }
        if (!attached) Native.gtk_widget_destroy(entry.Menu);
        foreach (var callback in entry.Callbacks) if (callback.IsAllocated) callback.Free(); if (entry.AcceleratorGroup != 0) Native.g_object_unref(entry.AcceleratorGroup);
    }

    private Entry? Preferred(nint window)=>_entries.Values.LastOrDefault(value=>value.Target.Window==window&&value.Target.Kind==TargetKind.Window)??_entries.Values.LastOrDefault(value=>value.Target.Window==window&&value.Target.Kind==TargetKind.Application);
    private void ApplyPreferred(nint window)
    {
        if(!_hosts.TryGetValue(window,out var host))return;var preferred=Preferred(window);if(host.ActiveId is{} oldId&&_entries.TryGetValue(oldId,out var old)&&old.AcceleratorGroup!=0)Native.gtk_window_remove_accel_group(window,old.AcceleratorGroup);
        foreach(var entry in _entries.Values.Where(value=>value.Target.Window==window&&value.Target.Kind!=TargetKind.Context))if(ReferenceEquals(entry,preferred))Native.gtk_widget_show(entry.Menu);else Native.gtk_widget_hide(entry.Menu);
        if(preferred is{AcceleratorGroup:not 0}active)Native.gtk_window_add_accel_group(window,active.AcceleratorGroup);_hosts[window]=host with{ActiveId=preferred?.Target.Id};
    }

    private void RestoreHost(nint window, Host host)
    {
        _hosts.Remove(window); if (Native.gtk_widget_get_parent(host.Content) == host.Box) { Native.g_object_ref(host.Content); Native.gtk_container_remove(host.Box, host.Content); Native.gtk_container_remove(window, host.Box); Native.gtk_container_add(window, host.Content); Native.g_object_unref(host.Content); Native.gtk_widget_show_all(window); }
    }

    private void Activate(ActivationContext context)
    {
        if (!_entries.TryGetValue(context.TargetId, out var entry) || entry.Generation != context.Generation) return;
        if (context.CommandId is { } command) { try { _ = commands.ActivateAsync(command); } catch { } return; }
        try
        {
            switch (context.Role)
            {
                case NeoMenuRole.Copy: if (TryRoleWidget(entry.Target, out var copy)) Native.webkit_web_view_execute_editing_command(copy, "Copy"); break;
                case NeoMenuRole.Cut: if (TryRoleWidget(entry.Target, out var cut)) Native.webkit_web_view_execute_editing_command(cut, "Cut"); break;
                case NeoMenuRole.Paste: if (TryRoleWidget(entry.Target, out var paste)) Native.webkit_web_view_execute_editing_command(paste, "Paste"); break;
                case NeoMenuRole.SelectAll: if (TryRoleWidget(entry.Target, out var selectAll)) Native.webkit_web_view_execute_editing_command(selectAll, "SelectAll"); break;
                case NeoMenuRole.Undo: if (TryRoleWidget(entry.Target, out var undo)) Native.webkit_web_view_execute_editing_command(undo, "Undo"); break;
                case NeoMenuRole.Redo: if (TryRoleWidget(entry.Target, out var redo)) Native.webkit_web_view_execute_editing_command(redo, "Redo"); break;
                case NeoMenuRole.Minimize: if (TryWindow(entry.Target, out var minimize)) Native.gtk_window_iconify(minimize); break;
                case NeoMenuRole.CloseWindow: if (TryWindow(entry.Target, out var close)) Native.gtk_window_close(close); break;
                case NeoMenuRole.Quit: if (_application is { } app) _ = app.RequestQuitAsync(); break;
            }
        }
        catch { }
    }

    private Target ResolveTarget(string id)
    {
        var app = _application ?? throw new InvalidOperationException("The menu presenter must be bound to an application."); NeoWindow? window; NeoAstra? view = null;
        if (id == "application") window = app.MainWindow ?? throw new InvalidOperationException("An application menu requires a main window.");
        else if (id.StartsWith("window:", StringComparison.Ordinal)) { if (!app.TryGetWindow(id[7..], out window) || window is null) throw new ArgumentException("The window menu target does not exist.", nameof(id)); }
        else if (id.StartsWith("context:", StringComparison.Ordinal)) { if (!app.TryGetView(id[8..], out view) || view?.OwnedWindow is null) throw new ArgumentException("The context menu target does not exist.", nameof(id)); window = view.OwnedWindow; }
        else throw new ArgumentException("A native menu target must be 'application', 'window:<label>', or 'context:<view-label>'.", nameof(id));
        var kind = id.StartsWith("context:", StringComparison.Ordinal) ? TargetKind.Context : id == "application" ? TargetKind.Application : TargetKind.Window;
        var nativeWindow = window.GetNativeHandle(NeoNativeHandleKind.GtkWindow).Value;
        if (nativeWindow == 0) throw new InvalidOperationException("The native GTK menu target window is unavailable.");
        if (view is null) view = ResolveRoleView(app, window, out _);
        var widget = TryGetWidget(view, out var resolvedWidget) ? resolvedWidget : 0;
        if (kind == TargetKind.Context && widget == 0) throw new InvalidOperationException("The native GTK context-menu target view is unavailable.");
        return new(id, kind, window, view, nativeWindow, widget);
    }

    private static NeoAstra? ResolveRoleView(NeoApplication application, NeoWindow window, out nint widget)
    {
        var candidates = new List<RoleCandidate>();
        foreach (var candidate in application.GetRegisteredViews())
            if (TryGetWidget(candidate, out var candidateWidget)) candidates.Add(new(candidate, candidate.OwnedWindow, candidate.ViewLabel, candidateWidget));
        var selected = LinuxRoleTargetSelection.Select(candidates, window, static value => value.Owner, static value => value.Label, static value => value.Widget);
        widget = selected?.Widget ?? 0;
        return selected?.View;
    }

    private static bool TryGetWidget(NeoAstra? view, out nint widget)
    {
        widget = 0;
        if (view is null) return false;
        try { widget = view.GetNativeHandle(NeoNativeHandleKind.WebKitGtkWebView).Value; return widget != 0; }
        catch (ObjectDisposedException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private bool TryRoleWidget(Target target, out nint widget)
    {
        widget = 0; var app = _application;
        if (app is null || target.View is null || !ReferenceEquals(target.View.OwnedWindow, target.ManagedWindow)) return false;
        if (target.Kind == TargetKind.Context)
        {
            if (!app.GetRegisteredViews().Any(view => ReferenceEquals(view, target.View))) return false;
            return TryGetWidget(target.View, out widget) && LinuxRoleTargetSelection.IsCurrentWidget(target.Widget, widget);
        }
        var selected = ResolveRoleView(app, target.ManagedWindow, out widget);
        return ReferenceEquals(selected, target.View) && LinuxRoleTargetSelection.IsCurrentWidget(target.Widget, widget);
    }

    private static bool TryWindow(Target target, out nint window)
    {
        window = 0;
        try { window = target.ManagedWindow.GetNativeHandle(NeoNativeHandleKind.GtkWindow).Value; return window != 0 && window == target.Window; }
        catch (ObjectDisposedException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private bool RoleTargetAvailable(NeoMenuRole? role, Target target) => role switch
    {
        NeoMenuRole.Copy or NeoMenuRole.Cut or NeoMenuRole.Paste or NeoMenuRole.SelectAll or NeoMenuRole.Undo or NeoMenuRole.Redo => TryRoleWidget(target, out _),
        NeoMenuRole.Minimize or NeoMenuRole.CloseWindow => TryWindow(target, out _),
        _ => true,
    };

    private static nint CreateStockRoleItem(NeoMenuRole role)
    {
        var stock = role switch
        {
            NeoMenuRole.Copy => "gtk-copy", NeoMenuRole.Cut => "gtk-cut", NeoMenuRole.Paste => "gtk-paste", NeoMenuRole.SelectAll => "gtk-select-all",
            NeoMenuRole.Undo => "gtk-undo", NeoMenuRole.Redo => "gtk-redo", NeoMenuRole.CloseWindow => "gtk-close", NeoMenuRole.Quit => "gtk-quit",
            _ => throw new NotSupportedException("GTK3 does not expose a reliable localized standard label for this role. Supply an application-localized label with NeoMenuItem.RoleItem(id, role, localizedText)."),
        };
        return Native.gtk_image_menu_item_new_from_stock(stock, 0);
    }

    private void OwnerClosed(nint window) { _hosts.Remove(window); foreach (var id in _entries.Where(pair => pair.Value.Target.Window == window).Select(static pair => pair.Key).ToArray()) if (_entries.Remove(id, out var entry)) { foreach (var callback in entry.Callbacks) if (callback.IsAllocated) callback.Free(); if (entry.AcceleratorGroup != 0) Native.g_object_unref(entry.AcceleratorGroup); } }
    private void DisposeOnDispatcher() { if (_disposed) return; _disposed = true; foreach (var entry in _entries.Values.ToArray()) Destroy(entry, true); _entries.Clear(); _hosts.Clear(); }
    private void EnsureAccess() { var value = _dispatcher ?? throw new InvalidOperationException("The native menu presenter is not bound to a UI dispatcher."); if (!value.CheckAccess()) throw new InvalidOperationException("Native menu presenter mutations require the NeoAstra UI dispatcher."); }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static void Activated(nint widget, nint data) { try { var handle = GCHandle.FromIntPtr(data); if (handle.Target is ActivationContext context) context.Presenter.Activate(context); } catch { } }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static void OwnerDestroyed(nint widget, nint data) { try { var handle = GCHandle.FromIntPtr(data); if (handle.Target is LinuxMenuPresenter presenter) presenter.OwnerClosed(widget); } catch { } }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static void ReleaseHandle(nint data, nint closure) { try { var handle = GCHandle.FromIntPtr(data); if (handle.IsAllocated) handle.Free(); } catch { } }

    private enum TargetKind { Application, Window, Context }
    private sealed record Target(string Id, TargetKind Kind, NeoWindow ManagedWindow, NeoAstra? View, nint Window, nint Widget);
    private sealed record Entry(Target Target, nint Menu, nint AcceleratorGroup, IReadOnlyList<GCHandle> Callbacks, long Generation);
    private sealed record ActivationContext(LinuxMenuPresenter Presenter, string TargetId, long Generation, string? CommandId, NeoMenuRole? Role);
    private sealed record RoleCandidate(NeoAstra View, NeoWindow? Owner, string? Label, nint Widget);
    private readonly record struct Host(nint Box, nint Content, int References, string? ActiveId);
    [StructLayout(LayoutKind.Sequential)] private struct Rectangle { internal int X, Y, Width, Height; }

    private static partial class Native
    {
        [LibraryImport("libgtk-3.so.0")] internal static partial nint gtk_menu_bar_new();
        [LibraryImport("libgtk-3.so.0")] internal static partial nint gtk_menu_new();
        [LibraryImport("libgtk-3.so.0", StringMarshalling = StringMarshalling.Utf8)] internal static partial nint gtk_menu_item_new_with_label(string label);
        [LibraryImport("libgtk-3.so.0", StringMarshalling = StringMarshalling.Utf8)] internal static partial nint gtk_check_menu_item_new_with_label(string label);
        [LibraryImport("libgtk-3.so.0", StringMarshalling = StringMarshalling.Utf8)] internal static partial nint gtk_image_menu_item_new_from_stock(string stockId, nint acceleratorGroup);
        [LibraryImport("libgtk-3.so.0")] internal static partial nint gtk_separator_menu_item_new();
        [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_check_menu_item_set_active(nint item, [MarshalAs(UnmanagedType.Bool)] bool active);
        [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_menu_item_set_submenu(nint item, nint menu);
        [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_menu_shell_append(nint menu, nint item);
        [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_widget_show(nint widget);
        [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_widget_hide(nint widget);
        [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_widget_show_all(nint widget);
        [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_widget_destroy(nint widget);
        [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_widget_set_sensitive(nint widget, [MarshalAs(UnmanagedType.Bool)] bool sensitive);
        [LibraryImport("libgtk-3.so.0")] internal static partial nint gtk_widget_get_parent(nint widget);
        [LibraryImport("libgtk-3.so.0")] internal static partial nint gtk_widget_get_window(nint widget);
        [LibraryImport("libgtk-3.so.0")] internal static partial nint gtk_bin_get_child(nint bin);
        [LibraryImport("libgtk-3.so.0")] internal static partial nint gtk_box_new(int orientation, int spacing);
        [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_box_pack_start(nint box, nint child, [MarshalAs(UnmanagedType.Bool)] bool expand, [MarshalAs(UnmanagedType.Bool)] bool fill, uint padding);
        [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_box_pack_end(nint box, nint child, [MarshalAs(UnmanagedType.Bool)] bool expand, [MarshalAs(UnmanagedType.Bool)] bool fill, uint padding);
        [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_container_add(nint container, nint widget);
        [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_container_remove(nint container, nint widget);
        [LibraryImport("libgtk-3.so.0")] internal static partial nint gtk_accel_group_new();
        [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_window_add_accel_group(nint window, nint group);
        [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_window_remove_accel_group(nint window, nint group);
        [LibraryImport("libgtk-3.so.0", StringMarshalling = StringMarshalling.Utf8)] internal static partial void gtk_accelerator_parse(string accelerator, out uint key, out uint modifiers);
        [LibraryImport("libgtk-3.so.0", StringMarshalling = StringMarshalling.Utf8)] internal static partial void gtk_widget_add_accelerator(nint widget, string signal, nint group, uint key, uint modifiers, uint flags);
        [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_window_iconify(nint window);
        [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_window_close(nint window);
        [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_menu_popup_at_rect(nint menu, nint window, Rectangle* rectangle, int rectangleAnchor, int menuAnchor, nint triggerEvent);
        [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_menu_popup_at_pointer(nint menu, nint triggerEvent);
        [LibraryImport("libgobject-2.0.so.0", StringMarshalling = StringMarshalling.Utf8)] internal static partial nuint g_signal_connect_data(nint instance, string signal, nint callback, nint data, nint destroyData, uint flags);
        [LibraryImport("libgobject-2.0.so.0")] internal static partial nint g_object_ref(nint value);
        [LibraryImport("libgobject-2.0.so.0")] internal static partial void g_object_unref(nint value);
        [LibraryImport("libwebkit2gtk-4.1.so.0", StringMarshalling = StringMarshalling.Utf8)] internal static partial void webkit_web_view_execute_editing_command(nint view, string command);
    }
}
