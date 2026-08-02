// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace NeoAstra.Desktop.Clipboard;

/// <summary>Identifies an explicitly authorized clipboard format.</summary>
public enum NeoClipboardFormat
{
    /// <summary>Unicode plain text.</summary>
    Text,
    /// <summary>UTF-8 HTML; callers remain responsible for sanitizing untrusted markup.</summary>
    Html,
    /// <summary>PNG encoded image bytes.</summary>
    Png,
    /// <summary>UTF-8 JSON array of canonical file paths.</summary>
    FileList,
}

/// <summary>Provides bounded, format-specific clipboard operations.</summary>
public interface INeoClipboard
{
    /// <summary>Gets truthful platform support.</summary>
    NeoCapabilityInfo Support { get; }
    /// <summary>Reads and copies one exact format without logging content.</summary>
    ValueTask<NeoDesktopResult<byte[]>> ReadAsync(NeoClipboardFormat format, CancellationToken cancellationToken = default);
    /// <summary>Writes one exact copied format.</summary>
    ValueTask<NeoDesktopStatus> WriteAsync(NeoClipboardFormat format, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);
    /// <summary>Clears formats owned by the current platform clipboard.</summary>
    ValueTask<NeoDesktopStatus> ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>Creates statically selected system clipboard adapters.</summary>
public static class NeoClipboard
{
    /// <summary>Creates the current platform adapter without reflection or dynamic native plugin loading.</summary>
    public static INeoClipboard CreateSystem(NeoDispatcher? dispatcher = null)
    {
        if (OperatingSystem.IsWindows()) return new WindowsClipboard(dispatcher);
        if (OperatingSystem.IsMacOS()) return new MacClipboard(dispatcher);
        if (OperatingSystem.IsLinux()) return new LinuxClipboard(dispatcher);
        return new UnsupportedClipboard();
    }
}

/// <summary>Deterministic in-memory clipboard for tests; all returned data is copied.</summary>
public sealed class NeoFakeClipboard : INeoClipboard
{
    private readonly object _sync = new();
    private readonly Dictionary<NeoClipboardFormat, byte[]> _formats = [];

    /// <inheritdoc />
    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Emulated, 1, 0, "Bounded in-memory test clipboard.");

    /// <inheritdoc />
    public ValueTask<NeoDesktopResult<byte[]>> ReadAsync(NeoClipboardFormat format, CancellationToken cancellationToken = default)
    {
        ValidateFormat(format); cancellationToken.ThrowIfCancellationRequested();
        lock (_sync) return ValueTask.FromResult(_formats.TryGetValue(format, out var content) ? NeoDesktopResult<byte[]>.Success((byte[])content.Clone()) : NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.NotFound));
    }

    /// <inheritdoc />
    public ValueTask<NeoDesktopStatus> WriteAsync(NeoClipboardFormat format, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        Validate(format, content); cancellationToken.ThrowIfCancellationRequested();
        lock (_sync) _formats[format] = content.ToArray();
        return ValueTask.FromResult(NeoDesktopStatus.Success);
    }

    /// <inheritdoc />
    public ValueTask<NeoDesktopStatus> ClearAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); lock (_sync) _formats.Clear(); return ValueTask.FromResult(NeoDesktopStatus.Success); }

    internal static void Validate(NeoClipboardFormat format, ReadOnlyMemory<byte> content)
    {
        ValidateFormat(format);
        if (content.Length > NeoDesktopLimits.MaximumClipboardBytes) throw new ArgumentOutOfRangeException(nameof(content), $"Clipboard content may not exceed {NeoDesktopLimits.MaximumClipboardBytes} bytes.");
        if (format is NeoClipboardFormat.Text or NeoClipboardFormat.Html)
        {
            if (content.Span.Contains((byte)0)) throw new ArgumentException("Text clipboard formats cannot contain NUL because native text clipboards use NUL-terminated representations.", nameof(content));
            try { _ = new UTF8Encoding(false, true).GetString(content.Span); }
            catch (DecoderFallbackException exception) { throw new ArgumentException("Text clipboard formats require valid UTF-8.", nameof(content), exception); }
        }
        if (format == NeoClipboardFormat.Png && (content.Length < 8 || !content.Span[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))) throw new ArgumentException("Image clipboard data must be PNG encoded.", nameof(content));
        if (format == NeoClipboardFormat.FileList)
        {
            try
            {
                using var document = JsonDocument.Parse(content);
                if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > NeoDesktopLimits.MaximumDropItems) throw new ArgumentException("A file-list clipboard payload must be a bounded JSON array.", nameof(content));
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } path || path.Length > 32_768 || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path)) throw new ArgumentException("A file-list clipboard payload contains a malformed path.", nameof(content));
                }
            }
            catch (JsonException exception) { throw new ArgumentException("A file-list clipboard payload must be valid UTF-8 JSON.", nameof(content), exception); }
        }
    }

    internal static void ValidateFormat(NeoClipboardFormat format) { if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format)); }
}

