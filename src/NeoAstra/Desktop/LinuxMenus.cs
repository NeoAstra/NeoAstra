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

    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Limited, 1, 0, "Native GTK4 popover menu bars and context menus with window-scope override/fallback, WebKit edit roles, generation-safe callbacks, and deterministic widget teardown. GTK4 removed stock menu-item labels and GtkAccelGroup, so role labels must be supplied by the application and portable accelerator display/activation is not advertised. Wayland compositors choose context-menu positioning and do not expose global menu-bar export uniformly.");

    void INeoApplicationBoundDesktopService.BindApplication(NeoApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (_application is not null && !ReferenceEquals(_application, application)) throw new InvalidOperationException("The menu presenter is already bound to an application.");
        if (_dispatcher is not null && !ReferenceEquals(_dispatcher, application.Dispatcher)) throw new InvalidOperationException("The menu presenter is already bound to another dispatcher.");
        _application = application;
        _dispatcher = application.Dispatcher;
    }

    public void SetMenu(string targetId, IReadOnlyList<NeoMenuItem> items)
    {
        EnsureAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var target = ResolveTarget(targetId);
        var generation = checked(++_generation);
        var callbacks = new List<GCHandle>();
        nint model = 0, actions = 0, widget = 0;
        try
        {
            actions = Native.g_simple_action_group_new();
            if (actions == 0) throw new InvalidOperationException("Unable to allocate a GTK4 menu action group.");
            var nextAction = 0;
            model = Build(items, target, generation, actions, callbacks, ref nextAction);
            widget = target.Kind == TargetKind.Context ? Native.gtk_popover_menu_new_from_model(model) : Native.gtk_popover_menu_bar_new_from_model(model);
            if (widget == 0) throw new InvalidOperationException("Unable to allocate a GTK4 menu widget.");
            Native.gtk_widget_insert_action_group(widget, "neoastra", actions);
            if (target.Kind == TargetKind.Context) Native.gtk_widget_set_parent(widget, target.Widget);
            else Attach(target, widget);
            var replacement = new Entry(target, model, actions, widget, callbacks, generation);
            model = actions = widget = 0;
            if (_entries.Remove(targetId, out var old)) Destroy(old, detach: true);
            _entries[targetId] = replacement;
            if (target.Kind != TargetKind.Context) ApplyPreferred(target.Window);
        }
        catch
        {
            if (widget != 0 && Native.gtk_widget_get_parent(widget) != 0) Native.gtk_widget_unparent(widget);
            if (widget != 0) Native.g_object_unref(widget);
            if (model != 0) Native.g_object_unref(model);
            if (actions != 0) Native.g_object_unref(actions);
            foreach (var callback in callbacks) if (callback.IsAllocated) callback.Free();
            throw;
        }
    }

    public void RemoveMenu(string targetId)
    {
        EnsureAccess();
        if (_entries.Remove(targetId, out var entry))
        {
            Destroy(entry, detach: true);
            if (entry.Target.Kind != TargetKind.Context) ApplyPreferred(entry.Target.Window);
        }
    }

    public NeoDesktopStatus ShowContextMenu(string targetId, NeoPoint position)
    {
        EnsureAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_entries.TryGetValue(targetId, out var entry) || entry.Target.Kind != TargetKind.Context) return NeoDesktopStatus.NotFound;
        if (!TryRoleWidget(entry.Target, out _)) return NeoDesktopStatus.NotFound;
        var rectangle = new Rectangle { X = position.X, Y = position.Y, Width = 1, Height = 1 };
        Native.gtk_popover_set_pointing_to(entry.Widget, &rectangle);
        Native.gtk_popover_popup(entry.Widget);
        return NeoDesktopStatus.Success;
    }

    public ValueTask DisposeAsync()
    {
        var value = _dispatcher;
        if (value is not null && !value.CheckAccess()) return value.InvokeAsync(DisposeOnDispatcher);
        DisposeOnDispatcher();
        return ValueTask.CompletedTask;
    }

    private nint Build(IReadOnlyList<NeoMenuItem> items, Target target, long generation, nint actions, List<GCHandle> callbacks, ref int nextAction)
    {
        var menu = Native.g_menu_new();
        var section = Native.g_menu_new();
        if (menu == 0 || section == 0) throw new InvalidOperationException("Unable to allocate a GTK4 menu model.");
        Native.g_menu_append_section(menu, null, section);
        try
        {
            foreach (var item in items)
            {
                if (!item.IsVisible) continue;
                if (item.Kind == NeoMenuItemKind.Separator)
                {
                    Native.g_object_unref(section);
                    section = Native.g_menu_new();
                    Native.g_menu_append_section(menu, null, section);
                    continue;
                }
                var label = item.Text ?? throw new NotSupportedException("GTK4 does not provide localized stock menu-item labels. Supply an application-localized label with NeoMenuItem.RoleItem(id, role, localizedText).");
                if (item.Kind == NeoMenuItemKind.Submenu)
                {
                    var child = Build(item.Children, target, generation, actions, callbacks, ref nextAction);
                    Native.g_menu_append_submenu(section, label, child);
                    Native.g_object_unref(child);
                    continue;
                }
                var actionName = "item" + (++nextAction).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var detailedAction = "neoastra." + actionName;
                var action = item.IsChecked ? Native.g_simple_action_new_stateful(actionName, 0, Native.g_variant_new_boolean(true)) : Native.g_simple_action_new(actionName, 0);
                if (action == 0) throw new InvalidOperationException("Unable to allocate a GTK4 menu action.");
                var context = new ActivationContext(this, target.Id, generation, item.CommandId, item.Role);
                var handle = GCHandle.Alloc(context);
                callbacks.Add(handle);
                if (Native.g_signal_connect_data(action, "activate", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&Activated, GCHandle.ToIntPtr(handle), 0, 0) == 0) throw new InvalidOperationException("Unable to attach a GTK4 menu callback.");
                Native.g_simple_action_set_enabled(action, item.IsEnabled && RoleTargetAvailable(item.Role, target));
                Native.g_action_map_add_action(actions, action);
                Native.g_object_unref(action);
                Native.g_menu_append(section, label, detailedAction);
            }
            Native.g_object_unref(section);
            return menu;
        }
        catch
        {
            Native.g_object_unref(section);
            Native.g_object_unref(menu);
            throw;
        }
    }

    private void Attach(Target target, nint menu)
    {
        if (!_hosts.TryGetValue(target.Window, out var host))
        {
            var content = Native.gtk_window_get_child(target.Window);
            if (content != 0) Native.g_object_ref(content);
            Native.gtk_window_set_child(target.Window, 0);
            var box = Native.gtk_box_new(1, 0);
            if (box == 0) throw new InvalidOperationException("Unable to allocate a GTK4 menu host.");
            Native.gtk_window_set_child(target.Window, box);
            if (content != 0)
            {
                Native.gtk_widget_set_vexpand(content, true);
                Native.gtk_widget_set_hexpand(content, true);
                Native.gtk_box_append(box, content);
                Native.g_object_unref(content);
            }
            target.ManagedWindow.Closed += WindowClosed;
            host = new(box, content, 0, null, target.ManagedWindow);
            _hosts.Add(target.Window, host);
        }
        Native.gtk_box_prepend(host.Box, menu);
        Native.gtk_widget_set_visible(menu, false);
        _hosts[target.Window] = host with { References = host.References + 1 };
    }

    private void Destroy(Entry entry, bool detach)
    {
        if (entry.Target.Kind != TargetKind.Context && _hosts.TryGetValue(entry.Target.Window, out var host))
        {
            if (detach && Native.gtk_widget_get_parent(entry.Widget) == host.Box) Native.gtk_box_remove(host.Box, entry.Widget);
            var references = host.References - 1;
            if (references <= 0) RestoreHost(entry.Target.Window, host);
            else _hosts[entry.Target.Window] = host with { References = references };
        }
        else if (detach && Native.gtk_widget_get_parent(entry.Widget) != 0) Native.gtk_widget_unparent(entry.Widget);
        Native.g_object_unref(entry.Model);
        Native.g_object_unref(entry.Actions);
        foreach (var callback in entry.Callbacks) if (callback.IsAllocated) callback.Free();
    }

    private Entry? Preferred(nint window) => _entries.Values.LastOrDefault(value => value.Target.Window == window && value.Target.Kind == TargetKind.Window) ?? _entries.Values.LastOrDefault(value => value.Target.Window == window && value.Target.Kind == TargetKind.Application);

    private void ApplyPreferred(nint window)
    {
        if (!_hosts.TryGetValue(window, out var host)) return;
        var preferred = Preferred(window);
        foreach (var entry in _entries.Values.Where(value => value.Target.Window == window && value.Target.Kind != TargetKind.Context)) Native.gtk_widget_set_visible(entry.Widget, ReferenceEquals(entry, preferred));
        _hosts[window] = host with { ActiveId = preferred?.Target.Id };
    }

    private void RestoreHost(nint window, Host host)
    {
        _hosts.Remove(window);
        host.ManagedWindow.Closed -= WindowClosed;
        if (host.ManagedWindow.IsClosed) return;
        if (host.Content != 0 && Native.gtk_widget_get_parent(host.Content) == host.Box) Native.g_object_ref(host.Content);
        Native.gtk_window_set_child(window, 0);
        if (host.Content != 0)
        {
            Native.gtk_window_set_child(window, host.Content);
            Native.g_object_unref(host.Content);
        }
    }

    private void WindowClosed(object? sender, EventArgs args)
    {
        if (sender is not NeoWindow managed) return;
        var host = _hosts.FirstOrDefault(pair => ReferenceEquals(pair.Value.ManagedWindow, managed));
        if (host.Value is null) return;
        _hosts.Remove(host.Key);
        foreach (var id in _entries.Where(pair => pair.Value.Target.Window == host.Key).Select(static pair => pair.Key).ToArray())
            if (_entries.Remove(id, out var entry)) Destroy(entry, detach: false);
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
                case NeoMenuRole.Minimize: if (TryWindow(entry.Target, out var minimize)) Native.gtk_window_minimize(minimize); break;
                case NeoMenuRole.CloseWindow: if (TryWindow(entry.Target, out var close)) Native.gtk_window_close(close); break;
                case NeoMenuRole.Quit: if (_application is { } app) _ = app.RequestQuitAsync(); break;
            }
        }
        catch { }
    }

    private Target ResolveTarget(string id)
    {
        var app = _application ?? throw new InvalidOperationException("The menu presenter must be bound to an application.");
        NeoWindow? window;
        NeoAstra? view = null;
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
        foreach (var candidate in application.GetRegisteredViews()) if (TryGetWidget(candidate, out var candidateWidget)) candidates.Add(new(candidate, candidate.OwnedWindow, candidate.ViewLabel, candidateWidget));
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
        widget = 0;
        var app = _application;
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

    private void DisposeOnDispatcher()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _entries.Values.ToArray()) Destroy(entry, detach: true);
        _entries.Clear();
        _hosts.Clear();
    }

    private void EnsureAccess()
    {
        var value = _dispatcher ?? throw new InvalidOperationException("The native menu presenter is not bound to a UI dispatcher.");
        if (!value.CheckAccess()) throw new InvalidOperationException("Native menu presenter mutations require the NeoAstra UI dispatcher.");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Activated(nint action, nint parameter, nint data) { try { var handle = GCHandle.FromIntPtr(data); if (handle.Target is ActivationContext context) context.Presenter.Activate(context); } catch { } }

    private enum TargetKind { Application, Window, Context }
    private sealed record Target(string Id, TargetKind Kind, NeoWindow ManagedWindow, NeoAstra? View, nint Window, nint Widget);
    private sealed record Entry(Target Target, nint Model, nint Actions, nint Widget, IReadOnlyList<GCHandle> Callbacks, long Generation);
    private sealed record ActivationContext(LinuxMenuPresenter Presenter, string TargetId, long Generation, string? CommandId, NeoMenuRole? Role);
    private sealed record RoleCandidate(NeoAstra View, NeoWindow? Owner, string? Label, nint Widget);
    private sealed record Host(nint Box, nint Content, int References, string? ActiveId, NeoWindow ManagedWindow);
    [StructLayout(LayoutKind.Sequential)] private struct Rectangle { internal int X, Y, Width, Height; }

    private static partial class Native
    {
        private const string Gtk = "libgtk-4.so.1";
        private const string Gio = "libgio-2.0.so.0";
        [LibraryImport(Gio)] internal static partial nint g_menu_new();
        [LibraryImport(Gio, StringMarshalling = StringMarshalling.Utf8)] internal static partial void g_menu_append(nint menu, string label, string detailedAction);
        [LibraryImport(Gio, StringMarshalling = StringMarshalling.Utf8)] internal static partial void g_menu_append_section(nint menu, string? label, nint section);
        [LibraryImport(Gio, StringMarshalling = StringMarshalling.Utf8)] internal static partial void g_menu_append_submenu(nint menu, string label, nint submenu);
        [LibraryImport(Gio)] internal static partial nint g_simple_action_group_new();
        [LibraryImport(Gio, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint g_simple_action_new(string name, nint parameterType);
        [LibraryImport(Gio, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint g_simple_action_new_stateful(string name, nint parameterType, nint state);
        [LibraryImport(Gio)] internal static partial void g_simple_action_set_enabled(nint action, [MarshalAs(UnmanagedType.Bool)] bool enabled);
        [LibraryImport(Gio)] internal static partial void g_action_map_add_action(nint actionMap, nint action);
        [LibraryImport("libglib-2.0.so.0")] internal static partial nint g_variant_new_boolean([MarshalAs(UnmanagedType.Bool)] bool value);
        [LibraryImport(Gtk)] internal static partial nint gtk_popover_menu_new_from_model(nint model);
        [LibraryImport(Gtk)] internal static partial nint gtk_popover_menu_bar_new_from_model(nint model);
        [LibraryImport(Gtk)] internal static partial void gtk_popover_set_pointing_to(nint popover, Rectangle* rectangle);
        [LibraryImport(Gtk)] internal static partial void gtk_popover_popup(nint popover);
        [LibraryImport(Gtk)] internal static partial void gtk_widget_insert_action_group(nint widget, [MarshalAs(UnmanagedType.LPUTF8Str)] string prefix, nint group);
        [LibraryImport(Gtk)] internal static partial void gtk_widget_set_parent(nint widget, nint parent);
        [LibraryImport(Gtk)] internal static partial void gtk_widget_unparent(nint widget);
        [LibraryImport(Gtk)] internal static partial nint gtk_widget_get_parent(nint widget);
        [LibraryImport(Gtk)] internal static partial void gtk_widget_set_visible(nint widget, [MarshalAs(UnmanagedType.Bool)] bool visible);
        [LibraryImport(Gtk)] internal static partial void gtk_widget_set_vexpand(nint widget, [MarshalAs(UnmanagedType.Bool)] bool expand);
        [LibraryImport(Gtk)] internal static partial void gtk_widget_set_hexpand(nint widget, [MarshalAs(UnmanagedType.Bool)] bool expand);
        [LibraryImport(Gtk)] internal static partial nint gtk_window_get_child(nint window);
        [LibraryImport(Gtk)] internal static partial void gtk_window_set_child(nint window, nint child);
        [LibraryImport(Gtk)] internal static partial nint gtk_box_new(int orientation, int spacing);
        [LibraryImport(Gtk)] internal static partial void gtk_box_append(nint box, nint child);
        [LibraryImport(Gtk)] internal static partial void gtk_box_prepend(nint box, nint child);
        [LibraryImport(Gtk)] internal static partial void gtk_box_remove(nint box, nint child);
        [LibraryImport(Gtk)] internal static partial void gtk_window_minimize(nint window);
        [LibraryImport(Gtk)] internal static partial void gtk_window_close(nint window);
        [LibraryImport("libgobject-2.0.so.0", StringMarshalling = StringMarshalling.Utf8)] internal static partial nuint g_signal_connect_data(nint instance, string signal, nint callback, nint data, nint destroyData, uint flags);
        [LibraryImport("libgobject-2.0.so.0")] internal static partial nint g_object_ref(nint value);
        [LibraryImport("libgobject-2.0.so.0")] internal static partial void g_object_unref(nint value);
        [LibraryImport("libwebkitgtk-6.0.so.4", StringMarshalling = StringMarshalling.Utf8)] internal static partial void webkit_web_view_execute_editing_command(nint view, string command);
    }
}
