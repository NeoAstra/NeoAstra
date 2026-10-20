// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using NeoAstra.Interop;
using NeoAstra.Interop.Generated;

namespace NeoAstra;

/// <summary>Describes a display, in the coordinates and units of <see cref="NeoWindow.Position"/> and <see cref="NeoWindow.ClientSize"/>.</summary>
/// <remarks>
/// <para>
/// The bounds and the work area are measured from the top-left corner of the primary display, with the vertical axis pointing
/// down, as the position of a window is. On macOS and with GTK they are in logical units. On Windows they are in the units that
/// the system gives the process: the pixels of the display for an application that declares itself aware of display scaling,
/// and logical units for one that leaves the scaling to the system. A window placed at the origin of a work area with a client
/// size taken from that work area is therefore on that display, whatever its scale.
/// </para>
/// <para>
/// <see cref="Id"/> names the display after a restart where the platform tells its monitor and its connection
/// (<see cref="IsIdPersistent"/>): the device path of the monitor on Windows, its vendor, model, and serial numbers on macOS, its
/// connector, manufacturer, and model with GTK. Two identical monitors that the platform does not tell apart are told apart by
/// their order, and their identifiers are then not persistent.
/// </para>
/// </remarks>
public sealed record NeoDisplay
{
    /// <summary>Initializes a display, as a test or a saved layout describes one.</summary>
    /// <param name="id">The identifier of the display.</param>
    /// <param name="name">The name of the display, shown to a user.</param>
    /// <param name="bounds">The bounds of the display.</param>
    /// <param name="workArea">The part of the bounds that the taskbar, the menu bar, the Dock, or the panels leave to windows.</param>
    /// <param name="scaleFactor">The pixels of the display for one logical unit.</param>
    /// <param name="isPrimary">Whether the display is the primary one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> or <paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A size is not positive, or <paramref name="scaleFactor"/> is not a positive finite number.</exception>
    public NeoDisplay(string id, string name, NeoRect bounds, NeoRect workArea, double scaleFactor, bool isPrimary)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(name);
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new ArgumentOutOfRangeException(nameof(bounds), "The bounds of a display must have a positive size.");
        if (workArea.Width <= 0 || workArea.Height <= 0) throw new ArgumentOutOfRangeException(nameof(workArea), "The work area of a display must have a positive size.");
        if (!double.IsFinite(scaleFactor) || scaleFactor <= 0) throw new ArgumentOutOfRangeException(nameof(scaleFactor), "The scale factor of a display must be a positive finite number.");
        Id = id;
        Name = name;
        Bounds = bounds;
        WorkArea = workArea;
        ScaleFactor = scaleFactor;
        IsPrimary = isPrimary;
    }

    /// <summary>Gets the identifier of the display, which <see cref="IsIdPersistent"/> tells whether it lasts after a restart.</summary>
    public string Id { get; }

    /// <summary>Gets the name of the display that the platform shows to a user, such as the model of a monitor.</summary>
    public string Name { get; }

    /// <summary>Gets the bounds of the display.</summary>
    public NeoRect Bounds { get; }

    /// <summary>Gets the part of <see cref="Bounds"/> that the taskbar, the menu bar, the Dock, or the panels leave to windows.</summary>
    /// <remarks>Where the platform tells none (<see cref="IsWorkAreaReported"/> is <see langword="false"/>, as with GTK on Wayland), it is <see cref="Bounds"/>.</remarks>
    public NeoRect WorkArea { get; }

    /// <summary>Gets how many pixels of the display make one logical unit, as <see cref="NeoWindow.ScaleFactor"/> reports it for a window on the display.</summary>
    /// <remarks>On Windows it is 1 for an application that leaves the scaling to the system, and GTK reports whole numbers.</remarks>
    public double ScaleFactor { get; }

    /// <summary>Gets whether this is the primary display, the one whose top-left corner window positions are measured from.</summary>
    /// <remarks>GTK tells no primary display: the one whose bounds hold the origin is taken for it, or else the first one.</remarks>
    public bool IsPrimary { get; }

    /// <summary>Gets the clockwise rotation of the display, 0, 90, 180, or 270 degrees, or <see langword="null"/> when the platform does not tell it (GTK).</summary>
    public int? RotationDegrees { get; init; }

    /// <summary>Gets the refresh rate of the display in hertz, or <see langword="null"/> when the platform does not tell it.</summary>
    public double? RefreshRate { get; init; }

    /// <summary>Gets whether <see cref="Id"/> comes from the monitor and its connection and names the display after a restart.</summary>
    public bool IsIdPersistent { get; init; }

    /// <summary>Gets whether the platform told the work area, rather than <see cref="WorkArea"/> being <see cref="Bounds"/>.</summary>
    public bool IsWorkAreaReported { get; init; }

    // NEOASTRA_DISPLAY_* of native/include/neoastra.h.
    private const uint Primary = 1u << 0, WorkAreaReported = 1u << 1, IdPersistent = 1u << 2, RotationReported = 1u << 3;

    internal static NativeMethods.neoastra_display_info_t CreateNative() => new(new NativeMethods.neoastra_display_info
    {
        size = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<NativeMethods.neoastra_display_info>(),
        version = 1,
    });

    internal static NeoDisplay FromNative(NativeMethods.neoastra_display_info_t native)
    {
        var value = native.Value;
        var bounds = new NeoRect(value.bounds.Value.x, value.bounds.Value.y, Math.Max(1, value.bounds.Value.width), Math.Max(1, value.bounds.Value.height));
        var area = new NeoRect(value.work_area.Value.x, value.work_area.Value.y, value.work_area.Value.width, value.work_area.Value.height);
        var id = Utf8String.Decode(value.id);
        return new NeoDisplay(
            string.IsNullOrEmpty(id) ? "display" : id,
            Utf8String.Decode(value.name),
            bounds,
            area.Width > 0 && area.Height > 0 ? area : bounds,
            double.IsFinite(value.scale_factor) && value.scale_factor > 0 ? value.scale_factor : 1d,
            (value.flags & Primary) != 0)
        {
            RotationDegrees = (value.flags & RotationReported) != 0 ? (int)(value.rotation_degrees % 360) : null,
            RefreshRate = double.IsFinite(value.refresh_rate) && value.refresh_rate > 0 ? value.refresh_rate : null,
            IsIdPersistent = (value.flags & IdPersistent) != 0,
            IsWorkAreaReported = (value.flags & WorkAreaReported) != 0,
        };
    }
}

