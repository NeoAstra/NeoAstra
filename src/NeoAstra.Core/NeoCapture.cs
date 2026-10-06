// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Buffers.Binary;

namespace NeoAstra;

/// <summary>Identifies the encoding of a captured view image.</summary>
public enum NeoCaptureFormat
{
    /// <summary>A lossless PNG image.</summary>
    Png = 0,
    /// <summary>A JPEG image.</summary>
    Jpeg = 1,
}

/// <summary>Configures a capture of what a browser view shows.</summary>
public sealed class NeoCaptureOptions
{
    /// <summary>Gets or sets the encoding of the image. The default is <see cref="NeoCaptureFormat.Png"/>.</summary>
    public NeoCaptureFormat Format { get; set; }

    /// <summary>Gets or sets the JPEG quality from 1 through 100, or <see langword="null"/> for the backend default.</summary>
    /// <remarks>The value is ignored for <see cref="NeoCaptureFormat.Png"/>.</remarks>
    public int? Quality { get; set; }

    /// <summary>Gets or sets the part of the viewport to capture, or <see langword="null"/> for the whole viewport.</summary>
    /// <remarks>
    /// The rectangle is in CSS pixels of the top-level document, measured from the top-left corner of the visible viewport,
    /// which is what <c>Element.getBoundingClientRect()</c> reports. The part outside the viewport is not captured.
    /// </remarks>
    public NeoRect? Region { get; set; }

    /// <summary>Gets or sets whether the whole document is captured instead of the visible viewport.</summary>
    /// <remarks>
    /// Query <see cref="NeoCapability.CaptureFullPage"/> first: WKWebView has no such capture. <see cref="Region"/> must be
    /// <see langword="null"/>. A backend can limit the size of the image of a very long document.
    /// </remarks>
    public bool FullPage { get; set; }

    internal void Validate()
    {
        if (!Enum.IsDefined(Format)) throw new ArgumentOutOfRangeException(nameof(Format), Format, "The capture format is not defined.");
        if (Quality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(Quality), Quality, "The JPEG quality must be from 1 through 100.");
        if (Region is { } region)
        {
            if (FullPage) throw new ArgumentException("A full-page capture cannot be limited to a region.", nameof(Region));
            if (region.Width <= 0 || region.Height <= 0) throw new ArgumentException("The capture region must have a positive width and height.", nameof(Region));
        }
    }
}

/// <summary>Contains an encoded image captured from a browser view.</summary>
public sealed class NeoCapturedImage
{
    internal NeoCapturedImage(NeoCaptureFormat format, byte[] data)
    {
        Format = format;
        Data = data;
        (Width, Height) = ReadPixelSize(format, data);
    }

    /// <summary>Gets the encoding of <see cref="Data"/>.</summary>
    public NeoCaptureFormat Format { get; }

    /// <summary>Gets the media type of <see cref="Data"/>, such as <c>image/png</c>.</summary>
    public string ContentType => Format == NeoCaptureFormat.Jpeg ? "image/jpeg" : "image/png";

    /// <summary>Gets the encoded image.</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>Gets the image width in pixels, or zero when the encoded image does not state it.</summary>
    /// <remarks>The pixel size is the captured size in CSS pixels multiplied by the zoom and the device scale of the view.</remarks>
    public int Width { get; }

    /// <summary>Gets the image height in pixels, or zero when the encoded image does not state it.</summary>
    public int Height { get; }

    internal static (int Width, int Height) ReadPixelSize(NeoCaptureFormat format, ReadOnlySpan<byte> data)
    {
        if (format == NeoCaptureFormat.Png)
        {
            // An eight-byte signature, then the IHDR chunk: length, type, width, height.
            ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
            if (data.Length < 24 || !data[..8].SequenceEqual(signature) || !data.Slice(12, 4).SequenceEqual("IHDR"u8)) return default;
            var width = BinaryPrimitives.ReadUInt32BigEndian(data[16..]);
            var height = BinaryPrimitives.ReadUInt32BigEndian(data[20..]);
            return width > int.MaxValue || height > int.MaxValue ? default : ((int)width, (int)height);
        }

        // JPEG: walk the marker segments up to the first start-of-frame, which states the size.
        if (data.Length < 4 || data[0] != 0xff || data[1] != 0xd8) return default;
        var offset = 2;
        while (offset + 4 <= data.Length)
        {
            if (data[offset] != 0xff) return default;
            var marker = data[offset + 1];
            if (marker == 0xff) { offset++; continue; }
            if (marker is 0xd8 or 0x01 or (>= 0xd0 and <= 0xd7)) { offset += 2; continue; }
            var length = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            if (length < 2) return default;
            if (marker is >= 0xc0 and <= 0xcf and not (0xc4 or 0xc8 or 0xcc))
            {
                if (offset + 9 > data.Length) return default;
                return (BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 5)..]));
            }

            offset += 2 + length;
        }

        return default;
    }
}
