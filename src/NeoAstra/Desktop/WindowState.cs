// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeoAstra.Desktop.WindowState;

/// <summary>Contains the persisted placement of a window, in the units its window counts in.</summary>
/// <param name="NormalBounds">Last normal bounds: the position and the client size that <see cref="NeoWindow.Position"/> and <see cref="NeoWindow.ClientSize"/> report and accept.</param>
/// <param name="State">Last effective state.</param>
/// <param name="DisplayId">Stable-in-session display affinity hint.</param>
/// <param name="DisplayScaleFactor">The <see cref="NeoWindow.ScaleFactor"/> of the window when it was saved.</param>
/// <param name="WasVisible">Optional saved visibility.</param>
public sealed record NeoWindowPlacement(NeoRect NormalBounds, NeoWindowState State, string? DisplayId, double DisplayScaleFactor, bool? WasVisible)
{
    /// <summary>
    /// Gets the rectangle that the window showed on screen at its normal bounds, as <see cref="NeoWindow.FrameBounds"/> reports
    /// it, or <see langword="null"/> when it is not known: the normal bounds are then taken for it.
    /// </summary>
    /// <remarks>
    /// The normal bounds pair the corner of the frame of the window with the size of its client area. This is what the window
    /// shows of that frame, with its title bar and without the borders that are not drawn, which is what a restore compares
    /// with the displays. A placement that was saved before this member has none.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NeoRect? NormalFrame { get; init; }
}

/// <summary>Provides application-chosen persistence without granting renderer access.</summary>
public interface INeoWindowStateStore
{
    /// <summary>Loads one exact application/window key.</summary>
    ValueTask<NeoWindowPlacement?> LoadAsync(string key, CancellationToken cancellationToken = default);
    /// <summary>Atomically saves one exact application/window key.</summary>
    ValueTask SaveAsync(string key, NeoWindowPlacement placement, CancellationToken cancellationToken = default);
}

/// <summary>Persists one state per file through source-generated JSON and atomic replacement.</summary>
public sealed class NeoJsonWindowStateStore : INeoWindowStateStore
{
    private readonly string _directory;

    /// <summary>Initializes an absolute application-owned state directory.</summary>
    public NeoJsonWindowStateStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("A state directory must be absolute.", nameof(directory));
        _directory = Path.GetFullPath(directory); Directory.CreateDirectory(_directory);
    }

    /// <inheritdoc />
    public async ValueTask<NeoWindowPlacement?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key); var path = PathFor(key); if (!File.Exists(path)) return null;
        try
        {
            await using var input = File.OpenRead(path);
            if (input.Length > 64 * 1024) return null;
            var placement = await JsonSerializer.DeserializeAsync(input, WindowStateJsonContext.Default.NeoWindowPlacement, cancellationToken).ConfigureAwait(false);
            if (placement is not null) ValidatePlacement(placement);
            return placement;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <inheritdoc />
    public async ValueTask SaveAsync(string key, NeoWindowPlacement placement, CancellationToken cancellationToken = default)
    {
        ValidateKey(key); ValidatePlacement(placement);
        var target = PathFor(key); var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(output, placement, WindowStateJsonContext.Default.NeoWindowPlacement, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, target, overwrite: true);
        }
        finally { try { File.Delete(temporary); } catch { } }
    }

    internal static void ValidatePlacement(NeoWindowPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        if (placement.NormalBounds.Width <= 0 || placement.NormalBounds.Height <= 0 || !double.IsFinite(placement.DisplayScaleFactor) || placement.DisplayScaleFactor is < 0.25 or > 16 || !Enum.IsDefined(placement.State) || placement.DisplayId is { } id && (id.Length > 128 || id.Any(char.IsControl)) || placement.NormalFrame is { } frame && !IsFrameOf(frame, placement.NormalBounds)) throw new ArgumentException("A window placement is malformed.", nameof(placement));
    }

    // Whether a rectangle can be what a window with these bounds shows: a frame and a title bar are no more than this around them.
    internal static bool IsFrameOf(NeoRect frame, NeoRect bounds)
    {
        const int most = 1024;
        return frame.Width > 0 && frame.Height > 0 && Math.Abs((long)frame.X - bounds.X) <= most && Math.Abs((long)frame.Y - bounds.Y) <= most && Math.Abs((long)frame.Width - bounds.Width) <= most && Math.Abs((long)frame.Height - bounds.Height) <= most;
    }

    private string PathFor(string key) => Path.Combine(_directory, key + ".json");
    private static void ValidateKey(string key) { if (string.IsNullOrEmpty(key) || key.Length > 128 || key.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))) throw new ArgumentException("A window-state key is malformed.", nameof(key)); }
}

