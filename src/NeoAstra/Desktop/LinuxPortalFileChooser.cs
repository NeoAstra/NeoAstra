// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
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

    /// <summary>The well-known name of the desktop portal on the session bus.</summary>
    internal const string PortalName = "org.freedesktop.portal.Desktop";

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

    /// <summary>Tells whether a <c>NameOwnerChanged</c> signal of the bus says that the portal left it: its name has no owner any more.</summary>
    internal static bool PortalVanished(string? name, string? newOwner) => string.Equals(name, PortalName, StringComparison.Ordinal) && string.IsNullOrEmpty(newOwner);

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


/// <summary>How a call to the portal ended.</summary>
internal enum LinuxPortalReplyKind
{
    /// <summary>The portal answered.</summary>
    Ok,
    /// <summary>No portal, or no FileChooser of the portal, answers on the bus.</summary>
    Missing,
    /// <summary>The portal is there and the call failed.</summary>
    Failed,
    /// <summary>The call was canceled before it ended.</summary>
    Canceled,
}

/// <summary>The end of a call to the portal and, when it answered, its value.</summary>
internal readonly record struct LinuxPortalReply<T>(LinuxPortalReplyKind Kind, T? Value = default);

/// <summary>A call to the portal that has not ended; it can be canceled from any thread.</summary>
internal interface ILinuxPortalOperation
{
    /// <summary>Cancels the call; its completion then reports <see cref="LinuxPortalReplyKind.Canceled"/> unless it had ended.</summary>
    void Cancel();
}

/// <summary>
/// The session bus as the FileChooser needs it. Connect, the calls and the subscriptions run on the UI thread, whose main
/// loop delivers their callbacks; <see cref="ILinuxPortalOperation.Cancel"/>, <see cref="IDisposable.Dispose"/> of a
/// subscription, <see cref="CloseRequest"/> and <see cref="Release"/> may run on any thread.
/// </summary>
internal interface ILinuxPortalBus
{
    /// <summary>Connects to the session bus and returns the unique name of the connection, or null without a bus.</summary>
    string? Connect();
    /// <summary>Reads the version of the FileChooser interface.</summary>
    ILinuxPortalOperation GetVersion(Action<LinuxPortalReply<uint>> completed);
    /// <summary>Calls <c>OpenFile</c> or <c>SaveFile</c> with the GVariant text of its parameters; the reply is the path of the request.</summary>
    ILinuxPortalOperation Open(string method, string parameters, Action<LinuxPortalReply<string>> completed);
    /// <summary>Listens to the <c>Response</c> signal of the request at a path.</summary>
    IDisposable Subscribe(string path, Action<uint, IReadOnlyList<string>?> responded);
    /// <summary>Listens to the portal leaving the bus.</summary>
    IDisposable WatchPortal(Action vanished);
    /// <summary>Closes the request at a path, which closes its dialog; the answer is not awaited.</summary>
    void CloseRequest(string path);
    /// <summary>Releases the connection.</summary>
    void Release();
}

/// <summary>The UI thread of the application, where the calls to the portal start.</summary>
internal interface ILinuxPortalThread
{
    /// <summary>Runs an action on the UI thread.</summary>
    /// <exception cref="OperationCanceledException">The token was canceled before the action ran.</exception>
    ValueTask InvokeAsync(Action action, CancellationToken cancellationToken);
}

internal sealed class LinuxPortalDispatcherThread(NeoDispatcher dispatcher) : ILinuxPortalThread
{
    public ValueTask InvokeAsync(Action action, CancellationToken cancellationToken) => dispatcher.InvokeAsync(action, cancellationToken);
}

/// <summary>
/// Shows the file and folder dialogs of the desktop through its portal (<c>xdg-desktop-portal</c>): the dialogs of KDE, GNOME
/// and the other desktops, also from a sandbox. Every pending call and dialog ends: with its answer, with the cancellation of
/// its caller (which cancels the call or closes the dialog), when the portal leaves the bus, or when the application stops.
/// </summary>
internal sealed class LinuxPortalFileChooser : IAsyncDisposable
{
    private readonly ILinuxPortalBus _bus;
    private readonly object _sync = new();
    private readonly HashSet<Request> _requests = [];
    private readonly HashSet<VersionQuery> _queries = [];
    private string? _uniqueName;
    private IDisposable? _ownerWatch;
    private uint? _version;
    private bool _connected;
    private bool _missing;
    private bool _stopped;
    private bool _released;

