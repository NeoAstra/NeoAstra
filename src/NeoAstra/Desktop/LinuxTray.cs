// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using global::NeoAstra.Desktop.Menus;

namespace NeoAstra.Desktop.Tray;

/// <summary>Freedesktop StatusNotifierItem presenter backed by the session GIO D-Bus connection.</summary>
internal sealed unsafe partial class LinuxTrayPresenter : INeoTrayPresenter, INeoApplicationBoundDesktopService
{
    private const string WatcherName = "org.kde.StatusNotifierWatcher";
    private const string WatcherPath = "/StatusNotifierWatcher";
    private const string WatcherInterface = "org.kde.StatusNotifierWatcher";
    private const string ItemInterface = "org.kde.StatusNotifierItem";
    private const string MenuInterface = "com.canonical.dbusmenu";
    private const string IntrospectionXml = """
        <node>
          <interface name="org.kde.StatusNotifierItem">
            <method name="ContextMenu"><arg type="i" direction="in"/><arg type="i" direction="in"/></method>
            <method name="Activate"><arg type="i" direction="in"/><arg type="i" direction="in"/></method>
            <method name="SecondaryActivate"><arg type="i" direction="in"/><arg type="i" direction="in"/></method>
            <method name="Scroll"><arg type="i" direction="in"/><arg type="s" direction="in"/></method>
            <property name="Category" type="s" access="read"/><property name="Id" type="s" access="read"/>
            <property name="Title" type="s" access="read"/><property name="Status" type="s" access="read"/>
            <property name="WindowId" type="u" access="read"/><property name="IconName" type="s" access="read"/>
            <property name="IconThemePath" type="s" access="read"/><property name="IconPixmap" type="a(iiay)" access="read"/>
            <property name="OverlayIconName" type="s" access="read"/><property name="OverlayIconPixmap" type="a(iiay)" access="read"/>
            <property name="AttentionIconName" type="s" access="read"/><property name="AttentionIconPixmap" type="a(iiay)" access="read"/>
            <property name="AttentionMovieName" type="s" access="read"/><property name="ToolTip" type="(sa(iiay)ss)" access="read"/>
            <property name="ItemIsMenu" type="b" access="read"/><property name="Menu" type="o" access="read"/>
            <signal name="NewTitle"/><signal name="NewIcon"/><signal name="NewAttentionIcon"/><signal name="NewOverlayIcon"/>
            <signal name="NewToolTip"/><signal name="NewStatus"><arg type="s"/></signal>
          </interface>
          <interface name="com.canonical.dbusmenu">
            <method name="GetLayout"><arg name="parentId" type="i" direction="in"/><arg name="recursionDepth" type="i" direction="in"/><arg name="propertyNames" type="as" direction="in"/><arg name="revision" type="u" direction="out"/><arg name="layout" type="(ia{sv}av)" direction="out"/></method>
            <method name="GetGroupProperties"><arg name="ids" type="ai" direction="in"/><arg name="propertyNames" type="as" direction="in"/><arg name="properties" type="a(ia{sv})" direction="out"/></method>
            <method name="GetProperty"><arg name="id" type="i" direction="in"/><arg name="name" type="s" direction="in"/><arg name="value" type="v" direction="out"/></method>
            <method name="Event"><arg name="id" type="i" direction="in"/><arg name="eventId" type="s" direction="in"/><arg name="data" type="v" direction="in"/><arg name="timestamp" type="u" direction="in"/></method>
            <method name="AboutToShow"><arg name="id" type="i" direction="in"/><arg name="needUpdate" type="b" direction="out"/></method>
            <signal name="LayoutUpdated"><arg name="revision" type="u"/><arg name="parent" type="i"/></signal>
            <signal name="ItemsPropertiesUpdated"><arg name="updated" type="a(ia{sv})"/><arg name="removed" type="a(ias)"/></signal>
            <property name="Version" type="u" access="read"/><property name="TextDirection" type="s" access="read"/><property name="Status" type="s" access="read"/><property name="IconThemePath" type="as" access="read"/>
          </interface>
        </node>
        """;

