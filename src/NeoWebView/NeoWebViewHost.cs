// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoWebView;

/// <summary>Describes the borrowed native parent or owned window that hosts a browser view.</summary>
public sealed class NeoWebViewHost
{
    private NeoWebViewHost(NeoWindow? window, NeoNativeHandle? parent)
    {
        Window = window;
        Parent = parent;
    }

    /// <summary>Creates a host that keeps a view fitted to a NeoWebView-owned window.</summary>
    /// <param name="window">The owning window.</param>
    /// <returns>A window host descriptor.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="window"/> is <see langword="null"/>.</exception>
    public static NeoWebViewHost FillWindow(NeoWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return new NeoWebViewHost(window, null);
    }

    /// <summary>Creates a Windows host from a borrowed Win32 <c>HWND</c>.</summary>
    /// <param name="hwnd">A nonzero parent <c>HWND</c> valid on the UI thread for the view lifetime.</param>
    /// <returns>A native host descriptor.</returns>
    /// <exception cref="PlatformNotSupportedException">The current operating system is not Windows.</exception>
    /// <exception cref="ArgumentException"><paramref name="hwnd"/> is zero.</exception>
    public static NeoWebViewHost FromWin32Hwnd(nint hwnd)
    {
        EnsurePlatform(OperatingSystem.IsWindows(), "Windows");
        return FromNonZero(new NeoNativeHandle(NeoNativeHandleKind.Win32Hwnd, hwnd), nameof(hwnd));
    }

    /// <summary>Creates a macOS host from a borrowed Cocoa <c>NSView*</c>.</summary>
    /// <param name="nsView">A nonzero <c>NSView*</c> valid on the UI thread for the view lifetime.</param>
    /// <returns>A native host descriptor.</returns>
    /// <exception cref="PlatformNotSupportedException">The current operating system is not macOS.</exception>
    /// <exception cref="ArgumentException"><paramref name="nsView"/> is zero.</exception>
    public static NeoWebViewHost FromCocoaNSView(nint nsView)
    {
        EnsurePlatform(OperatingSystem.IsMacOS(), "macOS");
        return FromNonZero(new NeoNativeHandle(NeoNativeHandleKind.CocoaNSView, nsView), nameof(nsView));
    }

    /// <summary>Creates a Linux host from a borrowed GTK widget.</summary>
    /// <param name="gtkWidget">A nonzero <c>GtkWidget*</c> valid on the UI thread for the view lifetime.</param>
    /// <returns>A native host descriptor.</returns>
    /// <exception cref="PlatformNotSupportedException">The current operating system is not Linux.</exception>
    /// <exception cref="ArgumentException"><paramref name="gtkWidget"/> is zero.</exception>
    public static NeoWebViewHost FromGtkWidget(nint gtkWidget)
    {
        EnsurePlatform(OperatingSystem.IsLinux(), "Linux");
        return FromNonZero(new NeoNativeHandle(NeoNativeHandleKind.GtkWidget, gtkWidget), nameof(gtkWidget));
    }

    /// <summary>Creates a host from a typed borrowed native-parent handle.</summary>
    /// <param name="handle">A supported nonzero native parent valid on the UI thread for the view lifetime.</param>
    /// <returns>A native host descriptor.</returns>
    /// <exception cref="ArgumentException">The handle is zero.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The kind cannot be used as a view parent.</exception>
    /// <exception cref="PlatformNotSupportedException">The handle kind does not match the current operating system.</exception>
    public static NeoWebViewHost FromNativeParent(NeoNativeHandle handle)
    {
        return handle.Kind switch
        {
            NeoNativeHandleKind.Win32Hwnd => FromWin32Hwnd(handle.Value),
            NeoNativeHandleKind.CocoaNSView => FromCocoaNSView(handle.Value),
            NeoNativeHandleKind.GtkWidget => FromGtkWidget(handle.Value),
            _ => throw new ArgumentOutOfRangeException(nameof(handle), handle.Kind, "This native handle kind cannot host a view."),
        };
    }

    internal NeoWindow? Window { get; }

    internal NeoNativeHandle? Parent { get; }

    private static NeoWebViewHost FromNonZero(NeoNativeHandle handle, string parameterName)
    {
        if (handle.Value == 0)
        {
            throw new ArgumentException("A native parent handle must not be zero.", parameterName);
        }

        return new NeoWebViewHost(null, handle);
    }

    private static void EnsurePlatform(bool condition, string platform)
    {
        if (!condition)
        {
            throw new PlatformNotSupportedException($"This host kind is available only on {platform}.");
        }
    }
}
