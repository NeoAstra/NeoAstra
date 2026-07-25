// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.InteropServices;
using System.Text;
using NeoWebView.Interop.Generated;

namespace NeoWebView.Interop;

internal sealed unsafe class Utf8String : IDisposable
{
    private readonly byte[] _bytes;
    private GCHandle _pin;

    internal Utf8String(string? value)
    {
        _bytes = value is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(value);
        if (_bytes.Length != 0)
        {
            _pin = GCHandle.Alloc(_bytes, GCHandleType.Pinned);
        }
    }

    internal NativeMethods.neo_webview_string_view_t View
    {
        get
        {
            var value = new NativeMethods.neo_webview_string_view
            {
                data = _bytes.Length == 0 ? null : (byte*)_pin.AddrOfPinnedObject(),
                length = (ulong)_bytes.Length,
            };
            return new(value);
        }
    }

    internal int ByteLength => _bytes.Length;

    internal static string Decode(NativeMethods.neo_webview_string_view_t value)
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
        if (_pin.IsAllocated)
        {
            _pin.Free();
        }
    }
}