/// <summary>Represents a point of a view in the CSS pixels of its main viewport, as a page reports <c>clientX</c> and <c>clientY</c>.</summary>
/// <param name="X">The horizontal coordinate, from the left edge of the viewport.</param>
/// <param name="Y">The vertical coordinate, from the top edge of the viewport.</param>
public readonly record struct NeoViewPoint(double X, double Y);

/// <summary>Identifies what a backend can do with displays, the placement of windows, and virtual desktops.</summary>
public enum NeoWindowCapability
{
    /// <summary>The displays (<see cref="NeoApplication.GetDisplays"/>) and the display of a window (<see cref="NeoWindow.Display"/>).</summary>
    Displays = 0,
    /// <summary>
    /// A position given to a window is where it goes, and where a window is on screen is known: <see cref="NeoWindow.MoveToDisplay(NeoDisplay)"/>,
    /// <see cref="NeoApplication.GetWindowAt(NeoPoint)"/>, and <see cref="NeoAstra.ClientToScreen(NeoViewPoint)"/> depend on it.
    /// </summary>
    Placement = 1,
    /// <summary>Whether a window is on the current virtual desktop (<see cref="NeoWindow.IsOnCurrentVirtualDesktop"/>) and the identifier of its desktop (<see cref="NeoWindow.VirtualDesktopId"/>).</summary>
    VirtualDesktops = 2,
    /// <summary>Moving a window to another virtual desktop (<see cref="NeoWindow.MoveToVirtualDesktop(string)"/>).</summary>
    VirtualDesktopMove = 3,
    /// <summary>Showing a window on every virtual desktop (<see cref="NeoWindow.IsOnAllVirtualDesktops"/>).</summary>
    AllVirtualDesktops = 4,
}

/// <summary>Tells what became of <see cref="NeoWindow.MoveToVirtualDesktop(string)"/>.</summary>
public enum NeoVirtualDesktopMoveResult
{
    /// <summary>The window is on the virtual desktop, also when it already was.</summary>
    Moved = 0,
    /// <summary>The platform cannot move a window to another virtual desktop, or the loaded native library cannot.</summary>
    NotSupported = 1,
    /// <summary>No virtual desktop has that identifier, as when the desktop was closed.</summary>
    DesktopNotFound = 2,
    /// <summary>The window is on no virtual desktop yet, as a window that was never shown, or the platform refused to move it.</summary>
    Failed = 3,
}

/// <summary>Provides the displays after they changed.</summary>
/// <param name="displays">The displays there are now, the primary one first.</param>
public sealed class NeoDisplaysChangedEventArgs(IReadOnlyList<NeoDisplay> displays) : EventArgs
{
    /// <summary>Gets the displays there are now, the primary one first.</summary>
    public IReadOnlyList<NeoDisplay> Displays { get; } = displays;
}

/// <summary>Provides the display that a window left and the one it is on now.</summary>
/// <param name="oldDisplay">The display the window was on, as it was then, or <see langword="null"/> when it was not known.</param>
/// <param name="newDisplay">The display the window is on now.</param>
public sealed class NeoWindowDisplayChangedEventArgs(NeoDisplay? oldDisplay, NeoDisplay newDisplay) : EventArgs
{
    /// <summary>Gets the display the window was on, as it was then, or <see langword="null"/> when it was not known.</summary>
    public NeoDisplay? OldDisplay { get; } = oldDisplay;
    /// <summary>Gets the display the window is on now.</summary>
    public NeoDisplay NewDisplay { get; } = newDisplay;
}
