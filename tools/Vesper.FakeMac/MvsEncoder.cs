using System.Buffers.Binary;
using System.Drawing;

namespace Vesper.FakeMac;

/// <summary>
/// A small encoder for Apple's adaptive codec (1011), enough to drive Vesper's decoder through a
/// real session: base passes only, with white tiles, copies from the left and above, one- and
/// two-colour tiles in MVS's 20-bit YCbCr, and anything busier as a flat DCT block of its average
/// colour. That's lossy, as the real codec is, so it also keeps the picture a correct decoder
/// shows, for the tests to compare against.
/// </summary>
public sealed class MvsEncoder
{
    /// <summary>Encodes an 8-aligned rectangle; returns u32 length + body, and updates <paramref name="view"/>.</summary>
    public byte[] Encode(byte[] fb, int fbWidth, int fbHeight, Rectangle r, byte[] view)
    {
        var commands = new BitWriter();
        var operands = new BitWriter();
        commands.Write(0, 1); // initial state
        int tilesWide = (r.Width + 7) / 8, tilesHigh = (r.Height + 7) / 8;
        int prevLuma = 0, prevCb = 0, prevCr = 0;
        Span<uint> tile = stackalloc uint[64];
        Span<uint> other = stackalloc uint[64];
        Span<byte> masks = stackalloc byte[8];

        for (int ty = 0; ty < tilesHigh; ty++)
        {
            for (int tx = 0; tx < tilesWide; tx++)
            {
                int x = r.X + tx * 8, y = r.Y + ty * 8;
                int w = Math.Min(8, fbWidth - x), h = Math.Min(8, fbHeight - y);
                Read(fb, fbWidth, x, y, w, h, tile);

                if (IsAll(tile, w, h, 0xFFFFFF))
                {
                    commands.Write(0, 3);
                    commands.Write(0, 1);
                    Fill(view, fbWidth, x, y, w, h, 0xFFFFFF);
                    continue;
                }
                // Copies only from tiles drawn earlier in this pass, so the viewer has exactly them.
                if (tx > 0)
                {
                    Read(fb, fbWidth, x - 8, y, w, h, other);
                    if (Same(tile, other, w, h))
                    {
                        commands.Write(1, 3);
                        commands.Write(0, 1);
                        Copy(view, fbWidth, x - 8, y, x, y, w, h);
                        continue;
                    }
                }
                if (ty > 0)
                {
                    Read(fb, fbWidth, x, y - 8, w, h, other);
                    if (Same(tile, other, w, h))
                    {
                        commands.Write(2, 3);
                        commands.Write(0, 1);
                        Copy(view, fbWidth, x, y - 8, x, y, w, h);
                        continue;
                    }
                }

                uint first = tile[0], second = first;
                bool twoOrFewer = true;
                for (int i = 0; i < 64 && twoOrFewer; i++)
                {
                    if (i % 8 >= w || i / 8 >= h || tile[i] == first) continue;
                    if (second == first) second = tile[i];
                    else if (tile[i] != second) twoOrFewer = false;
                }

                if (twoOrFewer && second == first)
                {
                    commands.Write(4, 3);
                    commands.Write(0, 1);
                    operands.Write(0, 1); // one colour
                    operands.Write(0, 1); // a new one
                    uint shown = WriteColour(operands, first);
                    Fill(view, fbWidth, x, y, w, h, shown);
                }
                else if (twoOrFewer)
                {
                    commands.Write(4, 3);
                    commands.Write(0, 1);
                    operands.Write(1, 1); // two colours
                    operands.Write(0, 1); // new ones
                    uint shownFirst = WriteColour(operands, first);
                    uint shownSecond = WriteColour(operands, second);
                    // Rows of nothing but the first colour are marked uniform; others get a mask in
                    // which a set bit is the first colour. Columns past a clipped edge count as first.
                    uint uniformRows = 0;
                    for (int row = 0; row < 8; row++)
                    {
                        byte mask = 0;
                        for (int col = 0; col < 8; col++)
                        {
                            bool isFirst = row >= h || col >= w || tile[row * 8 + col] == first;
                            if (isFirst) mask |= (byte)(0x80 >> col);
                        }
                        masks[row] = mask;
                        if (mask == 0xFF) uniformRows |= 0x80u >> row;
                    }
                    operands.Write(uniformRows, 8);
                    for (int row = 0; row < 8; row++)
                    {
                        if ((uniformRows & (0x80u >> row)) == 0) operands.Write(masks[row], 8);
                    }
                    for (int row = 0; row < h; row++)
                    {
                        for (int col = 0; col < w; col++)
                        {
                            Set(view, fbWidth, x + col, y + row, tile[row * 8 + col] == first ? shownFirst : shownSecond);
                        }
                    }
                }
                else
                {
                    // A DCT block with only DC terms: the tile's average colour.
                    commands.Write(5, 3);
                    commands.Write(0, 1);
                    double sumY = 0, sumCb = 0, sumCr = 0;
                    for (int row = 0; row < h; row++)
                    {
                        for (int col = 0; col < w; col++)
                        {
                            var (Y, Cb, Cr) = ToYCbCr(tile[row * 8 + col]);
                            sumY += Y;
                            sumCb += Cb;
                            sumCr += Cr;
                        }
                    }
                    int n = w * h;
                    int luma = Math.Clamp((int)Math.Round((sumY / n - 128) / 2), -64, 63);
                    int cbHalf = (int)Math.Round((sumCb / n - 128) * 8 / 38);
                    int crHalf = (int)Math.Round((sumCr / n - 128) * 8 / 38);
                    operands.Write(0, 1); // a new block
                    operands.Write(0, 2); // predict both chroma DC terms
                    // The viewer computes cb = (prevCb / 2 − delta) · 2; this block's Cb is 2·cbHalf.
                    WriteDcRice(operands, prevCb / 2 - cbHalf);
                    WriteDcRice(operands, prevCr / 2 - crHalf);
                    WriteDcRice(operands, prevLuma - luma);
                    operands.Write(0, 1); // no AC term here
                    operands.Write(1, 2); // …
                    operands.Write(0, 1); // end of block
                    prevLuma = luma;
                    prevCb = cbHalf * 2;
                    prevCr = crHalf * 2;
                    int sampleY = Math.Clamp(luma * 2 + 128, 0, 255);
                    int sampleCb = Math.Clamp(((prevCb * 19 + 4) >> 3) + 128, 0, 255);
                    int sampleCr = Math.Clamp(((prevCr * 19 + 4) >> 3) + 128, 0, 255);
                    Fill(view, fbWidth, x, y, w, h, FromYCbCr(sampleY, sampleCb, sampleCr));
                }
            }
        }
        commands.Write(0x6d, 8);
        operands.Write(0x6d, 8);
        byte[] primary = commands.ToArray(), secondary = operands.ToArray();
        int offset = 6 + primary.Length;
        var packet = new byte[4 + offset + secondary.Length];
        BinaryPrimitives.WriteUInt32BigEndian(packet, (uint)(offset + secondary.Length));
        packet[4] = 0; // a base pass
        packet[7] = (byte)(offset >> 16);
        packet[8] = (byte)(offset >> 8);
        packet[9] = (byte)offset;
        primary.CopyTo(packet, 10);
        secondary.CopyTo(packet, 4 + offset);
        return packet;
    }

