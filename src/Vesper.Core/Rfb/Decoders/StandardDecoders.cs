namespace Vesper.Core.Rfb.Decoders;

/// <summary>
/// The RFC 6143 encodings. Every decoder validates geometry against the framebuffer before writing,
/// so a hostile or buggy server can't push writes outside it.
/// </summary>
internal static class StandardDecoders
{
    public static async ValueTask RawAsync(RfbReader r, Framebuffer fb, PixelRect rc, DecodeContext ctx, CancellationToken ct)
    {
        int rowBytes = rc.Width * 4;
        if (rowBytes == 0 || rc.Height == 0) return;
        int rowsPerChunk = Math.Max(1, (1 << 20) / rowBytes);
        var buf = ctx.Scratch(rowsPerChunk * rowBytes);
        for (int y = 0; y < rc.Height;)
        {
            int rows = Math.Min(rowsPerChunk, rc.Height - y);
            await r.ReadExactlyAsync(buf.AsMemory(0, rows * rowBytes), ct).ConfigureAwait(false);
            lock (fb.Sync)
            {
                Px.CopyRows(buf.AsSpan(0, rows * rowBytes), fb, rc.X, rc.Y + y, rc.Width, rows);
                fb.MarkDirty(rc.X, rc.Y + y, rc.Width, rows);
            }
            y += rows;
        }
    }

    public static async ValueTask CopyRectAsync(RfbReader r, Framebuffer fb, PixelRect rc, CancellationToken ct)
    {
        await r.EnsureAsync(4, ct).ConfigureAwait(false);
        int sx = r.U16(), sy = r.U16();
        if (!fb.Contains(sx, sy, rc.Width, rc.Height))
        {
            throw new ProtocolException("CopyRect source lies outside the screen.");
        }
        lock (fb.Sync)
        {
            var px = fb.Pixels.AsSpan();
            int rowBytes = rc.Width * 4;
            // Span.CopyTo is memmove, so overlap within a row is safe; copying bottom-up when the
            // source is above the destination handles vertical overlap.
            bool bottomUp = sy < rc.Y;
            for (int i = 0; i < rc.Height; i++)
            {
                int row = bottomUp ? rc.Height - 1 - i : i;
                px.Slice(((sy + row) * fb.Width + sx) * 4, rowBytes)
                  .CopyTo(px.Slice(((rc.Y + row) * fb.Width + rc.X) * 4, rowBytes));
            }
            fb.MarkDirty(rc.X, rc.Y, rc.Width, rc.Height);
        }
    }

    public static async ValueTask RreAsync(RfbReader r, Framebuffer fb, PixelRect rc, CancellationToken ct)
    {
        await r.EnsureAsync(8, ct).ConfigureAwait(false);
        uint count = r.U32();
        uint bg = Px.Pixel32(r.Take(4), 0);
        if (count > rc.Area)
        {
            throw new ProtocolException("RRE rectangle claims more subrectangles than pixels.");
        }
        lock (fb.Sync)
        {
            Px.Fill(fb, rc.X, rc.Y, rc.Width, rc.Height, bg);
        }
        const int SubrectSize = 12;
        int batchMax = r.Capacity / SubrectSize;
        while (count > 0)
        {
            int batch = (int)Math.Min(count, (uint)batchMax);
            await r.EnsureAsync(batch * SubrectSize, ct).ConfigureAwait(false);
            lock (fb.Sync)
            {
                for (int i = 0; i < batch; i++)
                {
                    uint colour = Px.Pixel32(r.Take(4), 0);
                    int x = r.U16(), y = r.U16(), w = r.U16(), h = r.U16();
                    if (x + w > rc.Width || y + h > rc.Height)
                    {
                        throw new ProtocolException("RRE subrectangle lies outside its rectangle.");
                    }
                    Px.Fill(fb, rc.X + x, rc.Y + y, w, h, colour);
                }
            }
            count -= (uint)batch;
        }
        lock (fb.Sync)
        {
            fb.MarkDirty(rc.X, rc.Y, rc.Width, rc.Height);
        }
    }