    internal LinuxPortalFileChooser() : this(new LinuxGioPortalBus()) { }

    internal LinuxPortalFileChooser(ILinuxPortalBus bus) => _bus = bus ?? throw new ArgumentNullException(nameof(bus));

    /// <summary>Gets the number of version queries and dialogs that have not ended.</summary>
    internal int PendingCount { get { lock (_sync) return _requests.Count + _queries.Count; } }

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
    /// After the application stopped, the answer is <see cref="NeoDesktopStatus.Canceled"/>.
    /// </summary>
    /// <exception cref="OperationCanceledException">The caller canceled; the call is canceled or the dialog closed.</exception>
    internal async ValueTask<NeoDesktopResult<IReadOnlyList<string>>?> PickAsync(ILinuxPortalThread thread, NeoFileDialogOptions options, bool folders, bool save, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(options);
        lock (_sync)
        {
            if (_stopped) return Stopped();
            if (_missing) return null;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var version = await QueryVersionAsync(thread, cancellationToken).ConfigureAwait(false);
        lock (_sync) if (_stopped) return Stopped();
        if (version is null || folders && version < LinuxPortalFileChooserProtocol.DirectoryVersion) return null;

        var token = LinuxPortalFileChooserProtocol.NewToken();
        var parameters = save ? LinuxPortalFileChooserProtocol.SaveFileParameters(options, token) : LinuxPortalFileChooserProtocol.OpenFileParameters(options, folders, token);
        var request = new Request(this, token, save ? "SaveFile" : "OpenFile", parameters);
        lock (_sync)
        {
            if (_stopped) return Stopped();
            _requests.Add(request);
        }
        try { await thread.InvokeAsync(request.Start, cancellationToken).ConfigureAwait(false); }
        catch { request.Finish(new(AnswerKind.Closed)); throw; }
        Answer answer;
        using (cancellationToken.Register(static state => ((Request)state!).Close(AnswerKind.Closed), request))
            answer = await request.Completion.Task.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        switch (answer.Kind)
        {
            case AnswerKind.Responded: return LinuxPortalFileChooserProtocol.Paths(answer.Response, answer.Uris);
            case AnswerKind.Missing: lock (_sync) _missing = true; return null;
            case AnswerKind.Vanished: return NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Failed, "portal_vanished");
            case AnswerKind.Closed or AnswerKind.Stopped: return Stopped();
            default: return null;
        }
    }

    /// <summary>
    /// The application stops: every dialog of the portal is closed, every call canceled, every listener removed, and every
    /// caller answered <see cref="NeoDesktopStatus.Canceled"/>. Later dialogs are not shown. Runs on any thread.
    /// </summary>
    internal void Stop()
    {
        Request[] requests;
        VersionQuery[] queries;
        IDisposable? watch;
        lock (_sync)
        {
            if (_stopped) return;
            _stopped = true;
            requests = [.. _requests];
            queries = [.. _queries];
            watch = _ownerWatch;
            _ownerWatch = null;
        }
        foreach (var request in requests) request.Close(AnswerKind.Stopped);
        foreach (var query in queries) query.Cancel(stopping: true);
        try { watch?.Dispose(); } catch { }
    }

    /// <summary>Stops, then releases the connection to the bus.</summary>
    public ValueTask DisposeAsync()
    {
        Stop();
        bool release;
        lock (_sync) { release = _connected && !_released; _released = true; }
        if (release) try { _bus.Release(); } catch { }
        return ValueTask.CompletedTask;
    }

