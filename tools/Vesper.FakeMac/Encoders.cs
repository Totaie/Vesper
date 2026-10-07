using System.Buffers.Binary;
using System.IO.Compression;

namespace Vesper.FakeMac;

/// <summary>
/// Server-side encoders for the pixel encodings Vesper decodes. The ZRLE encoder deliberately uses
/// every subencoding (solid, packed palette, palette RLE, plain RLE, raw), so a session against
/// FakeMac exercises every path in Vesper's decoder. Pixels are 32bpp B,G,R,x.
/// </summary>
public sealed class Encoders : IDisposable
{
    private readonly PersistentDeflate _zlib = new(CompressionLevel.Fastest);
    private readonly PersistentDeflate _zrle = new(CompressionLevel.Fastest);
    private readonly PersistentDeflate _rgb555 = new(CompressionLevel.Fastest);
    private readonly PersistentDeflate _gray = new(CompressionLevel.Optimal);

    /// <summary>zlib (6): raw pixels through the session's deflate stream; returns u32 length + data.</summary>
    public byte[] Zlib(byte[] fb, int stride, int x, int y, int w, int h)
    {
        var raw = new byte[w * h * 4];
        for (int row = 0; row < h; row++)
        {
            Buffer.BlockCopy(fb, (y + row) * stride + x * 4, raw, row * w * 4, w * 4);
        }
        return _zlib.Compress(raw);
    }

