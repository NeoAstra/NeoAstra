// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NeoAstra.Desktop.DragDrop;

namespace NeoAstra.Desktop;

internal sealed class NativeOutboundDragPresenter : INeoOutboundDragPresenter, INeoRendererOutboundDragPresenter, INeoApplicationBoundDesktopService, IDisposable
{
    private NeoApplication? _application;
    private readonly Dictionary<NeoAstra, ViewGestureHandlers> _invalidators = new(ReferenceEqualityComparer.Instance);
    private readonly object _activeLock=new();
    private readonly HashSet<CancellationTokenSource> _active=[];

    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Limited, 1, 0,
        "Native file, text, and URL drags use Shell OLE, AppKit dragging sessions, or GTK4 content providers with source-bound one-shot gestures; custom drag images are not yet portable.");

    void INeoApplicationBoundDesktopService.BindApplication(NeoApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (_application is not null && !ReferenceEquals(_application, application)) throw new InvalidOperationException("The native drag presenter is already bound to another application.");
        _application = application;
        if (OperatingSystem.IsWindows()) WindowsOutboundDrag.ObserveApplication(application);
        if (OperatingSystem.IsLinux()) { application.ViewRegistered += LinuxOutboundDrag.Observe; foreach (var view in application.GetRegisteredViews()) LinuxOutboundDrag.Observe(view); }
        application.ViewRegistered += ObserveView;
        foreach (var view in application.GetRegisteredViews()) ObserveView(view);
    }

    public ValueTask<NeoDesktopStatus> StartAsync(NeoOutboundDragRequest request, CancellationToken cancellationToken)
        => StartCoreAsync(request, null, cancellationToken);

    ValueTask<NeoDesktopStatus> INeoRendererOutboundDragPresenter.StartRendererAsync(string documentSessionId, NeoOutboundDragRequest request, CancellationToken cancellationToken)
        => StartCoreAsync(request, documentSessionId, cancellationToken);

    private async ValueTask<NeoDesktopStatus> StartCoreAsync(NeoOutboundDragRequest request, string? documentSessionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.DragImagePath is not null) return NeoDesktopStatus.Unsupported;
        var application = _application ?? throw new InvalidOperationException("The native drag presenter must be bound to an application.");
        if (!application.TryGetView(request.ViewLabel, out var view) || view is null) return NeoDesktopStatus.NotFound;
        if (!MatchesSession(view, documentSessionId)) return NeoDesktopStatus.Denied;
        var items = request.Items.ToArray();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock(_activeLock){if(!ReferenceEquals(_application,application))return NeoDesktopStatus.Canceled;_active.Add(lifetime);}
        void Cancel() { try { lifetime.Cancel(); } catch (ObjectDisposedException) { } }
        EventHandler windowClosed = (_, _) => Cancel(); view.NativeNavigationStarted += Cancel; view.Disposing += Cancel; if(view.OwnedWindow is not null)view.OwnedWindow.Closed += windowClosed;
        try
        {
            if (OperatingSystem.IsWindows()) return await application.Dispatcher.InvokeAsync(() => MatchesSession(view, documentSessionId) ? WindowsOutboundDrag.Start(view, items, lifetime.Token) : NeoDesktopStatus.Denied, lifetime.Token).ConfigureAwait(false);
            if (OperatingSystem.IsMacOS())
            {
                var pending = await application.Dispatcher.InvokeAsync(() => MatchesSession(view, documentSessionId) ? MacOutboundDrag.Start(view, items, lifetime.Token) : Task.FromResult(NeoDesktopStatus.Denied), lifetime.Token).ConfigureAwait(false);
                return await pending.ConfigureAwait(false);
            }
            if (OperatingSystem.IsLinux())
            {
                var pending = await application.Dispatcher.InvokeAsync(() => MatchesSession(view, documentSessionId) ? LinuxOutboundDrag.Start(view, items, application.Dispatcher, lifetime.Token) : Task.FromResult(NeoDesktopStatus.Denied), lifetime.Token).ConfigureAwait(false);
                return await pending.ConfigureAwait(false);
            }
            return NeoDesktopStatus.Unsupported;
        }
        finally { lock(_activeLock)_active.Remove(lifetime);view.NativeNavigationStarted -= Cancel; view.Disposing -= Cancel; if(view.OwnedWindow is not null)view.OwnedWindow.Closed -= windowClosed; }
    }

    private static bool MatchesSession(NeoAstra view, string? documentSessionId)
        => documentSessionId is null || string.Equals(view.TransportSession?.DocumentSessionId, documentSessionId, StringComparison.Ordinal);

    public void Dispose()
    {
        NeoApplication? application;CancellationTokenSource[] active;lock(_activeLock){application=_application;_application=null;active=_active.ToArray();}foreach(var operation in active){try{operation.Cancel();}catch(ObjectDisposedException){}}if (application is null) return;
        void Cleanup()
        {
            application.ViewRegistered -= ObserveView;
            foreach (var pair in _invalidators.ToArray()) DetachView(pair.Key,pair.Value);
            _invalidators.Clear();
            if (OperatingSystem.IsWindows()) WindowsOutboundDrag.UnobserveApplication(application);
            if (OperatingSystem.IsLinux()) { application.ViewRegistered -= LinuxOutboundDrag.Observe; foreach (var view in application.GetRegisteredViews()) LinuxOutboundDrag.Unobserve(view); }
        }
        if (application.Dispatcher.CheckAccess()) Cleanup(); else application.Dispatcher.InvokeAsync(Cleanup).AsTask().GetAwaiter().GetResult();
    }

    private void ObserveView(NeoAstra view)
    {
        if (_invalidators.ContainsKey(view)) return;
        void Invalidate() { if (OperatingSystem.IsWindows()) WindowsOutboundDrag.Invalidate(); else if (OperatingSystem.IsLinux()) LinuxOutboundDrag.Invalidate(view); }
        void Disposing() { Invalidate(); if (OperatingSystem.IsLinux()) LinuxOutboundDrag.Unobserve(view); if(_invalidators.Remove(view,out var handlers))DetachView(view,handlers); }
        void WindowClosed(object? sender, EventArgs args) { if (OperatingSystem.IsLinux()) LinuxOutboundDrag.Forget(view); else Invalidate(); if(_invalidators.Remove(view,out var handlers))DetachView(view,handlers); }
        var value=new ViewGestureHandlers(Invalidate,Disposing,WindowClosed);_invalidators.Add(view,value); view.NativeNavigationStarted += Invalidate; view.Disposing += Disposing; if (view.OwnedWindow is not null) view.OwnedWindow.Closed += WindowClosed;
    }

    private static void DetachView(NeoAstra view,ViewGestureHandlers handlers){view.NativeNavigationStarted-=handlers.Navigation;view.Disposing-=handlers.Disposing;if(view.OwnedWindow is not null)view.OwnedWindow.Closed-=handlers.WindowClosed;}
    private sealed record ViewGestureHandlers(Action Navigation,Action Disposing, EventHandler WindowClosed);
}

