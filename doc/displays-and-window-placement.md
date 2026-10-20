# Displays and window placement

NeoAstra tells which displays there are, which display holds a window, and where on screen a point of a page is, so that an
application with several windows can put each one on the display it belongs to, open a window at the pointer, and bring its
layout back after a restart. The members are on `NeoApplication`, `NeoWindow`, and the view (`NeoAstra`), and are called on the
UI thread, as the other window members are.

## Units and coordinates

A display is described in the coordinates and units of `NeoWindow.Position` and `NeoWindow.ClientSize`: measured from the
top-left corner of the primary display, with the vertical axis pointing down. A window put at the origin of the work area of a
display, with a size taken from that work area, is on that display and fits in it.

| Platform | Units of displays and windows | Primary display | Work area |
| --- | --- | --- | --- |
| Windows | The units that the system gives the process: the pixels of each display for an application that declares itself aware of display scaling, logical units for one that does not | The one Windows calls primary, at the origin | Reported: the display without the taskbar |
| macOS | Points (logical units) | The one with the menu bar | Reported: `NSScreen.visibleFrame`, without the menu bar and the Dock |
| GTK (X11) | Logical units | GTK tells none: the one at the origin, else the first | Reported by the X11 backend of GDK |
| GTK (Wayland) | Logical units | GTK tells none: the one at the origin, else the first | Not reported: the bounds |

`NeoDisplay.ScaleFactor` is the number of pixels of the display for one logical unit, as `NeoWindow.ScaleFactor` reports it for
a window on that display: on Windows it is 1 for an application that leaves the scaling to the system, and GTK reports whole
numbers. A size meant in logical units is multiplied by it where windows count in pixels, on Windows for an application aware of
display scaling. See [known limitations](known-limitations.md) for the units of `Position` and `ClientSize`.

## The displays

`NeoApplication.GetDisplays()` reads the displays, the primary one first. Each `NeoDisplay` has:

- `Id`, an identifier. Where `IsIdPersistent` is `true` it comes from the monitor and its connection and names the display after
  a restart: the device path of the monitor from the display configuration on Windows (`\\?\DISPLAY#...`), its vendor, model,
  and serial numbers on macOS (`cg:...`), its manufacturer, model, and connector with GTK (`gtk:...`). Two identical monitors
  that the platform does not tell apart get a suffix from their order, and are then not persistent. An identifier that is not
  persistent lasts for the session, as on Windows in a session without a display configuration.
- `Name`, what the platform shows a user: the friendly name of the monitor on Windows, `NSScreen.localizedName` on macOS, the
  description of the monitor with GTK.
- `Bounds` and `WorkArea`, and `IsWorkAreaReported`, which is `false` where the work area is the bounds.
- `ScaleFactor`, `IsPrimary`, `RotationDegrees` (`null` with GTK, which does not tell it), and `RefreshRate` in hertz (`null`
  when unknown).

`NeoApplication.DisplaysChanged` reports that a display came or went, or changed its bounds, work area, scale, name, or primary
role, with the displays there are then. The platform tells of the change: `WM_DISPLAYCHANGE`, `WM_SETTINGCHANGE` for the work
area, and `WM_DPICHANGED` on Windows, `NSApplicationDidChangeScreenParametersNotification` on macOS, and the list and the
properties of the GDK monitors with GTK. Nothing is polled.

## The display of a window

`NeoWindow.Display` is the display that holds most of the window, also while it is hidden or minimized: the one the platform says
the window is on (`MonitorFromWindow`, `NSWindow.screen`, the monitor of the GDK surface), or else the one its bounds cover most,
or else the primary one. `NeoWindow.DisplayChanged` reports the display it left and the one it is on, when the window is moved
there, when its scale changes, and when the display it was on is gone. With GTK a window that was never shown has no surface, and
its display is the one its bounds cover.

`NeoApplication.GetDisplayAt(point)` returns the display that holds a point, or else the one nearest to it, for example where a
drag ended: `PageDragEnded.ScreenPosition` and the points that a view converts are in the units of `Position`.

## Placing windows

A window is placed with its position and its client size:

- `NeoWindowOptions.X`, `Y`, `Width`, and `Height` with `StartupLocation = NeoWindowStartupLocation.Manual` create it there. On
  Windows a window created on a display has the scale of that display from the start.
- `NeoWindow.Bounds` sets the position and the client size in one request. Prefer it to `Position` followed by `ClientSize`: on
  Windows a window that a move takes to a display of another scale keeps the bounds it is given in the pixels of that display,
  where a `Position` alone keeps the size it had in pixels on the display it left.