/// <summary>Clamps persisted placement to a recoverable current work area and accounts for topology/scale changes.</summary>
public static class NeoWindowStateRestore
{
    /// <summary>Restores a safe placement. Minimized state is normalized by default.</summary>
    /// <param name="saved">The placement that was saved.</param>
    /// <param name="displays">The displays there are now.</param>
    /// <param name="restoreMinimized">Whether a window that was saved minimized comes back minimized.</param>
    /// <returns>
    /// The placement to give the window, in the units of <paramref name="saved"/>: its normal bounds are the position and the
    /// client size to assign, and its normal frame, when the saved placement has one, is what the window then shows.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The window keeps the size its content had. Where a window is counted in logical units, on macOS and with GTK, that
    /// is the saved size on every display. Where it is counted in the pixels of its display, on Windows, the size follows
    /// the scale of the display that the window comes back on, and the work areas, which a display snapshot carries in
    /// logical units, are compared with the window in those pixels.
    /// </para>
    /// <para>
    /// The display that the window comes back on is the one named by the saved placement, or else the one whose work
    /// area the saved bounds cover most, the primary one if they cover none. Its identifier and its scale are those of
    /// the returned placement. A window that is on that display alone is shrunk to its work area and moved into it.
    /// </para>
    /// <para>
    /// A window that is over several displays stays over them: it is shrunk to the rectangle around the work areas of
    /// the displays it is on, and moved into that rectangle, which leaves a window that is within it where it was. That
    /// rectangle has parts that no display shows where the displays are not aligned, so such a placement is returned
    /// only while a stretch of the title bar of the window is on the work area of one display: of its top 32 logical
    /// units, half the width of the window or 100 logical units, whichever is less. A window that has less than that
    /// comes back whole on the display it is mostly on, as does a window that was on a display that is gone.
    /// </para>
    /// <para>
    /// What is compared with the displays, shrunk, and moved is the rectangle that the window shows, the
    /// <see cref="NeoWindowPlacement.NormalFrame"/> of the saved placement, which <see cref="NeoWindowStateController"/> saves:
    /// a window whose visible edge was at the edge of a display stays there, and the title bar of a window that is shrunk is
    /// counted in. The frame keeps its place around the normal bounds, and follows the scale as the size does. The normal
    /// bounds are compared instead for a placement that has no frame, where a window is taken to show the rectangle of its
    /// position and its client size.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="saved"/> or <paramref name="displays"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="saved"/> or one of <paramref name="displays"/> is malformed.</exception>
    public static NeoWindowPlacement Clamp(NeoWindowPlacement saved, IReadOnlyList<SystemInfo.NeoDisplaySnapshot> displays, bool restoreMinimized = false)
        => Clamp(saved, displays, restoreMinimized, OperatingSystem.IsWindows());