internal static unsafe partial class WindowsOutboundDrag
{
    private static readonly object Sync = new();
    private static readonly Dictionary<uint, HookState> Hooks = [];

    internal static void ObserveApplication(NeoApplication application)
    {
        if (!application.Dispatcher.CheckAccess()) throw new InvalidOperationException("Windows drag gesture observation must be installed on the application UI thread.");
        var thread = Native.GetCurrentThreadId(); lock (Sync) { if (Hooks.TryGetValue(thread, out var existing)) { existing.References++; return; } var hook = Native.SetWindowsHookEx(14, (nint)(delegate* unmanaged[Stdcall]<int, nuint, nint, nint>)&MouseHook, 0, 0); if (hook == 0) throw new InvalidOperationException("Unable to install the Windows native drag gesture observer."); Hooks.Add(thread, new(hook)); }
    }
    internal static void UnobserveApplication(NeoApplication application) { var thread = Native.GetCurrentThreadId(); lock (Sync) if (Hooks.TryGetValue(thread, out var state) && --state.References == 0) { Hooks.Remove(thread); _ = Native.UnhookWindowsHookEx(state.Hook); } }
    internal static void Invalidate() { var thread = Native.GetCurrentThreadId(); lock (Sync) if (Hooks.TryGetValue(thread, out var state)) { state.Window = 0; state.Timestamp = 0; } }

    internal static NeoDesktopStatus Start(NeoAstra view, IReadOnlyList<NeoOutboundDragItem> items, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var owner = view.OwnedWindow?.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value ?? 0; var host = view.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value;
        if (owner == 0 || host == 0 || Native.GetForegroundWindow() != Native.GetAncestor(owner, 2) || !ConsumeGesture(host)) return NeoDesktopStatus.Denied;
        var paths=items.Where(static item=>item.Kind==NeoDragDataKind.File).Select(static item=>item.Value).ToArray();
        var pidls = stackalloc nint[paths.Length]; var created = 0; nint dataObject = 0; nint source=0;
        try
        {
            for (; created < paths.Length; created++) { pidls[created] = Native.ILCreateFromPath(paths[created]); if (pidls[created] == 0) return NeoDesktopStatus.NotFound; }
            var iid = new Guid("0000010e-0000-0000-C000-000000000046");
            if(paths.Length==items.Count){if (Native.SHCreateDataObject(0, (uint)paths.Length, pidls, 0, &iid, &dataObject) < 0 || dataObject == 0) return NeoDesktopStatus.Failed;}else dataObject=CreateTextDataObject(items);
            source = CreateDropSource(cancellationToken); uint effect = 0; var result = Native.SHDoDragDrop(owner, dataObject, source, 1, &effect);
            return result == 0x00040101 ? NeoDesktopStatus.Canceled : result < 0 ? NeoDesktopStatus.Failed : NeoDesktopStatus.Success;
        }
        finally { ReleaseSource(source);if (dataObject != 0) Marshal.Release(dataObject); for (var index = 0; index < created; index++) Native.ILFree(pidls[index]); }
    }

