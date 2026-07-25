// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoWebView;

/// <summary>Identifies a portable result returned by the native NeoWebView runtime.</summary>
public enum NeoErrorCode
{
    /// <summary>The operation succeeded.</summary>
    Success = 0,
    /// <summary>An unspecified failure occurred.</summary>
    Unknown = -1,
    /// <summary>An argument was invalid.</summary>
    InvalidArgument = -2,
    /// <summary>The object was not in a state that permits the operation.</summary>
    InvalidState = -3,
    /// <summary>The operation is unsupported by the active backend.</summary>
    NotSupported = -4,
    /// <summary>The requested component has not been initialized.</summary>
    NotInitialized = -5,
    /// <summary>The requested component was already initialized.</summary>
    AlreadyInitialized = -6,
    /// <summary>The operation was called from the wrong thread.</summary>
    WrongThread = -7,
    /// <summary>The operation was canceled.</summary>
    Canceled = -8,
    /// <summary>The operation timed out.</summary>
    TimedOut = -9,
    /// <summary>No platform backend is available.</summary>
    BackendUnavailable = -10,
    /// <summary>The platform web runtime is unavailable.</summary>
    RuntimeUnavailable = -11,
    /// <summary>The platform backend reported a native failure.</summary>
    NativeFailure = -12,
    /// <summary>The target object has been disposed.</summary>
    Disposed = -13,
    /// <summary>The operation was rejected for security reasons.</summary>
    Security = -14,
}

/// <summary>Controls when a standalone application exits.</summary>
public enum NeoApplicationShutdownMode
{
    /// <summary>The application exits only when <see cref="NeoApplication.Shutdown(int)"/> is called.</summary>
    Explicit = 0,
    /// <summary>The application exits after its last window closes.</summary>
    OnLastWindowClosed = 1,
    /// <summary>The application exits after <see cref="NeoApplication.MainWindow"/> closes.</summary>
    OnMainWindowClosed = 2,
}

/// <summary>Describes how a backend implements a capability.</summary>
public enum NeoSupportLevel
{
    /// <summary>The capability is unavailable.</summary>
    None = 0,
    /// <summary>The backend implements the capability natively.</summary>
    Native = 1,
    /// <summary>NeoWebView emulates the capability.</summary>
    Emulated = 2,
    /// <summary>The capability is available with documented limitations.</summary>
    Limited = 3,
}

/// <summary>Identifies a portable backend capability.</summary>
public enum NeoCapability
{
    /// <summary>Custom URI schemes.</summary>
    CustomScheme,
    /// <summary>Document-start script injection.</summary>
    ScriptDocumentStart,
    /// <summary>Document-end script injection.</summary>
    ScriptDocumentEnd,
    /// <summary>Isolated script worlds.</summary>
    ScriptIsolatedWorld,
    /// <summary>Script injection into all frames.</summary>
    ScriptAllFrames,
    /// <summary>Origin information on web messages.</summary>
    MessageOrigin,
    /// <summary>Messages from subframes.</summary>
    MessageSubframes,
    /// <summary>Named profiles.</summary>
    ProfileNamed,
    /// <summary>Ephemeral profiles.</summary>
    ProfileEphemeral,
    /// <summary>Cookie management.</summary>
    Cookies,
    /// <summary>Browsing-data clearing by time range.</summary>
    ClearDataByTime,
    /// <summary>Downloads.</summary>
    Downloads,
    /// <summary>Download pause and resume.</summary>
    DownloadPause,
    /// <summary>Permission decisions.</summary>
    Permissions,
    /// <summary>Persistent permission decisions.</summary>
    PermissionPersistence,
    /// <summary>Network observation.</summary>
    NetworkObservation,
    /// <summary>Network interception.</summary>
    NetworkInterception,
    /// <summary>Native print dialogs.</summary>
    PrintDialog,
    /// <summary>PDF printing.</summary>
    PrintPdf,
    /// <summary>Viewport capture.</summary>
    CaptureViewport,
    /// <summary>Full-page capture.</summary>
    CaptureFullPage,
    /// <summary>Developer tools.</summary>
    DevTools,
    /// <summary>Find in page.</summary>
    Find,
    /// <summary>Transparent view backgrounds.</summary>
    TransparentBackground,
    /// <summary>Composition hosting.</summary>
    Composition,
    /// <summary>Page zoom control.</summary>
    Zoom,
}

/// <summary>Identifies a backend-specific borrowed native handle.</summary>
public enum NeoNativeHandleKind
{
    /// <summary>A Win32 <c>HWND</c>.</summary>
    Win32Hwnd = 1,
    /// <summary>A Cocoa <c>NSWindow*</c>.</summary>
    CocoaNSWindow = 2,
    /// <summary>A Cocoa <c>NSView*</c>.</summary>
    CocoaNSView = 3,
    /// <summary>A GTK window widget.</summary>
    GtkWindow = 4,
    /// <summary>A GTK widget.</summary>
    GtkWidget = 5,
    /// <summary>A WebView2 controller.</summary>
    WebView2Controller = 6,
    /// <summary>A WebView2 core object.</summary>
    WebView2Core = 7,
    /// <summary>A <c>WKWebView*</c>.</summary>
    WkWebView = 8,
    /// <summary>A WebKitGTK web view.</summary>
    WebKitGtkWebView = 9,
}

