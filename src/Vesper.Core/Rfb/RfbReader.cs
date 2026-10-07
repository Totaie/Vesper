using System.Buffers.Binary;

namespace Vesper.Core.Rfb;

/// <summary>
/// Buffered big-endian reader over the server stream. Callers ensure a fixed number of bytes is
/// buffered with <see cref="EnsureAsync"/> and then parse synchronously, so the hot decode loops
/// don't pay for an async state machine per field.
/// </summary>
internal sealed class RfbReader
{
    private Stream _stream;
    private readonly byte[] _buffer;
    private int _start;
    private int _end;

    public RfbReader(Stream stream, int bufferSize = 1 << 18)
    {
        _stream = stream;
        _buffer = new byte[bufferSize];
    }

    /// <summary>Total bytes pulled off the wire, for the throughput readout.</summary>
    public long BytesReceived { get; private set; }

    public int Buffered => _end - _start;

    /// <summary>
    /// Switches to reading from <paramref name="next"/>, handing back whatever was already pulled
    /// off the old stream but not consumed. Used when Apple's record layer starts: those bytes are
    /// the front of the first encrypted record and must not be lost.
    /// </summary>
    public void SwitchSource(Func<byte[], Stream> next)
    {
        byte[] leftover = _buffer.AsSpan(_start, _end - _start).ToArray();
        _start = _end = 0;
        _stream = next(leftover);
    }

    public int Capacity => _buffer.Length;

    public ValueTask EnsureAsync(int count, CancellationToken ct)
    {
        if (_end - _start >= count)
        {
            return ValueTask.CompletedTask;
        }
        if (count > _buffer.Length)
        {
            throw new ProtocolException($"Read of {count} bytes exceeds the receive buffer.");
        }
        return FillAsync(count, ct);
    }

    private async ValueTask FillAsync(int count, CancellationToken ct)
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }
        while (_end < count)
        {
            int n = await _stream.ReadAsync(_buffer.AsMemory(_end, _buffer.Length - _end), ct).ConfigureAwait(false);
            if (n <= 0)
            {
                throw new ConnectionClosedException();
            }
            _end += n;
            BytesReceived += n;
        }
    }

    // Synchronous accessors: valid only after EnsureAsync covered the bytes.

    public byte U8() => _buffer[_start++];

    public ushort U16()
    {
        ushort v = BinaryPrimitives.ReadUInt16BigEndian(_buffer.AsSpan(_start, 2));
        _start += 2;
        return v;
    }

    public uint U32()
    {
        uint v = BinaryPrimitives.ReadUInt32BigEndian(_buffer.AsSpan(_start, 4));
        _start += 4;
        return v;
    }

    public int S32()
    {
        int v = BinaryPrimitives.ReadInt32BigEndian(_buffer.AsSpan(_start, 4));
        _start += 4;
        return v;
    }

    public ReadOnlySpan<byte> Take(int count)
    {
        var span = _buffer.AsSpan(_start, count);
        _start += count;
        return span;
    }

    public void Skip(int count) => _start += count;

    // Async conveniences for the non-hot paths (handshake, message headers).

    public async ValueTask<byte> ReadU8Async(CancellationToken ct)
    {
        await EnsureAsync(1, ct).ConfigureAwait(false);
        return U8();
    }

    public async ValueTask<ushort> ReadU16Async(CancellationToken ct)
    {
        await EnsureAsync(2, ct).ConfigureAwait(false);
        return U16();
    }

    public async ValueTask<uint> ReadU32Async(CancellationToken ct)
    {
        await EnsureAsync(4, ct).ConfigureAwait(false);
        return U32();
    }

    public async ValueTask<int> ReadS32Async(CancellationToken ct)
    {
        await EnsureAsync(4, ct).ConfigureAwait(false);
        return S32();
    }

    /// <summary>Reads exactly <paramref name="destination"/>.Length bytes, which may exceed the buffer size.</summary>
    public async ValueTask ReadExactlyAsync(Memory<byte> destination, CancellationToken ct)
    {
        int fromBuffer = Math.Min(_end - _start, destination.Length);
        _buffer.AsMemory(_start, fromBuffer).CopyTo(destination);
        _start += fromBuffer;
        var rest = destination[fromBuffer..];
        while (rest.Length > 0)
        {
            int n = await _stream.ReadAsync(rest, ct).ConfigureAwait(false);
            if (n <= 0)
            {
                throw new ConnectionClosedException();
            }
            BytesReceived += n;
            rest = rest[n..];
        }
    }

    public async ValueTask<byte[]> ReadBytesAsync(int count, CancellationToken ct)
    {
        var bytes = new byte[count];
        await ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return bytes;
    }

    public async ValueTask SkipAsync(long count, CancellationToken ct)
    {
        while (count > 0)
        {
            if (_end == _start)
            {
                await EnsureAsync(1, ct).ConfigureAwait(false);
            }
            int n = (int)Math.Min(count, _end - _start);
            _start += n;
            count -= n;
        }
    }
}
