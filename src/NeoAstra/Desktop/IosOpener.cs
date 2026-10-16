// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NeoAstra.Desktop.Opener;

/// <summary>Hands a web address to iOS, which opens it in the browser of the user.</summary>
internal static unsafe partial class IosOpener
{
    private const string System = "/usr/lib/libSystem.B.dylib";

    // UIApplication belongs to the main thread, and the opener can be called from any thread.
    internal static NeoDesktopStatus OpenUrl(string url)
    {
        var context = GCHandle.ToIntPtr(GCHandle.Alloc(url));
        DispatchAsync(NativeLibrary.GetExport(NativeLibrary.Load(System), "_dispatch_main_q"), context, &Open);
        return NeoDesktopStatus.Success;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Open(nint context)
    {
        var handle = GCHandle.FromIntPtr(context);
        try
        {
            var url = IosObjC.Send(IosObjC.Class("NSURL"), "URLWithString:", IosObjC.String((string)handle.Target!));
            if (url == 0) return;
            var application = IosObjC.Send(IosObjC.Class("UIApplication"), "sharedApplication");
            IosObjC.Send(application, IosObjC.Selector("openURL:options:completionHandler:"), url, IosObjC.Send(IosObjC.Class("NSDictionary"), "dictionary"), 0);
        }
        catch { }
        finally { handle.Free(); }
    }

    [LibraryImport(System, EntryPoint = "dispatch_async_f")]
    private static partial void DispatchAsync(nint queue, nint context, delegate* unmanaged[Cdecl]<nint, void> work);
}