/// <summary>Describes the presentation state of a window.</summary>
public enum NeoWindowState
{
    /// <summary>The normal restored state.</summary>
    Normal,
    /// <summary>The minimized state.</summary>
    Minimized,
    /// <summary>The maximized state.</summary>
    Maximized,
    /// <summary>The fullscreen state.</summary>
    Fullscreen,
}

/// <summary>Controls the initial placement of a new window.</summary>
public enum NeoWindowStartupLocation
{
    /// <summary>Use the supplied position.</summary>
    Manual,
    /// <summary>Allow the operating system to choose a position.</summary>
    Default,
    /// <summary>Center the window on its owner or the primary work area.</summary>
    Center,
}

/// <summary>Represents a tri-state backend option.</summary>
public enum NeoOptionState
{
    /// <summary>Use the backend default.</summary>
    Default,
    /// <summary>Enable the option.</summary>
    Enabled,
    /// <summary>Disable the option.</summary>
    Disabled,
}

/// <summary>Identifies a browser permission.</summary>
public enum NeoPermissionKind
{
    /// <summary>An unknown backend permission.</summary>
    Unknown,
    /// <summary>Geolocation access.</summary>
    Geolocation,
    /// <summary>Camera access.</summary>
    Camera,
    /// <summary>Microphone access.</summary>
    Microphone,
    /// <summary>Notification access.</summary>
    Notifications,
    /// <summary>Clipboard read access.</summary>
    ClipboardRead,
    /// <summary>Clipboard write access.</summary>
    ClipboardWrite,
    /// <summary>MIDI access.</summary>
    Midi,
    /// <summary>Screen capture access.</summary>
    ScreenCapture,
    /// <summary>Pointer-lock access.</summary>
    PointerLock,
    /// <summary>Local-font access.</summary>
    LocalFonts,
    /// <summary>File-system access.</summary>
    FileSystem,
    /// <summary>Persistent-storage access.</summary>
    PersistentStorage,
}

/// <summary>Identifies the portable category of a browser process failure.</summary>
public enum NeoProcessFailureKind
{
    /// <summary>The backend could not classify the failure.</summary>
    Unknown,
    /// <summary>A web-content or renderer process exited.</summary>
    WebProcessExited,
    /// <summary>The browser process exited.</summary>
    BrowserProcessExited,
    /// <summary>A web-content process became unresponsive.</summary>
    ProcessUnresponsive,
}

/// <summary>Identifies the recommended recovery after a browser process failure.</summary>
public enum NeoProcessRecoveryAction
{
    /// <summary>No portable recovery recommendation is available.</summary>
    None,
    /// <summary>Dispose and recreate the affected web view.</summary>
    RecreateView,
    /// <summary>Restart the application and its browser environment.</summary>
    RestartApplication,
}

/// <summary>Identifies categories of browser data.</summary>
[Flags]
public enum NeoBrowsingDataKinds : ulong
{
    /// <summary>No data.</summary>
    None = 0,
    /// <summary>Cookies.</summary>
    Cookies = 1UL << 0,
    /// <summary>HTTP and resource caches.</summary>
    Cache = 1UL << 1,
    /// <summary>Local storage.</summary>
    LocalStorage = 1UL << 2,
    /// <summary>IndexedDB data.</summary>
    IndexedDb = 1UL << 3,
    /// <summary>Service-worker data.</summary>
    ServiceWorkers = 1UL << 4,
    /// <summary>Stored permission decisions.</summary>
    Permissions = 1UL << 5,
    /// <summary>Download history.</summary>
    DownloadHistory = 1UL << 6,
    /// <summary>All browser data.</summary>
    All = ulong.MaxValue,
}

/// <summary>Describes the SameSite attribute of a cookie.</summary>
public enum NeoCookieSameSite
{
    /// <summary>No explicit SameSite value.</summary>
    Unspecified,
    /// <summary>SameSite=None.</summary>
    None,
    /// <summary>SameSite=Lax.</summary>
    Lax,
    /// <summary>SameSite=Strict.</summary>
    Strict,
}

/// <summary>Represents an integer point in logical units.</summary>
/// <param name="X">The horizontal coordinate.</param>
/// <param name="Y">The vertical coordinate.</param>
public readonly record struct NeoPoint(int X, int Y);

/// <summary>Represents an integer size in logical units.</summary>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
public readonly record struct NeoSize(int Width, int Height)
{
    /// <summary>Gets an empty size.</summary>
    public static NeoSize Empty => default;
}

