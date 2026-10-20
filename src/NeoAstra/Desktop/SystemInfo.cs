// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Globalization;
using System.Runtime.InteropServices;

namespace NeoAstra.Desktop.SystemInfo;

/// <summary>Identifies a portable system theme.</summary>
public enum NeoSystemTheme
{
    /// <summary>Theme is unavailable.</summary>
    Unknown,
    /// <summary>Light appearance.</summary>
    Light,
    /// <summary>Dark appearance.</summary>
    Dark,
    /// <summary>High-contrast appearance.</summary>
    HighContrast,
}

/// <summary>Identifies standard application directory intent.</summary>
public enum NeoStandardDirectory
{
    /// <summary>Per-user application data.</summary>
    ApplicationData,
    /// <summary>Per-user cache data.</summary>
    Cache,
    /// <summary>User documents.</summary>
    Documents,
    /// <summary>User downloads.</summary>
    Downloads,
    /// <summary>Temporary files.</summary>
    Temporary,
}

/// <summary>Contains an immutable theme/accessibility snapshot.</summary>
/// <param name="Theme">Effective portable theme.</param>
/// <param name="AccentColor">Optional <c>#RRGGBB</c> accent.</param>
/// <param name="ReducedMotion">Reliable reduced-motion preference, when known.</param>
/// <param name="ReducedTransparency">Reliable reduced-transparency preference, when known.</param>
public sealed record NeoThemeSnapshot(NeoSystemTheme Theme, string? AccentColor, bool? ReducedMotion, bool? ReducedTransparency);

/// <summary>Contains immutable display coordinates in logical desktop units.</summary>
/// <remarks>
/// With the native library of an application, a snapshot is a <see cref="NeoDisplay"/> of <see cref="NeoApplication.GetDisplays"/>
/// in logical units: on Windows, where a display is described in the pixels of each display for an application aware of display
/// scaling, each coordinate is divided by the scale of its display, and elsewhere the units are the same.
/// </remarks>
/// <param name="Id">The display ID: the <see cref="NeoDisplay.Id"/> of the display with the native library of an application, which names it after a restart where the platform tells its monitor, and an ID that lasts for the session otherwise.</param>
/// <param name="Bounds">Logical bounds.</param>
/// <param name="WorkArea">Logical usable bounds.</param>
/// <param name="ScaleFactor">Physical pixels per logical unit.</param>
/// <param name="IsPrimary">Whether this is the primary display.</param>
/// <param name="OrientationDegrees">Optional clockwise orientation.</param>
/// <param name="RefreshRate">Optional refresh rate.</param>
public sealed record NeoDisplaySnapshot(string Id, NeoRect Bounds, NeoRect WorkArea, double ScaleFactor, bool IsPrimary, int? OrientationDegrees, double? RefreshRate)
{
    /// <summary>Gets the name of the display shown to a user, or <see langword="null"/> where the presenter tells none.</summary>
    public string? Name { get; init; }

    // A display of the native library in logical units, as the snapshots of the presenters have them.
    internal static NeoDisplaySnapshot FromDisplay(NeoDisplay display)
    {
        // On Windows a display counts in the units of the windows, the pixels of the display for an application aware of scaling.
        var scale = OperatingSystem.IsWindows() ? display.ScaleFactor : 1d;
        var id = display.Id.Length <= 128 && !display.Id.Any(char.IsControl) ? display.Id
            : "display:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(display.Id)))[..32];
        return new(id, Logical(display.Bounds), Logical(display.WorkArea), display.ScaleFactor is >= 0.25 and <= 16 ? display.ScaleFactor : 1, display.IsPrimary,
            display.RotationDegrees, display.RefreshRate is >= 1 and <= 1000 ? display.RefreshRate : null) { Name = display.Name };

        NeoRect Logical(NeoRect value) => scale == 1 ? value : new(
            (int)Math.Round(value.X / scale), (int)Math.Round(value.Y / scale),
            Math.Max(1, (int)Math.Round(value.Width / scale)), Math.Max(1, (int)Math.Round(value.Height / scale)));
    }