    private static NeoDesktopResult<IReadOnlyList<string>> Stopped() => NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Canceled);

    // The version of the FileChooser of the portal, read once; null when no portal answers.
    private async ValueTask<uint?> QueryVersionAsync(ILinuxPortalThread thread, CancellationToken cancellationToken)
    {
        var query = new VersionQuery(this);
        lock (_sync)
        {
            if (_version is { } known) return known;
            if (_stopped) return null;
            _queries.Add(query);
        }
        try { await thread.InvokeAsync(query.Start, cancellationToken).ConfigureAwait(false); }
        catch { query.Cancel(stopping: false); throw; }
        using (cancellationToken.Register(static state => ((VersionQuery)state!).Cancel(stopping: false), query))
            return await query.Completion.Task.ConfigureAwait(false);
    }

    // On the UI thread: the connection and the watch of the portal, made once.
    private bool EnsureConnected()
    {
        lock (_sync) if (_connected) return true;
        var unique = _bus.Connect();
        if (string.IsNullOrEmpty(unique)) return false;
        var watch = _bus.WatchPortal(PortalVanished);
        bool stopped;
        lock (_sync)
        {
            _uniqueName = unique;
            _connected = true;
            stopped = _stopped || _released;
            if (!stopped) _ownerWatch = watch;
        }
        if (stopped) watch.Dispose();
        return true;
    }

    // The portal left the bus: its dialogs answer nothing any more, and a portal that comes back is asked its version again.
    private void PortalVanished()
    {
        Request[] requests;
        lock (_sync) { _version = null; requests = [.. _requests]; }
        foreach (var request in requests) request.Finish(new(AnswerKind.Vanished));
    }

    private enum AnswerKind { Responded, Closed, Stopped, Vanished, Missing, Failed }

    private readonly record struct Answer(AnswerKind Kind, uint Response = 0, IReadOnlyList<string>? Uris = null);

    private sealed class VersionQuery(LinuxPortalFileChooser owner)
    {
        private readonly object _gate = new();
        private ILinuxPortalOperation? _operation;
        private bool _done;

        internal TaskCompletionSource<uint?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // On the UI thread.
        internal void Start()
        {
            lock (_gate) if (_done) return;
            ILinuxPortalOperation operation;
            try
            {
                if (!owner.EnsureConnected()) { lock (owner._sync) owner._missing = true; Finish(null); return; }
                operation = owner._bus.GetVersion(Completed);
            }
            catch { Finish(null); return; }
            bool cancel;
            lock (_gate) { cancel = _done; if (!cancel) _operation = operation; }
            if (cancel) operation.Cancel();
        }

        // Any thread: the caller canceled, or the application stops.
        internal void Cancel(bool stopping)
        {
            ILinuxPortalOperation? operation;
            lock (_gate)
            {
                if (_done) return;
                _done = true;
                operation = _operation;
                _operation = null;
            }
            lock (owner._sync) owner._queries.Remove(this);
            try { operation?.Cancel(); } catch { }
            if (stopping) Completion.TrySetResult(null);
            else Completion.TrySetCanceled();
        }

        private void Completed(LinuxPortalReply<uint> reply)
        {
            lock (owner._sync)
            {
                if (reply.Kind == LinuxPortalReplyKind.Ok) owner._version = reply.Value;
                else if (reply.Kind == LinuxPortalReplyKind.Missing) owner._missing = true;
            }
            Finish(reply.Kind == LinuxPortalReplyKind.Ok ? reply.Value : null);
        }

        private void Finish(uint? version)
        {
            lock (_gate)
            {
                if (_done) return;
                _done = true;
                _operation = null;
            }
            lock (owner._sync) owner._queries.Remove(this);
            Completion.TrySetResult(version);
        }
    }

    // One dialog. Start and the callbacks of the bus run on the UI thread; Close and Finish run on any thread.
    private sealed class Request(LinuxPortalFileChooser owner, string token, string method, string parameters)
    {
        private readonly object _gate = new();
        private IDisposable? _subscription;
        private string? _path;
        private bool _done;
        private bool _closeWhenStarted;

        internal TaskCompletionSource<Answer> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Listens for the response before it calls, at the path that the portal gives a request with this token.
        internal void Start()
        {
            lock (_gate) if (_done) return;
            try
            {
                string? unique;
                lock (owner._sync) unique = owner._uniqueName;
                if (string.IsNullOrEmpty(unique)) { Finish(new(AnswerKind.Failed)); return; }
                if (!Subscribe(LinuxPortalFileChooserProtocol.RequestPath(unique, token))) return;
                owner._bus.Open(method, parameters, Started);
            }
            catch { Finish(new(AnswerKind.Failed)); }
        }

        // The caller canceled (Closed) or the application stops (Stopped): the dialog of the portal is closed.
        internal void Close(AnswerKind kind)
        {
            string? path;
            lock (_gate)
            {
                if (_done) return;
                _closeWhenStarted = true;
                path = _path;
            }
            if (path is not null) try { owner._bus.CloseRequest(path); } catch { }
            Finish(new(kind));
        }

        internal void Finish(Answer answer)
        {
            IDisposable? subscription;
            lock (_gate)
            {
                if (_done) return;
                _done = true;
                subscription = _subscription;
                _subscription = null;
            }
            lock (owner._sync) owner._requests.Remove(this);
            try { subscription?.Dispose(); } catch { }
            Completion.TrySetResult(answer);
        }

        // A portal older than the handle_token option names the request otherwise: it is followed at the path it gives.
        private void Started(LinuxPortalReply<string> reply)
        {
            bool done, closeWhenStarted;
            lock (_gate) { done = _done; closeWhenStarted = _closeWhenStarted; }
            if (done)
            {
                if (closeWhenStarted && reply.Kind == LinuxPortalReplyKind.Ok && !string.IsNullOrEmpty(reply.Value)) try { owner._bus.CloseRequest(reply.Value); } catch { }
                return;
            }
            if (reply.Kind != LinuxPortalReplyKind.Ok || string.IsNullOrEmpty(reply.Value))
            {
                Finish(new(reply.Kind == LinuxPortalReplyKind.Missing ? AnswerKind.Missing : AnswerKind.Failed));
                return;
            }
            string? current;
            lock (_gate) current = _path;
            if (!string.Equals(reply.Value, current, StringComparison.Ordinal))
            {
                try { Subscribe(reply.Value); } catch { Finish(new(AnswerKind.Failed)); }
            }
        }

        private void Responded(uint response, IReadOnlyList<string>? uris) => Finish(new(AnswerKind.Responded, response, uris));

        // False when the request ended meanwhile, whose new subscription is then removed at once.
        private bool Subscribe(string path)
        {
            var subscription = owner._bus.Subscribe(path, Responded);
            IDisposable? previous = null;
            bool done;
            lock (_gate)
            {
                done = _done;
                if (!done) { previous = _subscription; _subscription = subscription; _path = path; }
            }
            try { previous?.Dispose(); } catch { }
            if (done) { try { subscription.Dispose(); } catch { } return false; }
            return true;
        }
    }
}

