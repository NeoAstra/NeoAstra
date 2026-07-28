// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using global::NeoAstra.Desktop.Menus;

namespace NeoAstra.Desktop.Tray;

internal sealed unsafe partial class LinuxTrayPresenter(NeoCommandService commands, NeoDispatcher? dispatcher) : INeoTrayPresenter, INeoApplicationBoundDesktopService
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private NeoDispatcher? _dispatcher = dispatcher;
    private NeoApplication? _application;
    private bool _disposed;

    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Limited, 1, 0, "Native GTK status icons with tooltip, decoded image policy, native popup menus, ordered primary/secondary activation, and deterministic signal/object teardown. GTK3 stock resources localize Copy, Cut, Paste, Select All, Undo, Redo, Close, and Quit; Minimize requires an application-localized label. macOS-style template-image intent is rejected; GtkStatusIcon bounds and visibility depend on the XEmbed host, native Wayland compositors may provide no status area without an AppIndicator extension, and no portable attention semantic exists.");
    public event Action<string, bool>? Activated;

    void INeoApplicationBoundDesktopService.BindApplication(NeoApplication application) { ArgumentNullException.ThrowIfNull(application); if (_application is not null && !ReferenceEquals(_application, application)) throw new InvalidOperationException("The tray presenter is already bound to an application."); if (_dispatcher is not null && !ReferenceEquals(_dispatcher, application.Dispatcher)) throw new InvalidOperationException("The tray presenter is already bound to another dispatcher."); _application = application; _dispatcher = application.Dispatcher; }

    public void Set(NeoTrayItemOptions options)
    {
        EnsureAccess(); ObjectDisposedException.ThrowIf(_disposed, this); nint icon;
        if(options.IsTemplateImage)throw new NotSupportedException("GTK status icons do not expose macOS template-image rendering semantics.");
        if (options.IconPath is { } path) { if (!File.Exists(path)) throw new FileNotFoundException("The tray icon does not exist.", path); icon = Native.gtk_status_icon_new_from_file(path); }
        else icon = Native.gtk_status_icon_new_from_icon_name("application-x-executable");
        if (icon == 0) throw new ArgumentException("GTK could not decode or allocate the tray icon.", nameof(options));
        var callbacks = new List<GCHandle>(); nint menu = 0;
        try
        {
            if (options.ToolTip is { } tooltip) Native.gtk_status_icon_set_tooltip_text(icon, tooltip); Native.gtk_status_icon_set_visible(icon, true);
            var activation = GCHandle.Alloc(new ActivationContext(this, options.Id)); callbacks.Add(activation);
            if (Native.g_signal_connect_data(icon, "activate", (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&PrimaryActivated, GCHandle.ToIntPtr(activation), 0, 0) == 0) throw new InvalidOperationException("Unable to attach GTK tray activation.");
            var secondary = GCHandle.Alloc(new ActivationContext(this, options.Id)); callbacks.Add(secondary);
            if (Native.g_signal_connect_data(icon, "popup-menu", (nint)(delegate* unmanaged[Cdecl]<nint, uint, uint, nint, void>)&SecondaryActivated, GCHandle.ToIntPtr(secondary), 0, 0) == 0) throw new InvalidOperationException("Unable to attach GTK tray secondary activation.");
            if (options.Menu.Count != 0) menu = BuildMenu(options.Menu, callbacks);
            var replacement = new Entry(icon, menu, callbacks, options); _entries.TryGetValue(options.Id, out var previous); _entries[options.Id] = replacement; if (previous is not null) Destroy(previous); icon = 0; menu = 0;
        }
        catch { if (menu != 0) Native.gtk_widget_destroy(menu); if (icon != 0) { Native.gtk_status_icon_set_visible(icon, false); Native.g_object_unref(icon); } foreach (var callback in callbacks) if (callback.IsAllocated) callback.Free(); throw; }
    }

    public bool Remove(string id) { EnsureAccess(); if (!_entries.Remove(id, out var entry)) return false; Destroy(entry); return true; }
    public ValueTask DisposeAsync() { var value = _dispatcher; if (value is not null && !value.CheckAccess()) return value.InvokeAsync(DisposeOnDispatcher); DisposeOnDispatcher(); return ValueTask.CompletedTask; }

    private nint BuildMenu(IReadOnlyList<NeoMenuItem> items, List<GCHandle> callbacks)
    {
        var menu = Native.gtk_menu_new(); if (menu == 0) throw new InvalidOperationException("Unable to allocate a GTK tray menu.");
        try
        {
            foreach (var item in items)
            {
                if (!item.IsVisible) continue; nint native;
                if (item.Kind == NeoMenuItemKind.Separator) native = Native.gtk_separator_menu_item_new();
                else
                {
                    native = item.Kind == NeoMenuItemKind.Role && item.Text is null ? CreateStockRoleItem(item.Role!.Value) : item.IsChecked ? Native.gtk_check_menu_item_new_with_label(item.Text!) : Native.gtk_menu_item_new_with_label(item.Text!); if (native == 0) throw new InvalidOperationException("Unable to allocate a GTK tray menu item."); Native.gtk_widget_set_sensitive(native, item.IsEnabled && RoleTargetAvailable(item.Role)); if (item.IsChecked) Native.gtk_check_menu_item_set_active(native, true);
                    if (item.Kind == NeoMenuItemKind.Submenu) Native.gtk_menu_item_set_submenu(native, BuildMenu(item.Children, callbacks));
                    else { var handle = GCHandle.Alloc(new MenuContext(this, item.CommandId, item.Role)); callbacks.Add(handle); if (Native.g_signal_connect_data(native, "activate", (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&MenuActivated, GCHandle.ToIntPtr(handle), 0, 0) == 0) throw new InvalidOperationException("Unable to attach a GTK tray menu callback."); }
                }
                Native.gtk_menu_shell_append(menu, native); Native.gtk_widget_show(native);
            }
            return menu;
        }
        catch { Native.gtk_widget_destroy(menu); throw; }
    }

    private void Raise(string id, bool secondary)
    {
        if (!_entries.ContainsKey(id)) return; try { Activated?.Invoke(id, secondary); } catch { }
        if (secondary && _entries.TryGetValue(id, out var entry) && entry.Menu != 0) Native.gtk_menu_popup_at_pointer(entry.Menu, 0);
    }
    private void ActivateMenu(MenuContext context)
    {
        if (context.CommandId is { } command) { try { _ = commands.ActivateAsync(command); } catch { } }
        else if (context.Role is { } role && _application is { } app) try
        {
            var window = app.MainWindow; var nativeWindow = TryGetWindow(window, out var resolvedWindow) ? resolvedWindow : 0; var view = TryResolveRoleWidget(app, window, out var resolvedView) ? resolvedView : 0;
            switch (role) { case NeoMenuRole.Copy: if (view != 0) Native.webkit_web_view_execute_editing_command(view, "Copy"); break; case NeoMenuRole.Cut: if (view != 0) Native.webkit_web_view_execute_editing_command(view, "Cut"); break; case NeoMenuRole.Paste: if (view != 0) Native.webkit_web_view_execute_editing_command(view, "Paste"); break; case NeoMenuRole.SelectAll: if (view != 0) Native.webkit_web_view_execute_editing_command(view, "SelectAll"); break; case NeoMenuRole.Undo: if (view != 0) Native.webkit_web_view_execute_editing_command(view, "Undo"); break; case NeoMenuRole.Redo: if (view != 0) Native.webkit_web_view_execute_editing_command(view, "Redo"); break; case NeoMenuRole.Minimize: if (nativeWindow != 0) Native.gtk_window_iconify(nativeWindow); break; case NeoMenuRole.CloseWindow: if (nativeWindow != 0) Native.gtk_window_close(nativeWindow); break; case NeoMenuRole.Quit: _ = app.RequestQuitAsync(); break; }
        }
        catch { }
    }
    private void Destroy(Entry entry) { if (entry.Menu != 0) Native.gtk_widget_destroy(entry.Menu); Native.gtk_status_icon_set_visible(entry.Icon, false); Native.g_object_unref(entry.Icon); foreach (var callback in entry.Callbacks) if (callback.IsAllocated) callback.Free(); }
    private void DisposeOnDispatcher() { if (_disposed) return; _disposed = true; Activated = null; foreach (var entry in _entries.Values) Destroy(entry); _entries.Clear(); }
    private void EnsureAccess() { var value = _dispatcher ?? throw new InvalidOperationException("The tray presenter is not bound to the UI dispatcher."); if (!value.CheckAccess()) throw new InvalidOperationException("Native tray mutations require the NeoAstra UI dispatcher."); }

    private bool RoleTargetAvailable(NeoMenuRole? role)
    {
        var app = _application; var window = app?.MainWindow;
        return role switch
        {
            NeoMenuRole.Copy or NeoMenuRole.Cut or NeoMenuRole.Paste or NeoMenuRole.SelectAll or NeoMenuRole.Undo or NeoMenuRole.Redo => app is not null && TryResolveRoleWidget(app, window, out _),
            NeoMenuRole.Minimize or NeoMenuRole.CloseWindow => TryGetWindow(window, out _),
            _ => true,
        };
    }

    private static bool TryResolveRoleWidget(NeoApplication application, NeoWindow? window, out nint widget)
    {
        widget = 0; if (window is null) return false; var candidates = new List<RoleCandidate>();
        foreach (var view in application.GetRegisteredViews())
        {
            try { var value = view.GetNativeHandle(NeoNativeHandleKind.WebKitGtkWebView).Value; if (value != 0) candidates.Add(new(view.OwnedWindow, view.ViewLabel, value)); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }
        var selected = LinuxRoleTargetSelection.Select(candidates, window, static value => value.Owner, static value => value.Label, static value => value.Widget);
        widget = selected?.Widget ?? 0; return widget != 0;
    }

    private static bool TryGetWindow(NeoWindow? window, out nint native)
    {
        native = 0; if (window is null) return false;
        try { native = window.GetNativeHandle(NeoNativeHandleKind.GtkWindow).Value; return native != 0; }
        catch (ObjectDisposedException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

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

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static void PrimaryActivated(nint icon, nint data) { try { if (GCHandle.FromIntPtr(data).Target is ActivationContext context) context.Presenter.Raise(context.Id, false); } catch { } }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static void SecondaryActivated(nint icon, uint button, uint time, nint data) { try { if (GCHandle.FromIntPtr(data).Target is ActivationContext context) context.Presenter.Raise(context.Id, true); } catch { } }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static void MenuActivated(nint item, nint data) { try { if (GCHandle.FromIntPtr(data).Target is MenuContext context) context.Presenter.ActivateMenu(context); } catch { } }

    private sealed record Entry(nint Icon, nint Menu, IReadOnlyList<GCHandle> Callbacks, NeoTrayItemOptions Options);
    private sealed record ActivationContext(LinuxTrayPresenter Presenter, string Id);
    private sealed record MenuContext(LinuxTrayPresenter Presenter, string? CommandId, NeoMenuRole? Role);
    private sealed record RoleCandidate(NeoWindow? Owner, string? Label, nint Widget);
    private static partial class Native
    {
        [LibraryImport("libgtk-3.so.0", StringMarshalling = StringMarshalling.Utf8)] internal static partial nint gtk_status_icon_new_from_file(string path); [LibraryImport("libgtk-3.so.0", StringMarshalling = StringMarshalling.Utf8)] internal static partial nint gtk_status_icon_new_from_icon_name(string name); [LibraryImport("libgtk-3.so.0", StringMarshalling = StringMarshalling.Utf8)] internal static partial void gtk_status_icon_set_tooltip_text(nint icon, string text); [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_status_icon_set_visible(nint icon, [MarshalAs(UnmanagedType.Bool)] bool visible);
        [LibraryImport("libgtk-3.so.0")] internal static partial nint gtk_menu_new(); [LibraryImport("libgtk-3.so.0", StringMarshalling = StringMarshalling.Utf8)] internal static partial nint gtk_menu_item_new_with_label(string label); [LibraryImport("libgtk-3.so.0", StringMarshalling = StringMarshalling.Utf8)] internal static partial nint gtk_check_menu_item_new_with_label(string label); [LibraryImport("libgtk-3.so.0", StringMarshalling = StringMarshalling.Utf8)] internal static partial nint gtk_image_menu_item_new_from_stock(string stockId, nint acceleratorGroup); [LibraryImport("libgtk-3.so.0")] internal static partial nint gtk_separator_menu_item_new(); [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_check_menu_item_set_active(nint item, [MarshalAs(UnmanagedType.Bool)] bool active); [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_menu_item_set_submenu(nint item, nint menu); [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_menu_shell_append(nint menu, nint item); [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_widget_show(nint widget); [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_widget_destroy(nint widget); [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_widget_set_sensitive(nint widget, [MarshalAs(UnmanagedType.Bool)] bool sensitive); [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_menu_popup_at_pointer(nint menu, nint triggerEvent);
        [LibraryImport("libgobject-2.0.so.0", StringMarshalling = StringMarshalling.Utf8)] internal static partial nuint g_signal_connect_data(nint instance, string signal, nint callback, nint data, nint destroyData, uint flags); [LibraryImport("libgobject-2.0.so.0")] internal static partial void g_object_unref(nint value);
        [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_window_iconify(nint window); [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_window_close(nint window);
        [LibraryImport("libwebkit2gtk-4.1.so.0", StringMarshalling = StringMarshalling.Utf8)] internal static partial void webkit_web_view_execute_editing_command(nint view, string command);
    }
}
