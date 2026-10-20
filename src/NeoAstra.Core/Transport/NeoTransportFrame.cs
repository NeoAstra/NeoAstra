// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Buffers;
using System.Text;

namespace NeoAstra;

// One transport frame as UTF-8 JSON, in an array of ArrayPool<byte>.Shared, from where it is written to where it is sent
// or parsed: it is never turned into a string on the way unless a public string API asks for one. A frame the host writes
// keeps room before it for the envelope of the transport, which is then written around the frame where it lies, so that
// posting it copies nothing but the envelope.
//
// A frame has one owner at a time, and only that owner releases it, once, when nothing reads it any more: the buffer goes
// back to the pool, and a later access throws instead of reading what another frame wrote there. The buffer is returned
// without being cleared, as System.Text.Json returns the buffers it serializes into: what a frame carries is in managed
// memory already (the values it was serialized from, the strings of the page), and the pool stays in the process.
internal sealed class NeoTransportFrame : IBufferWriter<byte>
{
    // {"__neoastraTransport":1,"hostViewBinding":"<32 hex>","rendererDocumentId":"<id>","frame": is 110 bytes and the
    // renderer document ID, which the bootstrap makes of 32 characters: the room of a frame of the host fits it. A longer
    // envelope moves the frame once (Wrap).
    internal const int EnvelopeReserve = 256;
    private const int MinimumCapacity = 512;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private byte[]? _buffer;
    private int _start;
    private int _length;

    private static long s_outstanding;

    private NeoTransportFrame(byte[] buffer, int start, int length)
    {
        _buffer = buffer;
        _start = start;
        _length = length;
        Interlocked.Increment(ref s_outstanding);
    }

    // The frames not released yet, of every owner: a test of the owners of frames checks that what they took they gave back.
    internal static long Outstanding => Interlocked.Read(ref s_outstanding);

    // An empty frame of the host, written through IBufferWriter<byte>, with room for its envelope.
    internal static NeoTransportFrame Create(int capacity = 0)
        => new(ArrayPool<byte>.Shared.Rent(EnvelopeReserve + Math.Max(capacity, MinimumCapacity) + 1), EnvelopeReserve, 0);

    // A copy of UTF-8 the caller keeps, such as a frame of the page in the buffer of a native event.
    internal static NeoTransportFrame CopyOf(ReadOnlySpan<byte> utf8)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(utf8.Length, 1));
        utf8.CopyTo(buffer);
        return new(buffer, 0, utf8.Length);
    }

    // The UTF-8 of a string of a public API, with room for an envelope. A string that is not valid UTF-16 (a lone surrogate)
    // is refused with the ArgumentException that System.Text.Json throws for it when it parses the string.
    /// <exception cref="ArgumentException"><paramref name="json"/> is not valid UTF-16.</exception>
    internal static NeoTransportFrame FromString(string json, int reserve = EnvelopeReserve)
    {
        var length = StrictUtf8.GetByteCount(json);
        var buffer = ArrayPool<byte>.Shared.Rent(reserve + length + 1);
        try
        {
            StrictUtf8.GetBytes(json, buffer.AsSpan(reserve));
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
        return new(buffer, reserve, length);
    }

    internal int Length => _length;

    internal ReadOnlySpan<byte> Span => Buffer.AsSpan(_start, _length);

    internal ReadOnlyMemory<byte> Memory => Buffer.AsMemory(_start, _length);

    private byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(NeoTransportFrame));

    /// <inheritdoc />
    public void Advance(int count)
    {
        if (count < 0 || count > Buffer.Length - _start - _length) throw new ArgumentOutOfRangeException(nameof(count));
        _length += count;
    }

    /// <inheritdoc />
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Reserve(sizeHint);
        return Buffer.AsMemory(_start + _length);
    }

    /// <inheritdoc />
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Reserve(sizeHint);
        return Buffer.AsSpan(_start + _length);
    }

    internal void Write(byte value)
    {
        Reserve(1);
        Buffer[_start + _length++] = value;
    }

    // Writes the envelope of the transport around the frame in its buffer, the prefix before it and the closing brace after
    // it, and returns the whole message. The frame itself is not changed, so it can be wrapped again.
    internal ReadOnlyMemory<byte> Wrap(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length > _start) Move(prefix.Length);
        Reserve(1);
        var buffer = Buffer;
        var start = _start - prefix.Length;
        prefix.CopyTo(buffer.AsSpan(start));
        buffer[_start + _length] = (byte)'}';
        return buffer.AsMemory(start, prefix.Length + _length + 1);
    }

    // Gives the buffer back to the pool. Only the owner of the frame calls it, once nothing reads the frame.
    internal void Release()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is null) return;
        ArrayPool<byte>.Shared.Return(buffer);
        Interlocked.Decrement(ref s_outstanding);
    }

    /// <summary>Decodes the frame, for an API that gives it as a string.</summary>
    /// <returns>The frame as a string.</returns>
    public override string ToString() => Encoding.UTF8.GetString(Span);

    private void Reserve(int sizeHint)
    {
        var buffer = Buffer;
        var needed = (long)_start + _length + Math.Max(sizeHint, 1);
        if (needed <= buffer.Length) return;
        // One byte more than asked stays free for the closing brace of the envelope.
        var size = Math.Max(needed + 1, Math.Min((long)buffer.Length * 2, Array.MaxLength));
        if (size > Array.MaxLength) throw new OutOfMemoryException("A transport frame exceeds the largest array.");
        Replace((int)size, _start);
    }

    // Moves the frame so that a prefix of the given length fits before it.
    private void Move(int start)
    {
        var size = (long)start + _length + 1;
        if (size > Array.MaxLength) throw new OutOfMemoryException("A transport frame exceeds the largest array.");
        Replace((int)size, start);
    }

    private void Replace(int size, int start)
    {
        var buffer = Buffer;
        var next = ArrayPool<byte>.Shared.Rent(size);
        buffer.AsSpan(_start, _length).CopyTo(next.AsSpan(start));
        _buffer = next;
        _start = start;
        ArrayPool<byte>.Shared.Return(buffer);
    }
}
