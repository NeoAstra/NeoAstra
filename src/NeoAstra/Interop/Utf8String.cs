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