    internal static NeoWindowPlacement Clamp(NeoWindowPlacement saved, IReadOnlyList<SystemInfo.NeoDisplaySnapshot> displays, bool restoreMinimized, bool countsInDisplayPixels)
    {
        NeoJsonWindowStateStore.ValidatePlacement(saved); ArgumentNullException.ThrowIfNull(displays);
        if (displays.Count == 0) return saved with { State = saved.State == NeoWindowState.Minimized && !restoreMinimized ? NeoWindowState.Normal : saved.State };
        if (displays.Any(static value => value.WorkArea.Width <= 0 || value.WorkArea.Height <= 0 || !double.IsFinite(value.ScaleFactor) || value.ScaleFactor is < 0.25 or > 16)) throw new ArgumentException("A display snapshot is malformed.", nameof(displays));
        // What the window shows is what is on a display, and the saved bounds stand for it where it is not known.
        var normal = saved.NormalBounds;
        var frame = saved.NormalFrame ?? normal;
        var display = displays.FirstOrDefault(value => value.Id == saved.DisplayId) ?? displays.OrderByDescending(value => IntersectionArea(frame, WorkArea(value, countsInDisplayPixels))).ThenByDescending(static value => value.IsPrimary).First();
        var work = WorkArea(display, countsInDisplayPixels);
        // A logical unit is the same on every display; a pixel is worth what the scale of its display says.
        var ratio = countsInDisplayPixels ? display.ScaleFactor / saved.DisplayScaleFactor : 1d;
        // Where the frame is around the bounds, which follows the scale as the size of the window does.
        long left = Scaled((long)frame.X - normal.X), top = Scaled((long)frame.Y - normal.Y), wider = Scaled((long)frame.Width - normal.Width), higher = Scaled((long)frame.Height - normal.Height);
        var wanted = new Area(normal.X + left, normal.Y + top, Scaled(normal.Width) + wider, Scaled(normal.Height) + higher);
        var home = new Area(work.X, work.Y, work.Width, work.Height);
        var shown = Fit(wanted, home);
        // The rectangle around the displays that the window is on has parts that no display shows where they are not aligned.
        var around = home;
        foreach (var other in displays)
        {
            var area = WorkArea(other, countsInDisplayPixels);
            if (IntersectionArea(wanted, area) > 0) around = around.Around(new(area.X, area.Y, area.Width, area.Height));
        }
        if (around != home && Fit(wanted, around) is var over && displays.Any(value => HasTitleBarOn(over, value, countsInDisplayPixels))) shown = over;
        var state = saved.State == NeoWindowState.Minimized && !restoreMinimized ? NeoWindowState.Normal : saved.State;
        var bounds = new NeoRect(Whole(shown.X - left), Whole(shown.Y - top), (int)Math.Max(1, shown.Width - wider), (int)Math.Max(1, shown.Height - higher));
        NeoRect? restored = saved.NormalFrame is null ? null : new(Whole(bounds.X + left), Whole(bounds.Y + top), (int)Math.Max(1, bounds.Width + wider), (int)Math.Max(1, bounds.Height + higher));
        // A frame that a change of scale took further from the bounds than a store accepts is left out.
        if (restored is { } frameOfBounds && !NeoJsonWindowStateStore.IsFrameOf(frameOfBounds, bounds)) restored = null;
        return new(bounds, state, display.Id, display.ScaleFactor, saved.WasVisible) { NormalFrame = restored };

        long Scaled(long value) => (long)Math.Round(value * ratio);
        static int Whole(long value) => (int)Math.Clamp(value, int.MinValue, int.MaxValue);
    }

    // The height of a title bar and the least stretch of it that a user takes a window by, in logical units.
    private const int TitleBarHeight = 32, TitleBarStretch = 100;

    // A rectangle in the units of a window. The one around several displays can be wider than a display may be.
    private readonly record struct Area(long X, long Y, long Width, long Height)
    {
        internal Area Around(Area other)
        {
            long left = Math.Min(X, other.X), top = Math.Min(Y, other.Y);
            return new(left, top, Math.Max(X + Width, other.X + other.Width) - left, Math.Max(Y + Height, other.Y + other.Height) - top);
        }
    }

    // Shrinks a window to an area, to no less than 100 units where the area has them, and moves it into the area.
    private static Area Fit(Area window, Area area)
    {
        long wide = Math.Min(area.Width, int.MaxValue), high = Math.Min(area.Height, int.MaxValue);
        var width = Math.Clamp(window.Width, Math.Min(100, wide), wide);
        var height = Math.Clamp(window.Height, Math.Min(100, high), high);
        return new(Math.Clamp(window.X, area.X, area.X + area.Width - width), Math.Clamp(window.Y, area.Y, area.Y + area.Height - height), width, height);
    }

    // Whether enough of the top of a window is on the work area of a display for a user to take the window by it.
    private static bool HasTitleBarOn(Area window, SystemInfo.NeoDisplaySnapshot display, bool countsInDisplayPixels)
    {
        var work = WorkArea(display, countsInDisplayPixels);
        var unit = countsInDisplayPixels ? display.ScaleFactor : 1d;
        if (window.Y < work.Y || window.Y + (long)Math.Ceiling(TitleBarHeight * unit) > (long)work.Y + work.Height) return false;
        var stretch = Math.Min(window.X + window.Width, (long)work.X + work.Width) - Math.Max(window.X, work.X);
        return stretch >= Math.Min((window.Width + 1) / 2, (long)Math.Ceiling(TitleBarStretch * unit));
    }

