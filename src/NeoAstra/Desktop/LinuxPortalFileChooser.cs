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

    /// <summary>
    /// Tells whether a <c>NameOwnerChanged</c> signal of the bus says that the portal that owned the name is gone: the name
    /// has no owner any more, or it went straight to another owner. Its requests and calls belong to the owner that is gone.
    /// The first owner of the name, a portal that the bus starts, is not a loss.
    /// </summary>
    internal static bool PortalOwnerLost(string? name, string? oldOwner, string? newOwner)
        => string.Equals(name, PortalName, StringComparison.Ordinal) && !string.IsNullOrEmpty(oldOwner) && !string.Equals(oldOwner, newOwner, StringComparison.Ordinal);

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
/// subscription, <see cref="CloseRequest"/>, <see cref="Abandon"/> and <see cref="Release"/> may run on any thread.
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
    /// <summary>Listens to the loss of the owner of the portal's name: the portal left the bus, or another one replaced it.</summary>
    IDisposable WatchPortal(Action lost);
    /// <summary>Closes the request at a path, which closes its dialog; the answer is not awaited.</summary>
    void CloseRequest(string path);
    /// <summary>
    /// Cancels the calls that have not ended and waits, without blocking a thread, for the main loop to report their end;
    /// those still running when the token is canceled are abandoned.
    /// </summary>
    ValueTask DrainAsync(CancellationToken cancellationToken);
    /// <summary>Cancels and forgets at once every call that has not ended, for a main loop that may not run any more.</summary>
    void Abandon();
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
/// A native call or signal subscription that a <see cref="LinuxPortalCallTracker"/> follows until the main loop reports its
/// end: the end of a call, or the release (<c>GDestroyNotify</c>) of a subscription after its queued signals.
/// </summary>
internal interface ILinuxPortalTrackedCall
{
    /// <summary>Gets a task completed when the call or subscription ended or was abandoned.</summary>
    Task Ended { get; }
    /// <summary>Cancels the call, or removes the subscription; the main loop then reports its end.</summary>
    void Cancel();
    /// <summary>Cancels and releases what it holds at once; a later report of its end finds nothing.</summary>
    void Abandon();
}

/// <summary>
/// The native calls and subscriptions that have not ended, so that none outlives the stop of the application. Its stop (a
/// drain or an abandonment) is final: nothing is added after it.
/// </summary>
internal sealed class LinuxPortalCallTracker
{
    private readonly object _sync = new();
    private readonly HashSet<ILinuxPortalTrackedCall> _calls = [];
    private bool _stopped;

    /// <summary>Gets the number of calls and subscriptions that have not ended.</summary>
    internal int Count { get { lock (_sync) return _calls.Count; } }

    /// <summary>Follows a call or a subscription that is about to start; false after the stop, when it must not start.</summary>
    internal bool TryAdd(ILinuxPortalTrackedCall call)
    {
        lock (_sync)
        {
            if (_stopped) return false;
            _calls.Add(call);
            return true;
        }
    }

    internal void Remove(ILinuxPortalTrackedCall call) { lock (_sync) _calls.Remove(call); }