- `NeoWindow.MoveToDisplay(display, bounds)` places it with a position relative to the top-left corner of the work area of the
  display, and `NeoWindow.MoveToDisplay(display)` moves it to the same place in the work area of another display with the same
  size in logical units, shrunk and moved to fit that work area.

The position is that of the frame. On Windows the frame of a resizable window has borders that are not drawn on its left, right,
and bottom sides, so a window put at the origin of a work area shows its visible edge a few pixels to the right of it;
`NeoWindow.FrameBounds` is what the window shows, and its offset from `Position` tells how far to move it. On macOS AppKit keeps
the title bar of a titled window below the menu bar.

GTK 4 cannot place a window and does not tell where it is: a position is kept but not applied, and on Wayland the compositor
places windows. `MoveToDisplay` throws `NotSupportedException` there, and
`NeoApplication.GetWindowCapability(NeoWindowCapability.Placement)` reports `NeoSupportLevel.None`.

## Tabs dragged out of a window

An application that tracks a tab drag with `setPointerCapture` in its page, rather than with HTML drag and drop, can follow the
pointer across its windows and displays:

- `NeoAstra.ClientToScreen(new NeoViewPoint(clientX, clientY))` converts a point of the page, in CSS pixels as a pointer event
  reports it, to the units of `Position`, counting in the bounds of the view in its window, the place of the window on screen, the
  scale of its display, and `ZoomFactor`. `ScreenToClient` converts back, exactly.
- `NeoApplication.GetWindowAt(point, excluded)` returns the front-most window of the application whose `FrameBounds` holds the
  point, among its visible windows that are not minimized and are on the current virtual desktop, and passes over `excluded`,
  for example a window that follows the pointer. The windows of other applications are not looked at.
- `NeoApplication.GetDisplayAt(point)` tells the display to open a new window on when no window is there.

Both conversions and `GetWindowAt` return `null` with GTK, which does not know where its windows are.

Whether a page keeps receiving `pointermove` with coordinates outside its window while a button is held after `setPointerCapture`
is up to the engine: WebView2 is expected to through the Win32 mouse capture, WKWebView through `mouseDragged`, and WebKitGTK
through the implicit grab of the press. None of the three has been verified by a test yet.

## Virtual desktops

| Member | Windows | macOS | GTK |
| --- | --- | --- | --- |
| `NeoWindow.IsOnCurrentVirtualDesktop` | `IVirtualDesktopManager` | `NSWindow.isOnActiveSpace` | `null` |
| `NeoWindow.VirtualDesktopId` | The GUID of the desktop, in braces, for a window that was shown | `null`: macOS has no public identifier of a Space | `null` |
| `NeoWindow.MoveToVirtualDesktop(id)` | Moves the window; `DesktopNotFound` for a desktop that is gone | `NotSupported` | `NotSupported` |
| `NeoWindow.IsOnAllVirtualDesktops` | `false`; assigning `true` throws `NotSupportedException` | `NSWindowCollectionBehaviorCanJoinAllSpaces` | `false`; assigning `true` throws |

Windows has no virtual desktops where the shell lacks `IVirtualDesktopManager`, as on Windows Server without Explorer;
`GetWindowCapability(NeoWindowCapability.VirtualDesktops)` then reports `None`. A window that was never shown is on no desktop
yet. GTK 4 tells nothing of workspaces, on X11 or Wayland.

## Capabilities

`NeoApplication.GetWindowCapability` reports, with details, what the backend does:

| Capability | Windows | macOS | GTK |
| --- | --- | --- | --- |
| `Displays` | Native | Native | Limited: no primary display, no rotation, no work area on Wayland |
| `Placement` | Native | Native | None |
| `VirtualDesktops` | Native, where the shell has them | Limited: whether a window is on the active Space | None |
| `VirtualDesktopMove` | Native, where the shell has them | None | None |
| `AllVirtualDesktops` | None | Native | None |

## With an older native library

With a native library from before displays (`NeoRuntimeInfo.BuildFeatures` without bit 3), `GetDisplays` returns an empty list,
`Display`, `GetDisplayAt`, `GetWindowAt`, `ClientToScreen`, `IsOnCurrentVirtualDesktop`, and `VirtualDesktopId` return `null`,
`MoveToVirtualDesktop` returns `NotSupported`, no change is reported, and every window capability is `None`.