    /// <summary>Writes a colour as Y and the top six bits of Cb and Cr; returns how a viewer shows it.</summary>
    private static uint WriteColour(BitWriter w, uint rgb)
    {
        var (y, cb, cr) = ToYCbCr(rgb);
        int y8 = Math.Clamp((int)Math.Round(y), 0, 255);
        int cb6 = Math.Clamp((int)Math.Round(cb / 4), 0, 63);
        int cr6 = Math.Clamp((int)Math.Round(cr / 4), 0, 63);
        w.Write((uint)y8, 8);
        w.Write((uint)cb6, 6);
        w.Write((uint)cr6, 6);
        return FromYCbCr(y8, cb6 * 4, cr6 * 4);
    }

    /// <summary>The inverse of Apple's Rice-coded DC term.</summary>
    private static void WriteDcRice(BitWriter w, int v)
    {
        uint sign = v < 0 ? 1u : 0u;
        int m = Math.Abs(v);
        if (m == 0)
        {
            w.Write(0, 2);
        }
        else if (m == 1)
        {
            w.Write(0b01, 2);
            w.Write(sign, 1);
        }
        else if (m <= 3)
        {
            w.Write(0b10, 2);
            w.Write((uint)(m - 2) << 1 | sign, 2);
        }
        else if (m <= 7)
        {
            w.Write(0b110, 3);
            w.Write((uint)(m - 4) << 1 | sign, 3);
        }
        else
        {
            int prefix = (m + 16) / 8;
            for (int i = 0; i < prefix; i++) w.Write(1, 1);
            w.Write(0, 1);
            w.Write((uint)(m - (prefix * 8 - 16)) << 1 | sign, 4);
        }
    }

