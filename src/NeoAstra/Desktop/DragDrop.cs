// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Security.Cryptography;

namespace NeoAstra.Desktop.DragDrop;

/// <summary>Identifies brokered drag/drop data kinds.</summary>
public enum NeoDragDataKind
{
    /// <summary>Plain text.</summary>
    Text,
    /// <summary>Absolute URL.</summary>
    Url,
    /// <summary>Brokered file.</summary>
    File,
}

/// <summary>Contains one bounded inbound drop item. File authority is represented by <see cref="FileToken"/>, not DOM text.</summary>
/// <param name="Kind">Data kind.</param>
/// <param name="Text">Owned text or URL data.</param>
/// <param name="FileToken">Opaque broker token for the exact user-dropped file.</param>
public sealed record NeoDropItem(NeoDragDataKind Kind, string? Text, string? FileToken);

/// <summary>Contains one trusted inbound drop event in logical view coordinates.</summary>
/// <param name="ViewLabel">Immutable target view label.</param>
/// <param name="WindowLabel">Immutable target window label.</param>
/// <param name="Position">Logical position.</param>
/// <param name="Items">Bounded owned item snapshots.</param>
public sealed record NeoDropEvent(string ViewLabel, string? WindowLabel, NeoPoint Position, IReadOnlyList<NeoDropItem> Items);

/// <summary>Contains a brokered inbound event and the exact trusted token owner.</summary>
/// <param name="Drop">Owned drop snapshot.</param>
/// <param name="Owner">Exact view or document-session owner.</param>
public sealed record NeoOwnedDropEvent(NeoDropEvent Drop, NeoPluginOwner Owner);

/// <summary>Describes copied data for one outbound native drag operation.</summary>
/// <param name="Kind">Portable data kind.</param>
/// <param name="Value">Bounded text, absolute URL, or scoped existing file path.</param>
public sealed record NeoOutboundDragItem(NeoDragDataKind Kind, string Value);

/// <summary>Describes an owner-bound outbound drag initiated by a trusted user gesture.</summary>
public sealed record NeoOutboundDragRequest
{
    /// <summary>Gets the immutable source view label.</summary>
    public required string ViewLabel { get; init; }
    /// <summary>Gets copied, declared drag data.</summary>
    public required IReadOnlyList<NeoOutboundDragItem> Items { get; init; }
    /// <summary>Gets an optional application-controlled absolute PNG drag-image path.</summary>
    public string? DragImagePath { get; init; }
}

/// <summary>Presents an outbound drag through a platform backend or WebView integration.</summary>
public interface INeoOutboundDragPresenter
{
    /// <summary>Gets truthful platform support.</summary>
    NeoCapabilityInfo Support { get; }
    /// <summary>Starts the validated outbound drag and completes when native ownership ends.</summary>
    ValueTask<NeoDesktopStatus> StartAsync(NeoOutboundDragRequest request, CancellationToken cancellationToken);
}

internal interface INeoRendererOutboundDragPresenter
{
    ValueTask<NeoDesktopStatus> StartRendererAsync(string documentSessionId, NeoOutboundDragRequest request, CancellationToken cancellationToken);
}

