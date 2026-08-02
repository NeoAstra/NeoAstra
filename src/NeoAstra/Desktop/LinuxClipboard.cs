// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace NeoAstra.Desktop.Clipboard;

internal sealed unsafe partial class LinuxClipboard(NeoDispatcher? dispatcher) : INeoClipboard, INeoApplicationBoundDesktopService
{
    private NeoDispatcher? _dispatcher = dispatcher;

    public NeoCapabilityInfo Support { get; } = new(
        NeoSupportLevel.Native,
        1,
        0,
        "Native GTK3 copied UTF-8 text, HTML, PNG, and URI file-list clipboard targets on the active X11 or Wayland desktop session.");

    void INeoApplicationBoundDesktopService.BindApplication(NeoApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (_dispatcher is not null && !ReferenceEquals(_dispatcher, application.Dispatcher)) throw new InvalidOperationException("The Linux clipboard is already bound to another dispatcher.");
        _dispatcher = application.Dispatcher;
    }

    public ValueTask<NeoDesktopResult<byte[]>> ReadAsync(NeoClipboardFormat format, CancellationToken cancellationToken = default)
    {
        NeoFakeClipboard.ValidateFormat(format);
        return Invoke(() => Read(format), cancellationToken);
    }

    public ValueTask<NeoDesktopStatus> WriteAsync(NeoClipboardFormat format, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        NeoFakeClipboard.Validate(format, content);
        var owned = content.ToArray();
        return Invoke(() =>
        {
            try { return Write(format, owned); }
            finally { Array.Clear(owned); }
        }, cancellationToken);
    }

    public ValueTask<NeoDesktopStatus> ClearAsync(CancellationToken cancellationToken = default)
        => Invoke(() => { Native.gtk_clipboard_clear(General()); return NeoDesktopStatus.Success; }, cancellationToken);

