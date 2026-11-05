// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace NeoAstra.Desktop.Dialogs;

/// <summary>
/// The messages of the FileChooser interface of the desktop portal (<c>org.freedesktop.portal.FileChooser</c>), written as
/// GVariant text, and the reading of its answers. Kept apart from the D-Bus calls so that it is checked on every platform.
/// </summary>
internal static class LinuxPortalFileChooserProtocol
{
    /// <summary>The version of the interface that has the <c>directory</c> option, without which a portal opens no folder dialog.</summary>
    internal const uint DirectoryVersion = 3;

    /// <summary>The GVariant type of the parameters of <c>OpenFile</c> and <c>SaveFile</c>: parent window, title, options.</summary>
    internal const string ParametersType = "(ssa{sv})";

    /// <summary>The GVariant type of the <c>Response</c> signal of a request: the response code and the results.</summary>
    internal const string ResponseType = "(ua{sv})";

    /// <summary>The most paths that one answer may give, as for the other dialogs.</summary>
    internal const int MaximumSelections = 256;

    /// <summary>A new token for the <c>handle_token</c> option: letters, digits and underscores, as an element of an object path.</summary>
    internal static string NewToken() => "neoastra_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>
    /// The object path of the request that a call with a token makes: the portal names it after the unique name of the caller
    /// on the bus, without its colon and with underscores for its dots, and after the token.
    /// </summary>
    /// <exception cref="ArgumentException">The unique name or the token is empty or malformed.</exception>
    internal static string RequestPath(string uniqueName, string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(uniqueName);
        ArgumentException.ThrowIfNullOrEmpty(token);
        if (uniqueName[0] != ':' || uniqueName.Length == 1 || uniqueName.Skip(1).Any(static c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))) throw new ArgumentException("A unique bus name is malformed.", nameof(uniqueName));
        if (token.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c == '_'))) throw new ArgumentException("A request token is malformed.", nameof(token));
        return "/org/freedesktop/portal/desktop/request/" + uniqueName[1..].Replace('.', '_').Replace('-', '_') + "/" + token;
    }

    /// <summary>The parameters of <c>OpenFile</c>: one or more files, or one or more folders.</summary>
    internal static string OpenFileParameters(NeoFileDialogOptions options, bool folders, string token)
    {
        ArgumentNullException.ThrowIfNull(options);
        var entries = new List<string> { Entry("handle_token", Quote(token)) };
        if (options.AllowMultiple) entries.Add(Entry("multiple", "true"));
        if (folders) entries.Add(Entry("directory", "true"));
        else if (Filters(options.Filters) is { } filters) entries.Add(Entry("filters", filters));
        if (options.InitialDirectory is { } initial) entries.Add(Entry("current_folder", ByteString(initial)));
        return Parameters(options.Title, entries);
    }

    /// <summary>The parameters of <c>SaveFile</c>: one file, in the initial folder, with the suggested name.</summary>
    internal static string SaveFileParameters(NeoFileDialogOptions options, string token)
    {
        ArgumentNullException.ThrowIfNull(options);
        var entries = new List<string> { Entry("handle_token", Quote(token)) };
        if (Filters(options.Filters) is { } filters) entries.Add(Entry("filters", filters));
        if (options.SuggestedFileName is { } name) entries.Add(Entry("current_name", Quote(name)));
        if (options.InitialDirectory is { } initial) entries.Add(Entry("current_folder", ByteString(initial)));
        return Parameters(options.Title, entries);
    }

    /// <summary>
    /// The paths that a response gives: its code is 0 when the user chose, 1 when the user canceled, and another value when
    /// the dialog ended otherwise. A chosen URI that is not a local file makes the answer invalid.
    /// </summary>
    internal static NeoDesktopResult<IReadOnlyList<string>> Paths(uint response, IReadOnlyList<string>? uris)
    {
        if (response == 1) return NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Canceled);
        if (response != 0) return NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Failed, "dialog_failed");
        if (uris is null || uris.Count == 0) return NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Canceled);
        if (uris.Count > MaximumSelections) return NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Failed, "invalid_native_result");
        var paths = new string[uris.Count];
        for (var index = 0; index < uris.Count; index++)
        {
            if (!TryGetLocalPath(uris[index], out var path)) return NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Failed, "invalid_native_result");
            paths[index] = path!;
        }
        return NeoDesktopResult<IReadOnlyList<string>>.Success(Array.AsReadOnly(paths));
    }

    /// <summary>
    /// Reads the absolute path of a <c>file:</c> URI without a host (or with <c>localhost</c>), whose escapes are UTF-8 bytes.
    /// A URI with a query, a fragment, another host, an invalid escape, a NUL or bytes that are not UTF-8 has none.
    /// </summary>
    internal static bool TryGetLocalPath(string? uri, out string? path)
    {
        path = null;
        if (uri is null || uri.Length > 32_768 || !uri.StartsWith("file://", StringComparison.Ordinal)) return false;
        var rest = uri.AsSpan("file://".Length);
        if (rest.StartsWith("localhost/", StringComparison.Ordinal)) rest = rest["localhost".Length..];
        if (rest.IsEmpty || rest[0] != '/') return false;
        var bytes = new List<byte>(rest.Length);
        Span<byte> encoded = stackalloc byte[4];
        for (var index = 0; index < rest.Length; index++)
        {
            var character = rest[index];
            if (character is '?' or '#' or '\0' || char.IsControl(character)) return false;
            if (character == '%')
            {
                if (index + 2 >= rest.Length || !byte.TryParse(rest.Slice(index + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value) || value == 0) return false;
                bytes.Add(value);
                index += 2;
                continue;
            }
            if (char.IsSurrogate(character))
            {
                if (!char.IsHighSurrogate(character) || index + 1 >= rest.Length || !char.IsLowSurrogate(rest[index + 1])) return false;
                var count = Encoding.UTF8.GetBytes(rest.Slice(index, 2), encoded);
                for (var offset = 0; offset < count; offset++) bytes.Add(encoded[offset]);
                index++;
                continue;
            }
            var length = Encoding.UTF8.GetBytes(rest.Slice(index, 1), encoded);
            for (var offset = 0; offset < length; offset++) bytes.Add(encoded[offset]);
        }
        try { path = new UTF8Encoding(false, true).GetString(bytes.ToArray()); }
        catch (DecoderFallbackException) { return false; }
        return true;
    }

    /// <summary>A GVariant string literal: quoted, with its backslashes, quotes and control characters escaped.</summary>
    internal static string Quote(string value)
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
                    if (char.IsControl(character)) builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else builder.Append(character);
                    break;
            }
        }
        return builder.Append('\'').ToString();
    }

    /// <summary>The bytes of a path as a NUL-terminated GVariant byte array (<c>ay</c>), as the portal takes a folder.</summary>
    internal static string ByteString(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(path);
        var builder = new StringBuilder(bytes.Length * 10 + 16).Append('[');
        foreach (var value in bytes) builder.Append("byte 0x").Append(value.ToString("x2", CultureInfo.InvariantCulture)).Append(", ");
        return builder.Append("byte 0x00]").ToString();
    }

    // Each filter is a name and its patterns: 0 for a glob of an extension, 1 for a MIME type.
    private static string? Filters(IReadOnlyList<NeoFileDialogFilter> filters)
    {
        if (filters.Count == 0) return null;
        var entries = filters.Select(static filter =>
        {
            var patterns = filter.Extensions.Select(static extension => "(uint32 0, " + Quote("*." + extension) + ")")
                .Concat(filter.MimeTypes.Select(static mime => "(uint32 1, " + Quote(mime) + ")"));
            return "(" + Quote(filter.Name) + ", [" + string.Join(", ", patterns) + "])";
        });
        return "[" + string.Join(", ", entries) + "]";
    }

    private static string Entry(string name, string value) => Quote(name) + ": <" + value + ">";

    // No parent window: NeoAstra cannot name a window to the portal yet (see doc/known-limitations.md).
    private static string Parameters(string? title, List<string> entries) => "('', " + Quote(title ?? string.Empty) + ", {" + string.Join(", ", entries) + "})";
}