    /// <summary>Converts a logical point to physical pixels using this display scale.</summary>
    /// <exception cref="InvalidOperationException">The snapshot has an invalid scale.</exception>
    /// <exception cref="OverflowException">The converted coordinate exceeds the portable integer range.</exception>
    public NeoPoint ToPhysical(NeoPoint logical) { ValidateScale(); return new(checked((int)Math.Round(logical.X * ScaleFactor)), checked((int)Math.Round(logical.Y * ScaleFactor))); }
    /// <summary>Converts a physical point to logical desktop units.</summary>
    /// <exception cref="InvalidOperationException">The snapshot has an invalid scale.</exception>
    /// <exception cref="OverflowException">The converted coordinate exceeds the portable integer range.</exception>
    public NeoPoint ToLogical(NeoPoint physical) { ValidateScale(); return new(checked((int)Math.Round(physical.X / ScaleFactor)), checked((int)Math.Round(physical.Y / ScaleFactor))); }

    private void ValidateScale() { if (!double.IsFinite(ScaleFactor) || ScaleFactor is < 0.25 or > 16) throw new InvalidOperationException("The display scale factor is malformed."); }
}

/// <summary>Contains immutable app/OS/locale metadata without user paths or environment secrets.</summary>
/// <param name="ApplicationId">Explicit application identifier.</param>
/// <param name="ApplicationName">Display name.</param>
/// <param name="ApplicationVersion">Managed app version.</param>
/// <param name="OperatingSystem">OS description.</param>
/// <param name="Architecture">Process architecture.</param>
/// <param name="Backend">Desktop adapter backend.</param>
/// <param name="Locale">Current locale name.</param>
/// <param name="PreferredLanguages">Bounded preferred languages.</param>
public sealed record NeoApplicationMetadata(string ApplicationId, string ApplicationName, string ApplicationVersion, string OperatingSystem, string Architecture, string Backend, string Locale, IReadOnlyList<string> PreferredLanguages);

/// <summary>Provides truthful immutable system snapshots and coalesced UI-dispatched change events.</summary>
public sealed class NeoSystemInfoService : IDisposable
{
    private readonly NeoDispatcher? _dispatcher;
    private readonly NeoApplication? _application;
    private readonly object _sync = new();
    private Timer? _coalescingTimer;
    private readonly Timer? _platformTimer;
    private NeoThemeSnapshot? _pendingTheme;
    private int _refreshing;
    private bool _disposed;
    // Whether the displays come from the native library of the application, which reports their changes.
    private volatile bool _applicationDisplays;

    /// <summary>Initializes system information for an explicit application identity.</summary>
    /// <param name="applicationId">The application identifier.</param>
    /// <param name="applicationName">The name of the application.</param>
    /// <param name="applicationVersion">The version of the application.</param>
    /// <param name="dispatcher">
    /// The UI dispatcher of the application, which raises the change events, or <see langword="null"/>. With it, the displays are
    /// those of <see cref="NeoApplication.GetDisplays"/>, read on the UI thread and reported when the platform tells of a change;
    /// without it, or with a native library from before displays, they are read by the presenters of the platform every two seconds.
    /// </param>
    /// <exception cref="ArgumentException">The application metadata is malformed.</exception>
    public NeoSystemInfoService(string applicationId, string applicationName, string applicationVersion, NeoDispatcher? dispatcher = null)
        : this(applicationId, applicationName, applicationVersion, dispatcher, monitorPlatform: true)
    {
    }