/// <summary>Represents an integer rectangle in logical units.</summary>
/// <param name="X">The horizontal coordinate.</param>
/// <param name="Y">The vertical coordinate.</param>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
public readonly record struct NeoRect(int X, int Y, int Width, int Height)
{
    /// <summary>Gets the rectangle position.</summary>
    public NeoPoint Position => new(X, Y);
    /// <summary>Gets the rectangle size.</summary>
    public NeoSize Size => new(Width, Height);
}

/// <summary>Represents an sRGB color.</summary>
/// <param name="Red">The red component.</param>
/// <param name="Green">The green component.</param>
/// <param name="Blue">The blue component.</param>
/// <param name="Alpha">The alpha component.</param>
public readonly record struct NeoColor(byte Red, byte Green, byte Blue, byte Alpha = byte.MaxValue)
{
    /// <summary>Gets an opaque white color.</summary>
    public static NeoColor White => new(255, 255, 255);
    /// <summary>Gets a transparent color.</summary>
    public static NeoColor Transparent => new(0, 0, 0, 0);
}

/// <summary>Represents an optional inclusive browsing-data time range.</summary>
/// <param name="Start">The start instant, or <see langword="null"/> for no lower bound.</param>
/// <param name="End">The end instant, or <see langword="null"/> for no upper bound.</param>
public readonly record struct NeoTimeRange(DateTimeOffset? Start, DateTimeOffset? End)
{
    internal void Validate()
    {
        if (Start is not null && End is not null && Start > End)
        {
            throw new ArgumentException("The time-range start must not be later than its end.");
        }
    }
}

/// <summary>Contains immutable information about the loaded native backend.</summary>
/// <param name="BackendName">The backend name.</param>
/// <param name="BackendVersion">The backend version.</param>
/// <param name="BrowserVersion">The browser runtime version.</param>
/// <param name="OperatingSystem">The operating-system identifier.</param>
/// <param name="Architecture">The process architecture.</param>
/// <param name="BuildFeatures">Backend-defined build feature flags.</param>
/// <param name="IsDebugBuild">Whether the native library is a debug build.</param>
public sealed record NeoRuntimeInfo(string BackendName, string BackendVersion, string BrowserVersion, string OperatingSystem, string Architecture, ulong BuildFeatures, bool IsDebugBuild);

/// <summary>Contains support information for one portable capability.</summary>
/// <param name="SupportLevel">The support level.</param>
/// <param name="Version">The capability contract version.</param>
/// <param name="Flags">Backend-defined limitation flags.</param>
/// <param name="Details">Optional human-readable details.</param>
public sealed record NeoCapabilityInfo(NeoSupportLevel SupportLevel, uint Version, ulong Flags, string? Details)
{
    /// <summary>Gets whether some form of support is available.</summary>
    public bool IsSupported => SupportLevel != NeoSupportLevel.None;
}

/// <summary>
/// Represents a borrowed backend-specific handle. The value is valid only while its owning
/// NeoWebView object remains alive, must be used on the appropriate UI thread, and must not be released.
/// </summary>
/// <param name="Kind">The native handle kind.</param>
/// <param name="Value">The borrowed native value.</param>
public readonly record struct NeoNativeHandle(NeoNativeHandleKind Kind, nint Value)
{
    /// <summary>Returns this value as a Win32 <c>HWND</c>.</summary>
    /// <exception cref="PlatformNotSupportedException">The current operating system is not Windows.</exception>
    /// <exception cref="InvalidOperationException">This value does not contain a Win32 <c>HWND</c>.</exception>
    public nint GetWin32Hwnd() => Get(NeoNativeHandleKind.Win32Hwnd, OperatingSystem.IsWindows(), "Windows");

    /// <summary>Returns this value as a Cocoa <c>NSView*</c>.</summary>
    /// <exception cref="PlatformNotSupportedException">The current operating system is not macOS.</exception>
    /// <exception cref="InvalidOperationException">This value does not contain a Cocoa <c>NSView*</c>.</exception>
    public nint GetCocoaNSView() => Get(NeoNativeHandleKind.CocoaNSView, OperatingSystem.IsMacOS(), "macOS");

    /// <summary>Returns this value as a GTK widget.</summary>
    /// <exception cref="PlatformNotSupportedException">The current operating system is not Linux.</exception>
    /// <exception cref="InvalidOperationException">This value does not contain a GTK widget.</exception>
    public nint GetGtkWidget() => Get(NeoNativeHandleKind.GtkWidget, OperatingSystem.IsLinux(), "Linux");

    private nint Get(NeoNativeHandleKind expected, bool supportedPlatform, string platform)
    {
        if (!supportedPlatform)
        {
            throw new PlatformNotSupportedException($"This native handle is available only on {platform}.");
        }

        if (Kind != expected)
        {
            throw new InvalidOperationException($"The handle kind is {Kind}, not {expected}.");
        }

        return Value;
    }
}