    // The work area of a display in the units that its windows are counted in.
    private static NeoRect WorkArea(SystemInfo.NeoDisplaySnapshot display, bool countsInDisplayPixels)
    {
        var area = display.WorkArea;
        if (!countsInDisplayPixels) return area;
        var scale = display.ScaleFactor;
        int left = Pixels(area.X * scale), top = Pixels(area.Y * scale);
        return new(left, top, Math.Max(1, Pixels(((long)area.X + area.Width) * scale) - left), Math.Max(1, Pixels(((long)area.Y + area.Height) * scale) - top));

        static int Pixels(double value) => (int)Math.Clamp(Math.Round(value), int.MinValue / 2, int.MaxValue / 2);
    }

    private static long IntersectionArea(NeoRect left, NeoRect right) => IntersectionArea(new Area(left.X, left.Y, left.Width, left.Height), right);

    private static long IntersectionArea(Area left, NeoRect right)
    {
        var width = Math.Max(0L, Math.Min(left.X + left.Width, (long)right.X + right.Width) - Math.Max(left.X, right.X));
        var height = Math.Max(0L, Math.Min(left.Y + left.Height, (long)right.Y + right.Height) - Math.Max(left.Y, right.Y));
        return width * height;
    }
}

/// <summary>Debounces atomic window-state writes and detaches deterministically.</summary>
/// <remarks>
/// <para>
/// What is saved is the placement that the window goes back to. Its normal bounds are the bounds that the window has
/// while it is in its normal state: a window that is maximized, fullscreen, or minimized keeps the ones it had before.
/// Its state is the state of the window, or the one it had before it was minimized. A window that is not in its
/// normal state when the controller first sees it has the bounds it then has for its normal bounds, until it is
/// restored from a store or returns to its normal state.
/// </para>
/// <para>
/// The window is read once it has been left alone for the delay, so that a change of state that takes the platform
/// some time is over by then. A write that the store refuses is reported to the log of the application as an error
/// of the category <c>window.state</c>, and the next change is written again.
/// </para>
/// </remarks>
public sealed class NeoWindowStateController : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly NeoWindow _window;
    private readonly INeoWindowStateStore _store;
    private readonly string _key;
    private readonly TimeSpan _debounce;
    private readonly Timer _timer;
    private NeoRect? _normalBounds;
    private NeoRect? _normalFrame;
    private NeoWindowState _state;
    private bool _disposed;
    private Task _lastWrite = Task.CompletedTask;

    /// <summary>Initializes and starts observing one window.</summary>
    public NeoWindowStateController(NeoWindow window, INeoWindowStateStore store, string key, TimeSpan? debounce = null)
    {
        ArgumentNullException.ThrowIfNull(window); ArgumentNullException.ThrowIfNull(store);
        _ = new NeoJsonWindowStateStoreValidator(key);
        _debounce = debounce ?? TimeSpan.FromMilliseconds(250);
        if (_debounce < TimeSpan.FromMilliseconds(50) || _debounce > TimeSpan.FromSeconds(10)) throw new ArgumentOutOfRangeException(nameof(debounce));
        _window = window; _store = store; _key = key;
        _normalBounds = Usable(new(window.Position.X, window.Position.Y, window.ClientSize.Width, window.ClientSize.Height));
        _normalFrame = _normalBounds is { } bounds ? Frame(bounds) : null;
        _state = window.State is var state && state != NeoWindowState.Minimized ? state : NeoWindowState.Normal;
        _timer = new Timer(static state => ((NeoWindowStateController)state!).QueueWrite(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        window.BoundsChanged += OnChanged;
        window.StateChanged += OnChanged;
    }

    /// <summary>Loads and clamps state before a window is shown.</summary>
    public async ValueTask<NeoWindowPlacement?> RestoreAsync(IReadOnlyList<SystemInfo.NeoDisplaySnapshot> displays, bool restoreVisibility = false, CancellationToken cancellationToken = default)
    {
        var saved = await _store.LoadAsync(_key, cancellationToken).ConfigureAwait(false); if (saved is null) return null;
        var restored = NeoWindowStateRestore.Clamp(saved, displays);
        await _window.Application.Dispatcher.InvokeAsync(() =>
        {
            _window.Position = restored.NormalBounds.Position; _window.ClientSize = restored.NormalBounds.Size; _window.State = restored.State;
            // A window that comes back in another state than the normal one never shows these bounds, and still goes back to them.
            lock (_sync) { _normalBounds = restored.NormalBounds; _normalFrame = restored.NormalFrame; _state = restored.State == NeoWindowState.Minimized ? NeoWindowState.Normal : restored.State; }
            if (restoreVisibility && restored.WasVisible == true) _window.Show();
        }, cancellationToken).ConfigureAwait(false);
        return restored;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); }
        _window.BoundsChanged -= OnChanged;
        _window.StateChanged -= OnChanged;
        _timer.Dispose();
        QueueWrite(force: true);
        Task write; lock (_sync) write = _lastWrite;
        try { await write.ConfigureAwait(false); } catch { }
    }

    // The window is read when it is written, not here: a window may hear of its new bounds before it hears of its new state.
    private void OnChanged(object? sender, EventArgs args)
    {
        lock (_sync) { if (_disposed) return; _timer.Change(_debounce, Timeout.InfiniteTimeSpan); }
    }

    private void QueueWrite(bool force = false)
    {
        lock (_sync) { if (_disposed && !force) return; _lastWrite = _lastWrite.ContinueWith(_ => WriteAsync(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap(); }
    }

    private async Task WriteAsync()
    {
        try
        {
            NeoWindowPlacement? placement = null;
            await _window.Application.Dispatcher.InvokeAsync(() => placement = Observe()).ConfigureAwait(false);
            if (placement is not null) await _store.SaveAsync(_key, placement).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { }
        catch (Exception exception) { _window.Application.ReportLifecycleFailure("window.state", exception, _window.Id); }
    }

    // What the window goes back to, from what it is now. Runs on the UI thread.
    private NeoWindowPlacement? Observe()
    {
        var state = _window.State;
        var bounds = Usable(new(_window.Position.X, _window.Position.Y, _window.ClientSize.Width, _window.ClientSize.Height));
        var frame = state == NeoWindowState.Normal && bounds is { } shown ? Frame(shown) : null;
        lock (_sync)
        {
            if (state == NeoWindowState.Normal && bounds is { } read) { _normalFrame = frame ?? Moved(_normalFrame, _normalBounds, read); _normalBounds = read; }
            if (state != NeoWindowState.Minimized) _state = state;
            return _normalBounds is { } normal ? new(normal, _state, null, _window.ScaleFactor, _window.IsVisible) { NormalFrame = _normalFrame } : null;
        }
    }

    // What the window shows at these bounds. A window tells it on its own thread and until it is closed, and tells nothing
    // here that a store would refuse.
    private NeoRect? Frame(NeoRect bounds)
    {
        try { return _window.FrameBounds is var frame && NeoJsonWindowStateStore.IsFrameOf(frame, bounds) ? frame : null; }
        catch (Exception exception) when (exception is InvalidOperationException or NeoAstraException) { return null; }
    }

    // The last write can come after the window was closed: the frame that was known keeps its place around the bounds.
    private static NeoRect? Moved(NeoRect? frame, NeoRect? from, NeoRect to)
    {
        if (frame is not { } known || from is not { } around) return null;
        var moved = new NeoRect(to.X + (known.X - around.X), to.Y + (known.Y - around.Y), to.Width + (known.Width - around.Width), to.Height + (known.Height - around.Height));
        return NeoJsonWindowStateStore.IsFrameOf(moved, to) ? moved : null;
    }

    // A minimized window may report no size at all, as it does on Windows.
    private static NeoRect? Usable(NeoRect bounds) => bounds.Width > 0 && bounds.Height > 0 ? bounds : null;

    private readonly struct NeoJsonWindowStateStoreValidator
    {
        internal NeoJsonWindowStateStoreValidator(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length > 128 || key.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))) throw new ArgumentException("A window-state key is malformed.", nameof(key));
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(NeoWindowPlacement))]
internal sealed partial class WindowStateJsonContext : JsonSerializerContext;