internal sealed class UnsupportedClipboard : INeoClipboard
{
    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.None, 1, 0, "No reviewed native clipboard helper is available.");
    public ValueTask<NeoDesktopResult<byte[]>> ReadAsync(NeoClipboardFormat format, CancellationToken cancellationToken = default) { NeoFakeClipboard.ValidateFormat(format); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.Unsupported)); }
    public ValueTask<NeoDesktopStatus> WriteAsync(NeoClipboardFormat format, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) { NeoFakeClipboard.Validate(format, content); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(NeoDesktopStatus.Unsupported); }
    public ValueTask<NeoDesktopStatus> ClearAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(NeoDesktopStatus.Unsupported); }
}

internal sealed class ProcessClipboard(string readExecutable, string writeExecutable, IReadOnlyList<string> arguments) : INeoClipboard
{
    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Limited, 1, 2, "Native text, HTML, PNG, and URI file-list clipboard MIME targets through a trusted Wayland/X11 helper; clipboard ownership and helper availability depend on the desktop session.");

    public async ValueTask<NeoDesktopResult<byte[]>> ReadAsync(NeoClipboardFormat format, CancellationToken cancellationToken = default)
    {
        NeoFakeClipboard.ValidateFormat(format);
        try
        {
            var readArgs = Arguments(readExecutable, arguments, format, read: true);
            var result = await DesktopProcess.RunAsync(readExecutable, readArgs, default, TimeSpan.FromSeconds(10), true, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0) return NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.Failed, "clipboard_read_failed");
            if (format != NeoClipboardFormat.FileList) return NeoDesktopResult<byte[]>.Success(result.Output);
            var paths = Encoding.UTF8.GetString(result.Output).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(static value => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : null).Where(static value => value is not null).Take(NeoDesktopLimits.MaximumDropItems + 1).ToArray();
            return paths.Length > NeoDesktopLimits.MaximumDropItems ? NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.LimitExceeded) : NeoDesktopResult<byte[]>.Success(JsonSerializer.SerializeToUtf8Bytes(paths, MacClipboardJsonContext.Default.StringArray));
        }
        catch (OperationCanceledException) { throw; }
        catch { return NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.Failed, "clipboard_read_failed"); }
    }

    public async ValueTask<NeoDesktopStatus> WriteAsync(NeoClipboardFormat format, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        NeoFakeClipboard.Validate(format, content);
        try
        {
            var writeArgs = Arguments(writeExecutable, arguments, format, read: false);
            ReadOnlyMemory<byte> payload = content;
            if (format == NeoClipboardFormat.FileList)
            {
                var paths = JsonSerializer.Deserialize(content.Span, MacClipboardJsonContext.Default.StringArray) ?? [];
                payload = Encoding.UTF8.GetBytes(string.Join("\r\n", paths.Select(static path => new Uri(path).AbsoluteUri)) + "\r\n");
            }
            var result = await DesktopProcess.RunAsync(writeExecutable, writeArgs, payload, TimeSpan.FromSeconds(10), false, cancellationToken, closeStandardInput: true).ConfigureAwait(false);
            return result.ExitCode == 0 ? NeoDesktopStatus.Success : NeoDesktopStatus.Failed;
        }
        catch (OperationCanceledException) { throw; }
        catch { return NeoDesktopStatus.Failed; }
    }

    public ValueTask<NeoDesktopStatus> ClearAsync(CancellationToken cancellationToken = default) => WriteAsync(NeoClipboardFormat.Text, ReadOnlyMemory<byte>.Empty, cancellationToken);

    private static IReadOnlyList<string> Arguments(string executable, IReadOnlyList<string> prefix, NeoClipboardFormat format, bool read)
    {
        var mime = format switch { NeoClipboardFormat.Text => "text/plain;charset=utf-8", NeoClipboardFormat.Html => "text/html", NeoClipboardFormat.Png => "image/png", NeoClipboardFormat.FileList => "text/uri-list", _ => throw new ArgumentOutOfRangeException(nameof(format)) };
        if (executable.EndsWith("xclip", StringComparison.Ordinal)) return [.. prefix, "-target", mime, read ? "-out" : "-in"];
        return [.. prefix, "--type", mime];
    }
}