    private static nint CreateDropSource(CancellationToken token)
    {
        var value = (DropSource*)NativeMemory.Alloc((nuint)sizeof(DropSource)); var table = (nint*)NativeMemory.Alloc((nuint)(5 * sizeof(nint))); var handle = GCHandle.Alloc(new DropState(token));
        table[0]=(nint)(delegate* unmanaged[Stdcall]<DropSource*,Guid*,void**,int>)&SourceQueryInterface;table[1]=(nint)(delegate* unmanaged[Stdcall]<DropSource*,uint>)&SourceAddRef;table[2]=(nint)(delegate* unmanaged[Stdcall]<DropSource*,uint>)&SourceRelease;table[3]=(nint)(delegate* unmanaged[Stdcall]<DropSource*,int,uint,int>)&QueryContinueDrag;table[4]=(nint)(delegate* unmanaged[Stdcall]<DropSource*,uint,int>)&GiveFeedback;
        *value=new(){Table=table,References=1,State=GCHandle.ToIntPtr(handle)};return (nint)value;
    }
    private static void ReleaseSource(nint source) { if(source!=0)ReleaseCore((DropSource*)source); }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static int SourceQueryInterface(DropSource* self,Guid* iid,void** value){if(value is null)return unchecked((int)0x80004003);*value=null;if(iid is null)return unchecked((int)0x80004002);var unknown=new Guid("00000000-0000-0000-C000-000000000046");var source=new Guid("00000121-0000-0000-C000-000000000046");if(*iid!=unknown&&*iid!=source)return unchecked((int)0x80004002);*value=self;AddRefCore(self);return 0;}
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static uint SourceAddRef(DropSource* self)=>AddRefCore(self);
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static uint SourceRelease(DropSource* self)=>ReleaseCore(self);
    private static uint AddRefCore(DropSource* self)=>(uint)Interlocked.Increment(ref self->References);
    private static uint ReleaseCore(DropSource* self){var count=Interlocked.Decrement(ref self->References);if(count==0){var handle=GCHandle.FromIntPtr(self->State);if(handle.IsAllocated)handle.Free();NativeMemory.Free(self->Table);NativeMemory.Free(self);}return(uint)count;}
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static int QueryContinueDrag(DropSource* self,int escape,uint keys){try{var state=(DropState?)GCHandle.FromIntPtr(self->State).Target;if(escape!=0||state is null||state.Token.IsCancellationRequested)return 0x00040101;if((keys&1)==0)return 0x00040100;return 0;}catch{return 0x00040101;}}
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static int GiveFeedback(DropSource* self,uint effect)=>unchecked((int)0x00040102);
    private sealed class DropState(CancellationToken token){internal CancellationToken Token{get;}=token;}
    [StructLayout(LayoutKind.Sequential)] private struct DropSource{internal nint* Table;internal int References;internal nint State;}