    private readonly NeoCommandService _commands;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> _objects = new(StringComparer.Ordinal);
    private NeoDispatcher? _dispatcher;
    private NeoApplication? _application;
    private nint _connection;
    private nint _nodeInfo;
    private nint _itemInfo;
    private nint _menuInfo;
    private nint _vtable;
    private GCHandle _self;
    private uint _watch;
    private volatile bool _watcherAvailable;
    private long _generation;
    private bool _disposed;

    internal LinuxTrayPresenter(NeoCommandService commands, NeoDispatcher? dispatcher)
    {
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _dispatcher = dispatcher;
        try
        {
            nint error = 0;
            _connection = Native.g_bus_get_sync(2, 0, &error);
            if (_connection == 0) { FreeError(error); return; }
            _nodeInfo = Native.g_dbus_node_info_new_for_xml(IntrospectionXml, &error);
            if (_nodeInfo == 0) { FreeError(error); Native.g_object_unref(_connection); _connection = 0; return; }
            _itemInfo = Native.g_dbus_node_info_lookup_interface(_nodeInfo, ItemInterface);
            _menuInfo = Native.g_dbus_node_info_lookup_interface(_nodeInfo, MenuInterface);
            if (_itemInfo == 0 || _menuInfo == 0) { Native.g_dbus_node_info_unref(_nodeInfo); Native.g_object_unref(_connection); _nodeInfo = _connection = 0; return; }
            _vtable = (nint)NativeMemory.Alloc((nuint)sizeof(GDBusInterfaceVTable));
            *(GDBusInterfaceVTable*)_vtable = VTable;
            _self = GCHandle.Alloc(this);
            _watch = Native.g_bus_watch_name_on_connection(_connection, WatcherName, 0, &WatcherAppeared, &WatcherVanished, GCHandle.ToIntPtr(_self), 0);
        }
        catch (DllNotFoundException) { ResetNative(); }
        catch (EntryPointNotFoundException) { ResetNative(); }
    }

    public NeoCapabilityInfo Support => _connection == 0
        ? new(NeoSupportLevel.None, 1, 0, "The GIO session D-Bus client is unavailable.")
        : !_watcherAvailable
            ? new(NeoSupportLevel.None, 1, 0, "The desktop session does not currently provide org.kde.StatusNotifierWatcher. KDE Plasma includes one; GNOME generally requires an AppIndicator-compatible shell extension.")
            : new(NeoSupportLevel.Limited, 1, 0, "Freedesktop StatusNotifierItem and DBusMenu are available through the desktop's session-bus watcher. GTK4 itself has no tray API, and bounds/attention are unavailable.");

    public event Action<string, bool>? Activated;

    void INeoApplicationBoundDesktopService.BindApplication(NeoApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (_application is not null && !ReferenceEquals(_application, application)) throw new InvalidOperationException("The tray presenter is already bound to another application.");
        if (_dispatcher is not null && !ReferenceEquals(_dispatcher, application.Dispatcher)) throw new InvalidOperationException("The tray presenter is already bound to another dispatcher.");
        _application = application;
        _dispatcher = application.Dispatcher;
    }

    public void Set(NeoTrayItemOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        EnsureAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connection == 0) throw new NotSupportedException("A GIO session D-Bus connection is required for Linux tray items.");
        if (!_watcherAvailable) throw new NotSupportedException("The Linux desktop session does not provide a StatusNotifierItem watcher.");
        var snapshot = options with { Menu = Array.AsReadOnly(options.Menu.ToArray()) };
        if (_entries.TryGetValue(options.Id, out var current))
        {
            current.Update(snapshot, checked(current.Revision + 1));
            Emit(current.ItemPath, ItemInterface, "NewTitle", 0);
            Emit(current.ItemPath, ItemInterface, "NewIcon", 0);
            Emit(current.ItemPath, ItemInterface, "NewToolTip", 0);
            Emit(current.MenuPath, MenuInterface, "LayoutUpdated", Parse("(ui)", $"(uint32 {current.Revision}, int32 0)"));
            return;
        }

