// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.InteropServices;
using System.Text;

namespace NeoAstra.Desktop;

/// <summary>The Objective-C runtime calls that the iOS adapters use; UIKit objects are reached through it as on macOS.</summary>
internal static unsafe partial class IosObjC
{
    private const string Runtime = "/usr/lib/libobjc.A.dylib";

    [LibraryImport(Runtime, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)] internal static partial nint Class(string name);
    [LibraryImport(Runtime, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)] internal static partial nint Selector(string name);
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")] internal static partial nint Send(nint receiver, nint selector);
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")] internal static partial nint Send(nint receiver, nint selector, nint first);
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")] internal static partial nint Send(nint receiver, nint selector, nint first, nint second);
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")] internal static partial nint Send(nint receiver, nint selector, nint first, nint second, nint third);
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")] private static partial nint SendBytes(nint receiver, nint selector, byte* bytes);

    internal static nint Send(nint receiver, string selector) => Send(receiver, Selector(selector));
    internal static nint Send(nint receiver, string selector, nint first) => Send(receiver, Selector(selector), first);

    /// <summary>Creates an autoreleased NSString from UTF-8 text.</summary>
    internal static nint String(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value + '\0');
        fixed (byte* pointer = bytes) return SendBytes(Class("NSString"), Selector("stringWithUTF8String:"), pointer);
    }

    /// <summary>Reads an NSString as UTF-8 text.</summary>
    internal static string? Text(nint value) => value == 0 ? null : Marshal.PtrToStringUTF8(Send(value, Selector("UTF8String")));
}
