// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json;

namespace NeoAstra.Tests;

[TestClass]
public sealed class WindowsDropTargetTests
{
    private const int DropEffectCopy = 1;
    private const int DropEffectMove = 2;
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
                            "(() => { const r = document.getElementById('target').getBoundingClientRect(); return [(r.left + r.width / 2) * devicePixelRatio, (r.top + r.height / 2) * devicePixelRatio]; })()", timeout.Token)).RootElement;
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
                    var pressed = false;
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
                        MoveMouse(source);
                        await Task.Delay(100, timeout.Token);
                        SendButton(0x0002);
                        pressed = true;
                        for (var step = 1; step <= 35; step++)
                        {
                            var next = step <= 30 ? new Point { X = source.X + (target.X - source.X) * step / 30, Y = source.Y + (target.Y - source.Y) * step / 30 } : target;
                            MoveMouse(next);
                            await Task.Delay(step <= 30 ? 15 : 30, timeout.Token);
                            // Someone else moves the mouse: the test lets go at once rather than fight them.
                            if (GetCursorPos(out var now) && (Math.Abs(now.X - next.X) > 2 || Math.Abs(now.Y - next.Y) > 2))
                            {
                                inconclusive = "The mouse was moved by someone else during the drag; the test let go.";
                                return;
                            }
                        }
                        SendButton(0x0004);
                        pressed = false;
                        for (var index = 0; index < 150 && await view.EvaluateScriptAsync("globalThis.events.some(value => value.startsWith('drop:'))", timeout.Token) != "true"; index++)
                            await Task.Delay(20, timeout.Token);
                        Assert.AreEqual("true", await view.EvaluateScriptAsync("globalThis.events.some(value => value.startsWith('drop:'))", timeout.Token),
                            "The page saw no drop: " + await view.EvaluateScriptAsync("globalThis.trace.join(' ') + ' lastOver ' + globalThis.lastOver + '(' + globalThis.lastOverAt + ') ratio ' + globalThis.ratio + ' target ' + JSON.stringify(document.getElementById('target').getBoundingClientRect())", timeout.Token) + " host drops: " + hostDrops.Count + " log: " + string.Join(" | ", logged.Select(static value => value.Message)));
                        Assert.AreEqual("\"drop:payload|tab-7\"", await view.EvaluateScriptAsync("globalThis.events.find(value => value.startsWith('drop:'))", timeout.Token));
                        StringAssert.Contains(await view.EvaluateScriptAsync("globalThis.events.join(',')", timeout.Token), "enter:");
                        Assert.IsTrue(hostDrops.IsEmpty, "An in-page drag that carries text must not reach the host as an inbound drop.");
                    }
                    finally
                    {
                        if (pressed) SendButton(0x0004);
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

    private static async Task<Point> ScreenCenterAsync(global::NeoAstra.NeoAstra view, nint parent, string id, CancellationToken cancellationToken)
    {
        var center = JsonDocument.Parse(await view.EvaluateScriptAsync(
            $"(() => {{ const r = document.getElementById('{id}').getBoundingClientRect(); return [(r.left + r.width / 2) * devicePixelRatio, (r.top + r.height / 2) * devicePixelRatio]; }})()", cancellationToken)).RootElement;
        var point = new Point { X = (int)Math.Round(center[0].GetDouble()), Y = (int)Math.Round(center[1].GetDouble()) };
        Assert.IsTrue(ClientToScreen(parent, ref point));
        return point;
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
                event.dataTransfer.effectAllowed = 'move';
            });
            const target = document.getElementById('target');
            target.addEventListener('dragenter', event => { event.preventDefault(); globalThis.events.push('enter:' + [...event.dataTransfer.types].join('|')); });
            target.addEventListener('dragover', event => event.preventDefault());
            target.addEventListener('drop', event => {
                event.preventDefault();
                globalThis.events.push('drop:' + event.dataTransfer.getData('text/plain') + '|' + event.dataTransfer.getData('application/x-neoastra-test'));
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