    public static async ValueTask HextileAsync(RfbReader r, Framebuffer fb, PixelRect rc, CancellationToken ct)
    {
        const int Raw = 1, BackgroundSpecified = 2, ForegroundSpecified = 4, AnySubrects = 8, SubrectsColoured = 16;
        uint bg = Px.Opaque, fg = Px.Opaque;
        for (int ty = rc.Y; ty < rc.Bottom; ty += 16)
        {
            int th = Math.Min(16, rc.Bottom - ty);
            for (int tx = rc.X; tx < rc.Right; tx += 16)
            {
                int tw = Math.Min(16, rc.Right - tx);
                await r.EnsureAsync(1, ct).ConfigureAwait(false);
                int flags = r.U8();
                if ((flags & Raw) != 0)
                {
                    int bytes = tw * th * 4;
                    await r.EnsureAsync(bytes, ct).ConfigureAwait(false);
                    lock (fb.Sync)
                    {
                        Px.CopyRows(r.Take(bytes), fb, tx, ty, tw, th);
                    }
                    continue;
                }
                int header = ((flags & BackgroundSpecified) != 0 ? 4 : 0) + ((flags & ForegroundSpecified) != 0 ? 4 : 0) + ((flags & AnySubrects) != 0 ? 1 : 0);
                await r.EnsureAsync(header, ct).ConfigureAwait(false);
                if ((flags & BackgroundSpecified) != 0) bg = Px.Pixel32(r.Take(4), 0);
                if ((flags & ForegroundSpecified) != 0) fg = Px.Pixel32(r.Take(4), 0);
                int subrects = (flags & AnySubrects) != 0 ? r.U8() : 0;
                bool coloured = (flags & SubrectsColoured) != 0;
                int subrectBytes = subrects * (coloured ? 6 : 2);
                await r.EnsureAsync(subrectBytes, ct).ConfigureAwait(false);
                lock (fb.Sync)
                {
                    Px.Fill(fb, tx, ty, tw, th, bg);
                    for (int i = 0; i < subrects; i++)
                    {
                        uint colour = coloured ? Px.Pixel32(r.Take(4), 0) : fg;
                        int xy = r.U8(), wh = r.U8();
                        int sx = xy >> 4, sy = xy & 15, sw = (wh >> 4) + 1, sh = (wh & 15) + 1;
                        if (sx + sw > tw || sy + sh > th)
                        {
                            throw new ProtocolException("Hextile subrectangle lies outside its tile.");
                        }
                        Px.Fill(fb, tx + sx, ty + sy, sw, sh, colour);
                    }
                }
            }
        }
        lock (fb.Sync)
        {
            fb.MarkDirty(rc.X, rc.Y, rc.Width, rc.Height);
        }
    }

    public static async ValueTask ZlibAsync(RfbReader r, Framebuffer fb, PixelRect rc, DecodeContext ctx, CancellationToken ct)
    {
        int expected = checked(rc.Width * rc.Height * 4);
        var compressed = await ReadCompressedAsync(r, ctx, expected, ct).ConfigureAwait(false);
        var output = ctx.Scratch(expected);
        int produced = ctx.Zlib.Inflate(compressed, output, expected);
        if (produced != expected)
        {
            throw new ProtocolException("Zlib rectangle inflated to the wrong size.");
        }
        lock (fb.Sync)
        {
            Px.CopyRows(output.AsSpan(0, expected), fb, rc.X, rc.Y, rc.Width, rc.Height);
            fb.MarkDirty(rc.X, rc.Y, rc.Width, rc.Height);
        }
    }

    /// <summary>Reads a u32 length plus that many compressed bytes, bounded by what the rect could need.</summary>
    internal static async ValueTask<ArraySegment<byte>> ReadCompressedAsync(RfbReader r, DecodeContext ctx, long uncompressedBound, CancellationToken ct)
    {
        uint length = await r.ReadU32Async(ct).ConfigureAwait(false);
        // Deflate never expands incompressible data by more than a few bytes per 16 KiB block.
        if (length > uncompressedBound + (uncompressedBound >> 6) + 4096)
        {
            throw new ProtocolException("Compressed rectangle is larger than its contents could be.");
        }
        var buf = ctx.Compressed((int)length);
        await r.ReadExactlyAsync(buf.AsMemory(0, (int)length), ct).ConfigureAwait(false);
        return new ArraySegment<byte>(buf, 0, (int)length);
    }
}
