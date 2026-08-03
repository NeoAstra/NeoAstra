// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace NeoAstra.Desktop.Clipboard;

internal sealed partial class LinuxClipboard(NeoDispatcher? dispatcher) : INeoClipboard, INeoApplicationBoundDesktopService
{
    private NeoDispatcher? _dispatcher = dispatcher;

    public NeoCapabilityInfo Support { get; } = new(
        NeoSupportLevel.Native,
        1,
        0,
        "Native GTK4 UTF-8 text, HTML, PNG, and URI file-list clipboard content on the active X11 or Wayland desktop session.");

    void INeoApplicationBoundDesktopService.BindApplication(NeoApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (_dispatcher is not null && !ReferenceEquals(_dispatcher, application.Dispatcher)) throw new InvalidOperationException("The Linux clipboard is already bound to another dispatcher.");
        _dispatcher = application.Dispatcher;
    }

    public async ValueTask<NeoDesktopResult<byte[]>> ReadAsync(NeoClipboardFormat format, CancellationToken cancellationToken = default)
    {
        NeoFakeClipboard.ValidateFormat(format);
        cancellationToken.ThrowIfCancellationRequested();
        var value = _dispatcher ?? throw new InvalidOperationException("The Linux clipboard must be bound to the NeoAstra UI dispatcher before use.");
        var completion = await value.InvokeAsync(() => StartRead(format), cancellationToken).ConfigureAwait(false);
        return await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
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
        => Invoke(() => Native.gdk_clipboard_set_content(General(), 0) ? NeoDesktopStatus.Success : NeoDesktopStatus.Failed, cancellationToken);

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

    private static unsafe Task<NeoDesktopResult<byte[]>> StartRead(NeoClipboardFormat format)
    {
        var context = new ReadContext(format);
        var handle = GCHandle.Alloc(context);
        var names = Targets(format);
        var pointers = new nint[names.Count + 1];
        try
        {
            for (var index = 0; index < names.Count; index++) pointers[index] = Marshal.StringToCoTaskMemUTF8(names[index]);
            fixed (nint* values = pointers)
                Native.gdk_clipboard_read_async(General(), values, 0, 0, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&ReadCompleted, GCHandle.ToIntPtr(handle));
            return context.Completion.Task;
        }
        catch
        {
            handle.Free();
            throw;
        }
        finally
        {
            foreach (var pointer in pointers) if (pointer != 0) Marshal.FreeCoTaskMem(pointer);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void ReadCompleted(nint clipboard, nint result, nint data)
    {
        var handle = GCHandle.FromIntPtr(data);
        try
        {
            if (handle.Target is not ReadContext context) return;
            nint error = 0;
            var stream = Native.gdk_clipboard_read_finish(clipboard, result, out _, ref error);
            if (stream == 0)
            {
                context.Completion.TrySetResult(NeoDesktopResult<byte[]>.Failure(error == 0 ? NeoDesktopStatus.NotFound : NeoDesktopStatus.Failed, error == 0 ? null : "clipboard_failed"));
                if (error != 0) Native.g_error_free(error);
                return;
            }
            try
            {
                using var output = new MemoryStream();
                var buffer = new byte[8192];
                while (true)
                {
                    nint readError = 0;
                    nint count;
                    fixed (byte* pointer = buffer) count = Native.g_input_stream_read(stream, pointer, (nuint)buffer.Length, 0, ref readError);
                    if (count < 0)
                    {
                        if (readError != 0) Native.g_error_free(readError);
                        context.Completion.TrySetResult(NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.Failed, "clipboard_read_failed"));
                        return;
                    }
                    if (count == 0) break;
                    if (output.Length + count > NeoDesktopLimits.MaximumClipboardBytes)
                    {
                        context.Completion.TrySetResult(NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.LimitExceeded));
                        return;
                    }
                    output.Write(buffer, 0, checked((int)count));
                }
                var bytes = output.ToArray();
                context.Completion.TrySetResult(context.Format == NeoClipboardFormat.FileList ? DecodeFileList(bytes) : NeoDesktopResult<byte[]>.Success(bytes));
            }
            finally { Native.g_object_unref(stream); }
        }
        catch { if (handle.Target is ReadContext context) context.Completion.TrySetResult(NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.Failed, "clipboard_failed")); }
        finally { if (handle.IsAllocated) handle.Free(); }
    }

    private static unsafe NeoDesktopStatus Write(NeoClipboardFormat format, byte[] content)
    {
        var payload = format == NeoClipboardFormat.FileList ? EncodeFileList(content) : content;
        var mimeType = Targets(format)[0];
        nint bytes = 0;
        nint provider = 0;
        try
        {
            fixed (byte* pointer = payload) bytes = Native.g_bytes_new(pointer, (nuint)payload.Length);
            if (bytes == 0) return NeoDesktopStatus.Failed;
            provider = Native.gdk_content_provider_new_for_bytes(mimeType, bytes);
            return provider != 0 && Native.gdk_clipboard_set_content(General(), provider) ? NeoDesktopStatus.Success : NeoDesktopStatus.Failed;
        }
        finally
        {
            if (provider != 0) Native.g_object_unref(provider);
            if (bytes != 0) Native.g_bytes_unref(bytes);
            if (!ReferenceEquals(payload, content)) Array.Clear(payload);
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
        var display = Native.gdk_display_get_default();
        var clipboard = display == 0 ? 0 : Native.gdk_display_get_clipboard(display);
        if (clipboard == 0) throw new InvalidOperationException("GTK could not access the desktop clipboard.");
        return clipboard;
    }

    internal static IReadOnlyList<string> Targets(NeoClipboardFormat format) => format switch
    {
        NeoClipboardFormat.Text => ["text/plain;charset=utf-8", "text/plain"],
        NeoClipboardFormat.Html => ["text/html"],
        NeoClipboardFormat.Png => ["image/png"],
        NeoClipboardFormat.FileList => ["text/uri-list"],
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private sealed class ReadContext(NeoClipboardFormat format)
    {
        internal NeoClipboardFormat Format { get; } = format;
        internal TaskCompletionSource<NeoDesktopResult<byte[]>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static unsafe partial class Native
    {
        private const string Gtk = "libgtk-4.so.1";
        [LibraryImport(Gtk)] internal static partial nint gdk_display_get_default();
        [LibraryImport(Gtk)] internal static partial nint gdk_display_get_clipboard(nint display);
        [LibraryImport(Gtk)] internal static partial void gdk_clipboard_read_async(nint clipboard, nint* mimeTypes, int priority, nint cancellable, nint callback, nint userData);
        [LibraryImport(Gtk)] internal static partial nint gdk_clipboard_read_finish(nint clipboard, nint result, out nint mimeType, ref nint error);
        [LibraryImport(Gtk)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool gdk_clipboard_set_content(nint clipboard, nint provider);
        [LibraryImport(Gtk, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint gdk_content_provider_new_for_bytes(string mimeType, nint bytes);
        [LibraryImport("libgio-2.0.so.0")] internal static partial nint g_input_stream_read(nint stream, byte* buffer, nuint count, nint cancellable, ref nint error);
        [LibraryImport("libglib-2.0.so.0")] internal static partial nint g_bytes_new(byte* data, nuint size);
        [LibraryImport("libglib-2.0.so.0")] internal static partial void g_bytes_unref(nint bytes);
        [LibraryImport("libglib-2.0.so.0")] internal static partial void g_error_free(nint error);
        [LibraryImport("libgobject-2.0.so.0")] internal static partial void g_object_unref(nint value);
    }
}