    /// <summary>
    /// Stops, cancels every call and subscription, and waits without blocking until the main loop reported their end; those
    /// not ended when the token is canceled are abandoned.
    /// </summary>
    internal async ValueTask DrainAsync(CancellationToken cancellationToken)
    {
        ILinuxPortalTrackedCall[] calls;
        lock (_sync) { _stopped = true; calls = [.. _calls]; }
        foreach (var call in calls) try { call.Cancel(); } catch { }
        if (calls.Length != 0)
        {
            try { await Task.WhenAll(calls.Select(static call => call.Ended)).WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        Abandon();
    }

    /// <summary>Stops and abandons every call and subscription that has not ended.</summary>
    internal void Abandon()
    {
        ILinuxPortalTrackedCall[] calls;
        lock (_sync) { _stopped = true; calls = [.. _calls]; _calls.Clear(); }
        foreach (var call in calls) try { call.Abandon(); } catch { }
    }
}

/// <summary>
/// Shows the file and folder dialogs of the desktop through its portal (<c>xdg-desktop-portal</c>): the dialogs of KDE, GNOME
/// and the other desktops, also from a sandbox. Every pending call and dialog ends: with its answer, with the cancellation of
/// its caller (which cancels the call or closes the dialog), when the owner of the portal is lost, or when the application
/// stops, whose main loop then reports the end of the native calls before it stops.
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
    private bool _connectAttempted;
    private bool _connecting;
    private bool _missing;
    private bool _stopped;
    private bool _disposed;

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
    /// After the application stopped, the answer is <see cref="NeoDesktopStatus.Canceled"/>; a dialog whose portal is lost
    /// is <see cref="NeoDesktopStatus.Failed"/>.
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
        var (version, end) = await QueryVersionAsync(thread, cancellationToken).ConfigureAwait(false);
        if (end == QueryEnd.Lost) return Lost();
        lock (_sync) if (_stopped || end == QueryEnd.Stopped) return Stopped();
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
        catch { request.Close(AnswerKind.Closed); throw; }
        Answer answer;
        using (cancellationToken.Register(static state => ((Request)state!).Close(AnswerKind.Closed), request))
            answer = await request.Completion.Task.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        switch (answer.Kind)
        {
            case AnswerKind.Responded: return LinuxPortalFileChooserProtocol.Paths(answer.Response, answer.Uris);
            case AnswerKind.Missing: lock (_sync) _missing = true; return null;
            case AnswerKind.Lost: return Lost();
            case AnswerKind.Closed or AnswerKind.Stopped: return Stopped();
            default: return null;
        }
    }