/// <summary>
/// Shows the file and folder dialogs of the desktop through its portal (<c>xdg-desktop-portal</c>): the dialogs of KDE, GNOME
/// and the other desktops, also from a sandbox. The calls go over the session bus with GIO, on the UI thread of the
/// application, whose main loop delivers their answers.
/// </summary>
internal sealed partial class LinuxPortalFileChooser
{
    private const string PortalName = "org.freedesktop.portal.Desktop";
    private const string PortalPath = "/org/freedesktop/portal/desktop";
    private const string FileChooserInterface = "org.freedesktop.portal.FileChooser";
    private const string RequestInterface = "org.freedesktop.portal.Request";
    private const int SessionBus = 2;

    // The errors that say that no portal, or no FileChooser of the portal, answers on this bus.
    private static readonly HashSet<string> MissingPortalErrors = new(StringComparer.Ordinal)
    {
        "org.freedesktop.DBus.Error.ServiceUnknown", "org.freedesktop.DBus.Error.NameHasNoOwner", "org.freedesktop.DBus.Error.UnknownMethod",
        "org.freedesktop.DBus.Error.UnknownInterface", "org.freedesktop.DBus.Error.UnknownObject", "org.freedesktop.DBus.Error.UnknownProperty",
        "org.freedesktop.DBus.Error.Spawn.ServiceNotFound",
    };

