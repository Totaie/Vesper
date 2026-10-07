using System.IO.Compression;

namespace Vesper.Core.Rfb.Decoders;

/// <summary>
/// One persistent zlib stream, as RFB's Zlib/ZRLE/Tight encodings require: the server keeps a
/// single deflate context for the whole session and sync-flushes at the end of every rectangle,
/// so each rectangle's compressed bytes inflate completely on their own once fed in order.
/// </summary>
internal sealed class ZlibInflater : IDisposable
{
    private readonly FeedStream _feed = new();
    private readonly ZLibStream _zlib;

    public ZlibInflater() => _zlib = new ZLibStream(_feed, CompressionMode.Decompress, leaveOpen: true);

    /// <summary>
    /// Inflates one rectangle's compressed bytes into <paramref name="output"/>. Producing more than
    /// <paramref name="maxOutput"/> bytes is a protocol error, which stops a hostile server's zip bomb.
    /// </summary>
    public int Inflate(ArraySegment<byte> input, byte[] output, int maxOutput)
    {
        _feed.Set(input);
        int total = 0;
        try
        {
            while (true)
            {
                if (total == maxOutput)
                {
                    // Anything beyond the bound is either garbage or an attack. (The probe can't
                    // just check the feed: the inflater buffers input internally.)
                    Span<byte> probe = stackalloc byte[1];
                    if (_zlib.Read(probe) > 0)
                    {
                        throw new ProtocolException("Compressed rectangle inflates past its maximum size.");
                    }
                    break;
                }
                int n = _zlib.Read(output, total, maxOutput - total);
                if (n == 0) break;
                total += n;
            }
        }
        catch (InvalidDataException e)
        {
            throw new ProtocolException("Corrupt compressed data from the Mac: " + e.Message);
        }
        return total;
    }

    public void Dispose()
    {
        _zlib.Dispose();
        _feed.Dispose();
    }

    private sealed class FeedStream : Stream
    {
        private ArraySegment<byte> _data;
        private int _pos;

        public void Set(ArraySegment<byte> data)
        {
            _data = data;
            _pos = 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = Math.Min(buffer.Length, _data.Count - _pos);
            _data.AsSpan(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
