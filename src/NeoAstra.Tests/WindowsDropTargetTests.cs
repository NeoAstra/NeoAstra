// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace NeoAstra.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class WindowsDropTargetTests
{
    private const int DropEffectCopy = 1;
    private const int DropEffectMove = 2;
    private const short ClipboardFormatUnicodeText = 13;
    private const short ClipboardFormatHDrop = 15;
    private const string CustomType = "application/x-neoastra-test";

    [TestMethod]
    [Timeout(60000)]
    public async Task DragOfACustomTypeReachesThePageAndExternalFilesStayWithTheHost()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This regression drives the OLE drop target of a WebView2 view.");
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stage = "startup";
        var root = Path.Combine(Path.GetTempPath(), "neoastra-drop-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var thread = new Thread(() =>
        {
            try
            {
                NeoApplication.Run(new NeoApplicationOptions { ApplicationName = "NeoAstra drop target test", QueueInitialLaunchEvent = false, ShutdownMode = NeoApplicationShutdownMode.Explicit }, async application =>
                {
                    try
                    {
                        var window = application.CreateWindow(new NeoWindowOptions { Label = "drop-window", Title = "Drop", Width = 480, Height = 320 });
                        window.Show();
                        stage = "browser creation";
                        await using var environment = await application.CreateEnvironmentAsync(new NeoEnvironmentOptions
                        {
                            UserDataRoot = TestUserData.Shared,
                            CustomSchemes = [NeoCustomScheme.Application("app", new DropPageProvider())],
                        });
                        await using var view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), new NeoAstraOptions { ViewLabel = "drop-view" });
                        var hostDrops = new ConcurrentQueue<(int Kind, string[] Values)>();
                        view.NativeDropReceived += (kind, values, _) => hostDrops.Enqueue((kind, values.ToArray()));
                        await view.NavigateAsync(new Uri("app://neoastra/index.html"));
                        await WaitAsync(view, "globalThis.ready === true", timeout.Token);

                        var parent = window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value;
                        var controllerWindow = FindWindowEx(parent, 0, "Chrome_WidgetWin_0", null);
                        var targetPointer = GetProp(controllerWindow, "NeoAstra.DropTarget");
                        Assert.AreNotEqual(0, targetPointer, "The WebView controller child must own NeoAstra's OLE drop target.");
                        // What OLE calls for a drag over the view: NeoAstra's target, which replaced WebView2's.
                        var dropTarget = (IOleDropTarget)Marshal.GetObjectForIUnknown(targetPointer);
                        var center = JsonDocument.Parse(await view.EvaluateScriptAsync(
                            "(() => { const r = document.getElementById('target').getBoundingClientRect(); return [(r.left + r.width / 2) * devicePixelRatio, (r.top + r.height / 2) * devicePixelRatio]; })()", timeout.Token) ?? throw new AssertFailedException("The page gave no position.")).RootElement;
                        var point = new Point { X = (int)Math.Round(center[0].GetDouble()), Y = (int)Math.Round(center[1].GetDouble()) };
                        Assert.IsTrue(ClientToScreen(parent, ref point));
                        // OLE hands the point of its drag loop in physical pixels, whatever the DPI awareness of the target.
                        LogicalToPhysicalPointForPerMonitorDPI(parent, ref point);

                        stage = "page drag of a custom type";
                        var custom = new TestDataObject();
                        custom.Add("Chromium Web Custom MIME Data Format", CustomDataPickle((CustomType, "space-42")));
                        var effect = DropEffectCopy | DropEffectMove;
                        Assert.AreEqual(0, dropTarget.DragEnter(custom, 0, point, ref effect));
                        Assert.AreNotEqual(0, effect, "A drag NeoAstra does not take belongs to the page and must not be refused.");
                        effect = DropEffectCopy | DropEffectMove;
                        Assert.AreEqual(0, dropTarget.DragOver(0, point, ref effect));
                        effect = DropEffectCopy | DropEffectMove;
                        Assert.AreEqual(0, dropTarget.Drop(custom, 0, point, ref effect));
                        Assert.AreNotEqual(0, effect);
                        await WaitAsync(view, "globalThis.events.includes('drop:space-42')", timeout.Token);
                        StringAssert.Contains(await view.EvaluateScriptAsync("globalThis.events.join(',')", timeout.Token), "enter:" + CustomType);
                        Assert.IsTrue(hostDrops.IsEmpty, "A page drag must not reach the host as an inbound drop.");

                        stage = "external file drop";
                        var file = Path.Combine(root, "drop.txt");
                        await File.WriteAllTextAsync(file, "drop");
                        var files = new TestDataObject();
                        files.Add(ClipboardFormatHDrop, DropFiles(file));
                        effect = DropEffectCopy | DropEffectMove;
                        Assert.AreEqual(0, dropTarget.DragEnter(files, 0, point, ref effect));
                        Assert.AreEqual(DropEffectCopy, effect);
                        effect = DropEffectCopy | DropEffectMove;
                        Assert.AreEqual(0, dropTarget.Drop(files, 0, point, ref effect));
                        Assert.AreEqual(DropEffectCopy, effect);
                        await WaitUntilAsync(() => !hostDrops.IsEmpty, timeout.Token);
                        Assert.IsTrue(hostDrops.TryDequeue(out var drop));
                        Assert.AreEqual(2, drop.Kind);
                        CollectionAssert.AreEqual(new[] { Path.GetFullPath(file) }, drop.Values);
                        await Task.Delay(200, timeout.Token);
                        Assert.AreEqual("1", await view.EvaluateScriptAsync("globalThis.events.filter(value => value.startsWith('drop:')).length", timeout.Token), "An external file drop stays with the host.");

                        // The mark of a page drag of the application, without the nonce of a drag that runs: anyone can
                        // write that format, so it must not turn an external text drop into a drop of the page.
                        stage = "external text with the format of a page drag";
                        var forged = new TestDataObject();
                        forged.Add("NeoAstra Page Drag", Encoding.Unicode.GetBytes("{6B29FC40-CA47-1067-B31D-00DD010662DA}\0"));
                        forged.Add(ClipboardFormatUnicodeText, Encoding.Unicode.GetBytes("forged\0"));
                        effect = DropEffectCopy | DropEffectMove;
                        Assert.AreEqual(0, dropTarget.DragEnter(forged, 0, point, ref effect));
                        Assert.AreEqual(DropEffectCopy, effect);
                        effect = DropEffectCopy | DropEffectMove;
                        Assert.AreEqual(0, dropTarget.Drop(forged, 0, point, ref effect));
                        Assert.AreEqual(DropEffectCopy, effect);
                        await WaitUntilAsync(() => !hostDrops.IsEmpty, timeout.Token);
                        Assert.IsTrue(hostDrops.TryDequeue(out drop));
                        Assert.AreEqual(0, drop.Kind);
                        CollectionAssert.AreEqual(new[] { "forged" }, drop.Values);
                        await Task.Delay(200, timeout.Token);
                        Assert.AreEqual("1", await view.EvaluateScriptAsync("globalThis.events.filter(value => value.startsWith('drop:')).length", timeout.Token), "An external text drop stays with the host.");

                        // What the native library tells the host when a drag of the page ends: the view that took the drop is
                        // named by the address of its native view, which a view that is gone no longer has.
                        stage = "end of a page drag";
                        var endings = new List<NeoPageDragEndedEventArgs>();
                        view.PageDragEnded += (_, args) => endings.Add(args);
                        const ulong PageDragEnded = (1UL << 63) | (3UL << 56);
                        view.RaisePageDragEnded(PageDragEnded | 2 | (2 << 4), view.NativeViewIdentity, new NeoPoint(10, 20), """{"items":[{"mimeType":"application/x-neoastra-test","data":"tab-7"}],"dragOperationsMask":16}""");
                        view.RaisePageDragEnded(PageDragEnded | 2 | (1 << 4), 8, new NeoPoint(0, 0), "{}");
                        view.RaisePageDragEnded(PageDragEnded, 0, new NeoPoint(-5, 7), "{}");
                        Assert.AreEqual(3, endings.Count);
                        Assert.AreEqual(NeoPageDragOutcome.DroppedOnView, endings[0].Outcome);
                        Assert.AreSame(view, endings[0].TargetView);
                        Assert.AreEqual(NeoDropEffect.Move, endings[0].Effect);
                        Assert.AreEqual(new NeoPoint(10, 20), endings[0].ScreenPosition);
                        CollectionAssert.AreEqual(new[] { new NeoPageDragItem(CustomType, "tab-7") }, endings[0].Items.ToArray());
                        Assert.IsNull(endings[1].TargetView);
                        Assert.AreEqual(NeoDropEffect.Copy, endings[1].Effect);
                        Assert.AreEqual(NeoPageDragOutcome.Canceled, endings[2].Outcome);
                        Assert.AreEqual(NeoDropEffect.None, endings[2].Effect);
                        Assert.AreEqual(new NeoPoint(-5, 7), endings[2].ScreenPosition);
                        Marshal.ReleaseComObject(dropTarget);
                    }
                    finally { application.ForceShutdown(); }
                });
                completion.TrySetResult();
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(50)); }
        catch (TimeoutException) { Assert.Fail($"The drop target test hung during {stage}."); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { Assert.Fail($"The drop target test timed out during {stage}."); }
        catch (NeoAstraNativeLibraryException) { Assert.Inconclusive("The Windows native runtime is not staged."); }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    // A real in-page drag needs the real mouse: WebView2's drag loop reads the state of the physical button. The test
    // takes the pointer of the desktop for about a second, so it runs only when NEOASTRA_TEST_REAL_INPUT is 1.
    [TestMethod]
    [Timeout(60000)]
    public async Task RealMouseDragOfADraggableElementReachesTheElementItIsDroppedOn()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("NEOASTRA_TEST_REAL_INPUT") != "1")
        {
            Assert.Inconclusive("Set NEOASTRA_TEST_REAL_INPUT=1 to let this test drive the mouse of a Windows desktop.");
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stage = "startup";
        string? inconclusive = null;
        var userData = Path.Combine(Path.GetTempPath(), "neoastra-real-drag-" + Guid.NewGuid().ToString("N"));
        var logged = new ConcurrentQueue<NeoLogMessage>();
        var thread = new Thread(() =>
        {
            try
            {
                // Screen, client, and page coordinates all in device pixels.
                SetThreadDpiAwarenessContext(-4);
                NeoApplication.Run(new NeoApplicationOptions { ApplicationName = "NeoAstra real drag test", QueueInitialLaunchEvent = false, ShutdownMode = NeoApplicationShutdownMode.Explicit, LogCallback = logged.Enqueue }, async application =>
                {
                    GetCursorPos(out var original);
                    try
                    {
                        var window = application.CreateWindow(new NeoWindowOptions { Label = "drag-window", Title = "Drag", Width = 960, Height = 540 });
                        window.Show();
                        var parent = window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value;
                        SetWindowPos(parent, -1, 0, 0, 0, 0, 0x0001 | 0x0002); // Topmost, so that the pointer lands on it.
                        SetForegroundWindow(parent);
                        stage = "browser creation";
                        // A browser of its own: the ones of the other tests serve windows that are not DPI aware.
                        await using var environment = await application.CreateEnvironmentAsync(new NeoEnvironmentOptions
                        {
                            UserDataRoot = userData,
                            CustomSchemes = [NeoCustomScheme.Application("app", new DragPageProvider())],
                        });
                        await using var view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), new NeoAstraOptions { ViewLabel = "drag-view" });
                        var hostDrops = new ConcurrentQueue<int>();
                        view.NativeDropReceived += (kind, _, _) => hostDrops.Enqueue(kind);
                        var ended = new ConcurrentQueue<NeoPageDragEndedEventArgs>();
                        view.PageDragEnded += (_, args) => ended.Enqueue(args);
                        await view.NavigateAsync(new Uri("app://neoastra/index.html"));
                        await WaitAsync(view, "globalThis.ready === true", timeout.Token);
                        var source = await ScreenCenterAsync(view, parent, "source", timeout.Token);
                        var target = await ScreenCenterAsync(view, parent, "target", timeout.Token);
                        if (!IsWithin(parent, source) || !IsWithin(parent, target))
                        {
                            GetWindowRect(parent, out var bounds);
                            var hit = WindowFromPoint(source);
                            var name = new System.Text.StringBuilder(256);
                            GetClassName(hit, name, name.Capacity);
                            inconclusive = $"Another window covers the test window; the mouse was left alone (source {source.X},{source.Y}, target {target.X},{target.Y}, window {bounds.Left},{bounds.Top}-{bounds.Right},{bounds.Bottom}, hit {hit:X} {name} root {GetAncestor(hit, 2):X} parent {parent:X}).";
                            return;
                        }

                        stage = "real mouse drag";
                        var drag = await DriveRealDragAsync(application, source, target, 30);
                        if (drag.Interrupted is not null)
                        {
                            inconclusive = drag.Interrupted;
                            return;
                        }
                        Assert.IsTrue(drag.Started, "The drag of the page did not start.");
                        Assert.IsTrue(drag.DispatchedWhileHeld, "The UI thread ran no work posted to the application while the drag of the page was held still.");
                        Assert.IsTrue(drag.UiTicksWhileHeld >= 4, $"The continuations of the UI thread ran {drag.UiTicksWhileHeld} times in the half second that the drag of the page was held still.");
                        for (var index = 0; index < 150 && await view.EvaluateScriptAsync("globalThis.events.some(value => value.startsWith('drop:'))", timeout.Token) != "true"; index++)
                            await Task.Delay(20, timeout.Token);
                        Assert.AreEqual("true", await view.EvaluateScriptAsync("globalThis.events.some(value => value.startsWith('drop:'))", timeout.Token),
                            "The page saw no drop: " + await view.EvaluateScriptAsync("globalThis.trace.join(' ') + ' lastOver ' + globalThis.lastOver + '(' + globalThis.lastOverAt + ') ratio ' + globalThis.ratio + ' target ' + JSON.stringify(document.getElementById('target').getBoundingClientRect())", timeout.Token) + " host drops: " + hostDrops.Count + " log: " + string.Join(" | ", logged.Select(static value => value.Message)));
                        Assert.AreEqual("true", await view.EvaluateScriptAsync("globalThis.events.includes('drop:payload|tab-7|<b>Tab</b>')", timeout.Token),
                            "The drop lacks data of the drag: " + await view.EvaluateScriptAsync("globalThis.events.join(',')", timeout.Token));
                        StringAssert.Contains(await view.EvaluateScriptAsync("globalThis.events.join(',')", timeout.Token), "enter:");
                        Assert.IsTrue(hostDrops.IsEmpty, "An in-page drag that carries text must not reach the host as an inbound drop.");
                        await WaitUntilAsync(() => !ended.IsEmpty, timeout.Token);
                        Assert.IsTrue(ended.TryDequeue(out var end));
                        Assert.AreEqual(NeoPageDragOutcome.DroppedOnView, end.Outcome);
                        Assert.AreSame(view, end.TargetView);
                        Assert.AreEqual(NeoDropEffect.Move, end.Effect);
                        CollectionAssert.Contains(end.Items.ToArray(), new NeoPageDragItem("application/x-neoastra-test", "tab-7"));
                    }
                    finally
                    {
                        SetCursorPos(original.X, original.Y);
                        application.ForceShutdown();
                    }
                });
                completion.TrySetResult();
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(50)); }
        catch (TimeoutException) { Assert.Fail($"The real drag test hung during {stage}."); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { Assert.Fail($"The real drag test timed out during {stage}: the page saw no drop."); }
        catch (NeoAstraNativeLibraryException) { Assert.Inconclusive("The Windows native runtime is not staged."); }
        finally { try { Directory.Delete(userData, recursive: true); } catch { } }
        if (inconclusive is not null) Assert.Inconclusive(inconclusive);
    }

    // A page that captures the pointer, as one that drags a tab out of its window does, keeps hearing of it outside the window
    // while the button is held, and NeoAstra.ClientToScreen gives back where the pointer is. Takes the real mouse, as the test
    // above, from a thread of its own, and always releases the button.
    [TestMethod]
    [Timeout(60000)]
    public async Task RealMousePointerCaptureReportsPointsOutsideTheWindow()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("NEOASTRA_TEST_REAL_INPUT") != "1")
        {
            Assert.Inconclusive("Set NEOASTRA_TEST_REAL_INPUT=1 to let this test drive the mouse of a Windows desktop.");
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stage = "startup";
        string? inconclusive = null;
        var userData = Path.Combine(Path.GetTempPath(), "neoastra-real-capture-" + Guid.NewGuid().ToString("N"));
        var thread = new Thread(() =>
        {
            try
            {
                // Screen, client, and page coordinates all in device pixels.
                SetThreadDpiAwarenessContext(-4);
                NeoApplication.Run(new NeoApplicationOptions { ApplicationName = "NeoAstra real capture test", QueueInitialLaunchEvent = false, ShutdownMode = NeoApplicationShutdownMode.Explicit }, async application =>
                {
                    GetCursorPos(out var original);
                    try
                    {
                        var window = application.CreateWindow(new NeoWindowOptions { Label = "capture-window", Title = "Capture", Width = 640, Height = 400 });
                        window.Show();
                        var parent = window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value;
                        SetWindowPos(parent, -1, 0, 0, 0, 0, 0x0001 | 0x0002); // Topmost, so that the pointer lands on it.
                        SetForegroundWindow(parent);
                        stage = "browser creation";
                        await using var environment = await application.CreateEnvironmentAsync(new NeoEnvironmentOptions
                        {
                            UserDataRoot = userData,
                            CustomSchemes = [NeoCustomScheme.Application("app", new CapturePageProvider())],
                        });
                        await using var view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), new NeoAstraOptions { ViewLabel = "capture-view" });
                        await view.NavigateAsync(new Uri("app://neoastra/index.html"));
                        await WaitAsync(view, "globalThis.ready === true", timeout.Token);
                        var source = await ScreenCenterAsync(view, parent, "source", timeout.Token);
                        if (!IsWithin(parent, source))
                        {
                            inconclusive = "Another window covers the test window; the mouse was left alone.";
                            return;
                        }

                        // A point beside the window, on the display it is on.
                        var frame = window.FrameBounds;
                        var display = application.GetDisplayAt(new NeoPoint(source.X, source.Y))!.Bounds;
                        var x = frame.X + frame.Width + 150 < display.X + display.Width ? frame.X + frame.Width + 150 : frame.X - 150;
                        if (x < display.X)
                        {
                            inconclusive = "The display has no room beside the test window.";
                            return;
                        }

                        var target = new Point { X = x, Y = source.Y };
                        stage = "real pointer capture";
                        var interrupted = await DrivePointerAsync(source, target, 20);
                        if (interrupted is not null)
                        {
                            inconclusive = interrupted;
                            return;
                        }

                        await WaitAsync(view, "globalThis.released === true", timeout.Token);
                        // The result of a script is given as JSON already.
                        var last = JsonDocument.Parse(await view.EvaluateScriptAsync("globalThis.moves.at(-1) ?? null", timeout.Token) ?? "null").RootElement;
                        Assert.AreEqual(JsonValueKind.Array, last.ValueKind, "The page heard of no move of the captured pointer.");
                        var css = new NeoViewPoint(last[0].GetDouble(), last[1].GetDouble());
                        Assert.IsTrue(css.X < 0 || css.X > window.ClientSize.Width / window.ScaleFactor, $"The last move the page heard of, {css}, is in its window.");
                        // The point of the page is where the pointer was on screen.
                        var screen = view.ClientToScreen(css);
                        Assert.IsNotNull(screen);
                        Assert.AreEqual(target.X, screen.Value.X, 2d, $"{css} is {screen} on screen, the pointer was at {target.X},{target.Y}.");
                        Assert.AreEqual(target.Y, screen.Value.Y, 2d, $"{css} is {screen} on screen, the pointer was at {target.X},{target.Y}.");
                        Assert.IsNull(application.GetWindowAt(screen.Value));
                    }
                    finally
                    {
                        SetCursorPos(original.X, original.Y);
                        application.ForceShutdown();
                    }
                });
                completion.TrySetResult();
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(50)); }
        catch (TimeoutException) { Assert.Fail($"The real capture test hung during {stage}."); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { Assert.Fail($"The real capture test timed out during {stage}."); }
        catch (NeoAstraNativeLibraryException) { Assert.Inconclusive("The Windows native runtime is not staged."); }
        finally { try { Directory.Delete(userData, recursive: true); } catch { } }
        if (inconclusive is not null) Assert.Inconclusive(inconclusive);
    }

    // Presses the button at a point, moves to another one, and releases it, from a thread of its own; null, or why it let go.
    private static Task<string?> DrivePointerAsync(Point source, Point target, int steps) => Task.Factory.StartNew<string?>(() =>
    {
        SetThreadDpiAwarenessContext(-4);
        var pressed = false;
        try
        {
            MoveMouse(source);
            Thread.Sleep(100);
            SendButton(0x0002);
            pressed = true;
            Thread.Sleep(100);
            for (var step = 1; step <= steps; step++)
            {
                var next = new Point { X = source.X + (target.X - source.X) * step / steps, Y = source.Y + (target.Y - source.Y) * step / steps };
                MoveMouse(next);
                Thread.Sleep(20);
                // Someone else moves the mouse: the test lets go at once rather than fight them.
                if (GetCursorPos(out var now) && (Math.Abs(now.X - next.X) > 2 || Math.Abs(now.Y - next.Y) > 2)) return "The mouse was moved by someone else during the capture; the test let go.";
            }

            Thread.Sleep(200);
            return null;
        }
        finally
        {
            if (pressed) SendButton(0x0004);
        }
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private sealed class CapturePageProvider : INeoResourceProvider
    {
        public NeoResourceResponse GetResponse(NeoResourceRequest request) => NeoResourceResponse.FromBytes("""
            <!doctype html><title>real capture</title>
            <style>body { margin: 0; user-select: none; } #source { position: absolute; left: 40px; top: 60px; width: 120px; height: 60px; background: #ddd; touch-action: none; }</style>
            <div id="source">Tab</div>
            <script>
            globalThis.moves = [];
            const source = document.getElementById('source');
            source.addEventListener('pointerdown', event => { source.setPointerCapture(event.pointerId); globalThis.captured = true; });
            source.addEventListener('pointermove', event => { if (globalThis.captured) globalThis.moves.push([event.clientX, event.clientY]); });
            source.addEventListener('pointerup', () => { globalThis.released = true; });
            globalThis.ready = true;
            </script>
            """u8.ToArray(), "text/html; charset=utf-8");
    }

    // A drag of a page dropped on the view of another window: that page gets the drop with the data of the drag, its text
    // and HTML included, rather than its host an external text drop. Takes the real mouse, as the test above.
    [TestMethod]
    [Timeout(60000)]
    public async Task RealMouseDragOfADraggableElementReachesThePageOfAnotherWindow()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("NEOASTRA_TEST_REAL_INPUT") != "1")
        {
            Assert.Inconclusive("Set NEOASTRA_TEST_REAL_INPUT=1 to let this test drive the mouse of a Windows desktop.");
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stage = "startup";
        string? inconclusive = null;
        var userData = Path.Combine(Path.GetTempPath(), "neoastra-real-drag-" + Guid.NewGuid().ToString("N"));
        var thread = new Thread(() =>
        {
            try
            {
                // Screen, client, and page coordinates all in device pixels.
                SetThreadDpiAwarenessContext(-4);
                NeoApplication.Run(new NeoApplicationOptions { ApplicationName = "NeoAstra cross-window drag test", QueueInitialLaunchEvent = false, ShutdownMode = NeoApplicationShutdownMode.Explicit }, async application =>
                {
                    GetCursorPos(out var original);
                    try
                    {
                        var sourceWindow = application.CreateWindow(new NeoWindowOptions { Label = "source-window", Title = "Drag source", X = 20, Y = 80, Width = 800, Height = 400, StartupLocation = NeoWindowStartupLocation.Manual });
                        var targetWindow = application.CreateWindow(new NeoWindowOptions { Label = "target-window", Title = "Drag target", X = 860, Y = 80, Width = 800, Height = 400, StartupLocation = NeoWindowStartupLocation.Manual });
                        var sourceParent = sourceWindow.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value;
                        var targetParent = targetWindow.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value;
                        foreach (var window in new[] { sourceWindow, targetWindow }) window.Show();
                        foreach (var parent in new[] { sourceParent, targetParent }) SetWindowPos(parent, -1, 0, 0, 0, 0, 0x0001 | 0x0002); // Topmost.
                        SetForegroundWindow(sourceParent);
                        stage = "browser creation";
                        await using var environment = await application.CreateEnvironmentAsync(new NeoEnvironmentOptions
                        {
                            UserDataRoot = userData,
                            CustomSchemes = [NeoCustomScheme.Application("app", new DragPageProvider())],
                        });
                        await using var sourceView = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(sourceWindow), new NeoAstraOptions { ViewLabel = "source-view" });
                        await using var targetView = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(targetWindow), new NeoAstraOptions { ViewLabel = "target-view" });
                        var hostDrops = new ConcurrentQueue<int>();
                        sourceView.NativeDropReceived += (kind, _, _) => hostDrops.Enqueue(kind);
                        targetView.NativeDropReceived += (kind, _, _) => hostDrops.Enqueue(kind);
                        var ended = new ConcurrentQueue<NeoPageDragEndedEventArgs>();
                        sourceView.PageDragEnded += (_, args) => ended.Enqueue(args);
                        var targetEnded = 0;
                        targetView.PageDragEnded += (_, _) => Interlocked.Increment(ref targetEnded);
                        foreach (var view in new[] { sourceView, targetView })
                        {
                            await view.NavigateAsync(new Uri("app://neoastra/index.html"));
                            await WaitAsync(view, "globalThis.ready === true", timeout.Token);
                        }
                        var source = await ScreenCenterAsync(sourceView, sourceParent, "source", timeout.Token);
                        var target = await ScreenCenterAsync(targetView, targetParent, "target", timeout.Token);
                        if (!IsWithin(sourceParent, source) || !IsWithin(targetParent, target))
                        {
                            inconclusive = $"Another window covers the test windows; the mouse was left alone (source {source.X},{source.Y}, target {target.X},{target.Y}).";
                            return;
                        }

                        stage = "real mouse drag to another window";
                        var drag = await DriveRealDragAsync(application, source, target, 40);
                        if (drag.Interrupted is not null)
                        {
                            inconclusive = drag.Interrupted;
                            return;
                        }
                        Assert.IsTrue(drag.Started, "The drag of the page did not start.");
                        Assert.IsTrue(drag.DispatchedWhileHeld, "The UI thread ran no work posted to the application while the drag of the page was held still.");
                        Assert.IsTrue(drag.UiTicksWhileHeld >= 4, $"The continuations of the UI thread ran {drag.UiTicksWhileHeld} times in the half second that the drag of the page was held still.");
                        for (var index = 0; index < 150 && await targetView.EvaluateScriptAsync("globalThis.events.some(value => value.startsWith('drop:'))", timeout.Token) != "true"; index++)
                            await Task.Delay(20, timeout.Token);
                        Assert.AreEqual("true", await targetView.EvaluateScriptAsync("globalThis.events.some(value => value.startsWith('drop:'))", timeout.Token),
                            "The page of the other window saw no drop: " + await targetView.EvaluateScriptAsync("globalThis.trace.join(' ')", timeout.Token) + " source: " + await sourceView.EvaluateScriptAsync("globalThis.trace.join(' ')", timeout.Token) + " host drops: " + hostDrops.Count);
                        // Compared in the page: the JSON of a script result escapes '<'.
                        Assert.AreEqual("true", await targetView.EvaluateScriptAsync("globalThis.events.includes('drop:payload|tab-7|<b>Tab</b>')", timeout.Token),
                            "The drop lacks data of the drag: " + await targetView.EvaluateScriptAsync("globalThis.events.join(',')", timeout.Token));
                        StringAssert.Contains(await targetView.EvaluateScriptAsync("globalThis.events.join(',')", timeout.Token), "text/html");
                        // The drag ends in its own page, which saw no drop of its own.
                        for (var index = 0; index < 100 && await sourceView.EvaluateScriptAsync("globalThis.trace.some(value => value.startsWith('dragend'))", timeout.Token) != "true"; index++)
                            await Task.Delay(20, timeout.Token);
                        Assert.AreEqual("true", await sourceView.EvaluateScriptAsync("globalThis.trace.some(value => value.startsWith('dragend'))", timeout.Token), "The source page saw no dragend.");
                        Assert.AreEqual("false", await sourceView.EvaluateScriptAsync("globalThis.events.some(value => value.startsWith('drop:'))", timeout.Token));
                        Assert.IsTrue(hostDrops.IsEmpty, "A drag of a page of the application must not reach the host as an inbound drop.");
                        // The host of the source learns where the drag went, which its dragend cannot tell.
                        await WaitUntilAsync(() => !ended.IsEmpty, timeout.Token);
                        Assert.IsTrue(ended.TryDequeue(out var end));
                        Assert.AreEqual(NeoPageDragOutcome.DroppedOnView, end.Outcome);
                        Assert.AreSame(targetView, end.TargetView);
                        Assert.AreEqual(NeoDropEffect.Move, end.Effect);
                        Assert.AreEqual(0, Volatile.Read(ref targetEnded), "The view that took the drop did not start a drag.");
                        Assert.IsTrue(Math.Abs(end.ScreenPosition.X - target.X) <= 4 && Math.Abs(end.ScreenPosition.Y - target.Y) <= 4, $"The drag ended at {end.ScreenPosition.X},{end.ScreenPosition.Y}, not at {target.X},{target.Y}.");
                    }
                    finally
                    {
                        SetCursorPos(original.X, original.Y);
                        application.ForceShutdown();
                    }
                });
                completion.TrySetResult();
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(50)); }
        catch (TimeoutException) { Assert.Fail($"The cross-window drag test hung during {stage}."); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { Assert.Fail($"The cross-window drag test timed out during {stage}."); }
        catch (NeoAstraNativeLibraryException) { Assert.Inconclusive("The Windows native runtime is not staged."); }
        finally { try { Directory.Delete(userData, recursive: true); } catch { } }
        if (inconclusive is not null) Assert.Inconclusive(inconclusive);
    }

    private static async Task<Point> ScreenCenterAsync(global::NeoAstra.NeoAstra view, nint parent, string id, CancellationToken cancellationToken)
    {
        var center = JsonDocument.Parse(await view.EvaluateScriptAsync(
            $"(() => {{ const r = document.getElementById('{id}').getBoundingClientRect(); return [(r.left + r.width / 2) * devicePixelRatio, (r.top + r.height / 2) * devicePixelRatio]; }})()", cancellationToken) ?? throw new AssertFailedException("The page gave no position.")).RootElement;
        var point = new Point { X = (int)Math.Round(center[0].GetDouble()), Y = (int)Math.Round(center[1].GetDouble()) };
        Assert.IsTrue(ClientToScreen(parent, ref point));
        return point;
    }

    private sealed class RealDrag
    {
        // Why the test let go of the mouse before the drag was done, or null.
        public string? Interrupted { get; set; }
        // Whether the drag loop of OLE took the capture of the UI thread, that is, the drag of the page started.
        public bool Started { get; set; }
        // Whether the UI thread ran work posted to the application while the drag was held still.
        public bool DispatchedWhileHeld { get; set; }
        // How many continuations of the UI thread ran while the drag was held still.
        public int UiTicksWhileHeld { get; set; }
    }

    // Drives the real mouse from a thread of its own, never from the UI thread of the application: that thread runs the
    // drag loop of the page while the button is held, and the test must not depend on it to let the button go. The
    // button is released in every case, so that a drag that goes wrong never leaves the desktop with the button held.
    // The drag goes just past the threshold of a drag, then is held still for half a second from the moment the drag
    // loop runs: no move of the pointer reaches that loop, which then waits for one. Meanwhile the UI thread keeps
    // awaiting short delays, as an application does, and the test counts them: its work must go on during the drag.
    private static async Task<RealDrag> DriveRealDragAsync(NeoApplication application, Point source, Point target, int steps)
    {
        var uiThread = GetCurrentThreadId();
        var ticks = 0;
        var ticking = 1;
        var ticker = TickAsync();
        try
        {
            return await Drive();
        }
        finally
        {
            Volatile.Write(ref ticking, 0);
            await ticker;
        }

        async Task TickAsync()
        {
            while (Volatile.Read(ref ticking) == 1)
            {
                await Task.Delay(10);
                Interlocked.Increment(ref ticks);
            }
        }

        Task<RealDrag> Drive() => Task.Factory.StartNew(() =>
        {
            // The points are in device pixels, as for the UI thread of the test.
            SetThreadDpiAwarenessContext(-4);
            var drag = new RealDrag();
            var pressed = false;
            try
            {
                MoveMouse(source);
                Thread.Sleep(100);
                SendButton(0x0002);
                pressed = true;
                var dispatched = 0;
                for (var step = 1; step <= steps + 5; step++)
                {
                    var next = step <= steps ? new Point { X = source.X + (target.X - source.X) * step / steps, Y = source.Y + (target.Y - source.Y) * step / steps } : target;
                    MoveMouse(next);
                    Thread.Sleep(step <= steps ? 15 : 30);
                    // Someone else moves the mouse: the test lets go at once rather than fight them.
                    if (GetCursorPos(out var now) && (Math.Abs(now.X - next.X) > 2 || Math.Abs(now.Y - next.Y) > 2))
                    {
                        drag.Interrupted = "The mouse was moved by someone else during the drag; the test let go.";
                        return drag;
                    }
                    // Each move waits for the drag to start, so that the move that started it is the last one before the hold.
                    if (drag.Started) continue;
                    for (var wait = 0; wait < 15 && !(drag.Started = HoldsDragCapture(uiThread)); wait++) Thread.Sleep(10);
                    if (!drag.Started)
                    {
                        if (step >= 8) return drag;
                        continue;
                    }
                    var before = Volatile.Read(ref ticks);
                    application.Dispatcher.Post(() => Volatile.Write(ref dispatched, 1));
                    Thread.Sleep(500);
                    drag.UiTicksWhileHeld = Volatile.Read(ref ticks) - before;
                    drag.DispatchedWhileHeld = Volatile.Read(ref dispatched) == 1;
                }
                return drag;
            }
            finally
            {
                if (pressed) SendButton(0x0004);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    // The drag loop of OLE holds the capture of the thread that runs it with a window of its own.
    private static bool HoldsDragCapture(uint thread)
    {
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(thread, ref info) || info.Capture == 0) return false;
        var name = new System.Text.StringBuilder(64);
        return GetClassName(info.Capture, name, name.Capacity) > 0 && name.ToString() == "CLIPBRDWNDCLASS";
    }

    private static bool IsWithin(nint window, Point point)
    {
        // The browser windows under the pointer belong to another process; their root is the test window.
        return GetAncestor(WindowFromPoint(point), 2) == window;
    }

    private static void MoveMouse(Point point)
    {
        SetCursorPos(point.X, point.Y);
        SendMouse(0x0001); // A move of zero at the new position, as input rather than a cursor change only.
    }

    private static void SendButton(uint flags) => SendMouse(flags);

    private static void SendMouse(uint flags)
    {
        var input = new MouseInput { Type = 0, Flags = flags };
        SendInput(1, ref input, Marshal.SizeOf<MouseInput>());
    }

    private static async Task WaitAsync(global::NeoAstra.NeoAstra view, string condition, CancellationToken cancellationToken)
    {
        while (await view.EvaluateScriptAsync(condition, cancellationToken) != "true") await Task.Delay(20, cancellationToken);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition()) await Task.Delay(20, cancellationToken);
    }

    // Chromium's base::Pickle of custom MIME data: payload size, entry count, then UTF-16 strings padded to 4 bytes.
    private static byte[] CustomDataPickle(params (string Type, string Value)[] entries)
    {
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, Encoding.Unicode, leaveOpen: true))
        {
            writer.Write((uint)entries.Length);
            foreach (var (type, value) in entries)
            {
                foreach (var text in new[] { type, value })
                {
                    writer.Write(text.Length);
                    writer.Write(Encoding.Unicode.GetBytes(text));
                    if (text.Length % 2 != 0) writer.Write((ushort)0);
                }
            }
        }
        var bytes = new byte[4 + payload.Length];
        BitConverter.TryWriteBytes(bytes, (uint)payload.Length);
        payload.ToArray().CopyTo(bytes, 4);
        return bytes;
    }

    // DROPFILES followed by a double-terminated list of UTF-16 paths.
    private static byte[] DropFiles(params string[] paths)
    {
        var names = Encoding.Unicode.GetBytes(string.Join('\0', paths) + "\0\0");
        var bytes = new byte[20 + names.Length];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), 20);
        BitConverter.TryWriteBytes(bytes.AsSpan(16), 1);
        names.CopyTo(bytes, 20);
        return bytes;
    }

    private sealed class DropPageProvider : INeoResourceProvider
    {
        public NeoResourceResponse GetResponse(NeoResourceRequest request) => NeoResourceResponse.FromBytes("""
            <!doctype html><title>drop target regression</title>
            <style>body { margin: 0; } #target { position: absolute; left: 120px; top: 60px; width: 200px; height: 120px; background: #ddd; }</style>
            <div id="target">Drop here</div>
            <script>
            globalThis.events = [];
            const target = document.getElementById('target');
            target.addEventListener('dragenter', event => { event.preventDefault(); globalThis.events.push('enter:' + [...event.dataTransfer.types].join('|')); });
            target.addEventListener('dragover', event => event.preventDefault());
            target.addEventListener('drop', event => { event.preventDefault(); globalThis.events.push('drop:' + event.dataTransfer.getData('application/x-neoastra-test')); });
            globalThis.ready = true;
            </script>
            """u8.ToArray(), "text/html; charset=utf-8");
    }

    private sealed class DragPageProvider : INeoResourceProvider
    {
        public NeoResourceResponse GetResponse(NeoResourceRequest request) => NeoResourceResponse.FromBytes("""
            <!doctype html><title>real drag regression</title>
            <style>body { margin: 0; user-select: none; } div { position: absolute; top: 80px; width: 120px; height: 100px; background: #ddd; }
            #source { left: 40px; } #target { left: 240px; }</style>
            <div id="source" draggable="true">Tab</div>
            <div id="target">Drop here</div>
            <script>
            globalThis.events = [];
            globalThis.trace = [];
            for (const type of ['pointerdown', 'dragstart', 'dragenter', 'dragleave', 'drop', 'dragend', 'pointerup'])
                document.addEventListener(type, event => globalThis.trace.push(type + '@' + (event.target.id || event.target.nodeName) + '(' + event.clientX + ',' + event.clientY + ')' + (event.dataTransfer ? ':' + event.dataTransfer.dropEffect : '')), true);
            document.addEventListener('dragover', event => { globalThis.lastOver = event.target.id || event.target.nodeName; globalThis.lastOverAt = event.clientX + ',' + event.clientY; }, true);
            globalThis.ratio = devicePixelRatio;
            document.getElementById('source').addEventListener('dragstart', event => {
                event.dataTransfer.setData('text/plain', 'payload');
                event.dataTransfer.setData('application/x-neoastra-test', 'tab-7');
                event.dataTransfer.setData('text/html', '<b>Tab</b>');
                event.dataTransfer.effectAllowed = 'move';
            });
            const target = document.getElementById('target');
            target.addEventListener('dragenter', event => { event.preventDefault(); globalThis.events.push('enter:' + [...event.dataTransfer.types].join('|')); });
            target.addEventListener('dragover', event => event.preventDefault());
            target.addEventListener('drop', event => {
                event.preventDefault();
                const html = event.dataTransfer.getData('text/html');
                globalThis.events.push('drop:' + event.dataTransfer.getData('text/plain') + '|' + event.dataTransfer.getData('application/x-neoastra-test') + (html ? '|' + html : ''));
            });
            globalThis.ready = true;
            </script>
            """u8.ToArray(), "text/html; charset=utf-8");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public uint Type;
        private readonly uint _padding;
        public int X;
        public int Y;
        public uint Data;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleDropTarget
    {
        [PreserveSig] int DragEnter(IDataObject data, int keys, Point point, ref int effect);
        [PreserveSig] int DragOver(int keys, Point point, ref int effect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop(IDataObject data, int keys, Point point, ref int effect);
    }

    /// <summary>An OLE data object holding HGLOBAL formats, as an external drag source hands them over.</summary>
    [ComVisible(true)]
    private sealed class TestDataObject : IDataObject
    {
        private const int DataFormatNotFound = unchecked((int)0x80040064);
        private readonly Dictionary<short, byte[]> _formats = [];

        public void Add(string format, byte[] bytes) => _formats[(short)RegisterClipboardFormat(format)] = bytes;
        public void Add(short format, byte[] bytes) => _formats[format] = bytes;

        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            if (QueryGetData(ref format) != 0) Marshal.ThrowExceptionForHR(DataFormatNotFound);
            var bytes = _formats[format.cfFormat];
            var handle = GlobalAlloc(0x0002, (nuint)bytes.Length);
            var pointer = GlobalLock(handle);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            GlobalUnlock(handle);
            medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = handle };
        }

        public int QueryGetData(ref FORMATETC format)
            => _formats.ContainsKey(format.cfFormat) && (format.tymed & TYMED.TYMED_HGLOBAL) != 0 ? 0 : DataFormatNotFound;

        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new NotSupportedException();
        public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut) { formatOut = formatIn; return DataFormatNotFound; }
        public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release) => throw new NotSupportedException();
        public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => throw new NotSupportedException();
        public int DAdvise(ref FORMATETC format, ADVF advf, IAdviseSink adviseSink, out int connection) { connection = 0; return unchecked((int)0x80040003); }
        public void DUnadvise(int connection) => throw new NotSupportedException();
        public int EnumDAdvise(out IEnumSTATDATA? enumAdvise) { enumAdvise = null; return unchecked((int)0x80040003); }
    }

#pragma warning disable SYSLIB1054 // Test-only Win32 interop does not need source-generated interop.
    [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string? className, string? windowName);

    [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)]
    private static extern nint GetProp(nint window, string name);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint window, ref Point point);

    [DllImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogicalToPhysicalPointForPerMonitorDPI(nint window, ref Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint count, ref MouseInput input, int size);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(Point point);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out Rect rect);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, System.Text.StringBuilder name, int capacity);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint window, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo { public int Size; public uint Flags; public nint Active; public nint Focus; public nint Capture; public nint MenuOwner; public nint MoveSize; public nint Caret; public Rect CaretRect; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(nint parent, nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalLock(nint handle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint handle);
#pragma warning restore SYSLIB1054
}