    // Used on the UI thread only.
    private nint _connection;
    private uint? _version;
    private volatile bool _missing;

    /// <summary>Gets whether a session bus may be reached, where a portal could answer.</summary>
    internal static bool SessionBusConfigured()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"))) return true;
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return !string.IsNullOrEmpty(runtime) && File.Exists(Path.Combine(runtime, "bus"));
    }

    /// <summary>
    /// Shows a dialog of the portal and returns the chosen paths, not yet checked against the scope, or null when no portal
    /// shows it (no session bus, no portal, an older portal without folder dialogs, a failed call), for another presenter.
    /// </summary>
    /// <exception cref="OperationCanceledException">The caller canceled; the dialog is closed.</exception>
    internal async ValueTask<NeoDesktopResult<IReadOnlyList<string>>?> PickAsync(NeoDispatcher dispatcher, NeoFileDialogOptions options, bool folders, bool save, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(options);
        if (_missing) return null;
        cancellationToken.ThrowIfCancellationRequested();
        var version = await (await dispatcher.InvokeAsync(VersionAsync, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        if (version is null || folders && version < LinuxPortalFileChooserProtocol.DirectoryVersion) return null;

        var token = LinuxPortalFileChooserProtocol.NewToken();
        var parameters = save ? LinuxPortalFileChooserProtocol.SaveFileParameters(options, token) : LinuxPortalFileChooserProtocol.OpenFileParameters(options, folders, token);
        var request = new Request(this, token);
        await dispatcher.InvokeAsync(() => request.Start(save ? "SaveFile" : "OpenFile", parameters), cancellationToken).ConfigureAwait(false);
        Answer answer;
        using (cancellationToken.Register(() => { try { dispatcher.Post(request.Close); } catch (ObjectDisposedException) { request.Abandon(); } }))
            answer = await request.Completion.Task.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        switch (answer.Kind)
        {
            case AnswerKind.Responded: return LinuxPortalFileChooserProtocol.Paths(answer.Response, answer.Uris);
            case AnswerKind.Missing: _missing = true; return null;
            case AnswerKind.Closed: return NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Canceled);
            default: return null;
        }
    }

    // The version of the FileChooser of the portal, read once; null when no portal answers.
    private unsafe Task<uint?> VersionAsync()
    {
        if (_version is { } known) return Task.FromResult<uint?>(known);
        if (!EnsureConnection()) { _missing = true; return Task.FromResult<uint?>(null); }
        var call = new VersionCall(this);
        var handle = GCHandle.Alloc(call);
        try
        {
            var parameters = Parse("(ss)", "(" + LinuxPortalFileChooserProtocol.Quote(FileChooserInterface) + ", 'version')");
            try { Call(PortalPath, "org.freedesktop.DBus.Properties", "Get", parameters, "(v)", &VersionReceived, GCHandle.ToIntPtr(handle)); }
            finally { Native.g_variant_unref(parameters); }
        }
        catch
        {
            handle.Free();
            return Task.FromResult<uint?>(null);
        }
        return call.Completion.Task;
    }

    private unsafe bool EnsureConnection()
    {
        if (_connection != 0) return true;
        nint error = 0;
        _connection = Native.g_bus_get_sync(SessionBus, 0, &error);
        FreeError(error);
        return _connection != 0;
    }

    private unsafe void Call(string path, string interfaceName, string method, nint parameters, string? replyType, delegate* unmanaged[Cdecl]<nint, nint, nint, void> callback, nint userData)
    {
        var type = replyType is null ? 0 : Native.g_variant_type_new(replyType);
        try { Native.g_dbus_connection_call(_connection, PortalName, path, interfaceName, method, parameters, type, 0, -1, 0, callback, userData); }
        finally { if (type != 0) Native.g_variant_type_free(type); }
    }

    // A failed call tells whether the portal is missing, or whether it is there and failed.
    private static AnswerKind Failure(nint error)
    {
        if (error == 0) return AnswerKind.Failed;
        var name = Native.g_dbus_error_get_remote_error(error);
        try { return name != 0 && MissingPortalErrors.Contains(Marshal.PtrToStringUTF8(name) ?? string.Empty) ? AnswerKind.Missing : AnswerKind.Failed; }
        finally { if (name != 0) Native.g_free(name); Native.g_error_free(error); }
    }

    private static unsafe nint Parse(string signature, string text)
    {
        var type = Native.g_variant_type_new(signature);
        nint error = 0;
        try
        {
            var value = Native.g_variant_parse(type, text, 0, 0, &error);
            if (value == 0) { FreeError(error); throw new InvalidOperationException("Could not encode a desktop portal D-Bus value."); }
            return value;
        }
        finally { Native.g_variant_type_free(type); }
    }

    private static string? TypeString(nint value) => Marshal.PtrToStringUTF8(Native.g_variant_get_type_string(value));

    private static void FreeError(nint error) { if (error != 0) Native.g_error_free(error); }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void VersionReceived(nint source, nint result, nint data)
    {
        var handle = GCHandle.FromIntPtr(data);
        try
        {
            if (handle.Target is not VersionCall call) return;
            uint? version = null;
            try
            {
                nint error = 0;
                var reply = Native.g_dbus_connection_call_finish(source, result, &error);
                if (reply == 0)
                {
                    if (Failure(error) == AnswerKind.Missing) call.Owner._missing = true;
                }
                else
                {
                    try
                    {
                        var boxed = Native.g_variant_get_child_value(reply, 0);
                        try
                        {
                            var inner = Native.g_variant_get_variant(boxed);
                            try { if (TypeString(inner) == "u") version = Native.g_variant_get_uint32(inner); }
                            finally { Native.g_variant_unref(inner); }
                        }
                        finally { Native.g_variant_unref(boxed); }
                    }
                    finally { Native.g_variant_unref(reply); }
                    if (version is { } value) call.Owner._version = value;
                }
            }
            catch { version = null; }
            call.Completion.TrySetResult(version);
        }
        finally { handle.Free(); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void CallReturned(nint source, nint result, nint data)
    {
        var handle = GCHandle.FromIntPtr(data);
        try
        {
            if (handle.Target is not Request request) return;
            nint error = 0;
            var reply = Native.g_dbus_connection_call_finish(source, result, &error);
            if (reply == 0) { request.Fail(Failure(error)); return; }
            try
            {
                var child = Native.g_variant_get_child_value(reply, 0);
                try { request.Started(Marshal.PtrToStringUTF8(Native.g_variant_get_string(child, null))); }
                finally { Native.g_variant_unref(child); }
            }
            finally { Native.g_variant_unref(reply); }
        }
        catch { if (handle.Target is Request request) request.Fail(AnswerKind.Failed); }
        finally { handle.Free(); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void ResponseReceived(nint connection, nint sender, nint objectPath, nint interfaceName, nint signalName, nint parameters, nint data)
    {
        try
        {
            if (GCHandle.FromIntPtr(data).Target is not Request request) return;
            if (parameters == 0 || TypeString(parameters) != LinuxPortalFileChooserProtocol.ResponseType) { request.Fail(AnswerKind.Failed); return; }
            uint response;
            var code = Native.g_variant_get_child_value(parameters, 0);
            try { response = Native.g_variant_get_uint32(code); }
            finally { Native.g_variant_unref(code); }
            List<string>? uris = null;
            var results = Native.g_variant_get_child_value(parameters, 1);
            try
            {
                var type = Native.g_variant_type_new("as");
                nint list;
                try { list = Native.g_variant_lookup_value(results, "uris", type); }
                finally { Native.g_variant_type_free(type); }
                if (list != 0)
                {
                    try
                    {
                        var count = Native.g_variant_n_children(list);
                        uris = new List<string>((int)Math.Min(count, (nuint)LinuxPortalFileChooserProtocol.MaximumSelections + 1));
                        for (nuint index = 0; index < count && index <= (nuint)LinuxPortalFileChooserProtocol.MaximumSelections; index++)
                        {
                            var item = Native.g_variant_get_child_value(list, index);
                            try { uris.Add(Marshal.PtrToStringUTF8(Native.g_variant_get_string(item, null)) ?? string.Empty); }
                            finally { Native.g_variant_unref(item); }
                        }
                    }
                    finally { Native.g_variant_unref(list); }
                }
            }
            finally { Native.g_variant_unref(results); }
            request.Respond(response, uris);
        }
        catch
        {
            if (GCHandle.FromIntPtr(data).Target is Request request) request.Fail(AnswerKind.Failed);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SubscriptionReleased(nint data)
    {
        try { GCHandle.FromIntPtr(data).Free(); } catch { }
    }

    private enum AnswerKind { Responded, Closed, Missing, Failed }

    private readonly record struct Answer(AnswerKind Kind, uint Response = 0, IReadOnlyList<string>? Uris = null);

    private sealed class VersionCall(LinuxPortalFileChooser owner)
    {
        internal LinuxPortalFileChooser Owner { get; } = owner;
        internal TaskCompletionSource<uint?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // One dialog. Its methods run on the UI thread, except Abandon.
    private sealed class Request(LinuxPortalFileChooser owner, string token)
    {
        private uint _subscription;
        private string? _path;
        private bool _closeWhenStarted;

        internal TaskCompletionSource<Answer> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Listens for the response before it calls, at the path that the portal gives a request with this token.
        internal unsafe void Start(string method, string parameters)
        {
            try
            {
                var unique = Marshal.PtrToStringUTF8(Native.g_dbus_connection_get_unique_name(owner._connection));
                if (string.IsNullOrEmpty(unique)) { Fail(AnswerKind.Failed); return; }
                Subscribe(LinuxPortalFileChooserProtocol.RequestPath(unique, token));
                var value = Parse(LinuxPortalFileChooserProtocol.ParametersType, parameters);
                var handle = GCHandle.Alloc(this);
                try { owner.Call(PortalPath, FileChooserInterface, method, value, "(o)", &CallReturned, GCHandle.ToIntPtr(handle)); }
                catch { handle.Free(); throw; }
                finally { Native.g_variant_unref(value); }
            }
            catch { Fail(AnswerKind.Failed); }
        }

        // A portal older than the handle_token option names the request otherwise: it is followed at the path it gives.
        internal void Started(string? path)
        {
            if (Completion.Task.IsCompleted)
            {
                if (_closeWhenStarted && !string.IsNullOrEmpty(path)) CloseRequest(path);
                return;
            }
            if (string.IsNullOrEmpty(path)) { Fail(AnswerKind.Failed); return; }
            if (!string.Equals(path, _path, StringComparison.Ordinal)) Subscribe(path);
        }

        internal void Respond(uint response, IReadOnlyList<string>? uris)
        {
            Unsubscribe();
            Completion.TrySetResult(new(AnswerKind.Responded, response, uris));
        }

        internal void Fail(AnswerKind kind)
        {
            Unsubscribe();
            Completion.TrySetResult(new(kind));
        }

        // The caller canceled: the dialog of the portal is closed.
        internal void Close()
        {
            if (Completion.Task.IsCompleted) return;
            var path = _path;
            Unsubscribe();
            if (path is not null) CloseRequest(path);
            _closeWhenStarted = true;
            Completion.TrySetResult(new(AnswerKind.Closed));
        }

        // The application is shutting down: nothing runs on its UI thread any more.
        internal void Abandon() => Completion.TrySetResult(new(AnswerKind.Closed));

        private unsafe void CloseRequest(string path)
        {
            try { owner.Call(path, RequestInterface, "Close", 0, null, null, 0); } catch { }
        }

        private unsafe void Subscribe(string path)
        {
            Unsubscribe();
            var handle = GCHandle.Alloc(this);
            try { _subscription = Native.g_dbus_connection_signal_subscribe(owner._connection, PortalName, RequestInterface, "Response", path, null, 0, &ResponseReceived, GCHandle.ToIntPtr(handle), &SubscriptionReleased); }
            catch { handle.Free(); throw; }
            if (_subscription == 0) { handle.Free(); throw new InvalidOperationException("Could not listen to the desktop portal."); }
            _path = path;
        }

        private void Unsubscribe()
        {
            if (_subscription == 0) return;
            Native.g_dbus_connection_signal_unsubscribe(owner._connection, _subscription);
            _subscription = 0;
        }
    }

    private static unsafe partial class Native
    {
        private const string Gio = "libgio-2.0.so.0";
        private const string Glib = "libglib-2.0.so.0";
        [LibraryImport(Gio)] internal static partial nint g_bus_get_sync(int busType, nint cancellable, nint* error);
        [LibraryImport(Gio)] internal static partial nint g_dbus_connection_get_unique_name(nint connection);
        [LibraryImport(Gio, StringMarshalling = StringMarshalling.Utf8)] internal static partial void g_dbus_connection_call(nint connection, string busName, string objectPath, string interfaceName, string methodName, nint parameters, nint replyType, int flags, int timeoutMilliseconds, nint cancellable, delegate* unmanaged[Cdecl]<nint, nint, nint, void> callback, nint userData);
        [LibraryImport(Gio)] internal static partial nint g_dbus_connection_call_finish(nint connection, nint result, nint* error);
        [LibraryImport(Gio, StringMarshalling = StringMarshalling.Utf8)] internal static partial uint g_dbus_connection_signal_subscribe(nint connection, string sender, string interfaceName, string member, string objectPath, string? arg0, int flags, delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, void> callback, nint userData, delegate* unmanaged[Cdecl]<nint, void> userDataFree);
        [LibraryImport(Gio)] internal static partial void g_dbus_connection_signal_unsubscribe(nint connection, uint subscriptionId);
        [LibraryImport(Gio)] internal static partial nint g_dbus_error_get_remote_error(nint error);
        [LibraryImport(Glib, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint g_variant_type_new(string typeString);
        [LibraryImport(Glib)] internal static partial void g_variant_type_free(nint type);
        [LibraryImport(Glib, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint g_variant_parse(nint type, string text, nint limit, nint endPointer, nint* error);
        [LibraryImport(Glib)] internal static partial void g_variant_unref(nint value);
        [LibraryImport(Glib)] internal static partial nint g_variant_get_child_value(nint value, nuint index);
        [LibraryImport(Glib)] internal static partial nuint g_variant_n_children(nint value);
        [LibraryImport(Glib)] internal static partial uint g_variant_get_uint32(nint value);
        [LibraryImport(Glib)] internal static partial nint g_variant_get_string(nint value, nuint* length);
        [LibraryImport(Glib)] internal static partial nint g_variant_get_variant(nint value);
        [LibraryImport(Glib)] internal static partial nint g_variant_get_type_string(nint value);
        [LibraryImport(Glib, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint g_variant_lookup_value(nint dictionary, string key, nint expectedType);
        [LibraryImport(Glib)] internal static partial void g_error_free(nint error);
        [LibraryImport(Glib)] internal static partial void g_free(nint value);
    }
}
