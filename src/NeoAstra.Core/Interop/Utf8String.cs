// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.InteropServices;
using System.Text;
using NeoAstra.Interop.Generated;

namespace NeoAstra.Interop;

internal unsafe ref struct Utf8String
{
    private byte* _data;
    private int _byteLength;

    internal Utf8String(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var byteLength = Encoding.UTF8.GetByteCount(value);
        var data = (byte*)NativeMemory.Alloc((nuint)byteLength);
        try
        {
            _byteLength = Encoding.UTF8.GetBytes(value, new Span<byte>(data, byteLength));
            _data = data;
        }
        catch
        {
            NativeMemory.Free(data);
            throw;
        }
    }

    internal readonly NativeMethods.neoastra_string_view_t View
    {
        get
        {
            var value = new NativeMethods.neoastra_string_view
            {
                data = _data,
                length = (ulong)_byteLength,
            };
            return new(value);
        }
    }

    internal readonly int ByteLength => _byteLength;

    // A view of UTF-8 that the caller keeps alive, and pinned, for the native call.
    internal static NativeMethods.neoastra_string_view_t ViewOf(byte* data, int length)
        => new(new NativeMethods.neoastra_string_view { data = length == 0 ? null : data, length = (ulong)length });

    // The bytes of a native UTF-8 string, where they lie: valid while the native call that gave them lasts.
    internal static ReadOnlySpan<byte> Bytes(NativeMethods.neoastra_string_view_t value)
    {
        var raw = value.Value;
        if (raw.length == 0) return default;
        if (raw.data is null) throw new InvalidDataException("A native UTF-8 string has a null pointer and a nonzero length.");
        if (raw.length > int.MaxValue) throw new InvalidDataException("A native UTF-8 string exceeds the maximum managed string length.");
        return new ReadOnlySpan<byte>(raw.data, (int)raw.length);
    }

    internal static string Decode(NativeMethods.neoastra_string_view_t value)
    {
        var raw = value.Value;
        if (raw.length == 0)
        {
            return string.Empty;
        }

        if (raw.data is null)
        {
            throw new InvalidDataException("A native UTF-8 string has a null pointer and a nonzero length.");
        }

        if (raw.length > int.MaxValue)
        {
            throw new InvalidDataException("A native UTF-8 string exceeds the maximum managed string length.");
        }

        return Encoding.UTF8.GetString(raw.data, checked((int)raw.length));
    }

    // The native copy is cleared before it is freed: it carries the answers of decisions, among them the user name and the
    // password of an HTTP authentication, which nothing else of NeoAstra keeps in native memory. Transport frames do not go
    // through it: they are posted from their own pooled buffers (NeoTransportFrame), whose content is in managed memory.
    public void Dispose()
    {
        var data = _data;
        _data = null;
        if (data is not null)
        {
            NativeMemory.Clear(data, (nuint)_byteLength);
            NativeMemory.Free(data);
            _byteLength = 0;
        }
    }
}
