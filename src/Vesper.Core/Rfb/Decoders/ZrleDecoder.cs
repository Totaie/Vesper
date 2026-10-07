namespace Vesper.Core.Rfb.Decoders;

/// <summary>
/// ZRLE (RFC 6143 §7.7.6): zlib-compressed 64×64 tiles, each raw, solid, packed-palette, RLE or
/// palette-RLE. The whole rectangle is inflated first (bounded), then parsed synchronously.
/// </summary>
internal static class ZrleDecoder
{
    private const int TileSize = 64;

    public static async ValueTask DecodeAsync(RfbReader r, Framebuffer fb, PixelRect rc, DecodeContext ctx, CancellationToken ct)
    {
        long tiles = (long)((rc.Width + TileSize - 1) / TileSize) * ((rc.Height + TileSize - 1) / TileSize);
        // Worst case per tile: one subencoding byte, a 127-entry palette, and 4 bytes per pixel
        // (a 3-byte CPIXEL plus a 1-byte run for every single-pixel run).
        long bound = rc.Area * 4 + tiles * (1 + 127 * 3);
        if (bound > int.MaxValue)
        {
            throw new ProtocolException("ZRLE rectangle is too large.");
        }
        var compressed = await StandardDecoders.ReadCompressedAsync(r, ctx, bound, ct).ConfigureAwait(false);
        var data = ctx.Scratch((int)bound);
        int produced = ctx.Zrle.Inflate(compressed, data, (int)bound);
        lock (fb.Sync)
        {
            Parse(data.AsSpan(0, produced), fb, rc);
            fb.MarkDirty(rc.X, rc.Y, rc.Width, rc.Height);
        }
    }

    internal static void Parse(ReadOnlySpan<byte> d, Framebuffer fb, PixelRect rc)
    {
        var px = Px.Words(fb);
        int stride = fb.Width;
        Span<uint> palette = stackalloc uint[128];
        int p = 0;

        for (int ty = rc.Y; ty < rc.Bottom; ty += TileSize)
        {
            int th = Math.Min(TileSize, rc.Bottom - ty);
            for (int tx = rc.X; tx < rc.Right; tx += TileSize)
            {
                int tw = Math.Min(TileSize, rc.Right - tx);
                Need(d, p, 1);
                int sub = d[p++];

                if (sub == 0)
                {
                    Need(d, p, tw * th * 3);
                    for (int y = 0; y < th; y++)
                    {
                        var row = px.Slice((ty + y) * stride + tx, tw);
                        for (int x = 0; x < tw; x++, p += 3) row[x] = Px.CPixel(d, p);
                    }
                }
                else if (sub == 1)
                {
                    Need(d, p, 3);
                    uint c = Px.CPixel(d, p);
                    p += 3;
                    for (int y = 0; y < th; y++) px.Slice((ty + y) * stride + tx, tw).Fill(c);
                }
                else if (sub <= 16)
                {
                    int n = sub;
                    p = ReadPalette(d, p, palette, n);
                    int bits = n == 2 ? 1 : n <= 4 ? 2 : 4;
                    int mask = (1 << bits) - 1;
                    int rowBytes = (tw * bits + 7) / 8;
                    Need(d, p, rowBytes * th);
                    for (int y = 0; y < th; y++)
                    {
                        var row = px.Slice((ty + y) * stride + tx, tw);
                        for (int x = 0; x < tw; x++)
                        {
                            int bit = x * bits;
                            int idx = (d[p + (bit >> 3)] >> (8 - bits - (bit & 7))) & mask;
                            if (idx >= n) throw new ProtocolException("ZRLE palette index out of range.");
                            row[x] = palette[idx];
                        }
                        p += rowBytes;
                    }
                }
                else if (sub == 128)
                {
                    int total = tw * th, i = 0;
                    while (i < total)
                    {
                        Need(d, p, 3);
                        uint c = Px.CPixel(d, p);
                        p += 3;
                        int run = ReadRunLength(d, ref p, total - i);
                        WriteRun(px, stride, tx, ty, tw, i, run, c);
                        i += run;
                    }
                }
                else if (sub >= 130)
                {
                    int n = sub - 128;
                    p = ReadPalette(d, p, palette, n);
                    int total = tw * th, i = 0;
                    while (i < total)
                    {
                        Need(d, p, 1);
                        int b = d[p++];
                        int idx = b & 0x7F;
                        if (idx >= n) throw new ProtocolException("ZRLE palette index out of range.");
                        int run = (b & 0x80) != 0 ? ReadRunLength(d, ref p, total - i) : 1;
                        if (run > total - i) throw new ProtocolException("ZRLE run overflows its tile.");
                        WriteRun(px, stride, tx, ty, tw, i, run, palette[idx]);
                        i += run;
                    }
                }
                else
                {
                    throw new ProtocolException($"Unknown ZRLE subencoding {sub}.");
                }
            }
        }
    }

    private static int ReadPalette(ReadOnlySpan<byte> d, int p, Span<uint> palette, int n)
    {
        Need(d, p, n * 3);
        for (int i = 0; i < n; i++, p += 3) palette[i] = Px.CPixel(d, p);
        return p;
    }

    private static int ReadRunLength(ReadOnlySpan<byte> d, ref int p, int remaining)
    {
        int run = 1;
        int b;
        do
        {
            Need(d, p, 1);
            b = d[p++];
            run += b;
            if (run > remaining) throw new ProtocolException("ZRLE run overflows its tile.");
        }
        while (b == 255);
        return run;
    }

    /// <summary>Writes <paramref name="run"/> pixels starting at tile-linear index <paramref name="i"/>.</summary>
    private static void WriteRun(Span<uint> px, int stride, int tx, int ty, int tw, int i, int run, uint c)
    {
        int y = i / tw, x = i - y * tw;
        while (run > 0)
        {
            int n = Math.Min(run, tw - x);
            px.Slice((ty + y) * stride + tx + x, n).Fill(c);
            run -= n;
            x = 0;
            y++;
        }
    }

    private static void Need(ReadOnlySpan<byte> d, int p, int n)
    {
        if (p + n > d.Length) throw new ProtocolException("ZRLE data ended mid-tile.");
    }
}