/// <summary>
/// The state that native callbacks reach by an id rather than by a <see cref="GCHandle"/>: an entry removed when its
/// owner ends leaves a late callback with nothing to reach, instead of a freed handle.
/// </summary>
internal static class LinuxPortalCallbacks
{
    private static readonly ConcurrentDictionary<nint, object> Entries = new();
    private static long _next;

    /// <summary>Registers a value and returns its id, never 0.</summary>
    internal static nint Register(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var id = (nint)Interlocked.Increment(ref _next);
        Entries[id] = value;
        return id;
    }

    /// <summary>Finds the value of an id that is still registered.</summary>
    internal static bool TryGet<T>(nint id, [NotNullWhen(true)] out T? value) where T : class
    {
        value = Entries.TryGetValue(id, out var entry) ? entry as T : null;
        return value is not null;
    }

    /// <summary>Removes an id; false when it was not registered.</summary>
    internal static bool Remove(nint id) => Entries.TryRemove(id, out _);
}

/// <summary>The session bus through GIO, for <see cref="LinuxPortalFileChooser"/>.</summary>
internal sealed unsafe partial class LinuxGioPortalBus : ILinuxPortalBus
{
    private const string PortalPath = "/org/freedesktop/portal/desktop";
    private const string FileChooserInterface = "org.freedesktop.portal.FileChooser";
    private const string RequestInterface = "org.freedesktop.portal.Request";
    private const int SessionBus = 2;
    private const int IoErrorCancelled = 19;

