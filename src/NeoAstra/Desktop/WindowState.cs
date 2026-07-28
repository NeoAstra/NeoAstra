// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeoAstra.Desktop.WindowState;

/// <summary>Contains portable persisted normal window placement in logical desktop units.</summary>
/// <param name="NormalBounds">Last normal bounds.</param>
/// <param name="State">Last effective state.</param>
/// <param name="DisplayId">Stable-in-session display affinity hint.</param>
/// <param name="DisplayScaleFactor">Scale when saved.</param>
/// <param name="WasVisible">Optional saved visibility.</param>
public sealed record NeoWindowPlacement(NeoRect NormalBounds, NeoWindowState State, string? DisplayId, double DisplayScaleFactor, bool? WasVisible);

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
        if (placement.NormalBounds.Width <= 0 || placement.NormalBounds.Height <= 0 || !double.IsFinite(placement.DisplayScaleFactor) || placement.DisplayScaleFactor is < 0.25 or > 16 || !Enum.IsDefined(placement.State) || placement.DisplayId is { } id && (id.Length > 128 || id.Any(char.IsControl))) throw new ArgumentException("A window placement is malformed.", nameof(placement));
    }

    private string PathFor(string key) => Path.Combine(_directory, key + ".json");
    private static void ValidateKey(string key) { if (string.IsNullOrEmpty(key) || key.Length > 128 || key.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))) throw new ArgumentException("A window-state key is malformed.", nameof(key)); }
}

/// <summary>Clamps persisted placement to a recoverable current work area and accounts for topology/scale changes.</summary>
public static class NeoWindowStateRestore
{
    /// <summary>Restores a safe placement. Minimized state is normalized by default.</summary>
    public static NeoWindowPlacement Clamp(NeoWindowPlacement saved, IReadOnlyList<SystemInfo.NeoDisplaySnapshot> displays, bool restoreMinimized = false)
    {
        NeoJsonWindowStateStore.ValidatePlacement(saved); ArgumentNullException.ThrowIfNull(displays);
        if (displays.Count == 0) return saved with { State = saved.State == NeoWindowState.Minimized && !restoreMinimized ? NeoWindowState.Normal : saved.State };
        if (displays.Any(static value => value.WorkArea.Width <= 0 || value.WorkArea.Height <= 0 || !double.IsFinite(value.ScaleFactor) || value.ScaleFactor is < 0.25 or > 16)) throw new ArgumentException("A display snapshot is malformed.", nameof(displays));
        var display = displays.FirstOrDefault(value => value.Id == saved.DisplayId) ?? displays.OrderByDescending(value => IntersectionArea(saved.NormalBounds, value.WorkArea)).ThenByDescending(static value => value.IsPrimary).First();
        var work = display.WorkArea;
        var ratio = saved.DisplayScaleFactor / display.ScaleFactor;
        var width = (int)Math.Clamp(Math.Round(saved.NormalBounds.Width * ratio), Math.Min(100, work.Width), work.Width);
        var height = (int)Math.Clamp(Math.Round(saved.NormalBounds.Height * ratio), Math.Min(100, work.Height), work.Height);
        var x = (int)Math.Clamp((long)saved.NormalBounds.X, work.X, (long)work.X + work.Width - width);
        var y = (int)Math.Clamp((long)saved.NormalBounds.Y, work.Y, (long)work.Y + work.Height - height);
        var state = saved.State == NeoWindowState.Minimized && !restoreMinimized ? NeoWindowState.Normal : saved.State;
        return new(new(x, y, width, height), state, display.Id, display.ScaleFactor, saved.WasVisible);
    }

    private static long IntersectionArea(NeoRect left, NeoRect right)
    {
        var width = Math.Max(0L, Math.Min((long)left.X + left.Width, (long)right.X + right.Width) - Math.Max(left.X, right.X));
        var height = Math.Max(0L, Math.Min((long)left.Y + left.Height, (long)right.Y + right.Height) - Math.Max(left.Y, right.Y));
        return width * height;
    }
}

/// <summary>Debounces atomic window-state writes and detaches deterministically.</summary>
public sealed class NeoWindowStateController : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly NeoWindow _window;
    private readonly INeoWindowStateStore _store;
    private readonly string _key;
    private readonly TimeSpan _debounce;
    private readonly Timer _timer;
    private NeoRect _normalBounds;
    private bool _disposed;
    private Task _lastWrite = Task.CompletedTask;

    /// <summary>Initializes and starts observing one window.</summary>
    public NeoWindowStateController(NeoWindow window, INeoWindowStateStore store, string key, TimeSpan? debounce = null)
    {
        ArgumentNullException.ThrowIfNull(window); ArgumentNullException.ThrowIfNull(store);
        _ = new NeoJsonWindowStateStoreValidator(key);
        _debounce = debounce ?? TimeSpan.FromMilliseconds(250);
        if (_debounce < TimeSpan.FromMilliseconds(50) || _debounce > TimeSpan.FromSeconds(10)) throw new ArgumentOutOfRangeException(nameof(debounce));
        _window = window; _store = store; _key = key; _normalBounds = new(window.Position.X, window.Position.Y, window.ClientSize.Width, window.ClientSize.Height);
        _timer = new Timer(static state => ((NeoWindowStateController)state!).QueueWrite(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        window.BoundsChanged += OnBoundsChanged;
    }

    /// <summary>Loads and clamps state before a window is shown.</summary>
    public async ValueTask<NeoWindowPlacement?> RestoreAsync(IReadOnlyList<SystemInfo.NeoDisplaySnapshot> displays, bool restoreVisibility = false, CancellationToken cancellationToken = default)
    {
        var saved = await _store.LoadAsync(_key, cancellationToken).ConfigureAwait(false); if (saved is null) return null;
        var restored = NeoWindowStateRestore.Clamp(saved, displays);
        await _window.Application.Dispatcher.InvokeAsync(() => { _window.Position = restored.NormalBounds.Position; _window.ClientSize = restored.NormalBounds.Size; _window.State = restored.State; if (restoreVisibility && restored.WasVisible == true) _window.Show(); }, cancellationToken).ConfigureAwait(false);
        return restored;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); }
        _window.BoundsChanged -= OnBoundsChanged;
        _timer.Dispose();
        QueueWrite(force: true);
        Task write; lock (_sync) write = _lastWrite;
        try { await write.ConfigureAwait(false); } catch { }
    }

    private void OnBoundsChanged(object? sender, NeoWindowBoundsChangedEventArgs args)
    {
        lock (_sync) { if (_disposed) return; _normalBounds = args.NewBounds; _timer.Change(_debounce, Timeout.InfiniteTimeSpan); }
    }

    private void QueueWrite(bool force = false)
    {
        NeoRect bounds;
        lock (_sync) { if (_disposed && !force) return; bounds = _normalBounds; _lastWrite = _lastWrite.ContinueWith(_ => WriteAsync(bounds), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap(); }
    }

    private async Task WriteAsync(NeoRect bounds)
    {
        try
        {
            NeoWindowPlacement placement = null!;
            await _window.Application.Dispatcher.InvokeAsync(() => placement = new(bounds, _window.State == NeoWindowState.Minimized ? NeoWindowState.Normal : _window.State, null, _window.ScaleFactor, _window.IsVisible)).ConfigureAwait(false);
            await _store.SaveAsync(_key, placement).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { }
    }

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