    /// <summary>Apple 0x3ea: big-endian RGB 5-5-5 rows through their own deflate stream.</summary>
    public byte[] Rgb555(byte[] fb, int stride, int x, int y, int w, int h)
    {
        var raw = new byte[w * h * 2];
        for (int row = 0; row < h; row++)
        {
            for (int col = 0; col < w; col++)
            {
                int p = (y + row) * stride + (x + col) * 4;
                int v = (fb[p + 2] >> 3) << 10 | (fb[p + 1] >> 3) << 5 | fb[p] >> 3;
                BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan((row * w + col) * 2), (ushort)v);
            }
        }
        return _rgb555.Compress(raw);
    }

    /// <summary>Apple 0x3e9: 4-bit grey, high nibble first, grey = (5R + 9G + 2B) / 16.</summary>
    public byte[] Gray4(byte[] fb, int stride, int x, int y, int w, int h)
    {
        int rowBytes = (w + 1) / 2;
        var raw = new byte[rowBytes * h];
        for (int row = 0; row < h; row++)
        {
            for (int col = 0; col < w; col++)
            {
                int p = (y + row) * stride + (x + col) * 4;
                int grey = (5 * fb[p + 2] + 9 * fb[p + 1] + 2 * fb[p]) / 16;
                int nibble = grey >> 4;
                raw[row * rowBytes + col / 2] |= (byte)((col & 1) == 0 ? nibble << 4 : nibble);
            }
        }
        return _gray.Compress(raw);
    }

    /// <summary>ZRLE (16): 64×64 tiles, choosing the smallest subencoding per tile.</summary>
    public byte[] Zrle(byte[] fb, int stride, int x, int y, int w, int h)
    {
        using var tiles = new MemoryStream();
        var pixels = new uint[64 * 64];
        for (int ty = y; ty < y + h; ty += 64)
        {
            int th = Math.Min(64, y + h - ty);
            for (int tx = x; tx < x + w; tx += 64)
            {
                int tw = Math.Min(64, x + w - tx);
                int n = tw * th;
                for (int row = 0; row < th; row++)
                {
                    for (int col = 0; col < tw; col++)
                    {
                        pixels[row * tw + col] = BinaryPrimitives.ReadUInt32LittleEndian(fb.AsSpan((ty + row) * stride + (tx + col) * 4)) & 0xFFFFFF;
                    }
                }
                EncodeTile(tiles, pixels.AsSpan(0, n), tw);
            }
        }
        return _zrle.Compress(tiles.ToArray());
    }

    private static void EncodeTile(MemoryStream o, ReadOnlySpan<uint> px, int tw)
    {
        var palette = new List<uint>();
        var index = new Dictionary<uint, int>();
        foreach (uint c in px)
        {
            if (index.ContainsKey(c)) continue;
            if (palette.Count == 127) { palette.Clear(); index.Clear(); break; }
            index[c] = palette.Count;
            palette.Add(c);
        }
        bool fitsPalette = palette.Count > 0;

        if (fitsPalette && palette.Count == 1)
        {
            o.WriteByte(1);
            WriteCPixel(o, palette[0]);
            return;
        }

        var candidates = new List<byte[]>();
        if (fitsPalette && palette.Count <= 16) candidates.Add(PackedPalette(px, tw, palette, index));
        if (fitsPalette) candidates.Add(PaletteRle(px, palette, index));
        candidates.Add(PlainRle(px));
        candidates.Add(Raw(px));
        o.Write(candidates.MinBy(c => c.Length)!);
    }

    private static byte[] Raw(ReadOnlySpan<uint> px)
    {
        using var m = new MemoryStream();
        m.WriteByte(0);
        foreach (uint c in px) WriteCPixel(m, c);
        return m.ToArray();
    }

    private static byte[] PackedPalette(ReadOnlySpan<uint> px, int tw, List<uint> palette, Dictionary<uint, int> index)
    {
        using var m = new MemoryStream();
        int n = palette.Count;
        m.WriteByte((byte)n);
        foreach (uint c in palette) WriteCPixel(m, c);
        int bits = n == 2 ? 1 : n <= 4 ? 2 : 4;
        int th = px.Length / tw;
        for (int row = 0; row < th; row++)
        {
            int acc = 0, used = 0;
            for (int col = 0; col < tw; col++)
            {
                acc = acc << bits | index[px[row * tw + col]];
                used += bits;
                if (used == 8) { m.WriteByte((byte)acc); acc = 0; used = 0; }
            }
            if (used > 0) m.WriteByte((byte)(acc << (8 - used)));
        }
        return m.ToArray();
    }

    private static byte[] PaletteRle(ReadOnlySpan<uint> px, List<uint> palette, Dictionary<uint, int> index)
    {
        using var m = new MemoryStream();
        m.WriteByte((byte)(128 + palette.Count));
        foreach (uint c in palette) WriteCPixel(m, c);
        for (int i = 0; i < px.Length;)
        {
            int run = 1;
            while (i + run < px.Length && px[i + run] == px[i]) run++;
            if (run == 1)
            {
                m.WriteByte((byte)index[px[i]]);
            }
            else
            {
                m.WriteByte((byte)(index[px[i]] | 0x80));
                WriteRunLength(m, run);
            }
            i += run;
        }
        return m.ToArray();
    }

    private static byte[] PlainRle(ReadOnlySpan<uint> px)
    {
        using var m = new MemoryStream();
        m.WriteByte(128);
        for (int i = 0; i < px.Length;)
        {
            int run = 1;
            while (i + run < px.Length && px[i + run] == px[i]) run++;
            WriteCPixel(m, px[i]);
            WriteRunLength(m, run);
            i += run;
        }
        return m.ToArray();
    }

    private static void WriteRunLength(MemoryStream m, int run)
    {
        int rest = run - 1;
        while (rest >= 255) { m.WriteByte(255); rest -= 255; }
        m.WriteByte((byte)rest);
    }

    private static void WriteCPixel(MemoryStream m, uint c)
    {
        m.WriteByte((byte)c);
        m.WriteByte((byte)(c >> 8));
        m.WriteByte((byte)(c >> 16));
    }

    public void Dispose()
    {
        _zlib.Dispose();
        _zrle.Dispose();
        _rgb555.Dispose();
        _gray.Dispose();
    }

    /// <summary>One deflate context for a whole session, sync-flushed per rectangle, as RFB servers do it.</summary>
    private sealed class PersistentDeflate(CompressionLevel level) : IDisposable
    {
        private readonly MemoryStream _sink = new();
        private ZLibStream? _zlib;

        public byte[] Compress(byte[] data)
        {
            _zlib ??= new ZLibStream(_sink, level, leaveOpen: true);
            long start = _sink.Length;
            _zlib.Write(data);
            _zlib.Flush();
            int length = (int)(_sink.Length - start);
            var result = new byte[4 + length];
            BinaryPrimitives.WriteUInt32BigEndian(result, (uint)length);
            Array.Copy(_sink.GetBuffer(), start, result, 4, length);
            _sink.SetLength(0);
            return result;
        }

        public void Dispose()
        {
            _zlib?.Dispose();
            _sink.Dispose();
        }
    }
}