    internal NeoSystemInfoService(string applicationId, string applicationName, string applicationVersion, NeoDispatcher? dispatcher, bool monitorPlatform)
    {
        NeoPluginMetadata.ValidateId(applicationId, nameof(applicationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationVersion);
        if (applicationName.Length > 256 || applicationName.Any(char.IsControl) || applicationVersion.Length > 64 || applicationVersion.Any(char.IsControl)) throw new ArgumentException("Application metadata is malformed.");
        _dispatcher = dispatcher;
        _application = monitorPlatform ? dispatcher?.Application : null;
        Theme = NeoSystemInfoPlatform.ReadInitialTheme();
        Displays = NeoSystemInfoPlatform.ReadInitialDisplays();
        var locale = CultureInfo.CurrentUICulture.Name;
        Metadata = new(applicationId, applicationName, applicationVersion, RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(), OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "cocoa" : OperatingSystem.IsLinux() ? "gtk" : "unknown", locale, Array.AsReadOnly(new[] { locale }));
        ThemeSupport = NeoSystemInfoPlatform.ThemeSupport;
        DisplaySupport = NeoSystemInfoPlatform.DisplaySupport;
        if (_application is not null)
        {
            try
            {
                if (_application.ReadWindowCapability(NeoWindowCapability.Displays) is { IsSupported: true } support) DisplaySupport = support;
                _application.DisplaysChanged += OnApplicationDisplaysChanged;
                Dispatch(ReadApplicationDisplays);
            }
            catch (ObjectDisposedException) { }
        }
        if (monitorPlatform && (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()))
            _platformTimer = new Timer(static state => _ = ((NeoSystemInfoService)state!).RefreshPlatformAsync(), this, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    /// <summary>Gets theme support details.</summary>
    public NeoCapabilityInfo ThemeSupport { get; }
    /// <summary>Gets display support details.</summary>
    public NeoCapabilityInfo DisplaySupport { get; }
    /// <summary>Gets the latest immutable theme snapshot.</summary>
    public NeoThemeSnapshot Theme { get; private set; }
    /// <summary>Gets immutable display snapshots in one virtual logical coordinate system.</summary>
    public IReadOnlyList<NeoDisplaySnapshot> Displays { get; private set; }
    /// <summary>Gets immutable app/OS/locale metadata.</summary>
    public NeoApplicationMetadata Metadata { get; }
    /// <summary>Occurs after coalescing on the configured UI dispatcher.</summary>
    public event EventHandler<NeoThemeSnapshot>? ThemeChanged;
    /// <summary>Occurs after a native presenter supplies a new immutable display topology.</summary>
    public event EventHandler<IReadOnlyList<NeoDisplaySnapshot>>? DisplaysChanged;

    /// <summary>Gets a standard path for backend use. Renderer disclosure still requires a scoped command.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="category"/> is invalid.</exception>
    /// <exception cref="DirectoryNotFoundException">The operating system did not report that standard directory.</exception>
    public string GetStandardDirectory(NeoStandardDirectory category)
    {
        if (!Enum.IsDefined(category)) throw new ArgumentOutOfRangeException(nameof(category));
        var path = category switch
        {
            NeoStandardDirectory.ApplicationData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            NeoStandardDirectory.Cache => OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"),
            NeoStandardDirectory.Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            NeoStandardDirectory.Downloads => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            NeoStandardDirectory.Temporary => Path.GetTempPath(),
            _ => throw new ArgumentOutOfRangeException(nameof(category)),
        };
        if (string.IsNullOrEmpty(path)) throw new DirectoryNotFoundException($"The {category} standard directory is unavailable.");
        return Path.GetFullPath(path);
    }

    /// <summary>Publishes a trusted adapter theme snapshot with a 50 ms coalescing window.</summary>
    public void PublishTheme(NeoThemeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Enum.IsDefined(snapshot.Theme) || snapshot.AccentColor is { } accent && (accent.Length != 7 || accent[0] != '#' || accent[1..].Any(static c => !Uri.IsHexDigit(c)))) throw new ArgumentException("A theme snapshot is malformed.", nameof(snapshot));
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pendingTheme = snapshot;
            _coalescingTimer ??= new Timer(static state => ((NeoSystemInfoService)state!).FlushTheme(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _coalescingTimer.Change(TimeSpan.FromMilliseconds(50), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Publishes a trusted, validated display snapshot.</summary>
    public void PublishDisplays(IEnumerable<NeoDisplaySnapshot> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);
        var values = displays.Take(65).ToArray();
        if (values.Length is < 1 or > 64 || values.Any(static value => value is null || string.IsNullOrEmpty(value.Id) || value.Id.Length > 128 || value.Id.Any(char.IsControl) || !double.IsFinite(value.ScaleFactor) || value.ScaleFactor is < 0.25 or > 16 || value.Bounds.Width <= 0 || value.Bounds.Height <= 0 || value.WorkArea.Width <= 0 || value.WorkArea.Height <= 0 || value.OrientationDegrees is not (null or 0 or 90 or 180 or 270) || value.RefreshRate is { } refresh && (!double.IsFinite(refresh) || refresh is < 1 or > 1000)) || values.Select(static value => value.Id).Distinct(StringComparer.Ordinal).Count() != values.Length || values.Count(static value => value.IsPrimary) != 1) throw new ArgumentException("Display topology is malformed.", nameof(displays));
        void Raise()
        {
            EventHandler<IReadOnlyList<NeoDisplaySnapshot>>? handler;
            lock (_sync) { if (_disposed) return; Displays = Array.AsReadOnly(values); handler = DisplaysChanged; }
            try { handler?.Invoke(this, Displays); } catch { }
        }
        Dispatch(Raise);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; _pendingTheme = null; _platformTimer?.Dispose(); _coalescingTimer?.Dispose(); _coalescingTimer = null; ThemeChanged = null; DisplaysChanged = null; }
        if (_application is not null) _application.DisplaysChanged -= OnApplicationDisplaysChanged;
    }

    // Runs on the UI thread, where the application reads its displays.
    private void ReadApplicationDisplays()
    {
        IReadOnlyList<NeoDisplay> displays;
        try { displays = _application!.GetDisplays(); }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException) { return; }
        // A native library from before displays tells none, and the presenters go on.
        if (displays.Count != 0) PublishApplicationDisplays(displays);
    }

    private void OnApplicationDisplaysChanged(object? sender, NeoDisplaysChangedEventArgs args)
    {
        if (args.Displays.Count != 0) PublishApplicationDisplays(args.Displays);
    }

    private void PublishApplicationDisplays(IReadOnlyList<NeoDisplay> displays)
    {
        var snapshots = displays.Select(NeoDisplaySnapshot.FromDisplay).ToArray();
        bool changed;
        lock (_sync)
        {
            if (_disposed) return;
            _applicationDisplays = true;
            changed = !snapshots.SequenceEqual(Displays);
        }
        if (changed) PublishDisplays(snapshots);
    }

    private async Task RefreshPlatformAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) != 0) return;
        try
        {
            var snapshot = await NeoSystemInfoPlatform.ReadAsync(readDisplays: !_applicationDisplays).ConfigureAwait(false);
            bool themeChanged;
            bool displaysChanged;
            lock (_sync)
            {
                if (_disposed) return;
                themeChanged = snapshot.Theme != Theme;
                displaysChanged = !_applicationDisplays && snapshot.Displays.Count != 0 && !snapshot.Displays.SequenceEqual(Displays);
            }
            if (themeChanged) PublishTheme(snapshot.Theme);
            if (displaysChanged) PublishDisplays(snapshot.Displays);
        }
        catch { }
        finally { Volatile.Write(ref _refreshing, 0); }
    }

    private void FlushTheme()
    {
        NeoThemeSnapshot? value;
        lock (_sync) { if (_disposed) return; value = _pendingTheme; _pendingTheme = null; }
        if (value is null) return;
        Dispatch(() =>
        {
            EventHandler<NeoThemeSnapshot>? handler;
            lock (_sync) { if (_disposed) return; Theme = value; handler = ThemeChanged; }
            try { handler?.Invoke(this, value); } catch { }
        });
    }

    private void Dispatch(Action action)
    {
        try { if (_dispatcher is not null && !_dispatcher.CheckAccess()) _dispatcher.Post(action); else action(); }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException) { }
    }
}
