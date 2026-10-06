// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Buffers.Binary;
using System.IO.Compression;

namespace NeoAstra.Tests;

/// <summary>Decodes the 8-bit truecolor PNG images that browser captures produce, to check their pixels.</summary>
internal sealed class TestPng
{
    private readonly byte[] _pixels;
    private readonly int _channels;

    private TestPng(int width, int height, int channels, byte[] pixels)
    {
        Width = width;
        Height = height;
        _channels = channels;
        _pixels = pixels;
    }

    internal int Width { get; }

    internal int Height { get; }

    internal (byte Red, byte Green, byte Blue) GetPixel(int x, int y)
    {
        var offset = (y * Width + x) * _channels;
        return (_pixels[offset], _pixels[offset + 1], _pixels[offset + 2]);
    }

    internal static TestPng Decode(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
        if (data.Length < 8 || !data[..8].SequenceEqual(signature)) throw new InvalidDataException("The data is not a PNG image.");
        int width = 0, height = 0, channels = 0;
        using var compressed = new MemoryStream();
        for (var offset = 8; offset + 12 <= data.Length;)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[offset..]));
            var type = data.Slice(offset + 4, 4);
            var body = data.Slice(offset + 8, length);
            if (type.SequenceEqual("IHDR"u8))
            {
                width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(body));
                height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]));
                if (body[8] != 8 || body[12] != 0) throw new InvalidDataException("Only non-interlaced 8-bit PNG images are supported.");
                channels = body[9] switch
                {
                    2 => 3,
                    6 => 4,
                    _ => throw new InvalidDataException($"PNG color type {body[9]} is not supported."),
                };
            }
            else if (type.SequenceEqual("IDAT"u8)) compressed.Write(body);
            else if (type.SequenceEqual("IEND"u8)) break;
            offset += 12 + length;
        }

        if (width <= 0 || height <= 0) throw new InvalidDataException("The PNG image has no header.");
        compressed.Position = 0;
        var stride = width * channels;
        var raw = new byte[(stride + 1) * height];
        using (var inflate = new ZLibStream(compressed, CompressionMode.Decompress))
        {
            inflate.ReadExactly(raw);
        }

        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            var input = raw.AsSpan(y * (stride + 1) + 1, stride);
            var output = pixels.AsSpan(y * stride, stride);
            var previous = y == 0 ? default : pixels.AsSpan((y - 1) * stride, stride);
            for (var x = 0; x < stride; x++)
            {
                int left = x >= channels ? output[x - channels] : 0;
                int up = y == 0 ? 0 : previous[x];
                int upLeft = y == 0 || x < channels ? 0 : previous[x - channels];
                var predicted = filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, upLeft),
                    _ => throw new InvalidDataException($"PNG filter {filter} is not defined."),
                };
                output[x] = (byte)(input[x] + predicted);
            }
        }

        return new TestPng(width, height, channels, pixels);
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        var estimate = left + up - upLeft;
        var distanceLeft = Math.Abs(estimate - left);
        var distanceUp = Math.Abs(estimate - up);
        var distanceUpLeft = Math.Abs(estimate - upLeft);
        return distanceLeft <= distanceUp && distanceLeft <= distanceUpLeft ? left : distanceUp <= distanceUpLeft ? up : upLeft;
    }
}