internal sealed class WindowsClipboard : INeoClipboard
{
    private const uint UnicodeText = 13;
    private const uint FileDrop = 15;
    private const uint Moveable = 0x0002;
    private const int MaximumOpenAttempts = 10;
    private readonly object _sync = new();
    private NeoDispatcher? _dispatcher;
    private readonly uint _htmlFormat = WindowsClipboardNative.RegisterClipboardFormat("HTML Format");
    private readonly uint _pngFormat = WindowsClipboardNative.RegisterClipboardFormat("PNG");

    internal WindowsClipboard(NeoDispatcher? dispatcher) => _dispatcher = dispatcher;

    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Native, 1, 2, "Native copied Unicode text, CF_HTML, PNG, and HDROP file lists. Open/use/close execute synchronously on one bound UI thread; a busy clipboard is retried for a bounded interval.");

    internal void BindDispatcher(NeoDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        lock (_sync)
        {
            if (_dispatcher is not null && !ReferenceEquals(_dispatcher, dispatcher)) throw new InvalidOperationException("The Windows clipboard is already bound to another dispatcher.");
            _dispatcher = dispatcher;
        }
    }

    public ValueTask<NeoDesktopResult<byte[]>> ReadAsync(NeoClipboardFormat format, CancellationToken cancellationToken = default)
    {
        NeoFakeClipboard.ValidateFormat(format);
        var dispatcher = GetDispatcher();
        return dispatcher.InvokeAsync(() => ReadOnDispatcher(format, cancellationToken), cancellationToken);
    }

    public ValueTask<NeoDesktopStatus> WriteAsync(NeoClipboardFormat format, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        NeoFakeClipboard.Validate(format, content);
        var copied = content.ToArray();
        var dispatcher = GetDispatcher();
        return dispatcher.InvokeAsync(() =>
        {
            try { return WriteOnDispatcher(format, copied, cancellationToken); }
            finally { if (format != NeoClipboardFormat.Png) Array.Clear(copied); }
        }, cancellationToken);
    }

    public ValueTask<NeoDesktopStatus> ClearAsync(CancellationToken cancellationToken = default)
    {
        var dispatcher = GetDispatcher();
        return dispatcher.InvokeAsync(() =>
        {
            if (!OpenOnCurrentThread(cancellationToken)) return NeoDesktopStatus.Failed;
            try { return WindowsClipboardNative.EmptyClipboard() ? NeoDesktopStatus.Success : NeoDesktopStatus.Failed; }
            finally { _ = WindowsClipboardNative.CloseClipboard(); }
        }, cancellationToken);
    }

    private NeoDispatcher GetDispatcher()
    {
        lock (_sync) return _dispatcher ?? throw new InvalidOperationException("The Windows clipboard must be bound to the NeoAstra UI dispatcher before use.");
    }

    private NeoDesktopResult<byte[]> ReadOnDispatcher(NeoClipboardFormat format, CancellationToken cancellationToken)
    {
        if (!OpenOnCurrentThread(cancellationToken)) return NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.Failed, "clipboard_busy");
        try
        {
            var nativeFormat = Format(format);
            if (nativeFormat == 0) return NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.Unsupported);
            if (!WindowsClipboardNative.IsClipboardFormatAvailable(nativeFormat)) return NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.NotFound);
            return format switch
            {
                NeoClipboardFormat.Text => ReadText(),
                NeoClipboardFormat.Html => ReadHtml(_htmlFormat),
                NeoClipboardFormat.Png => ReadBytes(_pngFormat),
                NeoClipboardFormat.FileList => ReadFiles(),
                _ => NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.Unsupported),
            };
        }
        finally { _ = WindowsClipboardNative.CloseClipboard(); }
    }

    private NeoDesktopStatus WriteOnDispatcher(NeoClipboardFormat format, byte[] content, CancellationToken cancellationToken)
    {
        if (!OpenOnCurrentThread(cancellationToken)) return NeoDesktopStatus.Failed;
        try
        {
            if (!WindowsClipboardNative.EmptyClipboard()) return NeoDesktopStatus.Failed;
            return format switch
            {
                NeoClipboardFormat.Text => WriteText(new UTF8Encoding(false, true).GetString(content)),
                NeoClipboardFormat.Html => WriteBytes(_htmlFormat, BuildClipboardHtml(content), appendNull: true),
                NeoClipboardFormat.Png => WriteBytes(_pngFormat, content, appendNull: false),
                NeoClipboardFormat.FileList => WriteFiles(content),
                _ => NeoDesktopStatus.Unsupported,
            };
        }
        finally { _ = WindowsClipboardNative.CloseClipboard(); }
    }

    private static bool OpenOnCurrentThread(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaximumOpenAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (WindowsClipboardNative.OpenClipboard(0)) return true;
            if (attempt + 1 < MaximumOpenAttempts) Thread.Sleep(20);
        }
        return false;
    }

    private uint Format(NeoClipboardFormat format) => format switch { NeoClipboardFormat.Text => UnicodeText, NeoClipboardFormat.Html => _htmlFormat, NeoClipboardFormat.Png => _pngFormat, NeoClipboardFormat.FileList => FileDrop, _ => 0 };

    private static unsafe NeoDesktopResult<byte[]> ReadText()
    {
        var handle = WindowsClipboardNative.GetClipboardData(UnicodeText);
        if (handle == 0) return NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.NotFound);
        var pointer = WindowsClipboardNative.GlobalLock(handle);
        if (pointer == 0) return NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.Failed, "clipboard_lock_failed");
        try
        {
            var length = 0;
            var chars = (char*)pointer;
            var allocatedChars = WindowsClipboardNative.GlobalSize(handle) / 2;
            var maximumChars = Math.Min(allocatedChars, (nuint)(NeoDesktopLimits.MaximumClipboardBytes / 2 + 1));
            while ((nuint)length < maximumChars && chars[length] != '\0') length++;
            if ((nuint)length == maximumChars) return NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.LimitExceeded);
            return NeoDesktopResult<byte[]>.Success(Encoding.UTF8.GetBytes(new string(chars, 0, length)));
        }
        finally { _ = WindowsClipboardNative.GlobalUnlock(handle); }
    }

    private static unsafe NeoDesktopStatus WriteText(string text)
    {
        nint memory = 0;
        try
        {
            if (!WindowsClipboardNative.EmptyClipboard()) return NeoDesktopStatus.Failed;
            var bytes = checked((long)((text.Length + 1) * 2));
            memory = WindowsClipboardNative.GlobalAlloc(Moveable, (nuint)bytes);
            if (memory == 0) return NeoDesktopStatus.Failed;
            var pointer = WindowsClipboardNative.GlobalLock(memory);
            if (pointer == 0) return NeoDesktopStatus.Failed;
            try { fixed (char* source = text) { Buffer.MemoryCopy(source, (void*)pointer, bytes, text.Length * 2); ((char*)pointer)[text.Length] = '\0'; } }
            finally { _ = WindowsClipboardNative.GlobalUnlock(memory); }
            if (WindowsClipboardNative.SetClipboardData(UnicodeText, memory) == 0) return NeoDesktopStatus.Failed;
            memory = 0;
            return NeoDesktopStatus.Success;
        }
        finally { if (memory != 0) _ = WindowsClipboardNative.GlobalFree(memory); }
    }

    private static unsafe NeoDesktopResult<byte[]> ReadBytes(uint format)
    {
        var handle = WindowsClipboardNative.GetClipboardData(format);
        if (handle == 0) return NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.NotFound);
        var size = WindowsClipboardNative.GlobalSize(handle);
        if (size > NeoDesktopLimits.MaximumClipboardBytes) return NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.LimitExceeded);
        var pointer = WindowsClipboardNative.GlobalLock(handle);
        if (pointer == 0) return NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.Failed, "clipboard_lock_failed");
        try { return NeoDesktopResult<byte[]>.Success(new ReadOnlySpan<byte>((void*)pointer, checked((int)size)).ToArray()); }
        finally { _ = WindowsClipboardNative.GlobalUnlock(handle); }
    }

    private static NeoDesktopResult<byte[]> ReadHtml(uint format)
    {
        var native = ReadBytes(format);
        if (!native.IsSuccess || native.Value is null) return native;
        var value = native.Value;
        try
        {
            var length = value.Length;
            while (length > 0 && value[length - 1] == 0) length--;
            var header = Encoding.ASCII.GetString(value, 0, Math.Min(length, 512));
            if (!TryOffset(header, "StartFragment:", out var start) || !TryOffset(header, "EndFragment:", out var end) || start < 0 || end < start || end > length)
                return NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.Corrupt, "invalid_cf_html");
            return NeoDesktopResult<byte[]>.Success(value.AsSpan(start, end - start).ToArray());
        }
        finally { Array.Clear(value); }
    }

    private static bool TryOffset(string header, string name, out int value)
    {
        value = 0;
        var index = header.IndexOf(name, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return false;
        index += name.Length;
        var end = header.IndexOfAny(['\r', '\n'], index);
        if (end < 0) end = header.Length;
        return int.TryParse(header.AsSpan(index, end - index), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static byte[] BuildClipboardHtml(byte[] fragment)
    {
        var prefix = "<html><body><!--StartFragment-->"u8;
        var suffix = "<!--EndFragment--></body></html>"u8;
        const string template = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        var placeholder = string.Format(CultureInfo.InvariantCulture, template, 0, 0, 0, 0);
        var headerBytes = Encoding.ASCII.GetByteCount(placeholder);
        var startFragment = headerBytes + prefix.Length;
        var endFragment = startFragment + fragment.Length;
        var endHtml = endFragment + suffix.Length;
        var header = Encoding.ASCII.GetBytes(string.Format(CultureInfo.InvariantCulture, template, headerBytes, endHtml, startFragment, endFragment));
        var output = new byte[endHtml];
        header.CopyTo(output, 0); prefix.CopyTo(output.AsSpan(headerBytes)); fragment.CopyTo(output, startFragment); suffix.CopyTo(output.AsSpan(endFragment));
        return output;
    }

    private static unsafe NeoDesktopStatus WriteBytes(uint format, byte[] bytes, bool appendNull)
    {
        nint memory = 0;
        try
        {
            var length = checked(bytes.Length + (appendNull ? 1 : 0));
            memory = WindowsClipboardNative.GlobalAlloc(Moveable, (nuint)length);
            if (memory == 0) return NeoDesktopStatus.Failed;
            var pointer = WindowsClipboardNative.GlobalLock(memory);
            if (pointer == 0) return NeoDesktopStatus.Failed;
            try { bytes.CopyTo(new Span<byte>((void*)pointer, length)); if (appendNull) ((byte*)pointer)[bytes.Length] = 0; }
            finally { _ = WindowsClipboardNative.GlobalUnlock(memory); }
            if (WindowsClipboardNative.SetClipboardData(format, memory) == 0) return NeoDesktopStatus.Failed;
            memory = 0;
            return NeoDesktopStatus.Success;
        }
        finally { if (memory != 0) _ = WindowsClipboardNative.GlobalFree(memory); }
    }

    private static NeoDesktopResult<byte[]> ReadFiles()
    {
        var handle = WindowsClipboardNative.GetClipboardData(FileDrop);
        if (handle == 0) return NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.NotFound);
        var count = WindowsClipboardNative.DragQueryFile(handle, uint.MaxValue, null, 0);
        if (count > NeoDesktopLimits.MaximumDropItems) return NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.LimitExceeded);
        var paths = new string[count];
        for (uint index = 0; index < count; index++)
        {
            var length = WindowsClipboardNative.DragQueryFile(handle, index, null, 0);
            if (length > 32_768) return NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.LimitExceeded);
            var buffer = new char[length + 1];
            if (WindowsClipboardNative.DragQueryFile(handle, index, buffer, (uint)buffer.Length) != length) return NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.Failed, "clipboard_file_failed");
            paths[index] = new string(buffer, 0, (int)length);
        }
        var writerBuffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(writerBuffer))
        {
            writer.WriteStartArray(); foreach (var path in paths) writer.WriteStringValue(path); writer.WriteEndArray();
        }
        return NeoDesktopResult<byte[]>.Success(writerBuffer.WrittenSpan.ToArray());
    }

    private static unsafe NeoDesktopStatus WriteFiles(byte[] content)
    {
        using var document = JsonDocument.Parse(content);
        var paths = document.RootElement.EnumerateArray().Select(static item => item.GetString()!).ToArray();
        var characterCount = paths.Sum(static path => path.Length + 1) + 1;
        var headerBytes = sizeof(DropFiles);
        var totalBytes = checked(headerBytes + characterCount * 2);
        nint memory = 0;
        try
        {
            memory = WindowsClipboardNative.GlobalAlloc(Moveable, (nuint)totalBytes);
            if (memory == 0) return NeoDesktopStatus.Failed;
            var pointer = WindowsClipboardNative.GlobalLock(memory);
            if (pointer == 0) return NeoDesktopStatus.Failed;
            try
            {
                *(DropFiles*)pointer = new DropFiles((uint)headerBytes, true);
                var destination = new Span<char>((void*)(pointer + headerBytes), characterCount);
                var offset = 0;
                foreach (var path in paths) { path.CopyTo(destination[offset..]); offset += path.Length; destination[offset++] = '\0'; }
                destination[offset] = '\0';
            }
            finally { _ = WindowsClipboardNative.GlobalUnlock(memory); }
            if (WindowsClipboardNative.SetClipboardData(FileDrop, memory) == 0) return NeoDesktopStatus.Failed;
            memory = 0;
            return NeoDesktopStatus.Success;
        }
        finally { if (memory != 0) _ = WindowsClipboardNative.GlobalFree(memory); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DropFiles(uint offset, bool wide)
    {
        private readonly uint _offset = offset;
        private readonly int _x;
        private readonly int _y;
        private readonly int _nonClient;
        private readonly int _wide = wide ? 1 : 0;
    }
}

internal static partial class WindowsClipboardNative
{
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool OpenClipboard(nint owner);
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool CloseClipboard();
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool EmptyClipboard();
    [LibraryImport("user32.dll", SetLastError = true)] internal static partial nint GetClipboardData(uint format);
    [LibraryImport("user32.dll", SetLastError = true)] internal static partial nint SetClipboardData(uint format, nint memory);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool IsClipboardFormatAvailable(uint format);
    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16)] internal static partial uint RegisterClipboardFormat(string name);
    [LibraryImport("kernel32.dll", SetLastError = true)] internal static partial nint GlobalAlloc(uint flags, nuint bytes);
    [LibraryImport("kernel32.dll", SetLastError = true)] internal static partial nint GlobalLock(nint memory);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool GlobalUnlock(nint memory);
    [LibraryImport("kernel32.dll", SetLastError = true)] internal static partial nuint GlobalSize(nint memory);
    [LibraryImport("kernel32.dll", SetLastError = true)] internal static partial nint GlobalFree(nint memory);
    [LibraryImport("shell32.dll", EntryPoint = "DragQueryFileW", StringMarshalling = StringMarshalling.Utf16)] internal static partial uint DragQueryFile(nint drop, uint file, [Out] char[]? buffer, uint characters);
}