    /// <summary>
    /// The application stops: every dialog of the portal is closed, every call canceled, every listener removed, and every
    /// caller answered <see cref="NeoDesktopStatus.Canceled"/>. Later dialogs are not shown. Runs on any thread and does not
    /// wait for the main loop: <see cref="StopAsync"/> also waits for the native calls to end.
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
        foreach (var query in queries) query.End(QueryEnd.Stopped);
        try { watch?.Dispose(); } catch { }
    }

    /// <summary>
    /// Stops, then waits without blocking, while the main loop still runs, until GIO reported the end of every native call;
    /// those still running when the token is canceled are abandoned.
    /// </summary>
    internal async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Stop();
        bool attempted;
        lock (_sync) attempted = _connectAttempted;
        if (attempted) await _bus.DrainAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops, abandons the native calls that have not ended (the main loop may not run any more, so nothing is awaited), and
    /// releases the connection to the bus; a connection that is still being made is released once it is made.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        Stop();
        bool attempted, release;
        lock (_sync)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            attempted = _connectAttempted;
            release = attempted && !_connecting;
        }
        if (attempted) try { _bus.Abandon(); } catch { }
        if (release) try { _bus.Release(); } catch { }
        return ValueTask.CompletedTask;
    }

    private static NeoDesktopResult<IReadOnlyList<string>> Stopped() => NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Canceled);

    private static NeoDesktopResult<IReadOnlyList<string>> Lost() => NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Failed, "portal_vanished");

    // The version of the FileChooser of the portal, read once; null when no portal answers.
    private async ValueTask<(uint? Version, QueryEnd End)> QueryVersionAsync(ILinuxPortalThread thread, CancellationToken cancellationToken)
    {
        var query = new VersionQuery(this);
        lock (_sync)
        {
            if (_version is { } known) return (known, QueryEnd.Answered);
            if (_stopped) return (null, QueryEnd.Stopped);
            _queries.Add(query);
        }
        try { await thread.InvokeAsync(query.Start, cancellationToken).ConfigureAwait(false); }
        catch { query.End(QueryEnd.Canceled); throw; }
        using (cancellationToken.Register(static state => ((VersionQuery)state!).End(QueryEnd.Canceled), query))
            return await query.Completion.Task.ConfigureAwait(false);
    }

    // On the UI thread: the connection and the watch of the portal, made once. A connection that the bus gives after the
    // chooser was disposed is released at once, and one given after it stopped is kept for the disposal to release.
    private ConnectState EnsureConnected()
    {
        lock (_sync)
        {
            if (_uniqueName is not null) return ConnectState.Connected;
            if (_stopped || _disposed) return ConnectState.Stopped;
            _connecting = true;
            _connectAttempted = true;
        }
        string? unique = null;
        bool disposed, stopped;
        try { unique = _bus.Connect(); }
        finally
        {
            lock (_sync)
            {
                _connecting = false;
                disposed = _disposed;
                stopped = _stopped;
                if (!stopped && !string.IsNullOrEmpty(unique)) _uniqueName = unique;
            }
            if (disposed) try { _bus.Release(); } catch { }
        }
        if (stopped) return ConnectState.Stopped;
        if (string.IsNullOrEmpty(unique)) return ConnectState.NoBus;
        var watch = _bus.WatchPortal(PortalLost);
        lock (_sync)
        {
            stopped = _stopped;
            if (!stopped) _ownerWatch = watch;
        }
        if (!stopped) return ConnectState.Connected;
        watch.Dispose();
        return ConnectState.Stopped;
    }

    // The owner of the portal is gone: its dialogs and calls answer nothing any more, and a new owner is asked its version.
    private void PortalLost()
    {
        Request[] requests;
        VersionQuery[] queries;
        lock (_sync) { _version = null; requests = [.. _requests]; queries = [.. _queries]; }
        foreach (var request in requests) request.Finish(new(AnswerKind.Lost));
        foreach (var query in queries) query.End(QueryEnd.Lost);
    }

    private enum ConnectState { Connected, NoBus, Stopped }

    private enum QueryEnd { Answered, Canceled, Stopped, Lost }

    private enum AnswerKind { Responded, Closed, Stopped, Lost, Missing, Failed }

    private readonly record struct Answer(AnswerKind Kind, uint Response = 0, IReadOnlyList<string>? Uris = null);

    private sealed class VersionQuery(LinuxPortalFileChooser owner)
    {
        private readonly object _gate = new();
        private ILinuxPortalOperation? _operation;
        private bool _done;

        internal TaskCompletionSource<(uint? Version, QueryEnd End)> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // On the UI thread.
        internal void Start()
        {
            lock (_gate) if (_done) return;
            ILinuxPortalOperation operation;
            try
            {
                switch (owner.EnsureConnected())
                {
                    case ConnectState.NoBus:
                        lock (owner._sync) owner._missing = true;
                        End(QueryEnd.Answered);
                        return;
                    case ConnectState.Stopped:
                        End(QueryEnd.Stopped);
                        return;
                }
                operation = owner._bus.GetVersion(Completed);
            }
            catch { End(QueryEnd.Answered); return; }
            bool cancel;
            lock (_gate) { cancel = _done; if (!cancel) _operation = operation; }
            if (cancel) operation.Cancel();
        }

        // Any thread: the caller canceled, the application stops, or the owner of the portal is lost.
        internal void End(QueryEnd end)
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
            if (end == QueryEnd.Canceled) Completion.TrySetCanceled();
            else Completion.TrySetResult((null, end));
        }

        // A reply that comes after the query ended (from a portal that was lost, for instance) is not kept.
        private void Completed(LinuxPortalReply<uint> reply)
        {
            lock (_gate)
            {
                if (_done) return;
                _done = true;
                _operation = null;
            }
            lock (owner._sync)
            {
                owner._queries.Remove(this);
                if (reply.Kind == LinuxPortalReplyKind.Ok) owner._version = reply.Value;
                else if (reply.Kind == LinuxPortalReplyKind.Missing) owner._missing = true;
            }
            Completion.TrySetResult((reply.Kind == LinuxPortalReplyKind.Ok ? reply.Value : null, QueryEnd.Answered));
        }
    }

    // One dialog. Start and the callbacks of the bus run on the UI thread; Close and Finish run on any thread. Its request lives
    // at the path that the portal spec predicts from the unique name and the token (handle_token, which every portal from
    // xdg-desktop-portal 0.9 on honours): a close always knows it, also before the portal answered the call. A portal that
    // returns another path is not followed: that request is closed and the dialog fails.
    private sealed class Request(LinuxPortalFileChooser owner, string token, string method, string parameters)
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _closed = new(StringComparer.Ordinal);
        private IDisposable? _subscription;
        private ILinuxPortalOperation? _open;
        private string? _path;
        private bool _done;

        internal TaskCompletionSource<Answer> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Listens for the response before it calls. The call is sent under the gate, so that a close either comes first and
        // nothing is opened, or comes after the call was sent, and its close of the path follows the call on the connection.
        internal void Start()
        {
            string? unique;
            lock (owner._sync) unique = owner._uniqueName;
            if (string.IsNullOrEmpty(unique)) { Finish(new(AnswerKind.Failed)); return; }
            try
            {
                if (!Subscribe(LinuxPortalFileChooserProtocol.RequestPath(unique, token))) return;
                lock (_gate)
                {
                    if (_done) return;
                    _open = owner._bus.Open(method, parameters, Started);
                }
            }
            catch { Finish(new(AnswerKind.Failed)); }
        }

        // The caller canceled (Closed) or the application stops (Stopped): the request is closed at its path, and a call that has
        // not returned is canceled when the application stops, since its answer could no longer be delivered.
        internal void Close(AnswerKind kind)
        {
            string? path = null;
            IDisposable? subscription;
            ILinuxPortalOperation? open;
            lock (_gate)
            {
                if (_done) return;
                _done = true;
                if (_path is not null && _closed.Add(_path)) path = _path;
                subscription = _subscription;
                _subscription = null;
                open = kind == AnswerKind.Stopped ? _open : null;
                _open = null;
            }
            Ended(subscription);
            if (path is not null) try { owner._bus.CloseRequest(path); } catch { }
            try { open?.Cancel(); } catch { }
            Completion.TrySetResult(new(kind));
        }

        // A response, a loss of the portal, or a failure: the dialog is over on the side of the portal.
        internal void Finish(Answer answer)
        {
            IDisposable? subscription;
            lock (_gate)
            {
                if (_done) return;
                _done = true;
                subscription = _subscription;
                _subscription = null;
                _open = null;
            }
            Ended(subscription);
            Completion.TrySetResult(answer);
        }

        private void Ended(IDisposable? subscription)
        {
            lock (owner._sync) owner._requests.Remove(this);
            try { subscription?.Dispose(); } catch { }
        }

        // The portal returned the path of the request: anything but the predicted one is closed, whether or not the dialog ended.
        private void Started(LinuxPortalReply<string> reply)
        {
            var path = reply.Kind == LinuxPortalReplyKind.Ok && !string.IsNullOrEmpty(reply.Value) ? reply.Value : null;
            string? closeNow = null;
            bool fail;
            lock (_gate)
            {
                _open = null;
                var predicted = string.Equals(path, _path, StringComparison.Ordinal);
                if (path is not null && !predicted && _closed.Add(path)) closeNow = path;
                fail = !_done && !predicted;
            }
            if (closeNow is not null) try { owner._bus.CloseRequest(closeNow); } catch { }
            if (fail) Finish(new(reply.Kind == LinuxPortalReplyKind.Missing ? AnswerKind.Missing : AnswerKind.Failed));
        }

        private void Responded(uint response, IReadOnlyList<string>? uris) => Finish(new(AnswerKind.Responded, response, uris));

        // False when the request ended meanwhile, whose subscription is then removed at once.
        private bool Subscribe(string path)
        {
            var subscription = owner._bus.Subscribe(path, Responded);
            bool done;
            lock (_gate)
            {
                done = _done;
                if (!done) { _subscription = subscription; _path = path; }
            }
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

/// <summary>
/// The session bus through GIO, for <see cref="LinuxPortalFileChooser"/>. Each native operation holds a reference of its own
/// on the connection, taken under the lock that <see cref="Release"/> takes, so that a release on another thread never frees
/// the connection under it; the calls are tracked until GIO reports their end, or abandoned.
/// </summary>
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
    private readonly LinuxPortalCallTracker _calls = new();
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
        try { return StartCall(PortalPath, "org.freedesktop.DBus.Properties", "Get", parameters, "(v)", (kind, reply) => completed(kind == LinuxPortalReplyKind.Ok ? Version(reply) : new(kind))); }
        finally { Native.g_variant_unref(parameters); }
    }

    public ILinuxPortalOperation Open(string method, string parameters, Action<LinuxPortalReply<string>> completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        var value = Parse(LinuxPortalFileChooserProtocol.ParametersType, parameters);
        try { return StartCall(PortalPath, FileChooserInterface, method, value, "(o)", (kind, reply) => completed(kind == LinuxPortalReplyKind.Ok ? RequestPath(reply) : new(kind))); }
        finally { Native.g_variant_unref(value); }
    }

    public IDisposable Subscribe(string path, Action<uint, IReadOnlyList<string>?> responded)
    {
        ArgumentNullException.ThrowIfNull(responded);
        return StartSubscription(LinuxPortalFileChooserProtocol.PortalName, RequestInterface, "Response", path, null, parameters =>
        {
            if (Response(parameters) is { } response) responded(response.Code, response.Uris);
            else responded(2, null);
        });
    }

    public IDisposable WatchPortal(Action lost)
    {
        ArgumentNullException.ThrowIfNull(lost);
        return StartSubscription("org.freedesktop.DBus", "org.freedesktop.DBus", "NameOwnerChanged", "/org/freedesktop/DBus", LinuxPortalFileChooserProtocol.PortalName, parameters =>
        {
            if (TypeString(parameters) != "(sss)") return;
            if (LinuxPortalFileChooserProtocol.PortalOwnerLost(ChildString(parameters, 0), ChildString(parameters, 1), ChildString(parameters, 2))) lost();
        });
    }

    public void CloseRequest(string path)
    {
        var connection = AcquireConnection();
        try { Native.g_dbus_connection_call(connection, LinuxPortalFileChooserProtocol.PortalName, path, RequestInterface, "Close", 0, 0, 0, -1, 0, null, 0); }
        finally { Native.g_object_unref(connection); }
    }

    public ValueTask DrainAsync(CancellationToken cancellationToken) => _calls.DrainAsync(cancellationToken);

    public void Abandon() => _calls.Abandon();

    public void Release()
    {
        nint connection;
        lock (_sync) { connection = _connection; _connection = 0; }
        if (connection != 0) Native.g_object_unref(connection);
    }

    // A reference of the caller's own on the connection; GIO keeps its own for a call or a subscription it starts.
    private nint AcquireConnection()
    {
        lock (_sync)
        {
            if (_connection == 0) throw new InvalidOperationException("The session bus is not connected.");
            Native.g_object_ref(_connection);
            return _connection;
        }
    }

    private Call StartCall(string path, string interfaceName, string method, nint parameters, string replyType, Action<LinuxPortalReplyKind, nint> completed)
    {
        var connection = AcquireConnection();
        try { return Call.Start(_calls, connection, path, interfaceName, method, parameters, replyType, completed); }
        finally { Native.g_object_unref(connection); }
    }

    private Subscription StartSubscription(string sender, string interfaceName, string member, string path, string? arg0, Action<nint> received)
    {
        var connection = AcquireConnection();
        try { return Subscription.Start(_calls, connection, sender, interfaceName, member, path, arg0, received); }
        finally { Native.g_object_unref(connection); }
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

    // One call with its own cancellable, which it holds until GIO reports its end or until it is abandoned. It starts under its
    // gate, which an abandonment takes too: the cancellable is never released while GIO has not taken it, and a call that the
    // stopped tracker refuses never starts.
    private sealed class Call : ILinuxPortalOperation, ILinuxPortalTrackedCall
    {
        private readonly object _gate = new();
        private readonly LinuxPortalCallTracker _tracker;
        private readonly Action<LinuxPortalReplyKind, nint> _completed;
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private nint _cancellable;
        private nint _id;

        private Call(LinuxPortalCallTracker tracker, Action<LinuxPortalReplyKind, nint> completed)
        {
            _tracker = tracker;
            _completed = completed;
        }

        public Task Ended => _ended.Task;

        /// <exception cref="OperationCanceledException">The application stopped: the call is not made.</exception>
        internal static Call Start(LinuxPortalCallTracker tracker, nint connection, string path, string interfaceName, string method, nint parameters, string replyType, Action<LinuxPortalReplyKind, nint> completed)
        {
            var call = new Call(tracker, completed);
            lock (call._gate)
            {
                if (!tracker.TryAdd(call)) throw new OperationCanceledException("The application stopped: no call to the desktop portal starts.");
                try
                {
                    call._cancellable = Native.g_cancellable_new();
                    call._id = LinuxPortalCallbacks.Register(call);
                    var type = Native.g_variant_type_new(replyType);
                    try { Native.g_dbus_connection_call(connection, LinuxPortalFileChooserProtocol.PortalName, path, interfaceName, method, parameters, type, 0, -1, call._cancellable, &Finished, call._id); }
                    finally { Native.g_variant_type_free(type); }
                }
                catch
                {
                    tracker.Remove(call);
                    call.Abandon();
                    throw;
                }
            }
            return call;
        }

        public void Cancel()
        {
            lock (_gate) if (_cancellable != 0) Native.g_cancellable_cancel(_cancellable);
        }

        public void Abandon()
        {
            LinuxPortalCallbacks.Remove(_id);
            ReleaseCancellable(cancel: true);
            _ended.TrySetResult();
        }

        private void ReleaseCancellable(bool cancel)
        {
            nint cancellable;
            lock (_gate)
            {
                cancellable = _cancellable;
                _cancellable = 0;
                if (cancel && cancellable != 0) Native.g_cancellable_cancel(cancellable);
            }
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
                if (call is not null)
                {
                    call.ReleaseCancellable(cancel: false);
                    call._tracker.Remove(call);
                }
                var kind = reply != 0 ? LinuxPortalReplyKind.Ok : Failure(error);
                error = 0;
                call?._completed(kind, reply);
            }
            catch { }
            finally
            {
                FreeError(error);
                if (reply != 0) Native.g_variant_unref(reply);
                call?._ended.TrySetResult();
            }
        }
    }

    // One signal subscription with its own reference on the connection. Disposing it unsubscribes once, from any thread, and
    // stops its deliveries; it ends only when GIO releases it (its GDestroyNotify, on the main loop, after the signals that
    // were already queued), which is what a drain waits for. It starts under its gate, as a call does.
    private sealed class Subscription : IDisposable, ILinuxPortalTrackedCall
    {
        private readonly object _gate = new();
        private readonly LinuxPortalCallTracker _tracker;
        private readonly Action<nint> _received;
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private nint _connection;
        private nint _id;
        private uint _subscription;
        private volatile bool _disposed;

        private Subscription(LinuxPortalCallTracker tracker, Action<nint> received)
        {
            _tracker = tracker;
            _received = received;
        }

        public Task Ended => _ended.Task;

        /// <exception cref="OperationCanceledException">The application stopped: nothing is subscribed.</exception>
        internal static Subscription Start(LinuxPortalCallTracker tracker, nint connection, string sender, string interfaceName, string member, string path, string? arg0, Action<nint> received)
        {
            var subscription = new Subscription(tracker, received);
            lock (subscription._gate)
            {
                if (!tracker.TryAdd(subscription)) throw new OperationCanceledException("The application stopped: nothing listens to the desktop portal.");
                subscription._connection = Native.g_object_ref(connection);
                subscription._id = LinuxPortalCallbacks.Register(subscription);
                try { subscription._subscription = Native.g_dbus_connection_signal_subscribe(connection, sender, interfaceName, member, path, arg0, 0, &Received, subscription._id, &Released); }
                catch { subscription._subscription = 0; }
                if (subscription._subscription == 0)
                {
                    tracker.Remove(subscription);
                    subscription.Abandon();
                    throw new InvalidOperationException("Could not listen to the desktop portal.");
                }
            }
            return subscription;
        }

        public void Cancel() => Dispose();

        public void Dispose()
        {
            uint subscription;
            nint connection;
            lock (_gate)
            {
                _disposed = true;
                subscription = _subscription;
                connection = _connection;
                _subscription = 0;
                _connection = 0;
            }
            if (subscription != 0) Native.g_dbus_connection_signal_unsubscribe(connection, subscription);
            if (connection != 0) Native.g_object_unref(connection);
        }

        // At the deadline of a drain, or for a main loop that may not run any more: GIO's release is not awaited.
        public void Abandon()
        {
            Dispose();
            LinuxPortalCallbacks.Remove(_id);
            _ended.TrySetResult();
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Received(nint connection, nint sender, nint objectPath, nint interfaceName, nint signalName, nint parameters, nint data)
        {
            try { if (LinuxPortalCallbacks.TryGet<Subscription>(data, out var subscription) && !subscription._disposed) subscription._received(parameters); }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Released(nint data)
        {
            try
            {
                if (!LinuxPortalCallbacks.TryGet<Subscription>(data, out var subscription)) return;
                LinuxPortalCallbacks.Remove(data);
                subscription._tracker.Remove(subscription);
                subscription._ended.TrySetResult();
            }
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