    private ValueTask<T> Invoke<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = _dispatcher ?? throw new InvalidOperationException("The Linux clipboard must be bound to the NeoAstra UI dispatcher before use.");
        return value.InvokeAsync(() => Contained(operation), cancellationToken);
    }

    private static T Contained<T>(Func<T> operation)
    {
        try { return operation(); }
        catch (OperationCanceledException) { throw; }
        catch { return typeof(T) == typeof(NeoDesktopStatus) ? (T)(object)NeoDesktopStatus.Failed : (T)(object)NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.Failed, "clipboard_failed"); }
    }

    private static NeoDesktopResult<byte[]> Read(NeoClipboardFormat format)
    {
        var clipboard = General();
        var targets = Targets(format);
        foreach (var target in targets)
        {
            var atom = Native.gdk_atom_intern(target, false);
            var selection = Native.gtk_clipboard_wait_for_contents(clipboard, atom);
            if (selection == 0) continue;
            try
            {
                var length = Native.gtk_selection_data_get_length(selection);
                if (length < 0) continue;
                if (length > NeoDesktopLimits.MaximumClipboardBytes) return NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.LimitExceeded);
                var pointer = Native.gtk_selection_data_get_data(selection);
                if (length != 0 && pointer == 0) return NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.Corrupt, "clipboard_data_missing");
                var bytes = new byte[length];
                if (length != 0) Marshal.Copy(pointer, bytes, 0, length);
                return format == NeoClipboardFormat.FileList ? DecodeFileList(bytes) : NeoDesktopResult<byte[]>.Success(bytes);
            }
            finally { Native.gtk_selection_data_free(selection); }
        }
        return NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.NotFound);
    }

    private static NeoDesktopStatus Write(NeoClipboardFormat format, byte[] content)
    {
        var payload = new ClipboardPayload(format == NeoClipboardFormat.FileList ? EncodeFileList(content) : (byte[])content.Clone());
        var names = Targets(format);
        var nativeNames = new nint[names.Count];
        var entries = stackalloc TargetEntry[names.Count];
        var handle = default(GCHandle);
        var transferred = false;
        try
        {
            for (var index = 0; index < names.Count; index++)
            {
                nativeNames[index] = Marshal.StringToCoTaskMemUTF8(names[index]);
                entries[index] = new TargetEntry { Target = nativeNames[index], Info = (uint)index };
            }
            handle = GCHandle.Alloc(payload);
            var success = Native.gtk_clipboard_set_with_data(
                General(),
                entries,
                (uint)names.Count,
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, uint, nint, void>)&ProvideData,
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&ReleaseData,
                GCHandle.ToIntPtr(handle));
            if (!success)
                return NeoDesktopStatus.Failed;
            transferred = true;
            Native.gtk_clipboard_store(General());
            return NeoDesktopStatus.Success;
        }
        finally
        {
            if (!transferred && handle.IsAllocated) { handle.Free(); payload.Clear(); }
            foreach (var pointer in nativeNames) if (pointer != 0) Marshal.FreeCoTaskMem(pointer);
        }
    }

    private static NeoDesktopResult<byte[]> DecodeFileList(byte[] content)
    {
        var paths = Encoding.UTF8.GetString(content)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(static value => !value.StartsWith('#'))
            .Select(static value => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : null)
            .Where(static value => value is not null)
            .Take(NeoDesktopLimits.MaximumDropItems + 1)
            .ToArray();
        return paths.Length > NeoDesktopLimits.MaximumDropItems
            ? NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.LimitExceeded)
            : NeoDesktopResult<byte[]>.Success(JsonSerializer.SerializeToUtf8Bytes(paths, MacClipboardJsonContext.Default.StringArray));
    }

    private static byte[] EncodeFileList(byte[] content)
    {
        var paths = JsonSerializer.Deserialize(content, MacClipboardJsonContext.Default.StringArray) ?? [];
        return Encoding.UTF8.GetBytes(string.Join("\r\n", paths.Select(static path => new Uri(path).AbsoluteUri)) + "\r\n");
    }

    private static nint General()
    {
        var selection = Native.gdk_atom_intern("CLIPBOARD", false);
        var clipboard = Native.gtk_clipboard_get(selection);
        if (clipboard == 0) throw new InvalidOperationException("GTK could not access the desktop clipboard.");
        return clipboard;
    }

    internal static IReadOnlyList<string> Targets(NeoClipboardFormat format) => format switch
    {
        NeoClipboardFormat.Text => ["text/plain;charset=utf-8", "UTF8_STRING"],
        NeoClipboardFormat.Html => ["text/html"],
        NeoClipboardFormat.Png => ["image/png"],
        NeoClipboardFormat.FileList => ["text/uri-list"],
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ProvideData(nint clipboard, nint selection, uint info, nint data)
    {
        try
        {
            var handle = GCHandle.FromIntPtr(data);
            if (handle.Target is not ClipboardPayload payload) return;
            fixed (byte* pointer = payload.Content)
                Native.gtk_selection_data_set(selection, Native.gtk_selection_data_get_target(selection), 8, pointer, payload.Content.Length);
        }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ReleaseData(nint clipboard, nint data)
    {
        try
        {
            var handle = GCHandle.FromIntPtr(data);
            if (handle.Target is ClipboardPayload payload) payload.Clear();
            if (handle.IsAllocated) handle.Free();
        }
        catch { }
    }

    private sealed class ClipboardPayload(byte[] content)
    {
        internal byte[] Content { get; } = content;
        internal void Clear() => Array.Clear(Content);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TargetEntry
    {
        internal nint Target;
        internal uint Flags;
        internal uint Info;
    }

    private static partial class Native
    {
        [LibraryImport("libgdk-3.so.0", StringMarshalling = StringMarshalling.Utf8)] internal static partial nint gdk_atom_intern(string atomName, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);
        [LibraryImport("libgtk-3.so.0")] internal static partial nint gtk_clipboard_get(nint selection);
        [LibraryImport("libgtk-3.so.0")] internal static partial nint gtk_clipboard_wait_for_contents(nint clipboard, nint target);
        [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_selection_data_free(nint selectionData);
        [LibraryImport("libgtk-3.so.0")] internal static partial int gtk_selection_data_get_length(nint selectionData);
        [LibraryImport("libgtk-3.so.0")] internal static partial nint gtk_selection_data_get_data(nint selectionData);
        [LibraryImport("libgtk-3.so.0")] internal static partial nint gtk_selection_data_get_target(nint selectionData);
        [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_selection_data_set(nint selectionData, nint type, int format, byte* data, int length);
        [LibraryImport("libgtk-3.so.0")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool gtk_clipboard_set_with_data(nint clipboard, TargetEntry* targets, uint targetCount, nint getFunction, nint clearFunction, nint userData);
        [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_clipboard_store(nint clipboard);
        [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_clipboard_clear(nint clipboard);
    }
}
