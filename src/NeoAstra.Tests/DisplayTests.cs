// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.InteropServices;
using NeoAstra.Desktop.WindowState;

namespace NeoAstra.Tests;

[TestClass]
public sealed class DisplayTests
{
    [TestMethod]
    public void Display_RejectsAMalformedDescription()
    {
        var bounds = new NeoRect(0, 0, 1920, 1080);
        Assert.ThrowsExactly<ArgumentNullException>(() => new NeoDisplay(null!, "Monitor", bounds, bounds, 1, true));
        Assert.ThrowsExactly<ArgumentException>(() => new NeoDisplay("", "Monitor", bounds, bounds, 1, true));
        Assert.ThrowsExactly<ArgumentNullException>(() => new NeoDisplay("id", null!, bounds, bounds, 1, true));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new NeoDisplay("id", "Monitor", bounds with { Width = 0 }, bounds, 1, true));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new NeoDisplay("id", "Monitor", bounds, bounds with { Height = -1 }, 1, true));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new NeoDisplay("id", "Monitor", bounds, bounds, double.NaN, true));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new NeoDisplay("id", "Monitor", bounds, bounds, 0, true));
        // Two descriptions of one display are equal, as two reads of the displays that did not change are.
        Assert.AreEqual(new NeoDisplay("id", "Monitor", bounds, bounds, 1.5, true) { RefreshRate = 60 }, new NeoDisplay("id", "Monitor", bounds, bounds, 1.5, true) { RefreshRate = 60 });
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Displays_AreTheMonitorsInTheUnitsOfWindows()
    {
        // A thread aware of display scaling counts in the pixels of each display, and one that is not in logical units.
        foreach (var aware in new[] { true, false })
        {
            await RunAsync(aware, application =>
            {
                var message = aware ? "aware" : "unaware";
                var displays = application.GetDisplays();
                Assert.IsTrue(displays.Count >= 1, message);
                Assert.AreEqual(1, displays.Count(static display => display.IsPrimary), message);
                Assert.IsTrue(displays[0].IsPrimary, message);
                Assert.AreEqual(new NeoPoint(0, 0), displays[0].Bounds.Position, message);
                Assert.AreEqual(displays.Count, displays.Select(static display => display.Id).Distinct(StringComparer.Ordinal).Count(), message);
                // The identifiers and the rest are those of a second read.
                CollectionAssert.AreEqual(displays.ToArray(), application.GetDisplays().ToArray(), message);

                var monitors = Monitors();
                Assert.AreEqual(monitors.Count, displays.Count, message);
                foreach (var display in displays)
                {
                    Assert.IsFalse(string.IsNullOrWhiteSpace(display.Name), message);
                    Assert.IsTrue(display.IsWorkAreaReported, message);
                    Assert.IsTrue(display.RotationDegrees is 0 or 90 or 180 or 270, message);
                    Assert.IsTrue(display.RefreshRate is null or > 1 and < 1000, message);
                    // What Win32 reports to the thread, which is what the bounds of a window are counted in.
                    Assert.IsTrue(monitors.Any(monitor => monitor.Bounds == display.Bounds && monitor.WorkArea == display.WorkArea), $"{display}, {message}");
                    Assert.IsTrue(display.WorkArea.X >= display.Bounds.X && display.WorkArea.Y >= display.Bounds.Y, message);
                    Assert.IsTrue(display.WorkArea.X + display.WorkArea.Width <= display.Bounds.X + display.Bounds.Width, message);
                    Assert.IsTrue(display.WorkArea.Y + display.WorkArea.Height <= display.Bounds.Y + display.Bounds.Height, message);
                    if (!aware) Assert.AreEqual(1d, display.ScaleFactor, message);
                    // The device path of the monitor names it after a restart, unless the session has no display configuration.
                    if (display.IsIdPersistent) StringAssert.StartsWith(display.Id, @"\\?\DISPLAY", message);
                }

                Assert.AreEqual(NeoSupportLevel.Native, application.GetWindowCapability(NeoWindowCapability.Displays).SupportLevel);
                Assert.AreEqual(NeoSupportLevel.Native, application.GetWindowCapability(NeoWindowCapability.Placement).SupportLevel);
                Assert.AreEqual(NeoSupportLevel.None, application.GetWindowCapability(NeoWindowCapability.AllVirtualDesktops).SupportLevel);
                Assert.IsFalse(string.IsNullOrEmpty(application.GetWindowCapability(NeoWindowCapability.VirtualDesktops).Details));
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => application.GetWindowCapability((NeoWindowCapability)99));
                return Task.CompletedTask;
            });
        }
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Window_PlacedOnTheWorkAreaOfADisplayIsOnThatDisplay()
    {
        foreach (var aware in new[] { true, false })
        {
            await RunAsync(aware, async application =>
            {
                foreach (var display in application.GetDisplays())
                {
                    var message = $"{display.Name}, {(aware ? "aware" : "unaware")}";
                    var area = display.WorkArea;
                    var size = new NeoSize(Math.Min(640, area.Width - 100), Math.Min(400, area.Height - 100));
                    await using var window = application.CreateWindow(new NeoWindowOptions { IsVisible = false, StartupLocation = NeoWindowStartupLocation.Manual, X = area.X, Y = area.Y, Width = size.Width, Height = size.Height });
                    // The units of a display are those of the window: it is where it was put, at the size it was given.
                    Assert.AreEqual(new NeoRect(area.X, area.Y, size.Width, size.Height), window.Bounds, message);
                    Assert.AreEqual(display.Id, window.Display?.Id, message);
                    Assert.AreEqual(display.ScaleFactor, window.ScaleFactor, message);
                    var frame = window.FrameBounds;
                    var center = new NeoPoint(frame.X + frame.Width / 2, frame.Y + frame.Height / 2);
                    Assert.AreEqual(application.GetDisplayAt(center), window.Display, message);

                    // A position relative to the work area of a display.
                    window.MoveToDisplay(display, new NeoRect(50, 40, size.Width - 10, size.Height - 10));
                    Assert.AreEqual(new NeoRect(area.X + 50, area.Y + 40, size.Width - 10, size.Height - 10), window.Bounds, message);
                    Assert.AreEqual(display.Id, window.Display?.Id, message);
                    // A window moved onto the display it is on stays where it is.
                    var bounds = window.Bounds;
                    window.MoveToDisplay(display);
                    Assert.AreEqual(bounds, window.Bounds, message);
                    // A hidden window is on no point.
                    Assert.IsNull(application.GetWindowAt(center), message);
                }

                // A point far from every display is on the nearest one.
                Assert.IsNotNull(application.GetDisplayAt(new NeoPoint(-1_000_000, -1_000_000)));
            });
        }
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Window_MovedToAnotherDisplayReportsIt()
    {
        foreach (var aware in new[] { true, false })
        {
            await RunAsync(aware, async application =>
            {
                var displays = application.GetDisplays();
                if (displays.Count < 2) Assert.Inconclusive("This machine has one display.");
                var first = displays[0];
                var second = displays[1];
                await using var window = application.CreateWindow(new NeoWindowOptions { IsVisible = false, StartupLocation = NeoWindowStartupLocation.Manual, X = first.WorkArea.X + 20, Y = first.WorkArea.Y + 20, Width = 500, Height = 300 });
                Assert.AreEqual(first.Id, window.Display?.Id);
                var changes = new List<NeoWindowDisplayChangedEventArgs>();
                window.DisplayChanged += (_, args) => changes.Add(args);
                window.MoveToDisplay(second);
                Assert.AreEqual(second.Id, window.Display?.Id);
                Assert.AreEqual(1, changes.Count);
                Assert.AreEqual(first.Id, changes[0].OldDisplay?.Id);
                Assert.AreEqual(second.Id, changes[0].NewDisplay.Id);
                // The size in logical units is kept: in the pixels of the display, it follows its scale.
                var ratio = aware ? second.ScaleFactor / first.ScaleFactor : 1d;
                Assert.AreEqual((int)Math.Round(500 * ratio), window.ClientSize.Width, 2);
                window.Bounds = new NeoRect(first.WorkArea.X + 30, first.WorkArea.Y + 30, 520, 310);
                Assert.AreEqual(first.Id, window.Display?.Id);
                Assert.AreEqual(2, changes.Count);
            });
        }
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Window_PlacedOnADisplayOfAnotherScaleKeepsTheBoundsItWasGiven()
    {
        // Windows used to scale a window that a move took to a display of another scale, after it had taken the requested size.
        await RunAsync(aware: true, async application =>
        {
            var displays = application.GetDisplays();
            var pair = (from left in displays from right in displays where left.ScaleFactor != right.ScaleFactor select (From: left, To: right)).FirstOrDefault();
            if (pair.From is null) Assert.Inconclusive("This machine has no two displays of different scales.");
            await using var window = application.CreateWindow(new NeoWindowOptions { IsVisible = false, StartupLocation = NeoWindowStartupLocation.Manual, X = pair.From.WorkArea.X + 20, Y = pair.From.WorkArea.Y + 20, Width = 500, Height = 300 });
            Assert.AreEqual(pair.From.ScaleFactor, window.ScaleFactor);
            var requested = new NeoRect(pair.To.WorkArea.X + 40, pair.To.WorkArea.Y + 30, 700, 450);
            window.Bounds = requested;
            Assert.AreEqual(pair.To.ScaleFactor, window.ScaleFactor);
            Assert.AreEqual(requested, window.Bounds);
            Assert.AreEqual(pair.To.Id, window.Display?.Id);
            // And back, with the position alone: the window keeps its size in pixels.
            window.Position = new NeoPoint(pair.From.WorkArea.X + 20, pair.From.WorkArea.Y + 20);
            Assert.AreEqual(new NeoRect(pair.From.WorkArea.X + 20, pair.From.WorkArea.Y + 20, 700, 450), window.Bounds);
            // A move to the work area of a display keeps the size in logical units.
            window.MoveToDisplay(pair.To);
            Assert.AreEqual((int)Math.Round(700 * pair.To.ScaleFactor / pair.From.ScaleFactor), window.ClientSize.Width, 2);
            await Task.CompletedTask;
        });
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Windows_AtAPointAreFoundFrontFirst()
    {
        await RunAsync(aware: true, async application =>
        {
            var area = application.GetDisplays()[0].WorkArea;
            var options = new NeoWindowOptions { IsVisible = false, StartupLocation = NeoWindowStartupLocation.Manual, X = area.X + 100, Y = area.Y + 100, Width = 300, Height = 200, ShowInTaskbar = false };
            await using var back = application.CreateWindow(options);
            await using var front = application.CreateWindow(options);
            front.IsAlwaysOnTop = true;
            back.Show();
            front.Show();
            var frame = front.FrameBounds;
            var inside = new NeoPoint(frame.X + frame.Width / 2, frame.Y + frame.Height / 2);
            Assert.AreSame(front, application.GetWindowAt(inside));
            Assert.AreSame(back, application.GetWindowAt(inside, front));
            Assert.IsNull(application.GetWindowAt(new NeoPoint(frame.X - 10, frame.Y + frame.Height / 2)));
            // The frame is what the window shows: its first point is in the window and the one before is not.
            Assert.AreSame(front, application.GetWindowAt(frame.Position));
            Assert.IsNull(application.GetWindowAt(new NeoPoint(frame.X + frame.Width, frame.Y)));
            back.Hide();
            Assert.IsNull(application.GetWindowAt(inside, front));
            await Task.CompletedTask;
        });
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Window_TellsItsVirtualDesktop()
    {
        await RunAsync(aware: true, async application =>
        {
            var area = application.GetDisplays()[0].WorkArea;
            await using var window = application.CreateWindow(new NeoWindowOptions { IsVisible = false, StartupLocation = NeoWindowStartupLocation.Manual, X = area.X + 100, Y = area.Y + 100, Width = 300, Height = 200, ShowInTaskbar = true });
            Assert.IsFalse(window.IsOnAllVirtualDesktops);
            Assert.ThrowsExactly<NotSupportedException>(() => window.IsOnAllVirtualDesktops = true);
            window.IsOnAllVirtualDesktops = false;
            Assert.ThrowsExactly<ArgumentException>(() => window.MoveToVirtualDesktop(" "));
            if (!application.GetWindowCapability(NeoWindowCapability.VirtualDesktops).IsSupported)
            {
                Assert.IsNull(window.IsOnCurrentVirtualDesktop);
                Assert.AreEqual(NeoVirtualDesktopMoveResult.NotSupported, window.MoveToVirtualDesktop("{8B5BE8B5-1E2C-4B0A-9E6C-3B8E7C6A1D20}"));
                Assert.Inconclusive("The shell of this machine has no virtual desktops.");
            }

            window.Show();
            Assert.IsTrue(window.IsOnCurrentVirtualDesktop);
            var desktop = window.VirtualDesktopId;
            Assert.IsNotNull(desktop);
            Assert.IsTrue(Guid.TryParseExact(desktop, "B", out var parsed) && parsed != Guid.Empty, desktop);
            // A window that is already on the desktop stays there, and a desktop that does not exist is not found.
            Assert.AreEqual(NeoVirtualDesktopMoveResult.Moved, window.MoveToVirtualDesktop(desktop));
            Assert.AreEqual(NeoVirtualDesktopMoveResult.DesktopNotFound, window.MoveToVirtualDesktop("{8B5BE8B5-1E2C-4B0A-9E6C-3B8E7C6A1D20}"));
            Assert.AreEqual(NeoVirtualDesktopMoveResult.DesktopNotFound, window.MoveToVirtualDesktop("not a desktop"));
            Assert.AreEqual(desktop, window.VirtualDesktopId);
            await Task.CompletedTask;
        });
    }

    [TestMethod]
    [Timeout(120000)]
    public async Task View_PointsConvertToTheScreenAndBack()
    {
        foreach (var aware in new[] { true, false })
        {
            await LiveBrowser.RunAsync(new Dictionary<string, string> { ["index.html"] = "<!doctype html><body style='margin:0'>points</body>" }, async session =>
            {
                await session.NavigateAsync("index.html");
                var message = aware ? "aware" : "unaware";
                var window = session.Window;
                var view = session.View;
                var hwnd = window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).GetWin32Hwnd();
                var origin = new NativePoint();
                Assert.IsTrue(ClientToScreen(hwnd, ref origin), message);
                // The view fills the client area, whose corner is where Win32 places it, in the units of the window.
                Assert.AreEqual(new NeoPoint(origin.X, origin.Y), view.ClientToScreen(new NeoViewPoint(0, 0)), message);
                var frame = window.FrameBounds;
                Assert.IsTrue(origin.X >= frame.X && origin.Y >= frame.Y && origin.X + window.ClientSize.Width <= frame.X + frame.Width && origin.Y + window.ClientSize.Height <= frame.Y + frame.Height, message);
                var scale = window.ScaleFactor;
                foreach (var zoom in new[] { 1d, 1.5d })
                {
                    view.ZoomFactor = zoom;
                    // A CSS pixel is the zoom times the scale of the display in the units of the window.
                    var converted = view.ClientToScreen(new NeoViewPoint(100, 50));
                    Assert.AreEqual(new NeoPoint(origin.X + (int)Math.Round(100 * zoom * scale), origin.Y + (int)Math.Round(50 * zoom * scale)), converted, $"{message}, zoom {zoom}");
                    // A screen point comes back exactly, and a point of the view within the size of a unit.
                    foreach (var screen in new[] { new NeoPoint(origin.X, origin.Y), new NeoPoint(origin.X + 37, origin.Y + 91), new NeoPoint(origin.X - 400, origin.Y + 5000) })
                    {
                        var client = view.ScreenToClient(screen);
                        Assert.IsNotNull(client, message);
                        Assert.AreEqual(screen, view.ClientToScreen(client.Value), $"{message}, zoom {zoom}, {screen}");
                    }

                    var back = view.ScreenToClient(converted!.Value)!.Value;
                    Assert.AreEqual(100, back.X, 1 / (zoom * scale), message);
                    Assert.AreEqual(50, back.Y, 1 / (zoom * scale), message);
                }

                view.ZoomFactor = 1;
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => view.ClientToScreen(new NeoViewPoint(double.NaN, 0)));
                // The point under the view is in its window, on its display.
                var center = view.ClientToScreen(new NeoViewPoint(window.ClientSize.Width / scale / 2, window.ClientSize.Height / scale / 2))!.Value;
                Assert.AreSame(window, session.Application.GetWindowAt(center), message);
                Assert.AreEqual(window.Display, session.Application.GetDisplayAt(center), message);
            }, perMonitorDpiAware: aware);
        }
    }

    [TestMethod]
    public async Task Placement_ComesBackOnTheDisplayItNames()
    {
        var left = new NeoDisplay("left", "Left", new NeoRect(0, 0, 1920, 1080), new NeoRect(0, 0, 1920, 1040), 1, true);
        var right = new NeoDisplay("right", "Right", new NeoRect(1920, 0, 2560, 1440), new NeoRect(1920, 0, 2560, 1400), 1, false);
        var saved = new NeoWindowPlacement(new NeoRect(2100, 200, 800, 600), NeoWindowState.Normal, "right", 1, true) { DisplayWorkArea = right.WorkArea };

        // The displays are as they were: the window is where it was.
        var same = NeoWindowStateRestore.Clamp(saved, [left, right], restoreMinimized: false, countsInDisplayPixels: false);
        Assert.AreEqual(saved.NormalBounds, same.NormalBounds);
        Assert.AreEqual("right", same.DisplayId);
        Assert.AreEqual(right.WorkArea, same.DisplayWorkArea);

        // The right display is now on the left of the other one: the window keeps its place on it.
        var rearranged = new NeoDisplay("right", "Right", new NeoRect(-2560, 0, 2560, 1440), new NeoRect(-2560, 0, 2560, 1400), 1, false);
        var followed = NeoWindowStateRestore.Clamp(saved, [left, rearranged], restoreMinimized: false, countsInDisplayPixels: false);
        Assert.AreEqual(new NeoRect(-2560 + 180, 200, 800, 600), followed.NormalBounds);
        Assert.AreEqual("right", followed.DisplayId);
        Assert.AreEqual(rearranged.WorkArea, followed.DisplayWorkArea);

        // Its scale doubled, in a window counted in pixels: the place and the size follow the scale.
        var scaled = new NeoDisplay("right", "Right", new NeoRect(1920, 0, 5120, 2880), new NeoRect(1920, 0, 5120, 2800), 2, false);
        var doubled = NeoWindowStateRestore.Clamp(saved, [left, scaled], restoreMinimized: false, countsInDisplayPixels: true);
        Assert.AreEqual(new NeoRect(1920 + 360, 400, 1600, 1200), doubled.NormalBounds);
        Assert.AreEqual(2d, doubled.DisplayScaleFactor);

        // The display is gone: the window comes back whole on the one it covers most, else the primary one.
        var gone = NeoWindowStateRestore.Clamp(saved, [left], restoreMinimized: false, countsInDisplayPixels: false);
        Assert.AreEqual("left", gone.DisplayId);
        Assert.AreEqual(new NeoRect(1920 - 800, 200, 800, 600), gone.NormalBounds);
        Assert.AreEqual(left.WorkArea, gone.DisplayWorkArea);

        // A placement saved before work areas were saved keeps its place on the desktop.
        var older = NeoWindowStateRestore.Clamp(saved with { DisplayWorkArea = null }, [left, rearranged], restoreMinimized: false, countsInDisplayPixels: false);
        Assert.AreEqual("right", older.DisplayId);
        Assert.AreEqual(rearranged.WorkArea.X + rearranged.WorkArea.Width - 800, older.NormalBounds.X);
        Assert.ThrowsExactly<ArgumentException>(() => NeoWindowStateRestore.Clamp(saved, [left, null!]));

        // A store keeps the work area, and refuses one without area.
        var root = Path.Combine(Path.GetTempPath(), "neoastra-tests", "placement-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new NeoJsonWindowStateStore(root);
            await store.SaveAsync("main", saved);
            Assert.AreEqual(saved, await store.LoadAsync("main"));
            await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await store.SaveAsync("main", saved with { DisplayWorkArea = new NeoRect(0, 0, 0, 10) }));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task WindowState_SavesTheDisplayOfTheWindowAndComesBackOnIt()
    {
        foreach (var aware in new[] { true, false })
        {
            var root = Path.Combine(Path.GetTempPath(), "neoastra-tests", "placement-" + Guid.NewGuid().ToString("N"));
            try
            {
                await RunAsync(aware, async application =>
                {
                    var message = aware ? "aware" : "unaware";
                    var store = new NeoJsonWindowStateStore(root);
                    var display = application.GetDisplays()[^1];
                    var area = display.WorkArea;
                    var bounds = new NeoRect(area.X + 60, area.Y + 50, Math.Min(700, area.Width - 200), Math.Min(450, area.Height - 200));
                    await using (var window = application.CreateWindow(new NeoWindowOptions { IsVisible = false, StartupLocation = NeoWindowStartupLocation.Manual, X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height }))
                    {
                        await using var controller = new NeoWindowStateController(window, store, "main", TimeSpan.FromMilliseconds(50));
                    }

                    var saved = await store.LoadAsync("main");
                    Assert.IsNotNull(saved, message);
                    Assert.AreEqual(display.Id, saved.DisplayId, message);
                    Assert.AreEqual(display.WorkArea, saved.DisplayWorkArea, message);
                    Assert.AreEqual(bounds, saved.NormalBounds, message);

                    // What the next launch does: the window comes back on that display, where it was.
                    await using (var window = application.CreateWindow(new NeoWindowOptions { IsVisible = false, Width = 400, Height = 300 }))
                    {
                        await using var controller = new NeoWindowStateController(window, store, "main", TimeSpan.FromMilliseconds(50));
                        var restored = await controller.RestoreAsync();
                        Assert.IsNotNull(restored, message);
                        Assert.AreEqual(bounds, window.Bounds, message);
                        Assert.AreEqual(display.Id, window.Display?.Id, message);
                        Assert.AreEqual(display.Id, restored.DisplayId, message);
                    }
                });
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }
    }

    // Runs a body in an application of its own on a UI thread of its own, aware of display scaling or not.
    private static async Task RunAsync(bool aware, Func<NeoApplication, Task> body)
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("The display tests read Win32 to compare; macOS and GTK are covered by the native ABI test.");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                if (aware) SetThreadDpiAwarenessContext(-4);
                NeoApplication.Run(new NeoApplicationOptions { ApplicationName = "NeoAstra display test", QueueInitialLaunchEvent = false, ShutdownMode = NeoApplicationShutdownMode.Explicit }, async application =>
                {
                    try { await body(application); }
                    finally { application.Shutdown(0); }
                });
                completion.TrySetResult();
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true, Name = "NeoAstra display test" };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(50)); }
        catch (NeoAstraNativeLibraryException) { Assert.Inconclusive("The native runtime is not staged."); }
    }

    private static List<(NeoRect Bounds, NeoRect WorkArea)> Monitors()
    {
        var values = new List<(NeoRect, NeoRect)>();
        EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfoW(monitor, ref info)) values.Add((info.Monitor.ToRect(), info.Work.ToRect()));
            return true;
        }, 0);
        return values;
    }

    private delegate bool MonitorEnumProc(nint monitor, nint deviceContext, nint clip, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left, Top, Right, Bottom;
        internal readonly NeoRect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal uint Size;
        internal NativeRect Monitor;
        internal NativeRect Work;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint deviceContext, nint clip, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint window, ref NativePoint point);
}
