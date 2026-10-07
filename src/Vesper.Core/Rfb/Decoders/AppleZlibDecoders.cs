namespace Vesper.Core.Rfb.Decoders;

/// <summary>
/// Apple's reduced-depth zlib encodings, each with its own persistent deflate stream:
/// 0x3ea is big-endian RGB 5-5-5 ("thousands of colors"), 0x3e9 is 4-bit grey, high nibble first.
/// They halve or better the bytes a Full-colour update needs, at a cost in colour fidelity.
/// </summary>
internal static class AppleZlibDecoders
{
    public static async ValueTask Rgb555Async(RfbReader r, Framebuffer fb, PixelRect rc, DecodeContext ctx, CancellationToken ct)
    {
        int rowBytes = rc.Width * 2;
        int expected = checked(rowBytes * rc.Height);
        var compressed = await StandardDecoders.ReadCompressedAsync(r, ctx, expected, ct).ConfigureAwait(false);
        var data = ctx.Scratch(expected);
        if (ctx.Rgb555.Inflate(compressed, data, expected) != expected)
        {
            throw new ProtocolException("RGB 5-5-5 rectangle inflated to the wrong size.");
        }
        lock (fb.Sync)
        {
            var px = Px.Words(fb);
            for (int y = 0; y < rc.Height; y++)
            {
                var row = px.Slice((rc.Y + y) * fb.Width + rc.X, rc.Width);
                int p = y * rowBytes;
                for (int x = 0; x < rc.Width; x++, p += 2)
                {
                    int v = data[p] << 8 | data[p + 1];
                    uint red = Expand5((v >> 10) & 31), green = Expand5((v >> 5) & 31), blue = Expand5(v & 31);
                    row[x] = blue | green << 8 | red << 16 | Px.Opaque;
                }
            }
            fb.MarkDirty(rc.X, rc.Y, rc.Width, rc.Height);
        }
    }

    public static async ValueTask Gray4Async(RfbReader r, Framebuffer fb, PixelRect rc, DecodeContext ctx, CancellationToken ct)
    {
        int rowBytes = (rc.Width + 1) / 2;
        int expected = checked(rowBytes * rc.Height);
        var compressed = await StandardDecoders.ReadCompressedAsync(r, ctx, expected, ct).ConfigureAwait(false);
        var data = ctx.Scratch(expected);
        if (ctx.Gray4.Inflate(compressed, data, expected) != expected)
        {
            throw new ProtocolException("Greyscale rectangle inflated to the wrong size.");
        }
        lock (fb.Sync)
        {
            var px = Px.Words(fb);
            for (int y = 0; y < rc.Height; y++)
            {
                var row = px.Slice((rc.Y + y) * fb.Width + rc.X, rc.Width);
                int p = y * rowBytes;
                for (int x = 0; x < rc.Width; x++)
                {
                    int nibble = (x & 1) == 0 ? data[p + x / 2] >> 4 : data[p + x / 2] & 15;
                    uint g = (uint)(nibble * 17);
                    row[x] = g | g << 8 | g << 16 | Px.Opaque;
                }
            }
            fb.MarkDirty(rc.X, rc.Y, rc.Width, rc.Height);
        }
    }

    private static uint Expand5(int v) => (uint)(v << 3 | v >> 2);
}
