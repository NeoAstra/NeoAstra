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

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

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

    [DllImport("kernel32.dll")]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalLock(nint handle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint handle);
#pragma warning restore SYSLIB1054
}
