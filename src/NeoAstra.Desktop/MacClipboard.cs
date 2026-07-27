// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeoAstra.Desktop.Clipboard;

internal sealed partial class MacClipboard(NeoDispatcher? dispatcher) : INeoClipboard
{
    private readonly NeoDispatcher? _dispatcher = dispatcher;
    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Native, 1, 0, "Native NSPasteboard copied UTF-8 text, HTML, PNG, and file-list formats.");

    public ValueTask<NeoDesktopResult<byte[]>> ReadAsync(NeoClipboardFormat format, CancellationToken cancellationToken = default)
    {
        NeoFakeClipboard.ValidateFormat(format);
        return Invoke(() => Read(format), cancellationToken);
    }

    public ValueTask<NeoDesktopStatus> WriteAsync(NeoClipboardFormat format, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        NeoFakeClipboard.Validate(format, content); var owned = content.ToArray();
        return Invoke(() => { try { return Write(format, owned); } finally { if (format is NeoClipboardFormat.Text or NeoClipboardFormat.Html or NeoClipboardFormat.FileList) Array.Clear(owned); } }, cancellationToken);
    }

    public ValueTask<NeoDesktopStatus> ClearAsync(CancellationToken cancellationToken = default)
        => Invoke(() => { var board = General(); ObjC.SendNuint(board, ObjC.Selector("clearContents")); return NeoDesktopStatus.Success; }, cancellationToken);

    private ValueTask<T> Invoke<T>(Func<T> operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_dispatcher is null || _dispatcher.CheckAccess()) return ValueTask.FromResult(Contained(operation));
        return _dispatcher.InvokeAsync(() => Contained(operation), token);
    }

    private static T Contained<T>(Func<T> operation)
    {
        try { return operation(); }
        catch (OperationCanceledException) { throw; }
        catch { return typeof(T) == typeof(NeoDesktopStatus) ? (T)(object)NeoDesktopStatus.Failed : (T)(object)NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.Failed, "pasteboard_failed"); }
    }

    private static unsafe NeoDesktopResult<byte[]> Read(NeoClipboardFormat format)
    {
        var board = General();
        if (format == NeoClipboardFormat.FileList)
        {
            var type = ObjC.String("NSFilenamesPboardType");
            try
            {
                var array = ObjC.SendArg(board, ObjC.Selector("propertyListForType:"), type); if (array == 0) return NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.NotFound);
                var count = ObjC.SendNuint(array, ObjC.Selector("count")); if (count > NeoDesktopLimits.MaximumDropItems) return NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.LimitExceeded);
                var paths = new string[(int)count];
                for (nuint index = 0; index < count; index++) { var text = ObjC.SendNuintArg(array, ObjC.Selector("objectAtIndex:"), index); paths[index] = Marshal.PtrToStringUTF8(ObjC.Send(text, ObjC.Selector("UTF8String"))) ?? string.Empty; }
                return NeoDesktopResult<byte[]>.Success(JsonSerializer.SerializeToUtf8Bytes(paths, MacClipboardJsonContext.Default.StringArray));
            }
            finally { ObjC.Release(type); }
        }
        var nativeType = ObjC.String(Type(format));
        try
        {
            var data = ObjC.SendArg(board, ObjC.Selector("dataForType:"), nativeType); if (data == 0) return NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.NotFound);
            var length = ObjC.SendNuint(data, ObjC.Selector("length")); if (length > NeoDesktopLimits.MaximumClipboardBytes) return NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.LimitExceeded);
            var bytes = ObjC.Send(data, ObjC.Selector("bytes")); var result = new byte[(int)length]; if (length != 0) Marshal.Copy(bytes, result, 0, result.Length);
            return NeoDesktopResult<byte[]>.Success(result);
        }
        finally { ObjC.Release(nativeType); }
    }

    private static unsafe NeoDesktopStatus Write(NeoClipboardFormat format, byte[] content)
    {
        var board = General(); _ = ObjC.SendNuint(board, ObjC.Selector("clearContents"));
        if (format == NeoClipboardFormat.FileList)
        {
            var paths = JsonSerializer.Deserialize(content, MacClipboardJsonContext.Default.StringArray) ?? []; var array = ObjC.SendNuintArg(ObjC.Class("NSMutableArray"), ObjC.Selector("arrayWithCapacity:"), (nuint)paths.Length);
            foreach (var path in paths) { var text = ObjC.String(path); try { ObjC.SendVoid(array, ObjC.Selector("addObject:"), text); } finally { ObjC.Release(text); } }
            var type = ObjC.String("NSFilenamesPboardType"); try { return ObjC.SendBool(board, ObjC.Selector("setPropertyList:forType:"), array, type) ? NeoDesktopStatus.Success : NeoDesktopStatus.Failed; } finally { ObjC.Release(type); }
        }
        fixed (byte* pointer = content)
        {
            var data = ObjC.SendBytes(ObjC.Class("NSData"), ObjC.Selector("dataWithBytes:length:"), pointer, (nuint)content.Length); var type = ObjC.String(Type(format));
            try { return ObjC.SendBool(board, ObjC.Selector("setData:forType:"), data, type) ? NeoDesktopStatus.Success : NeoDesktopStatus.Failed; } finally { ObjC.Release(type); }
        }
    }

    private static nint General() => ObjC.Send(ObjC.Class("NSPasteboard"), ObjC.Selector("generalPasteboard"));
    private static string Type(NeoClipboardFormat format) => format switch { NeoClipboardFormat.Text => "public.utf8-plain-text", NeoClipboardFormat.Html => "public.html", NeoClipboardFormat.Png => "public.png", _ => throw new ArgumentOutOfRangeException(nameof(format)) };

    private static partial class ObjC
    {
        internal static nint Class(string value) => GetClass(value);
        internal static nint Selector(string value) => RegisterSelector(value);
        internal static nint String(string value) { var item = Send(Class("NSString"), Selector("alloc")); return InitString(item, Selector("initWithUTF8String:"), value); }
        internal static void Release(nint value) { if (value != 0) SendVoid(value, Selector("release")); }
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)] private static partial nint GetClass(string name);
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)] private static partial nint RegisterSelector(string name);
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial nint Send(nint receiver, nint selector);
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial nint SendArg(nint receiver, nint selector, nint value);
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial nuint SendNuint(nint receiver, nint selector);
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial nint SendNuintArg(nint receiver, nint selector, nuint value);
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial void SendVoid(nint receiver, nint selector);
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial void SendVoid(nint receiver, nint selector, nint value);
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] internal static partial bool SendBool(nint receiver, nint selector, nint first, nint second);
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static unsafe partial nint SendBytes(nint receiver, nint selector, byte* bytes, nuint length);
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)] private static partial nint InitString(nint receiver, nint selector, string value);
    }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(string[]))]
internal sealed partial class MacClipboardJsonContext : JsonSerializerContext;