/// <summary>Brokers canonical user-selected file drops and one-shot outbound user gestures with bounded lifetime.</summary>
public sealed class NeoDragDropBroker : IAsyncDisposable, INeoApplicationBoundDesktopService
{
    private readonly object _sync = new();
    private readonly NeoFileScope _outboundFiles;
    private readonly Dictionary<string, (string Path, NeoPluginOwner Owner, DateTimeOffset Expires)> _tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset Expires, NeoPluginOwner Owner)> _gestures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _rendererSessions = new(StringComparer.Ordinal);
    private readonly INeoOutboundDragPresenter? _outboundPresenter;
    private readonly Dictionary<NeoAstra, ViewHandlers> _attachedViews = [];
    private NeoApplication? _application;
    private int _rendererRegistrations;
    private bool _disposed;

    /// <summary>Initializes the broker with the file policy used for outbound drags.</summary>
    public NeoDragDropBroker(NeoFileScope files, INeoOutboundDragPresenter? outboundPresenter = null) { ArgumentNullException.ThrowIfNull(files); _outboundFiles = files; _outboundPresenter = outboundPresenter; }

    /// <summary>Gets truthful support. Native WebView file drops are brokered automatically after application binding.</summary>
    public NeoCapabilityInfo Support => _outboundPresenter?.Support ?? new(NeoSupportLevel.Limited, 1, 2, "Native WebView inbound file drops are canonicalized, bounded, owner-scoped, and released on navigation/view teardown; no outbound native presenter is attached.");

    /// <summary>Occurs after a trusted native/WebView presenter successfully brokers an inbound drop.</summary>
    public event EventHandler<NeoOwnedDropEvent>? Inbound;

    void INeoApplicationBoundDesktopService.BindApplication(NeoApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        lock (_sync)
        {
            if (_application is not null && !ReferenceEquals(_application, application)) throw new InvalidOperationException("The drag/drop broker is already bound to another application.");
            _application = application;
        }
        application.ViewRegistered += AttachView;
        foreach (var view in application.GetRegisteredViews()) AttachView(view);
        if (_outboundPresenter is INeoApplicationBoundDesktopService presenter) presenter.BindApplication(application);
    }

    /// <summary>Creates a bounded trusted inbound drop event and opaque file tokens.</summary>
    public NeoDesktopResult<NeoDropEvent> BrokerInbound(string viewLabel, string? windowLabel, NeoPoint position, IEnumerable<(NeoDragDataKind Kind, string Value)> items, NeoPluginOwner owner)
    {
        ValidateLabel(viewLabel, nameof(viewLabel)); if (windowLabel is not null) ValidateLabel(windowLabel, nameof(windowLabel));
        owner.Validate();
        if (owner.Kind == NeoPluginOwnerKind.View && !string.Equals(owner.Id, viewLabel, StringComparison.Ordinal)) return NeoDesktopResult<NeoDropEvent>.Failure(NeoDesktopStatus.Denied, "owner_mismatch");
        ArgumentNullException.ThrowIfNull(items);
        var values = items.Take(NeoDesktopLimits.MaximumDropItems + 1).ToArray();
        if (values.Length > NeoDesktopLimits.MaximumDropItems) return NeoDesktopResult<NeoDropEvent>.Failure(NeoDesktopStatus.LimitExceeded);
        var validated = new List<(NeoDragDataKind Kind, string Value)>(values.Length);
        foreach (var item in values)
        {
            if (!Enum.IsDefined(item.Kind) || item.Value is null || item.Value.Length > 32_768 || item.Value.Any(static c => c == '\0')) return NeoDesktopResult<NeoDropEvent>.Failure(NeoDesktopStatus.Denied, "invalid_drop_metadata");
            if (item.Kind == NeoDragDataKind.File)
            {
                if (!TryCanonicalizeDroppedFile(item.Value, out var canonical)) return NeoDesktopResult<NeoDropEvent>.Failure(NeoDesktopStatus.Denied, "invalid_drop_path");
                validated.Add((item.Kind, canonical!));
            }
            else if (item.Kind == NeoDragDataKind.Url && (!Uri.TryCreate(item.Value, UriKind.Absolute, out var uri) || uri.IsFile)) return NeoDesktopResult<NeoDropEvent>.Failure(NeoDesktopStatus.Denied, "invalid_url");
            else validated.Add(item);
        }
        var output = new List<NeoDropItem>(values.Length);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CleanupFileTokens();
            if (_tokens.Count + validated.Count(static item => item.Kind == NeoDragDataKind.File) > 4096) return NeoDesktopResult<NeoDropEvent>.Failure(NeoDesktopStatus.LimitExceeded);
            foreach (var item in validated)
            {
                if (item.Kind == NeoDragDataKind.File)
                {
                    var token = NewToken(); _tokens.Add(token, (item.Value, owner, DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5))); output.Add(new(NeoDragDataKind.File, null, token));
                }
                else output.Add(new(item.Kind, item.Value, null));
            }
        }
        var drop = new NeoDropEvent(viewLabel, windowLabel, position, Array.AsReadOnly(output.ToArray()));
        try { Inbound?.Invoke(this, new(drop, owner)); } catch { }
        return NeoDesktopResult<NeoDropEvent>.Success(drop);
    }

    /// <summary>Resolves a file token only for its trusted owner.</summary>
    public bool TryResolveFile(string token, NeoPluginOwner owner, out string? canonicalPath)
    {
        ValidateToken(token); owner.Validate(); lock (_sync) { CleanupFileTokens(); if (_tokens.TryGetValue(token, out var value) && value.Owner == owner) { canonicalPath = value.Path; return true; } }
        canonicalPath = null; return false;
    }

    /// <summary>Releases every token owned by a view/session/resource.</summary>
    public void ReleaseOwner(NeoPluginOwner owner)
    {
        owner.Validate();
        lock (_sync)
        {
            foreach (var token in _tokens.Where(pair => pair.Value.Owner == owner).Select(static pair => pair.Key).ToArray()) _tokens.Remove(token);
            foreach (var token in _gestures.Where(pair => pair.Value.Owner == owner).Select(static pair => pair.Key).ToArray()) _gestures.Remove(token);
        }
    }

    /// <summary>Issues a one-shot user-gesture token valid for at most ten seconds.</summary>
    public string IssueUserGesture(NeoPluginOwner owner, TimeSpan lifetime)
    {
        owner.Validate();
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromSeconds(10)) throw new ArgumentOutOfRangeException(nameof(lifetime));
        lock (_sync) { ObjectDisposedException.ThrowIf(_disposed, this); CleanupGestures(); if (_gestures.Count >= 256) throw new InvalidOperationException("The outbound gesture limit was reached."); var token = NewToken(); _gestures.Add(token, (DateTimeOffset.UtcNow + lifetime, owner)); return token; }
    }

    /// <summary>Consumes an explicit one-shot user gesture before a platform outbound drag begins.</summary>
    public bool TryConsumeUserGesture(string token, NeoPluginOwner owner)
    {
        ValidateToken(token); owner.Validate();
        lock (_sync)
        {
            CleanupGestures();
            if (!_gestures.TryGetValue(token, out var gesture) || gesture.Owner != owner) return false;
            return _gestures.Remove(token);
        }
    }

    /// <summary>Consumes an owner-bound one-shot gesture and runs one validated outbound drag.</summary>
    /// <param name="gestureToken">Opaque one-shot gesture token.</param>
    /// <param name="owner">Trusted source owner.</param>
    /// <param name="request">Copied drag declaration.</param>
    /// <param name="cancellationToken">Cancels the native drag.</param>
    /// <returns>The completion status.</returns>
    public async ValueTask<NeoDesktopStatus> StartOutboundAsync(string gestureToken, NeoPluginOwner owner, NeoOutboundDragRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateLabel(request.ViewLabel, nameof(request.ViewLabel));
        owner.Validate();
        if (owner.Kind == NeoPluginOwnerKind.View && !string.Equals(owner.Id, request.ViewLabel, StringComparison.Ordinal)) return NeoDesktopStatus.Denied;
        if (request.Items is null || request.Items.Count is < 1 or > NeoDesktopLimits.MaximumDropItems) throw new ArgumentException("An outbound drag requires 1 to 256 declared items.", nameof(request));
        if (!TryConsumeUserGesture(gestureToken, owner)) return NeoDesktopStatus.Denied;
        lock (_sync) if (_application is not null && !_attachedViews.Keys.Any(view => string.Equals(view.ViewLabel, request.ViewLabel, StringComparison.Ordinal))) return NeoDesktopStatus.NotFound;

        var snapshot = ValidateOutboundRequest(request);
        if (snapshot is null) return NeoDesktopStatus.Denied;
        if (_outboundPresenter is null) return NeoDesktopStatus.Unsupported;
        try { return await _outboundPresenter.StartAsync(snapshot, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return NeoDesktopStatus.Failed; }
    }

    internal async ValueTask<NeoDesktopStatus> StartRendererOutboundAsync(NeoPluginOwner owner, NeoOutboundDragRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateLabel(request.ViewLabel, nameof(request.ViewLabel));
        owner.Validate();
        if (owner.Kind != NeoPluginOwnerKind.DocumentSession) return NeoDesktopStatus.Denied;
        if (request.Items is null || request.Items.Count is < 1 or > NeoDesktopLimits.MaximumDropItems) throw new ArgumentException("An outbound drag requires 1 to 256 declared items.", nameof(request));
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_rendererRegistrations == 0 || !_attachedViews.Any(pair => string.Equals(pair.Key.ViewLabel, request.ViewLabel, StringComparison.Ordinal) && string.Equals(pair.Value.DocumentSessionId, owner.Id, StringComparison.Ordinal))) return NeoDesktopStatus.Denied;
        }

        var snapshot = ValidateOutboundRequest(request);
        if (snapshot is null) return NeoDesktopStatus.Denied;
        if (_outboundPresenter is not INeoRendererOutboundDragPresenter presenter) return NeoDesktopStatus.Unsupported;
        try { return await presenter.StartRendererAsync(owner.Id!, snapshot, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return NeoDesktopStatus.Failed; }
    }

    private NeoOutboundDragRequest? ValidateOutboundRequest(NeoOutboundDragRequest request)
    {
        var items = new NeoOutboundDragItem[request.Items.Count];
        for (var index = 0; index < request.Items.Count; index++)
        {
            var item = request.Items[index] ?? throw new ArgumentException("An outbound drag item cannot be null.", nameof(request));
            if (!Enum.IsDefined(item.Kind) || item.Value is null || item.Value.Length is < 1 or > 32_768 || item.Value.Any(static value => value == '\0')) throw new ArgumentException("Outbound drag metadata is malformed.", nameof(request));
            var value = item.Value;
            if (item.Kind == NeoDragDataKind.File)
            {
                if (!_outboundFiles.TryAuthorize(value, requireExisting: true, out var canonical)) return null;
                value = canonical!;
            }
            else if (item.Kind == NeoDragDataKind.Url && (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.IsFile || url.OriginalString.Any(char.IsControl))) return null;
            items[index] = new(item.Kind, value);
        }
        if (request.DragImagePath is { } dragImage && (!_outboundFiles.TryAuthorize(dragImage, requireExisting: true, out var canonicalImage) || !string.Equals(Path.GetExtension(canonicalImage), ".png", StringComparison.OrdinalIgnoreCase))) return null;
        return request with { Items = Array.AsReadOnly(items), DragImagePath = request.DragImagePath is null ? null : NeoFileScope.Canonicalize(request.DragImagePath, requireExisting: true) };
    }

    internal void RegisterRenderer()
    {
        lock (_sync) { ObjectDisposedException.ThrowIf(_disposed, this); _rendererRegistrations = checked(_rendererRegistrations + 1); }
    }

    internal void UnregisterRenderer()
    {
        string[] sessions = [];
        lock (_sync)
        {
            if (_rendererRegistrations == 0) return;
            _rendererRegistrations--;
            if (_rendererRegistrations == 0)
            {
                sessions = _attachedViews.Values.Select(static handlers => handlers.DocumentSessionId).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
                _rendererSessions.Clear();
            }
        }
        foreach (var session in sessions) ReleaseOwner(NeoPluginOwner.DocumentSession(session));
    }

    internal void RegisterRendererSession(string documentSessionId)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _rendererSessions.TryGetValue(documentSessionId, out var references);
            _rendererSessions[documentSessionId] = checked(references + 1);
        }
    }

    internal void UnregisterRendererSession(string documentSessionId)
    {
        lock (_sync)
        {
            if (!_rendererSessions.TryGetValue(documentSessionId, out var references)) return;
            if (references == 1) _rendererSessions.Remove(documentSessionId); else _rendererSessions[documentSessionId] = references - 1;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        NeoApplication? application; NeoAstra[] views;
        lock (_sync) { _disposed = true; _rendererRegistrations = 0; _rendererSessions.Clear(); _tokens.Clear(); _gestures.Clear(); Inbound = null; application = _application; _application = null; views = _attachedViews.Keys.ToArray(); }
        if (application is not null) application.ViewRegistered -= AttachView;
        foreach (var view in views) DetachView(view);
        if (_outboundPresenter is IDisposable presenter) presenter.Dispose();
        return ValueTask.CompletedTask;
    }

    private void AttachView(NeoAstra view)
    {
        if (view.ViewLabel is null) return;
        Action<int, IReadOnlyList<string>, NeoPoint> drop = (kind, values, position) => ReceiveNativeDrop(view, kind, values, position);
        Action navigation = () => { ReleaseOwner(NeoPluginOwner.View(view.ViewLabel)); UpdateViewSession(view, null); };
        Action<NeoTransportSessionSnapshot?> sessionChanged = session => UpdateViewSession(view, session?.DocumentSessionId);
        Action disposing = () => DetachView(view);
        EventHandler? windowClosed = view.OwnedWindow is null ? null : (_, _) => DetachView(view);
        lock (_sync) { if (_disposed || _attachedViews.ContainsKey(view)) return; _attachedViews.Add(view, new(drop, navigation, sessionChanged, disposing, windowClosed, view.TransportSession?.DocumentSessionId)); }
        view.NativeDropReceived += drop;
        view.NativeNavigationStarted += navigation;
        view.TransportSessionChanged += sessionChanged;
        view.Disposing += disposing;
        if (windowClosed is not null) view.OwnedWindow!.Closed += windowClosed;
        UpdateViewSession(view, view.TransportSession?.DocumentSessionId);
    }

    private void DetachView(NeoAstra view)
    {
        ViewHandlers? handlers; lock (_sync) { if (!_attachedViews.Remove(view, out handlers)) return; }
        view.NativeDropReceived -= handlers.Drop; view.NativeNavigationStarted -= handlers.Navigation; view.TransportSessionChanged -= handlers.SessionChanged; view.Disposing -= handlers.Disposing;
        if (handlers.WindowClosed is not null && view.OwnedWindow is not null) view.OwnedWindow.Closed -= handlers.WindowClosed;
        if (handlers.DocumentSessionId is { } session) ReleaseOwner(NeoPluginOwner.DocumentSession(session));
        if (view.ViewLabel is not null) ReleaseOwner(NeoPluginOwner.View(view.ViewLabel));
    }

    private void UpdateViewSession(NeoAstra view, string? documentSessionId)
    {
        string? previous;
        lock (_sync)
        {
            if (!_attachedViews.TryGetValue(view, out var handlers)) return;
            previous = handlers.DocumentSessionId;
            handlers.DocumentSessionId = documentSessionId;
        }
        if (previous is not null && !string.Equals(previous, documentSessionId, StringComparison.Ordinal)) ReleaseOwner(NeoPluginOwner.DocumentSession(previous));
    }

    private void ReceiveNativeDrop(NeoAstra view, int kind, IReadOnlyList<string> values, NeoPoint position)
    {
        var label = view.ViewLabel;
        if (label is null) return;
        string? documentSessionId;
        lock (_sync)
        {
            if (_disposed || _rendererRegistrations == 0 || !_attachedViews.TryGetValue(view, out var handlers)) return;
            documentSessionId = handlers.DocumentSessionId;
        }
        if (documentSessionId is null) return;
        var owner = NeoPluginOwner.DocumentSession(documentSessionId);
        _ = BrokerInbound(label, view.OwnedWindow?.Label, position, values.Select(value => ((NeoDragDataKind)kind, value)), owner);
        lock (_sync)
        {
            if (_disposed || !_rendererSessions.ContainsKey(documentSessionId) || !_attachedViews.TryGetValue(view, out var handlers) || !string.Equals(handlers.DocumentSessionId, documentSessionId, StringComparison.Ordinal)) ReleaseOwner(owner);
        }
    }

    private sealed class ViewHandlers(Action<int, IReadOnlyList<string>, NeoPoint> drop, Action navigation, Action<NeoTransportSessionSnapshot?> sessionChanged, Action disposing, EventHandler? windowClosed, string? documentSessionId)
    {
        internal Action<int, IReadOnlyList<string>, NeoPoint> Drop { get; } = drop;
        internal Action Navigation { get; } = navigation;
        internal Action<NeoTransportSessionSnapshot?> SessionChanged { get; } = sessionChanged;
        internal Action Disposing { get; } = disposing;
        internal EventHandler? WindowClosed { get; } = windowClosed;
        internal string? DocumentSessionId { get; set; } = documentSessionId;
    }

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    private static bool TryCanonicalizeDroppedFile(string path, out string? canonicalPath)
    {
        try { canonicalPath = NeoFileScope.Canonicalize(path, requireExisting: true); return true; }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { canonicalPath = null; return false; }
    }
    private static void ValidateToken(string token) { if (token is null || token.Length != 32 || token.Any(static c => !Uri.IsHexDigit(c))) throw new ArgumentException("An opaque token is malformed.", nameof(token)); }
    private static void ValidateLabel(string value, string parameterName) { if (string.IsNullOrEmpty(value) || value.Length > 128 || value.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':'))) throw new ArgumentException("A target label is malformed.", parameterName); }
    private void CleanupGestures() { var now = DateTimeOffset.UtcNow; foreach (var token in _gestures.Where(pair => pair.Value.Expires <= now).Select(static pair => pair.Key).ToArray()) _gestures.Remove(token); }
    private void CleanupFileTokens() { var now = DateTimeOffset.UtcNow; foreach (var token in _tokens.Where(pair => pair.Value.Expires <= now).Select(static pair => pair.Key).ToArray()) _tokens.Remove(token); }
}