    private static nint CreateTextDataObject(IReadOnlyList<NeoOutboundDragItem> items)
    {
        var formats=new Dictionary<ushort,byte[]>();var portable=items.Where(static item=>item.Kind!=NeoDragDataKind.File).ToArray();if(portable.Length!=0)formats.Add(13,System.Text.Encoding.Unicode.GetBytes(string.Join("\r\n",portable.Select(static item=>item.Value))+'\0'));
        if(portable.Length==1&&portable[0].Kind==NeoDragDataKind.Url){var format=Native.RegisterClipboardFormat("UniformResourceLocatorW");if(format!=0)formats[(ushort)format]=System.Text.Encoding.Unicode.GetBytes(portable[0].Value+'\0');}
        var files=items.Where(static item=>item.Kind==NeoDragDataKind.File).Select(static item=>item.Value).ToArray();if(files.Length!=0){var names=System.Text.Encoding.Unicode.GetBytes(string.Join('\0',files)+"\0\0");var drop=new byte[20+names.Length];BitConverter.GetBytes(20u).CopyTo(drop,0);BitConverter.GetBytes(1u).CopyTo(drop,16);names.CopyTo(drop,20);formats.Add(15,drop);}
        var stateHandle=GCHandle.Alloc(new DataState(formats));var value=(DataObject*)NativeMemory.Alloc((nuint)sizeof(DataObject));var table=(nint*)NativeMemory.Alloc((nuint)(12*sizeof(nint)));
        table[0]=(nint)(delegate* unmanaged[Stdcall]<DataObject*,Guid*,void**,int>)&DataQueryInterface;table[1]=(nint)(delegate* unmanaged[Stdcall]<DataObject*,uint>)&DataAddRef;table[2]=(nint)(delegate* unmanaged[Stdcall]<DataObject*,uint>)&DataRelease;table[3]=(nint)(delegate* unmanaged[Stdcall]<DataObject*,FormatEtc*,StorageMedium*,int>)&GetData;table[4]=(nint)(delegate* unmanaged[Stdcall]<DataObject*,FormatEtc*,StorageMedium*,int>)&GetDataHere;table[5]=(nint)(delegate* unmanaged[Stdcall]<DataObject*,FormatEtc*,int>)&QueryGetData;table[6]=(nint)(delegate* unmanaged[Stdcall]<DataObject*,FormatEtc*,FormatEtc*,int>)&GetCanonicalFormat;table[7]=(nint)(delegate* unmanaged[Stdcall]<DataObject*,FormatEtc*,StorageMedium*,int,int>)&SetData;table[8]=(nint)(delegate* unmanaged[Stdcall]<DataObject*,uint,nint*,int>)&EnumFormats;table[9]=(nint)(delegate* unmanaged[Stdcall]<DataObject*,FormatEtc*,uint,nint,nint*,int>)&DAdvise;table[10]=(nint)(delegate* unmanaged[Stdcall]<DataObject*,uint,int>)&DUnadvise;table[11]=(nint)(delegate* unmanaged[Stdcall]<DataObject*,nint*,int>)&EnumDAdvise;
        *value=new(){Table=table,References=1,State=GCHandle.ToIntPtr(stateHandle)};return(nint)value;
    }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]private static int DataQueryInterface(DataObject* self,Guid* iid,void** value){if(value is null)return unchecked((int)0x80004003);*value=null;if(iid is null)return unchecked((int)0x80004002);var unknown=new Guid("00000000-0000-0000-C000-000000000046");var data=new Guid("0000010e-0000-0000-C000-000000000046");if(*iid!=unknown&&*iid!=data)return unchecked((int)0x80004002);*value=self;DataAddRefCore(self);return 0;}
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]private static uint DataAddRef(DataObject* self)=>DataAddRefCore(self);
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]private static uint DataRelease(DataObject* self)=>DataReleaseCore(self);
    private static uint DataAddRefCore(DataObject* self)=>(uint)Interlocked.Increment(ref self->References);
    private static uint DataReleaseCore(DataObject* self){var count=Interlocked.Decrement(ref self->References);if(count==0){var handle=GCHandle.FromIntPtr(self->State);if(handle.IsAllocated)handle.Free();NativeMemory.Free(self->Table);NativeMemory.Free(self);}return(uint)count;}
    private static bool Supported(DataState state,FormatEtc* format)=>format!=null&&format->Aspect==1&&format->Index==-1&&(format->Tymed&1)!=0&&state.Formats.ContainsKey(format->Format);
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]private static int GetData(DataObject* self,FormatEtc* format,StorageMedium* medium){try{if(medium is null)return unchecked((int)0x80004003);*medium=default;var state=(DataState?)GCHandle.FromIntPtr(self->State).Target;if(state is null||!Supported(state,format)||!state.Formats.TryGetValue(format->Format,out var bytes))return unchecked((int)0x80040064);var memory=Native.GlobalAlloc(0x42,(nuint)bytes.Length);if(memory==0)return unchecked((int)0x8007000E);var pointer=Native.GlobalLock(memory);if(pointer==0){_ = Native.GlobalFree(memory);return unchecked((int)0x8007000E);}Marshal.Copy(bytes,0,pointer,bytes.Length);_ = Native.GlobalUnlock(memory);*medium=new(){Tymed=1,Value=memory};return 0;}catch{return unchecked((int)0x80004005);}}
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]private static int GetDataHere(DataObject* self,FormatEtc* format,StorageMedium* medium)=>unchecked((int)0x80004001);
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]private static int QueryGetData(DataObject* self,FormatEtc* format){try{var state=(DataState?)GCHandle.FromIntPtr(self->State).Target;return state is not null&&Supported(state,format)?0:unchecked((int)0x80040064);}catch{return unchecked((int)0x80004005);}}
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]private static int GetCanonicalFormat(DataObject* self,FormatEtc* input,FormatEtc* output){if(output!=null){*output=default;output->TargetDevice=0;}return 0x00040130;}
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]private static int SetData(DataObject* self,FormatEtc* format,StorageMedium* medium,int release)=>unchecked((int)0x80004001);
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]private static int EnumFormats(DataObject* self,uint direction,nint* result){try{if(result is null)return unchecked((int)0x80004003);*result=0;if(direction!=1)return unchecked((int)0x80004001);var state=(DataState?)GCHandle.FromIntPtr(self->State).Target;if(state is null)return unchecked((int)0x80004005);var formats=stackalloc FormatEtc[state.Formats.Count];var index=0;foreach(var format in state.Formats.Keys)formats[index++]=new(){Format=format,Aspect=1,Index=-1,Tymed=1};return Native.SHCreateStdEnumFmtEtc((uint)state.Formats.Count,formats,result);}catch{return unchecked((int)0x80004005);}}
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]private static int DAdvise(DataObject* self,FormatEtc* format,uint flags,nint sink,nint* connection)=>unchecked((int)0x80040003);
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]private static int DUnadvise(DataObject* self,uint connection)=>unchecked((int)0x80040003);
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]private static int EnumDAdvise(DataObject* self,nint* result){if(result!=null)*result=0;return unchecked((int)0x80040003);}
    private sealed class DataState(Dictionary<ushort,byte[]> formats){internal Dictionary<ushort,byte[]> Formats{get;}=formats;}
    [StructLayout(LayoutKind.Sequential)]private struct DataObject{internal nint* Table;internal int References;internal nint State;}
    [StructLayout(LayoutKind.Sequential)]private struct FormatEtc{internal ushort Format;internal nint TargetDevice;internal uint Aspect;internal int Index;internal uint Tymed;}
    [StructLayout(LayoutKind.Sequential)]private struct StorageMedium{internal uint Tymed;internal nint Value;internal nint Release;}

    private static bool ConsumeGesture(nint host) { var thread = Native.GetCurrentThreadId(); lock (Sync) { if (!Hooks.TryGetValue(thread, out var state) || state.Window == 0 || Native.GetAsyncKeyState(1) >= 0 || unchecked(Native.GetTickCount() - state.Timestamp) > 1_000 || (state.Window != host && !Native.IsChild(host, state.Window))) return false; state.Window = 0; state.Timestamp = 0; return true; } }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static nint MouseHook(int code, nuint wParam, nint lParam) { try { if (code >= 0 && (uint)wParam == 0x0201 && lParam != 0) { var message = *(LowLevelMouse*)lParam; if ((message.Flags & 3) == 0) { var thread = Native.GetCurrentThreadId(); lock (Sync) if (Hooks.TryGetValue(thread, out var state)) { state.Window = Native.WindowFromPoint(message.Point); state.Timestamp = message.Time; } } } } catch { } return Native.CallNextHookEx(0, code, wParam, lParam); }
    private sealed class HookState(nint hook) { internal nint Hook { get; } = hook; internal int References = 1; internal nint Window; internal uint Timestamp; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct LowLevelMouse { internal NativePoint Point; internal uint MouseData; internal uint Flags; internal uint Time; internal nuint ExtraInfo; }
    private static partial class Native
    {
        [LibraryImport("user32.dll")] internal static partial nint GetForegroundWindow();
        [LibraryImport("user32.dll")] internal static partial nint GetAncestor(nint window, uint flags);
        [LibraryImport("kernel32.dll")] internal static partial uint GetCurrentThreadId();
        [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW")] internal static partial nint SetWindowsHookEx(int id, nint procedure, nint module, uint thread);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool UnhookWindowsHookEx(nint hook);
        [LibraryImport("user32.dll")] internal static partial nint CallNextHookEx(nint hook, int code, nuint wParam, nint lParam);
        [LibraryImport("user32.dll")] internal static partial nint WindowFromPoint(NativePoint point);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool IsChild(nint parent, nint child);
        [LibraryImport("user32.dll")] internal static partial short GetAsyncKeyState(int key);
        [LibraryImport("kernel32.dll")] internal static partial uint GetTickCount();
        [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)] internal static partial nint ILCreateFromPath(string path);
        [LibraryImport("shell32.dll")] internal static partial void ILFree(nint pidl);
        [LibraryImport("shell32.dll")] internal static partial int SHCreateDataObject(nint folder, uint count, nint* items, nint inner, Guid* iid, nint* result);
        [LibraryImport("shell32.dll")] internal static partial int SHDoDragDrop(nint owner, nint dataObject, nint source, uint effects, uint* effect);
        [LibraryImport("ole32.dll")] internal static partial int SHCreateStdEnumFmtEtc(uint count, FormatEtc* formats, nint* result);
        [LibraryImport("user32.dll",EntryPoint="RegisterClipboardFormatW",StringMarshalling=StringMarshalling.Utf16)] internal static partial uint RegisterClipboardFormat(string value);
        [LibraryImport("kernel32.dll")] internal static partial nint GlobalAlloc(uint flags,nuint bytes);
        [LibraryImport("kernel32.dll")] internal static partial nint GlobalLock(nint memory);
        [LibraryImport("kernel32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static partial bool GlobalUnlock(nint memory);
        [LibraryImport("kernel32.dll")] internal static partial nint GlobalFree(nint memory);
    }
}

internal static unsafe partial class MacOutboundDrag
{
    private static readonly object ClassLock = new();
    private static readonly ConcurrentDictionary<nint, Operation> Operations = new();
    private static readonly HashSet<(nint Window, nint Number)> ConsumedGestures = [];
    private static readonly Queue<((nint Window,nint Number) Key,long Expires)> GestureExpiry=[];
    private static nint s_sourceClass;

    internal static Task<NeoDesktopStatus> Start(NeoAstra view, IReadOnlyList<NeoOutboundDragItem> sourceItems, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); EnsureClass();
        var webView = view.GetNativeHandle(NeoNativeHandleKind.WkWebView).Value; var app = Send(Class("NSApplication"), "sharedApplication"); var nativeEvent = Send(app, "currentEvent"); var type = (long)Send(nativeEvent, "type");
        var process = Send(Class("NSProcessInfo"), "processInfo"); var age = SendDouble(process, Selector("systemUptime")) - SendDouble(nativeEvent, Selector("timestamp"));
        var eventWindow = Send(nativeEvent, "window"); var viewWindow = Send(webView, "window"); var eventNumber = Send(nativeEvent, "eventNumber");
        if (webView == 0 || nativeEvent == 0 || type is not (1 or 6) || age is < 0 or > 1 || eventWindow == 0 || eventWindow != viewWindow) return Task.FromResult(NeoDesktopStatus.Denied);
        lock (ClassLock) { var now=Environment.TickCount64;while(GestureExpiry.TryPeek(out var expired)&&expired.Expires<=now){GestureExpiry.Dequeue();ConsumedGestures.Remove(expired.Key);}var key=(eventWindow,eventNumber);if(ConsumedGestures.Count>=256||!ConsumedGestures.Add(key))return Task.FromResult(NeoDesktopStatus.Denied);GestureExpiry.Enqueue((key,now+2_000)); }
        var source = Send(Send(s_sourceClass, "alloc"), "init"); var operation = new Operation(); if (source == 0 || !Operations.TryAdd(source, operation)) return Task.FromResult(NeoDesktopStatus.Failed);
        operation.Registration = cancellationToken.Register(() => Interlocked.Exchange(ref operation.Canceled, 1));if(operation.Canceled!=0){Operations.TryRemove(source,out _);operation.Registration.Dispose();SendVoid(source,"release");return Task.FromResult(NeoDesktopStatus.Canceled);}
        var items = Send(Class("NSMutableArray"), "arrayWithCapacity:", (nint)sourceItems.Count);
        foreach (var sourceItem in sourceItems)
        {
            var text = String(sourceItem.Value); var writer = sourceItem.Kind switch { NeoDragDataKind.File => Send(Class("NSURL"), "fileURLWithPath:", text), NeoDragDataKind.Url => Send(Class("NSURL"), "URLWithString:", text), _ => text }; if(writer==0){Operations.TryRemove(source,out _);operation.Registration.Dispose();SendVoid(source,"release");return Task.FromResult(NeoDesktopStatus.Failed);}
            var item = Send(Send(Class("NSDraggingItem"), "alloc"), "initWithPasteboardWriter:", writer); var icon = sourceItem.Kind==NeoDragDataKind.File?Send(Send(Class("NSWorkspace"), "sharedWorkspace"), "iconForFile:", text):Send(app,"applicationIconImage");
            SendFrame(item, Selector("setDraggingFrame:contents:"), new Rect(0, 0, 32, 32), icon); SendVoid(items, "addObject:", item); SendVoid(item, "release");
        }
        var session = Send3(webView, Selector("beginDraggingSessionWithItems:event:source:"), items, nativeEvent, source);
        if (session == 0) { Operations.TryRemove(source, out _); operation.Registration.Dispose(); SendVoid(source, "release"); return Task.FromResult(NeoDesktopStatus.Failed); }
        return operation.Completion.Task;
    }

    private static void EnsureClass()
    {
        if (s_sourceClass != 0) return; lock (ClassLock) { if (s_sourceClass != 0) return; var name = "NeoAstraDragSource_v1"u8; fixed (byte* namePointer = name) { var type = Native.objc_lookUpClass(namePointer); if (type == 0) { type = Native.objc_allocateClassPair(Class("NSObject"), namePointer, 0); Add(type, "draggingSession:sourceOperationMaskForDraggingContext:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nuint>)&Mask, "Q@:@q"); Add(type, "draggingSession:endedAtPoint:operation:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, Point, nuint, void>)&Ended, "v@:@{CGPoint=dd}Q"); Native.objc_registerClassPair(type); } s_sourceClass = type; } }
    }
    private static void Add(nint type, string selector, nint callback, string encoding) { var bytes = System.Text.Encoding.UTF8.GetBytes(encoding + '\0'); fixed (byte* pointer = bytes) if (!Native.class_addMethod(type, Selector(selector), callback, pointer)) throw new InvalidOperationException("Unable to define the AppKit drag source callback."); }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static nuint Mask(nint self, nint selector, nint session, nint context) => 1;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static void Ended(nint self, nint selector, nint session, Point point, nuint operation) { try { if (Operations.TryRemove(self, out var value)) { value.Registration.Dispose(); value.Completion.TrySetResult(value.Canceled!=0||operation == 0 ? NeoDesktopStatus.Canceled : NeoDesktopStatus.Success); } SendVoid(self, "release"); } catch { } }
    private sealed class Operation { internal TaskCompletionSource<NeoDesktopStatus> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); internal CancellationTokenRegistration Registration; internal int Canceled; }
    private static nint Class(string value) { var bytes = System.Text.Encoding.UTF8.GetBytes(value + '\0'); fixed (byte* pointer = bytes) return Native.objc_getClass(pointer); }
    private static nint Selector(string value) { var bytes = System.Text.Encoding.UTF8.GetBytes(value + '\0'); fixed (byte* pointer = bytes) return Native.sel_registerName(pointer); }
    private static nint String(string value) { var pointer = Marshal.StringToCoTaskMemUTF8(value); try { return Send1(Class("NSString"), Selector("stringWithUTF8String:"), pointer); } finally { Marshal.FreeCoTaskMem(pointer); } }
    private static nint Send(nint target, string selector) => Send(target, Selector(selector)); private static nint Send(nint target, nint selector) => Native.Send(target, selector); private static nint Send(nint target, string selector, nint value) => Send1(target, Selector(selector), value); private static nint Send1(nint target, nint selector, nint value) => Native.Send1(target, selector, value); private static nint Send3(nint target, nint selector, nint first, nint second, nint third) => Native.Send3(target, selector, first, second, third); private static void SendVoid(nint target, string selector) => Native.SendVoid(target, Selector(selector)); private static void SendVoid(nint target, string selector, nint value) => Native.SendVoid1(target, Selector(selector), value); private static double SendDouble(nint target, nint selector) => Native.SendDouble(target, selector); private static void SendFrame(nint target, nint selector, Rect rect, nint value) => Native.SendFrame(target, selector, rect, value);
    [StructLayout(LayoutKind.Sequential)] private readonly struct Point(double x, double y) { private readonly double X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)] private readonly struct Rect(double x, double y, double width, double height) { private readonly Point Origin = new(x, y); private readonly Size Size = new(width, height); }
    [StructLayout(LayoutKind.Sequential)] private readonly struct Size(double width, double height) { private readonly double Width = width, Height = height; }
    private static partial class Native
    {
        [LibraryImport("/usr/lib/libobjc.A.dylib")] internal static partial nint objc_lookUpClass(byte* name); [LibraryImport("/usr/lib/libobjc.A.dylib")] internal static partial nint objc_allocateClassPair(nint superclass, byte* name, nuint extra); [LibraryImport("/usr/lib/libobjc.A.dylib")] internal static partial void objc_registerClassPair(nint value); [LibraryImport("/usr/lib/libobjc.A.dylib")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool class_addMethod(nint type, nint selector, nint implementation, byte* encoding); [LibraryImport("/usr/lib/libobjc.A.dylib")] internal static partial nint objc_getClass(byte* name); [LibraryImport("/usr/lib/libobjc.A.dylib")] internal static partial nint sel_registerName(byte* name);
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial nint Send(nint target, nint selector); [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial nint Send1(nint target, nint selector, nint value); [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial nint Send3(nint target, nint selector, nint first, nint second, nint third); [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial void SendVoid(nint target, nint selector); [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial void SendVoid1(nint target, nint selector, nint value); [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial double SendDouble(nint target, nint selector); [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial void SendFrame(nint target, nint selector, Rect rect, nint value);
    }
}

internal static unsafe partial class LinuxOutboundDrag
{
    private static readonly ConcurrentDictionary<nint, Observation> Observations = new();
    private static readonly ConcurrentDictionary<NeoAstra, nint> ViewWidgets = new(ReferenceEqualityComparer.Instance);

    internal static void Observe(NeoAstra view)
    {
        var widget = TryGetWidget(view);
        if (widget == 0 || Observations.ContainsKey(widget)) return;
        var source = Native.gtk_drag_source_new();
        var gesture = Native.gtk_gesture_click_new();
        if (source == 0 || gesture == 0) return;
        var observation = new Observation(widget, source, gesture);
        Native.gtk_drag_source_set_actions(source, 1);
        Native.gtk_gesture_single_set_button(gesture, 1);
        Native.gtk_event_controller_set_propagation_phase(gesture, 1);
        observation.Pressed = Native.g_signal_connect_data(gesture, "pressed", (nint)(delegate* unmanaged[Cdecl]<nint, int, double, double, nint, void>)&Pressed, widget, 0, 0);
        observation.Released = Native.g_signal_connect_data(gesture, "released", (nint)(delegate* unmanaged[Cdecl]<nint, int, double, double, nint, void>)&Released, widget, 0, 0);
        observation.Begin = Native.g_signal_connect_data(source, "drag-begin", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&Begin, widget, 0, 0);
        observation.End = Native.g_signal_connect_data(source, "drag-end", (nint)(delegate* unmanaged[Cdecl]<nint, nint, int, nint, void>)&End, widget, 0, 0);
        observation.Cancel = Native.g_signal_connect_data(source, "drag-cancel", (nint)(delegate* unmanaged[Cdecl]<nint, nint, int, nint, int>)&Canceled, widget, 0, 0);
        if (observation.Pressed == 0 || observation.Released == 0 || observation.Begin == 0 || observation.End == 0 || observation.Cancel == 0 || !Observations.TryAdd(widget, observation))
        {
            Native.g_object_unref(source);
            Native.g_object_unref(gesture);
            return;
        }
        Native.gtk_widget_add_controller(widget, source);
        Native.gtk_widget_add_controller(widget, gesture);
        ViewWidgets[view] = widget;
    }

    internal static void Invalidate(NeoAstra view)
    {
        var widget = ViewWidgets.TryGetValue(view, out var observedWidget) ? observedWidget : TryGetWidget(view);
        if (widget != 0 && Observations.TryGetValue(widget, out var observation))
        {
            observation.PressedAt = 0;
            Complete(observation, NeoDesktopStatus.Canceled, cancelNative: true);
        }
    }

    internal static void Forget(NeoAstra view)
    {
        if (!ViewWidgets.TryRemove(view, out var widget) || !Observations.TryRemove(widget, out var observation)) return;
        var operation = observation.Pending;
        observation.Pending = null;
        if (operation is null) return;
        operation.Registration.Dispose();
        operation.Completion.TrySetResult(NeoDesktopStatus.Canceled);
    }

    internal static void Unobserve(NeoAstra view)
    {
        var widget = ViewWidgets.TryRemove(view, out var observedWidget) ? observedWidget : TryGetWidget(view);
        if (widget == 0 || !Observations.TryRemove(widget, out var observation)) return;
        Complete(observation, NeoDesktopStatus.Canceled, cancelNative: true);
        Native.gtk_widget_remove_controller(widget, observation.Source);
        Native.gtk_widget_remove_controller(widget, observation.Gesture);
    }

    internal static Task<NeoDesktopStatus> Start(NeoAstra view, IReadOnlyList<NeoOutboundDragItem> items, NeoDispatcher dispatcher, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var widget = TryGetWidget(view);
        var now = Native.g_get_monotonic_time();
        if (widget == 0 || !Observations.TryGetValue(widget, out var observation) || observation.PressedAt == 0 || now - observation.PressedAt is < 0 or > 1_000_000) return Task.FromResult(NeoDesktopStatus.Denied);
        if (observation.Pending is not null) return Task.FromResult(NeoDesktopStatus.Failed);
        var provider = CreateProvider(items);
        if (provider == 0) return Task.FromResult(NeoDesktopStatus.Failed);
        var operation = new Operation();
        observation.Pending = operation;
        observation.PressedAt = 0;
        Native.gtk_drag_source_set_content(observation.Source, provider);
        Native.g_object_unref(provider);
        var registration = cancellationToken.Register(() =>
        {
            try { _ = dispatcher.InvokeAsync(() => Complete(observation, NeoDesktopStatus.Canceled, cancelNative: true)); }
            catch { }
        });
        operation.Registration = registration;
        if (!ReferenceEquals(observation.Pending, operation)) registration.Dispose();
        return operation.Completion.Task;
    }

    private static nint CreateProvider(IReadOnlyList<NeoOutboundDragItem> items)
    {
        var providers = new List<nint>(2);
        try
        {
            var text = items.Where(static item => item.Kind == NeoDragDataKind.Text).Select(static item => item.Value).ToArray();
            if (text.Length != 0) providers.Add(CreateBytesProvider("text/plain;charset=utf-8", System.Text.Encoding.UTF8.GetBytes(string.Join("\n", text))));
            var transferable = items.Where(static item => item.Kind != NeoDragDataKind.Text).ToArray();
            if (transferable.Length != 0)
            {
                var uris = transferable.Select(static item => item.Kind == NeoDragDataKind.File ? new Uri(item.Value).AbsoluteUri : item.Value);
                providers.Add(CreateBytesProvider("text/uri-list", System.Text.Encoding.UTF8.GetBytes(string.Join("\r\n", uris) + "\r\n")));
            }
            if (providers.Count == 0 || providers.Any(static value => value == 0)) return 0;
            if (providers.Count == 1) { var result = providers[0]; providers.Clear(); return result; }
            var valuesArray = providers.ToArray();
            fixed (nint* values = valuesArray)
            {
                var result = Native.gdk_content_provider_new_union(values, (nuint)providers.Count);
                providers.Clear();
                return result;
            }
        }
        finally { foreach (var provider in providers) if (provider != 0) Native.g_object_unref(provider); }
    }

    private static nint CreateBytesProvider(string mimeType, byte[] content)
    {
        nint bytes;
        fixed (byte* pointer = content) bytes = Native.g_bytes_new(pointer, (nuint)content.Length);
        if (bytes == 0) return 0;
        try { return Native.gdk_content_provider_new_for_bytes(mimeType, bytes); }
        finally { Native.g_bytes_unref(bytes); Array.Clear(content); }
    }

    private static void Complete(Observation observation, NeoDesktopStatus status, bool cancelNative)
    {
        var operation = observation.Pending;
        if (operation is null) return;
        observation.Pending = null;
        if (cancelNative && operation.Begun) Native.gtk_drag_source_drag_cancel(observation.Source);
        Native.gtk_drag_source_set_content(observation.Source, 0);
        operation.Registration.Dispose();
        operation.Completion.TrySetResult(status);
    }

    private static nint TryGetWidget(NeoAstra view)
    {
        try { return view.GetNativeHandle(NeoNativeHandleKind.WebKitGtkWebView).Value; }
        catch (ObjectDisposedException) { return 0; }
        catch (InvalidOperationException) { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Pressed(nint gesture, int count, double x, double y, nint data) { try { if (Observations.TryGetValue(data, out var observation)) observation.PressedAt = Native.g_get_monotonic_time(); } catch { } }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Released(nint gesture, int count, double x, double y, nint data) { try { if (Observations.TryGetValue(data, out var observation) && observation.Pending is { Begun: false }) Complete(observation, NeoDesktopStatus.Canceled, cancelNative: false); } catch { } }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Begin(nint source, nint drag, nint data) { try { if (Observations.TryGetValue(data, out var observation) && observation.Pending is { } operation) operation.Begun = true; } catch { } }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void End(nint source, nint drag, int deleteData, nint data) { try { if (Observations.TryGetValue(data, out var observation)) Complete(observation, NeoDesktopStatus.Success, cancelNative: false); } catch { } }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Canceled(nint source, nint drag, int reason, nint data) { try { if (Observations.TryGetValue(data, out var observation)) Complete(observation, reason == 0 ? NeoDesktopStatus.Canceled : NeoDesktopStatus.Failed, cancelNative: false); } catch { } return 0; }

    private sealed class Observation(nint widget, nint source, nint gesture)
    {
        internal nint Widget { get; } = widget;
        internal nint Source { get; } = source;
        internal nint Gesture { get; } = gesture;
        internal ulong Pressed, Released, Begin, End, Cancel;
        internal long PressedAt;
        internal Operation? Pending;
    }

    private sealed class Operation
    {
        internal TaskCompletionSource<NeoDesktopStatus> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenRegistration Registration;
        internal bool Begun;
    }

    private static partial class Native
    {
        private const string Gtk = "libgtk-4.so.1";
        [LibraryImport(Gtk)] internal static partial nint gtk_drag_source_new();
        [LibraryImport(Gtk)] internal static partial void gtk_drag_source_set_actions(nint source, int actions);
        [LibraryImport(Gtk)] internal static partial void gtk_drag_source_set_content(nint source, nint provider);
        [LibraryImport(Gtk)] internal static partial void gtk_drag_source_drag_cancel(nint source);
        [LibraryImport(Gtk)] internal static partial nint gtk_gesture_click_new();
        [LibraryImport(Gtk)] internal static partial void gtk_gesture_single_set_button(nint gesture, uint button);
        [LibraryImport(Gtk)] internal static partial void gtk_event_controller_set_propagation_phase(nint controller, int phase);
        [LibraryImport(Gtk)] internal static partial void gtk_widget_add_controller(nint widget, nint controller);
        [LibraryImport(Gtk)] internal static partial void gtk_widget_remove_controller(nint widget, nint controller);
        [LibraryImport(Gtk)] internal static partial nint gdk_content_provider_new_union(nint* providers, nuint count);
        [LibraryImport(Gtk, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint gdk_content_provider_new_for_bytes(string mimeType, nint bytes);
        [LibraryImport("libgobject-2.0.so.0", StringMarshalling = StringMarshalling.Utf8)] internal static partial ulong g_signal_connect_data(nint instance, string signal, nint handler, nint data, nint destroy, int flags);
        [LibraryImport("libgobject-2.0.so.0")] internal static partial void g_object_unref(nint value);
        [LibraryImport("libglib-2.0.so.0")] internal static partial nint g_bytes_new(byte* data, nuint size);
        [LibraryImport("libglib-2.0.so.0")] internal static partial void g_bytes_unref(nint bytes);
        [LibraryImport("libglib-2.0.so.0")] internal static partial long g_get_monotonic_time();
    }
}