        var suffix = "i" + checked(++_generation).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var entry = new Entry(snapshot, "/StatusNotifierItem/" + suffix, "/Menu/" + suffix, 1);
        nint error = 0;
        entry.ItemRegistration = Native.g_dbus_connection_register_object(_connection, entry.ItemPath, _itemInfo, (GDBusInterfaceVTable*)_vtable, GCHandle.ToIntPtr(_self), 0, &error);
        if (entry.ItemRegistration == 0) throw CreateException("Could not export the Linux StatusNotifierItem.", error);
        entry.MenuRegistration = Native.g_dbus_connection_register_object(_connection, entry.MenuPath, _menuInfo, (GDBusInterfaceVTable*)_vtable, GCHandle.ToIntPtr(_self), 0, &error);
        if (entry.MenuRegistration == 0)
        {
            Native.g_dbus_connection_unregister_object(_connection, entry.ItemRegistration);
            throw CreateException("Could not export the Linux tray DBusMenu.", error);
        }
        _entries.Add(options.Id, entry); _objects.Add(entry.ItemPath, entry); _objects.Add(entry.MenuPath, entry);
        try { RegisterWithWatcher(entry, throwOnFailure: true); }
        catch { _entries.Remove(options.Id); Destroy(entry); throw; }
    }

    public bool Remove(string id)
    {
        NeoMenuItem.ValidateId(id, nameof(id)); EnsureAccess(); ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_entries.Remove(id, out var entry)) return false;
        Destroy(entry);
        return true;
    }

    public ValueTask DisposeAsync()
    {
        var dispatcher = _dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess()) return dispatcher.InvokeAsync(DisposeOnDispatcher);
        DisposeOnDispatcher();
        return ValueTask.CompletedTask;
    }

    private void DisposeOnDispatcher()
    {
        if (_disposed) return;
        _disposed = true; Activated = null;
        foreach (var entry in _entries.Values.ToArray()) Destroy(entry);
        _entries.Clear(); _objects.Clear();
        if (_watch != 0) Native.g_bus_unwatch_name(_watch);
        _watch = 0;
        if (_nodeInfo != 0) Native.g_dbus_node_info_unref(_nodeInfo);
        if (_connection != 0) Native.g_object_unref(_connection);
        if (_vtable != 0) NativeMemory.Free((void*)_vtable);
        _nodeInfo = _itemInfo = _menuInfo = _connection = _vtable = 0;
        if (_self.IsAllocated) _self.Free();
    }

    private void Destroy(Entry entry)
    {
        _objects.Remove(entry.ItemPath); _objects.Remove(entry.MenuPath);
        if (_connection != 0)
        {
            if (entry.ItemRegistration != 0) Native.g_dbus_connection_unregister_object(_connection, entry.ItemRegistration);
            if (entry.MenuRegistration != 0) Native.g_dbus_connection_unregister_object(_connection, entry.MenuRegistration);
        }
    }

    private bool RegisterWithWatcher(Entry entry, bool throwOnFailure)
    {
        if (_connection == 0 || _disposed) return false;
        nint error = 0;
        var result = Native.g_dbus_connection_call_sync(_connection, WatcherName, WatcherPath, WatcherInterface, "RegisterStatusNotifierItem", Parse("(s)", $"({Quote(entry.ItemPath)},)"), 0, 0, 3000, 0, &error);
        if (result != 0) { Native.g_variant_unref(result); FreeError(error); return true; }
        if (throwOnFailure) throw CreateException("The Linux tray host rejected the StatusNotifierItem registration.", error);
        FreeError(error);
        return false;
    }

    private void OnWatcherAppeared()
    {
        if (_disposed) return;
        _watcherAvailable = true;
        foreach (var entry in _entries.Values) RegisterWithWatcher(entry, throwOnFailure: false);
    }

    private void InvokeActivation(Entry entry, bool secondary)
    {
        void Invoke() { try { Activated?.Invoke(entry.Options.Id, secondary); } catch { } }
        var dispatcher = _dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess()) { try { _ = dispatcher.InvokeAsync(Invoke); } catch { } }
        else Invoke();
    }

    private void InvokeMenu(Entry entry, int id)
    {
        if (!entry.MenuItems.TryGetValue(id, out var item) || !item.IsEnabled || !item.IsVisible) return;
        void Invoke()
        {
            try
            {
                if (item.CommandId is { } command) { _ = _commands.ActivateAsync(command); return; }
                switch (item.Role)
                {
                    case NeoMenuRole.Minimize: _application?.MainWindow?.Minimize(); break;
                    case NeoMenuRole.CloseWindow: _application?.MainWindow?.Close(); break;
                    case NeoMenuRole.Quit: if (_application is { } app) _ = app.RequestQuitAsync(); break;
                }
            }
            catch { }
        }
        var dispatcher = _dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess()) { try { _ = dispatcher.InvokeAsync(Invoke); } catch { } }
        else Invoke();
    }

    private nint GetProperty(Entry entry, string interfaceName, string property)
    {
        if (interfaceName == ItemInterface)
        {
            var iconPath = entry.Options.IconPath;
            return property switch
            {
                "Category" => Native.g_variant_new_string("ApplicationStatus"),
                "Id" => Native.g_variant_new_string(entry.Options.Id),
                "Title" => Native.g_variant_new_string(entry.Options.ToolTip ?? entry.Options.Id),
                "Status" => Native.g_variant_new_string("Active"),
                "WindowId" => Native.g_variant_new_uint32(0),
                "IconName" => Native.g_variant_new_string(iconPath is null ? "application-x-executable" : Path.GetFileNameWithoutExtension(iconPath)),
                "IconThemePath" => Native.g_variant_new_string(iconPath is null ? string.Empty : Path.GetDirectoryName(iconPath) ?? string.Empty),
                "IconPixmap" or "OverlayIconPixmap" or "AttentionIconPixmap" => Parse("a(iiay)", "[]"),
                "OverlayIconName" or "AttentionIconName" or "AttentionMovieName" => Native.g_variant_new_string(string.Empty),
                "ToolTip" => Parse("(sa(iiay)ss)", $"({Quote(iconPath is null ? string.Empty : Path.GetFileNameWithoutExtension(iconPath))}, [], {Quote(entry.Options.ToolTip ?? entry.Options.Id)}, '')"),
                "ItemIsMenu" => Native.g_variant_new_boolean(false),
                "Menu" => Native.g_variant_new_object_path(entry.MenuPath),
                _ => 0,
            };
        }
        if (interfaceName == MenuInterface)
        {
            return property switch
            {
                "Version" => Native.g_variant_new_uint32(4),
                "TextDirection" => Native.g_variant_new_string("ltr"),
                "Status" => Native.g_variant_new_string("normal"),
                "IconThemePath" => Parse("as", "[]"),
                _ => 0,
            };
        }
        return 0;
    }

    private void HandleMethod(Entry entry, string interfaceName, string method, nint parameters, nint invocation)
    {
        if (interfaceName == ItemInterface)
        {
            if (method == "Activate") InvokeActivation(entry, false);
            else if (method is "SecondaryActivate" or "ContextMenu") InvokeActivation(entry, true);
            Native.g_dbus_method_invocation_return_value(invocation, 0);
            return;
        }
        if (interfaceName != MenuInterface) { ReturnError(invocation, "org.neoastra.Error.Unsupported", "The tray interface is unsupported."); return; }
        switch (method)
        {
            case "GetLayout":
            {
                var parent = ChildInt32(parameters, 0);
                var layout = entry.Layout(parent);
                if (layout is null) { ReturnError(invocation, "com.canonical.dbusmenu.Error.InvalidMenu", "The menu item does not exist."); return; }
                Native.g_dbus_method_invocation_return_value(invocation, Parse("(u(ia{sv}av))", $"(uint32 {entry.Revision}, {layout})"));
                return;
            }
            case "GetGroupProperties":
            {
                Native.g_dbus_method_invocation_return_value(invocation, Parse("(a(ia{sv}))", $"({entry.GroupProperties()},)"));
                return;
            }
            case "GetProperty":
            {
                var id = ChildInt32(parameters, 0); var name = ChildString(parameters, 1);
                var property = entry.Property(id, name);
                if (property is null) { ReturnError(invocation, "com.canonical.dbusmenu.Error.InvalidMenu", "The menu property does not exist."); return; }
                Native.g_dbus_method_invocation_return_value(invocation, Parse("(v)", $"(<{property}>,)"));
                return;
            }
            case "Event":
                if (ChildString(parameters, 1) == "clicked") InvokeMenu(entry, ChildInt32(parameters, 0));
                Native.g_dbus_method_invocation_return_value(invocation, 0);
                return;
            case "AboutToShow":
                Native.g_dbus_method_invocation_return_value(invocation, Parse("(b)", "(false,)"));
                return;
            default:
                ReturnError(invocation, "org.neoastra.Error.Unsupported", "The DBusMenu method is unsupported.");
                return;
        }
    }

    private void Emit(string path, string interfaceName, string signal, nint parameters)
    {
        if (_connection == 0) return;
        nint error = 0; Native.g_dbus_connection_emit_signal(_connection, 0, path, interfaceName, signal, parameters, &error); FreeError(error);
    }

    private void EnsureAccess()
    {
        if (_dispatcher is not null && !_dispatcher.CheckAccess()) throw new InvalidOperationException("Linux tray mutation requires the NeoAstra UI dispatcher.");
    }

    private void ResetNative()
    {
        try { if (_watch != 0) Native.g_bus_unwatch_name(_watch); } catch { }
        try { if (_nodeInfo != 0) Native.g_dbus_node_info_unref(_nodeInfo); } catch { }
        try { if (_connection != 0) Native.g_object_unref(_connection); } catch { }
        if (_vtable != 0) NativeMemory.Free((void*)_vtable);
        if (_self.IsAllocated) _self.Free();
        _watch = 0; _nodeInfo = _itemInfo = _menuInfo = _connection = _vtable = 0;
    }

    private static int ChildInt32(nint tuple, nuint index) { var child = Native.g_variant_get_child_value(tuple, index); try { return Native.g_variant_get_int32(child); } finally { Native.g_variant_unref(child); } }
    private static string ChildString(nint tuple, nuint index) { var child = Native.g_variant_get_child_value(tuple, index); try { return Marshal.PtrToStringUTF8(Native.g_variant_get_string(child, null)) ?? string.Empty; } finally { Native.g_variant_unref(child); } }
    private static nint Parse(string signature, string text)
    {
        var type = Native.g_variant_type_new(signature); nint error = 0;
        try
        {
            var value = Native.g_variant_parse(type, text, 0, 0, &error);
            if (value == 0) throw CreateException("Could not encode a Linux tray D-Bus value.", error);
            return value;
        }
        finally { Native.g_variant_type_free(type); }
    }
    private static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('\'');
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\': builder.Append("\\\\"); break;
                case '\'': builder.Append("\\'"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (char.IsControl(character)) builder.Append("\\u").Append(((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    else builder.Append(character);
                    break;
            }
        }
        return builder.Append('\'').ToString();
    }
    private static Exception CreateException(string message, nint error)
    {
        var detail = error == 0 ? null : Marshal.PtrToStringUTF8(((GError*)error)->Message);
        FreeError(error); return new InvalidOperationException(detail is null ? message : message + " " + detail);
    }
    private static void FreeError(nint error) { if (error != 0) Native.g_error_free(error); }
    private static void ReturnError(nint invocation, string name, string message) => Native.g_dbus_method_invocation_return_dbus_error(invocation, name, message);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void MethodCalled(nint connection, nint sender, nint objectPath, nint interfaceName, nint methodName, nint parameters, nint invocation, nint userData)
    {
        try
        {
            var owner = (LinuxTrayPresenter)GCHandle.FromIntPtr(userData).Target!;
            var path = Marshal.PtrToStringUTF8(objectPath) ?? string.Empty;
            if (owner._objects.TryGetValue(path, out var entry)) owner.HandleMethod(entry, Marshal.PtrToStringUTF8(interfaceName) ?? string.Empty, Marshal.PtrToStringUTF8(methodName) ?? string.Empty, parameters, invocation);
            else ReturnError(invocation, "org.neoastra.Error.NotFound", "The tray item no longer exists.");
        }
        catch { ReturnError(invocation, "org.neoastra.Error.Failed", "The tray operation failed."); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint PropertyRead(nint connection, nint sender, nint objectPath, nint interfaceName, nint propertyName, nint error, nint userData)
    {
        try
        {
            var owner = (LinuxTrayPresenter)GCHandle.FromIntPtr(userData).Target!;
            var path = Marshal.PtrToStringUTF8(objectPath) ?? string.Empty;
            return owner._objects.TryGetValue(path, out var entry) ? owner.GetProperty(entry, Marshal.PtrToStringUTF8(interfaceName) ?? string.Empty, Marshal.PtrToStringUTF8(propertyName) ?? string.Empty) : 0;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void WatcherAppeared(nint connection, nint name, nint ownerName, nint userData)
    {
        try { ((LinuxTrayPresenter)GCHandle.FromIntPtr(userData).Target!).OnWatcherAppeared(); } catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void WatcherVanished(nint connection, nint name, nint userData)
    {
        try { ((LinuxTrayPresenter)GCHandle.FromIntPtr(userData).Target!)._watcherAvailable = false; } catch { }
    }

    private static GDBusInterfaceVTable VTable => new() { MethodCall = &MethodCalled, GetProperty = &PropertyRead };

    private sealed class Entry
    {
        internal Entry(NeoTrayItemOptions options, string itemPath, string menuPath, uint revision) { Options = options; ItemPath = itemPath; MenuPath = menuPath; Update(options, revision); }
        internal NeoTrayItemOptions Options { get; private set; }
        internal string ItemPath { get; }
        internal string MenuPath { get; }
        internal uint ItemRegistration { get; set; }
        internal uint MenuRegistration { get; set; }
        internal uint Revision { get; private set; }
        internal Dictionary<int, NeoMenuItem> MenuItems { get; } = [];
        private Dictionary<int, Node> Nodes { get; } = [];

        internal void Update(NeoTrayItemOptions options, uint revision)
        {
            Options = options; Revision = revision; MenuItems.Clear(); Nodes.Clear();
            var root = new Node(0, null); Nodes.Add(0, root); var next = 0; Add(options.Menu, root, ref next);
        }
        private void Add(IReadOnlyList<NeoMenuItem> items, Node parent, ref int next)
        {
            foreach (var item in items)
            {
                var node = new Node(++next, item); parent.Children.Add(node); Nodes.Add(node.Id, node); MenuItems.Add(node.Id, item);
                if (item.Children.Count != 0) Add(item.Children, node, ref next);
            }
        }
        internal string? Layout(int id) => Nodes.TryGetValue(id, out var node) ? Layout(node) : null;
        internal string GroupProperties() => "[" + string.Join(",", Nodes.Values.Select(node => $"(int32 {node.Id}, {Properties(node)})")) + "]";
        internal string? Property(int id, string name)
        {
            if (!Nodes.TryGetValue(id, out var node)) return null;
            return PropertyPairs(node).FirstOrDefault(pair => pair.Name == name).Value;
        }
        private static string Layout(Node node) => $"(int32 {node.Id}, {Properties(node)}, [{string.Join(",", node.Children.Select(child => "<" + Layout(child) + ">"))}])";
        private static string Properties(Node node) => "{" + string.Join(",", PropertyPairs(node).Select(pair => $"{Quote(pair.Name)}: <{pair.Value}>")) + "}";
        private static IEnumerable<(string Name, string Value)> PropertyPairs(Node node)
        {
            if (node.Item is null) { if (node.Children.Count != 0) yield return ("children-display", Quote("submenu")); yield break; }
            var item = node.Item;
            if (item.Kind == NeoMenuItemKind.Separator) { yield return ("type", Quote("separator")); yield break; }
            yield return ("label", Quote(item.Text ?? item.Role?.ToString() ?? item.Id));
            yield return ("enabled", item.IsEnabled ? "true" : "false");
            yield return ("visible", item.IsVisible ? "true" : "false");
            if (item.IsChecked) { yield return ("toggle-type", Quote("checkmark")); yield return ("toggle-state", "int32 1"); }
            if (node.Children.Count != 0) yield return ("children-display", Quote("submenu"));
        }
        private sealed class Node(int id, NeoMenuItem? item)
        {
            internal int Id { get; } = id;
            internal NeoMenuItem? Item { get; } = item;
            internal List<Node> Children { get; } = [];
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct GError { internal uint Domain; internal int Code; internal nint Message; }
    [StructLayout(LayoutKind.Sequential)] private struct GDBusInterfaceVTable
    {
        internal delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, void> MethodCall;
        internal delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint> GetProperty;
        internal nint SetProperty;
        internal nint Padding1, Padding2, Padding3, Padding4, Padding5, Padding6, Padding7, Padding8;
    }

    private static partial class Native
    {
        private const string Gio = "libgio-2.0.so.0";
        private const string Glib = "libglib-2.0.so.0";
        private const string GObject = "libgobject-2.0.so.0";
        [LibraryImport(Gio)] internal static partial nint g_bus_get_sync(int busType, nint cancellable, nint* error);
        [LibraryImport(Gio, StringMarshalling = StringMarshalling.Utf8)] internal static partial uint g_bus_watch_name_on_connection(nint connection, string name, int flags, delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void> appeared, delegate* unmanaged[Cdecl]<nint, nint, nint, void> vanished, nint userData, nint notify);
        [LibraryImport(Gio)] internal static partial void g_bus_unwatch_name(uint watcherId);
        [LibraryImport(Gio, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint g_dbus_node_info_new_for_xml(string xml, nint* error);
        [LibraryImport(Gio, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint g_dbus_node_info_lookup_interface(nint info, string name);
        [LibraryImport(Gio)] internal static partial void g_dbus_node_info_unref(nint info);
        [LibraryImport(Gio, StringMarshalling = StringMarshalling.Utf8)] internal static partial uint g_dbus_connection_register_object(nint connection, string objectPath, nint interfaceInfo, GDBusInterfaceVTable* vtable, nint userData, nint notify, nint* error);
        [LibraryImport(Gio)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool g_dbus_connection_unregister_object(nint connection, uint registrationId);
        [LibraryImport(Gio, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint g_dbus_connection_call_sync(nint connection, string busName, string objectPath, string interfaceName, string methodName, nint parameters, nint replyType, int flags, int timeoutMilliseconds, nint cancellable, nint* error);
        [LibraryImport(Gio, StringMarshalling = StringMarshalling.Utf8)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool g_dbus_connection_emit_signal(nint connection, nint destination, string objectPath, string interfaceName, string signalName, nint parameters, nint* error);
        [LibraryImport(Gio)] internal static partial void g_dbus_method_invocation_return_value(nint invocation, nint parameters);
        [LibraryImport(Gio, StringMarshalling = StringMarshalling.Utf8)] internal static partial void g_dbus_method_invocation_return_dbus_error(nint invocation, string errorName, string errorMessage);
        [LibraryImport(Glib, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint g_variant_type_new(string typeString);
        [LibraryImport(Glib)] internal static partial void g_variant_type_free(nint type);
        [LibraryImport(Glib, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint g_variant_parse(nint type, string text, nint limit, nint endPointer, nint* error);
        [LibraryImport(Glib)] internal static partial void g_variant_unref(nint value);
        [LibraryImport(Glib)] internal static partial nint g_variant_get_child_value(nint value, nuint index);
        [LibraryImport(Glib)] internal static partial int g_variant_get_int32(nint value);
        [LibraryImport(Glib)] internal static partial nint g_variant_get_string(nint value, nuint* length);
        [LibraryImport(Glib, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint g_variant_new_string(string value);
        [LibraryImport(Glib, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint g_variant_new_object_path(string value);
        [LibraryImport(Glib)] internal static partial nint g_variant_new_uint32(uint value);
        [LibraryImport(Glib)] internal static partial nint g_variant_new_boolean([MarshalAs(UnmanagedType.Bool)] bool value);
        [LibraryImport(Glib)] internal static partial void g_error_free(nint error);
        [LibraryImport(GObject)] internal static partial void g_object_unref(nint value);
    }
}