    private static (double Y, double Cb, double Cr) ToYCbCr(uint rgb)
    {
        double r = rgb >> 16 & 0xff, g = rgb >> 8 & 0xff, b = rgb & 0xff;
        return (0.299 * r + 0.587 * g + 0.114 * b,
            128 - 0.168736 * r - 0.331264 * g + 0.5 * b,
            128 + 0.5 * r - 0.418688 * g - 0.081312 * b);
    }

    /// <summary>JPEG's full-range conversion in Apple's 16.16 fixed point (as the viewer must do it).</summary>
    private static uint FromYCbCr(int y, int cb, int cr)
    {
        cb -= 128;
        cr -= 128;
        int r = Math.Clamp(y + ((91_881 * cr + 32_768) >> 16), 0, 255);
        int g = Math.Clamp(y + ((32_768 - 22_554 * cb - 46_802 * cr) >> 16), 0, 255);
        int b = Math.Clamp(y + ((116_130 * cb + 32_768) >> 16), 0, 255);
        return (uint)(r << 16 | g << 8 | b);
    }

    private static void Read(byte[] fb, int fbWidth, int x, int y, int w, int h, Span<uint> tile)
    {
        tile.Clear();
        for (int row = 0; row < h; row++)
        {
            for (int col = 0; col < w; col++)
            {
                int p = ((y + row) * fbWidth + x + col) * 4;
                tile[row * 8 + col] = (uint)(fb[p + 2] << 16 | fb[p + 1] << 8 | fb[p]);
            }
        }
    }

    private static bool IsAll(ReadOnlySpan<uint> tile, int w, int h, uint colour)
    {
        for (int row = 0; row < h; row++)
        {
            for (int col = 0; col < w; col++)
            {
                if (tile[row * 8 + col] != colour) return false;
            }
        }
        return true;
    }

    private static bool Same(ReadOnlySpan<uint> a, ReadOnlySpan<uint> b, int w, int h)
    {
        for (int row = 0; row < h; row++)
        {
            for (int col = 0; col < w; col++)
            {
                if (a[row * 8 + col] != b[row * 8 + col]) return false;
            }
        }
        return true;
    }

    private static void Set(byte[] view, int fbWidth, int x, int y, uint rgb)
    {
        int p = (y * fbWidth + x) * 4;
        view[p] = (byte)rgb;
        view[p + 1] = (byte)(rgb >> 8);
        view[p + 2] = (byte)(rgb >> 16);
        view[p + 3] = 0xff;
    }

    private static void Fill(byte[] view, int fbWidth, int x, int y, int w, int h, uint rgb)
    {
        for (int row = 0; row < h; row++)
        {
            for (int col = 0; col < w; col++) Set(view, fbWidth, x + col, y + row, rgb);
        }
    }

    private static void Copy(byte[] view, int fbWidth, int sx, int sy, int x, int y, int w, int h)
    {
        for (int row = 0; row < h; row++)
        {
            Buffer.BlockCopy(view, ((sy + row) * fbWidth + sx) * 4, view, ((y + row) * fbWidth + x) * 4, w * 4);
        }
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _bits;

        public void Write(uint value, int width)
        {
            for (int shift = width - 1; shift >= 0; shift--)
            {
                if (_bits % 8 == 0) _bytes.Add(0);
                if ((value >> shift & 1) != 0) _bytes[^1] |= (byte)(0x80 >> (_bits % 8));
                _bits++;
            }
        }

        public byte[] ToArray() => [.. _bytes];
    }
}