    // The errors that say that no portal, or no FileChooser of the portal, answers on this bus.
    private static readonly HashSet<string> MissingPortalErrors = new(StringComparer.Ordinal)
    {
        "org.freedesktop.DBus.Error.ServiceUnknown", "org.freedesktop.DBus.Error.NameHasNoOwner", "org.freedesktop.DBus.Error.UnknownMethod",
        "org.freedesktop.DBus.Error.UnknownInterface", "org.freedesktop.DBus.Error.UnknownObject", "org.freedesktop.DBus.Error.UnknownProperty",
        "org.freedesktop.DBus.Error.Spawn.ServiceNotFound",
    };

    private readonly object _sync = new();
    private nint _connection;

    public string? Connect()
    {
        lock (_sync)
        {
            if (_connection == 0)
            {
                nint error = 0;
                _connection = Native.g_bus_get_sync(SessionBus, 0, &error);
                FreeError(error);
            }
            return _connection == 0 ? null : Marshal.PtrToStringUTF8(Native.g_dbus_connection_get_unique_name(_connection));
        }
    }

    public ILinuxPortalOperation GetVersion(Action<LinuxPortalReply<uint>> completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        var parameters = Parse("(ss)", "(" + LinuxPortalFileChooserProtocol.Quote(FileChooserInterface) + ", 'version')");
        try { return Call.Start(Connection(), PortalPath, "org.freedesktop.DBus.Properties", "Get", parameters, "(v)", (kind, reply) => completed(kind == LinuxPortalReplyKind.Ok ? Version(reply) : new(kind))); }
        finally { Native.g_variant_unref(parameters); }
    }

    public ILinuxPortalOperation Open(string method, string parameters, Action<LinuxPortalReply<string>> completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        var value = Parse(LinuxPortalFileChooserProtocol.ParametersType, parameters);
        try { return Call.Start(Connection(), PortalPath, FileChooserInterface, method, value, "(o)", (kind, reply) => completed(kind == LinuxPortalReplyKind.Ok ? RequestPath(reply) : new(kind))); }
        finally { Native.g_variant_unref(value); }
    }

    public IDisposable Subscribe(string path, Action<uint, IReadOnlyList<string>?> responded)
    {
        ArgumentNullException.ThrowIfNull(responded);
        return Subscription.Start(Connection(), LinuxPortalFileChooserProtocol.PortalName, RequestInterface, "Response", path, null, parameters =>
        {
            if (Response(parameters) is { } response) responded(response.Code, response.Uris);
            else responded(2, null);
        });
    }

    public IDisposable WatchPortal(Action vanished)
    {
        ArgumentNullException.ThrowIfNull(vanished);
        return Subscription.Start(Connection(), "org.freedesktop.DBus", "org.freedesktop.DBus", "NameOwnerChanged", "/org/freedesktop/DBus", LinuxPortalFileChooserProtocol.PortalName, parameters =>
        {
            if (TypeString(parameters) != "(sss)") return;
            if (LinuxPortalFileChooserProtocol.PortalVanished(ChildString(parameters, 0), ChildString(parameters, 2))) vanished();
        });
    }

    public void CloseRequest(string path)
    {
        var connection = Connection();
        Native.g_dbus_connection_call(connection, LinuxPortalFileChooserProtocol.PortalName, path, RequestInterface, "Close", 0, 0, 0, -1, 0, null, 0);
    }

    public void Release()
    {
        nint connection;
        lock (_sync) { connection = _connection; _connection = 0; }
        if (connection != 0) Native.g_object_unref(connection);
    }

    private nint Connection()
    {
        lock (_sync) return _connection != 0 ? _connection : throw new InvalidOperationException("The session bus is not connected.");
    }

    private static LinuxPortalReply<uint> Version(nint reply)
    {
        var boxed = Native.g_variant_get_child_value(reply, 0);
        try
        {
            var inner = Native.g_variant_get_variant(boxed);
            try { return TypeString(inner) == "u" ? new(LinuxPortalReplyKind.Ok, Native.g_variant_get_uint32(inner)) : new(LinuxPortalReplyKind.Failed); }
            finally { Native.g_variant_unref(inner); }
        }
        finally { Native.g_variant_unref(boxed); }
    }

    private static LinuxPortalReply<string> RequestPath(nint reply)
    {
        var path = ChildString(reply, 0);
        return string.IsNullOrEmpty(path) ? new(LinuxPortalReplyKind.Failed) : new(LinuxPortalReplyKind.Ok, path);
    }

    private static (uint Code, List<string>? Uris)? Response(nint parameters)
    {
        if (parameters == 0 || TypeString(parameters) != LinuxPortalFileChooserProtocol.ResponseType) return null;
        uint code;
        var child = Native.g_variant_get_child_value(parameters, 0);
        try { code = Native.g_variant_get_uint32(child); }
        finally { Native.g_variant_unref(child); }
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
                    for (nuint index = 0; index < count && index <= (nuint)LinuxPortalFileChooserProtocol.MaximumSelections; index++) uris.Add(ChildString(list, index) ?? string.Empty);
                }
                finally { Native.g_variant_unref(list); }
            }
        }
        finally { Native.g_variant_unref(results); }
        return (code, uris);
    }

    private static string? ChildString(nint value, nuint index)
    {
        var child = Native.g_variant_get_child_value(value, index);
        try { return Marshal.PtrToStringUTF8(Native.g_variant_get_string(child, null)); }
        finally { Native.g_variant_unref(child); }
    }

    private static string? TypeString(nint value) => Marshal.PtrToStringUTF8(Native.g_variant_get_type_string(value));

    private static nint Parse(string signature, string text)
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

    // How a failed call ended: canceled, no portal, or a portal that failed. Frees the error.
    private static LinuxPortalReplyKind Failure(nint error)
    {
        if (error == 0) return LinuxPortalReplyKind.Failed;
        try
        {
            if (Native.g_error_matches(error, Native.g_io_error_quark(), IoErrorCancelled)) return LinuxPortalReplyKind.Canceled;
            var name = Native.g_dbus_error_get_remote_error(error);
            try { return name != 0 && MissingPortalErrors.Contains(Marshal.PtrToStringUTF8(name) ?? string.Empty) ? LinuxPortalReplyKind.Missing : LinuxPortalReplyKind.Failed; }
            finally { if (name != 0) Native.g_free(name); }
        }
        finally { Native.g_error_free(error); }
    }

    private static void FreeError(nint error) { if (error != 0) Native.g_error_free(error); }

    // One call with its own cancellable, which it holds until GIO reports its end: a cancel can never reach a freed one.
    private sealed class Call : ILinuxPortalOperation
    {
        private readonly object _gate = new();
        private readonly Action<LinuxPortalReplyKind, nint> _completed;
        private nint _cancellable;

        private Call(Action<LinuxPortalReplyKind, nint> completed) => _completed = completed;

        internal static Call Start(nint connection, string path, string interfaceName, string method, nint parameters, string replyType, Action<LinuxPortalReplyKind, nint> completed)
        {
            var call = new Call(completed) { _cancellable = Native.g_cancellable_new() };
            var id = LinuxPortalCallbacks.Register(call);
            var type = Native.g_variant_type_new(replyType);
            try { Native.g_dbus_connection_call(connection, LinuxPortalFileChooserProtocol.PortalName, path, interfaceName, method, parameters, type, 0, -1, call._cancellable, &Finished, id); }
            catch { LinuxPortalCallbacks.Remove(id); call.ReleaseCancellable(); throw; }
            finally { Native.g_variant_type_free(type); }
            return call;
        }

        public void Cancel()
        {
            lock (_gate) if (_cancellable != 0) Native.g_cancellable_cancel(_cancellable);
        }

        private void ReleaseCancellable()
        {
            nint cancellable;
            lock (_gate) { cancellable = _cancellable; _cancellable = 0; }
            if (cancellable != 0) Native.g_object_unref(cancellable);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Finished(nint source, nint result, nint data)
        {
            nint error = 0;
            nint reply = 0;
            try { reply = Native.g_dbus_connection_call_finish(source, result, &error); }
            catch { }
            LinuxPortalCallbacks.TryGet<Call>(data, out var call);
            LinuxPortalCallbacks.Remove(data);
            try
            {
                call?.ReleaseCancellable();
                var kind = reply != 0 ? LinuxPortalReplyKind.Ok : Failure(error);
                error = 0;
                call?._completed(kind, reply);
            }
            catch { }
            finally
            {
                FreeError(error);
                if (reply != 0) Native.g_variant_unref(reply);
            }
        }
    }

    // One signal subscription; disposing it unsubscribes once, from any thread, and a signal that GIO still delivers after
    // that finds no entry.
    private sealed class Subscription : IDisposable
    {
        private readonly object _gate = new();
        private readonly Action<nint> _received;
        private nint _connection;
        private nint _id;
        private uint _subscription;

        private Subscription(Action<nint> received) => _received = received;

        internal static Subscription Start(nint connection, string sender, string interfaceName, string member, string path, string? arg0, Action<nint> received)
        {
            var subscription = new Subscription(received) { _connection = connection };
            subscription._id = LinuxPortalCallbacks.Register(subscription);
            try { subscription._subscription = Native.g_dbus_connection_signal_subscribe(connection, sender, interfaceName, member, path, arg0, 0, &Received, subscription._id, null); }
            catch { LinuxPortalCallbacks.Remove(subscription._id); throw; }
            if (subscription._subscription == 0) { LinuxPortalCallbacks.Remove(subscription._id); throw new InvalidOperationException("Could not listen to the desktop portal."); }
            Native.g_object_ref(connection);
            return subscription;
        }

        public void Dispose()
        {
            uint subscription;
            nint connection;
            lock (_gate)
            {
                subscription = _subscription;
                connection = _connection;
                _subscription = 0;
                _connection = 0;
            }
            if (subscription == 0) return;
            LinuxPortalCallbacks.Remove(_id);
            Native.g_dbus_connection_signal_unsubscribe(connection, subscription);
            Native.g_object_unref(connection);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Received(nint connection, nint sender, nint objectPath, nint interfaceName, nint signalName, nint parameters, nint data)
        {
            try { if (LinuxPortalCallbacks.TryGet<Subscription>(data, out var subscription)) subscription._received(parameters); }
            catch { }
        }
    }

    private static partial class Native
    {
        private const string Gio = "libgio-2.0.so.0";
        private const string Glib = "libglib-2.0.so.0";
        private const string GObject = "libgobject-2.0.so.0";
        [LibraryImport(Gio)] internal static partial nint g_bus_get_sync(int busType, nint cancellable, nint* error);
        [LibraryImport(Gio)] internal static partial nint g_dbus_connection_get_unique_name(nint connection);
        [LibraryImport(Gio, StringMarshalling = StringMarshalling.Utf8)] internal static partial void g_dbus_connection_call(nint connection, string busName, string objectPath, string interfaceName, string methodName, nint parameters, nint replyType, int flags, int timeoutMilliseconds, nint cancellable, delegate* unmanaged[Cdecl]<nint, nint, nint, void> callback, nint userData);
        [LibraryImport(Gio)] internal static partial nint g_dbus_connection_call_finish(nint connection, nint result, nint* error);
        [LibraryImport(Gio, StringMarshalling = StringMarshalling.Utf8)] internal static partial uint g_dbus_connection_signal_subscribe(nint connection, string sender, string interfaceName, string member, string objectPath, string? arg0, int flags, delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, void> callback, nint userData, delegate* unmanaged[Cdecl]<nint, void> userDataFree);
        [LibraryImport(Gio)] internal static partial void g_dbus_connection_signal_unsubscribe(nint connection, uint subscriptionId);
        [LibraryImport(Gio)] internal static partial nint g_dbus_error_get_remote_error(nint error);
        [LibraryImport(Gio)] internal static partial nint g_cancellable_new();
        [LibraryImport(Gio)] internal static partial void g_cancellable_cancel(nint cancellable);
        [LibraryImport(Gio)] internal static partial uint g_io_error_quark();
        [LibraryImport(Glib)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool g_error_matches(nint error, uint domain, int code);
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
        [LibraryImport(GObject)] internal static partial nint g_object_ref(nint value);
        [LibraryImport(GObject)] internal static partial void g_object_unref(nint value);
    }
}
