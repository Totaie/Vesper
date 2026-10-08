using System.Buffers.Binary;
using Vesper.Core.Rfb;
using Vesper.Core.Rfb.Decoders;

namespace Vesper.Core.Tests;

/// <summary>
/// Apple's adaptive codec (1011). Ported from ard-rs's tests (MIT OR Apache-2.0), whose expected
/// pixels were checked against Apple's own decoder: the packets marked "accepted by Screen
/// Sharing" were displayed by macOS exactly so.
/// </summary>
public class MvsTests
{
    // ───────────────────────────── Packet builders ─────────────────────────────

    private static byte[] Bits(params (uint Value, int Width)[] fields)
    {
        int count = fields.Sum(f => f.Width);
        var bytes = new byte[(count + 7) / 8];
        int position = 0;
        foreach (var (value, width) in fields)
        {
            for (int shift = width - 1; shift >= 0; shift--, position++)
            {
                if ((value & (1u << shift)) != 0) bytes[position / 8] |= (byte)(0x80 >> (position % 8));
            }
        }
        return bytes;
    }

    /// <summary>A base pass (type 0): commands, then operands; both get their 0x6d marker here or in the call.</summary>
    private static byte[] BasePass((uint, int)[] commands, (uint, int)[]? operands = null, byte lumaLimit = 0, byte chromaLimit = 0)
    {
        byte[] primary = Bits(commands);
        byte[] secondary = Bits([.. operands ?? [], (0x6d, 8)]);
        int offset = 6 + primary.Length;
        return WithLength([0, lumaLimit, chromaLimit, (byte)(offset >> 16), (byte)(offset >> 8), (byte)offset, .. primary, .. secondary]);
    }

    private static byte[] Refinement(byte lumaLimit, byte chromaLimit, params (uint, int)[] fields) =>
        WithLength([1, lumaLimit, chromaLimit, .. Bits([.. fields, (0x6d, 8), (0x76, 8), (0x73, 8)])]);

    private static byte[] WithLength(byte[] body)
    {
        var packet = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(packet, (uint)body.Length);
        body.CopyTo(packet, 4);
        return packet;
    }

    /// <summary>The canonical code of a symbol in JPEG's luminance AC table.</summary>
    private static (uint, int) Huffman(byte symbol)
    {
        byte[] bits = [0x00, 0x00, 0x02, 0x01, 0x03, 0x03, 0x02, 0x04, 0x03, 0x05, 0x05, 0x04, 0x04, 0x00, 0x00, 0x01, 0x7d];
        byte[] values =
        [
            0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
            0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08, 0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0,
            0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
            0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
            0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
            0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
            0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7,
            0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5,
            0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
            0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
            0xf9, 0xfa,
        ];
        uint code = 0;
        int index = 0;
        for (int length = 1; length <= 16; length++)
        {
            for (int offset = 0; offset < bits[length]; offset++)
            {
                if (values[index + offset] == symbol) return (code + (uint)offset, length);
            }
            code = (code + bits[length]) << 1;
            index += bits[length];
        }
        throw new ArgumentException("not in the table", nameof(symbol));
    }

    /// <summary>A base pass with one Rice DCT tile: zero DC, one positive AC term at scan 1, end of block.</summary>
    private static readonly (uint, int)[] OneAcTerm =
    [
        (0, 1), // a new block
        (3, 2), // keep both chroma predictors
        (0, 1), (0, 1), // zero luma DC
        (0, 1), (2, 2), // a positive coefficient at scan 1
        (0, 1), (1, 2), (0, 1), // end of block
    ];

    private static async Task DecodeAsync(DecodeContext ctx, Framebuffer fb, byte[] packet, int x, int y, int w, int h)
    {
        var reader = new RfbReader(new MemoryStream(packet));
        await ctx.Mvs.DecodeAsync(reader, fb, new PixelRect(x, y, w, h), ctx, default);
        Assert.Equal(packet.Length, (int)reader.BytesReceived - reader.Buffered);
    }

    private static Task DecodeAsync(DecodeContext ctx, Framebuffer fb, byte[] packet) =>
        DecodeAsync(ctx, fb, packet, 0, 0, fb.Width, fb.Height);

    private static (byte R, byte G, byte B) Pixel(Framebuffer fb, int x, int y)
    {
        int i = (y * fb.Width + x) * 4;
        return (fb.Pixels[i + 2], fb.Pixels[i + 1], fb.Pixels[i]);
    }

    private static IEnumerable<(byte R, byte G, byte B)> AllPixels(Framebuffer fb)
    {
        for (int y = 0; y < fb.Height; y++)
        {
            for (int x = 0; x < fb.Width; x++) yield return Pixel(fb, x, y);
        }
    }

    private static byte[] RowRed(Framebuffer fb, int y) => Enumerable.Range(0, 8).Select(x => Pixel(fb, x, y).R).ToArray();

    // ───────────────────────────── Bits and transforms ─────────────────────────────

    [Fact]
    public void Bits_are_read_most_significant_first_across_bytes()
    {
        var bits = new MvsBits([0b1011_0010, 0b0110_0000]);
        Assert.Equal(0b101u, bits.Read(3));
        Assert.Equal(0b1001001u, bits.Read(7));
    }

    [Fact]
    public void Repeat_counts_decode_every_form()
    {
        var shortForms = new MvsBits(Bits((0, 1), (1, 1), (0, 4), (1, 1), (14, 4)));
        Assert.Equal(0u, shortForms.RepeatCount());
        Assert.Equal(1u, shortForms.RepeatCount());
        Assert.Equal(15u, shortForms.RepeatCount());

        var extended = new MvsBits(Bits((1, 1), (15, 4), (0b0000_0101, 8), (1, 1), (15, 4), (0b1000_0001, 8), (0b0000_0010, 8)));
        Assert.Equal(21u, extended.RepeatCount());
        Assert.Equal(273u, extended.RepeatCount());
    }

    [Fact]
    public void Running_out_of_bits_is_refused()
    {
        var bits = new MvsBits([0xff]);
        Assert.Equal(255u, bits.Read(8));
        try
        {
            bits.Read(1);
            Assert.Fail("Read past the end.");
        }
        catch (ProtocolException)
        {
        }
    }

    [Fact]
    public void Default_quantization_matches_Screen_Sharing()
    {
        var mvs = new MvsDecoder();
        Assert.Equal([16, 11, 11, 14, 24, 22, 24, 33], mvs.LuminanceQuantization[..8].ToArray());
        Assert.Equal([35, 30, 33, 38, 45, 55, 65, 75], mvs.LuminanceQuantization[56..].ToArray());
        Assert.Equal([19, 19, 24, 47, 76, 99, 99, 99], mvs.ChrominanceQuantization[..8].ToArray());
        Assert.Equal(76, mvs.ChrominanceQuantization[32]);
        Assert.All(mvs.ChrominanceQuantization[33..].ToArray(), q => Assert.Equal(99, q));
    }

    [Fact]
    public void Inverse_dct_clamps_samples()
    {
        var q = Enumerable.Repeat((ushort)16, 64).ToArray();
        var c = new short[64];
        var output = new int[64];
        c[0] = 100;
        MvsDecoder.InverseDct(c, q, output);
        Assert.All(output, s => Assert.Equal(255, s));
        c[0] = -100;
        MvsDecoder.InverseDct(c, q, output);
        Assert.All(output, s => Assert.Equal(0, s));
        Assert.Equal(255, MvsDecoder.DcSample(100, 16));
        Assert.Equal(0, MvsDecoder.DcSample(-100, 16));
    }

    [Fact]
    public void Inverse_dct_matches_Screen_Sharings_jpeg_idct_islow()
    {
        // Coefficient 16 at zigzag position 1 under the default luminance table: the row Screen
        // Sharing 6.1 produces, repeated on every row.
        var c = new short[64];
        c[1] = 16;
        var output = new int[64];
        MvsDecoder.InverseDct(c, new MvsDecoder().LuminanceQuantization, output);
        for (int row = 0; row < 8; row++)
        {
            Assert.Equal([159, 154, 145, 134, 122, 111, 102, 97], output[(row * 8)..(row * 8 + 8)]);
        }
    }

    [Fact]
    public void Inverse_dct_is_within_one_level_of_an_exact_transform()
    {
        long[][] basis =
        [
            [11585, 11585, 11585, 11585, 11585, 11585, 11585, 11585],
            [16069, 13623, 9102, 3196, -3196, -9102, -13623, -16069],
            [15137, 6270, -6270, -15137, -15137, -6270, 6270, 15137],
            [13623, -3196, -16069, -9102, 9102, 16069, 3196, -13623],
            [11585, -11585, -11585, 11585, 11585, -11585, -11585, 11585],
            [9102, -16069, 3196, 13623, -13623, -3196, 16069, -9102],
            [6270, -15137, 15137, -6270, -6270, 15137, -15137, 6270],
            [3196, -9102, 13623, -16069, 16069, -13623, 9102, -3196],
        ];
        int differing = 0;
        var actual = new int[64];
        for (int seed = 0; seed < 24; seed++)
        {
            var c = Enumerable.Range(0, 64).Select(i => i % 7 == 0 ? (short)0 : (short)((i + seed) * 37 % 65 - 32)).ToArray();
            var q = Enumerable.Range(0, 64).Select(i => (ushort)(i * 19 % 99 + 1)).ToArray();
            MvsDecoder.InverseDct(c, q, actual);
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    long sum = 0;
                    for (int v = 0; v < 8; v++)
                    {
                        for (int u = 0; u < 8; u++) sum += c[v * 8 + u] * (long)q[v * 8 + u] * basis[u][x] * basis[v][y];
                    }
                    int expected = Math.Clamp((int)((sum + (1L << 29)) >> 30) + 128, 0, 255);
                    int difference = Math.Abs(actual[y * 8 + x] - expected);
                    Assert.True(difference <= 1, $"seed {seed} ({x},{y}): {actual[y * 8 + x]} vs {expected}");
                    if (difference != 0) differing++;
                }
            }
        }
        Assert.True(differing < 200);
    }

    [Theory]
    [InlineData(4, 0, 4)]
    [InlineData(3, 0, 2)]
    [InlineData(-4, 0, -4)]
    [InlineData(-3, 0, -2)]
    [InlineData(-4, 1, -6)]
    [InlineData(-4, -1, -2)]
    public void Chroma_dc_prediction_rounds_like_Apple(short predictor, short delta, short expected) =>
        Assert.Equal(expected, MvsDecoder.PredictChromaDc(predictor, delta));

    // ───────────────────────────── Whole updates ─────────────────────────────

    [Fact]
    public async Task Quantization_tables_are_replaced_by_a_type_2_update()
    {
        using var ctx = new DecodeContext();
        var fb = new Framebuffer(1, 1);
        byte[] body = [2, .. Enumerable.Range(0, 128).Select(i => (byte)i)];
        await DecodeAsync(ctx, fb, WithLength(body), 0, 0, 0, 0);
        Assert.Equal(0, ctx.Mvs.LuminanceQuantization[0]);
        Assert.Equal(63, ctx.Mvs.LuminanceQuantization[63]);
        Assert.Equal(64, ctx.Mvs.ChrominanceQuantization[0]);
        Assert.Equal(127, ctx.Mvs.ChrominanceQuantization[63]);
    }

    [Fact]
    public async Task A_64x64_white_packet_Screen_Sharing_accepted_paints_white()
    {
        byte[] packet = BasePass([(0, 1), (0, 3), (1, 1), (15, 4), (47, 8), (0x6d, 8)]);
        Assert.Equal(15, packet.Length);
        using var ctx = new DecodeContext();
        var fb = new Framebuffer(64, 64);
        await DecodeAsync(ctx, fb, packet);
        Assert.All(AllPixels(fb), p => Assert.Equal((255, 255, 255), p));
    }

    [Fact]
    public async Task A_solid_grey_tile_then_white_tiles_as_Screen_Sharing_showed_them()
    {
        byte[] packet = BasePass(
            [(0, 1), (4, 3), (0, 1), (0, 3), (1, 1), (15, 4), (46, 8), (0x6d, 8)],
            [(0, 1), (0, 1), (200, 8), (32, 6), (32, 6)]);
        using var ctx = new DecodeContext();
        var fb = new Framebuffer(64, 64);
        await DecodeAsync(ctx, fb, packet);
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                Assert.Equal(x < 8 && y < 8 ? ((byte)200, (byte)200, (byte)200) : ((byte)255, (byte)255, (byte)255), Pixel(fb, x, y));
            }
        }
    }

    [Fact]
    public async Task Two_colour_tiles_take_the_second_colour_for_a_clear_bit()
    {
        var operands = new List<(uint, int)>
        {
            (1, 1), (0, 1), // two colours, read new ones
            (255, 8), (32, 6), (32, 6), // first: white
            (0, 8), (32, 6), (32, 6), // second: black
            (0, 8), // every row has its own mask
        };
        operands.AddRange(Enumerable.Repeat<(uint, int)>((0x7f, 8), 8));
        byte[] packet = BasePass([(0, 1), (4, 3), (0, 1), (0x6d, 8)], [.. operands]);
        using var ctx = new DecodeContext();
        var fb = new Framebuffer(8, 8);
        await DecodeAsync(ctx, fb, packet);
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                Assert.Equal(x == 0 ? ((byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255), Pixel(fb, x, y));
            }
        }
    }

    [Fact]
    public async Task A_zero_DCT_block_Screen_Sharing_accepted_is_mid_grey()
    {
        byte[] packet = BasePass(
            [(0, 1), (5, 3), (0, 1), (0, 3), (1, 1), (15, 4), (46, 8), (0x6d, 8)],
            [(0, 1), (3, 2), (0, 1), (0, 1), (0, 1), (1, 2), (0, 1)]);
        using var ctx = new DecodeContext();
        var fb = new Framebuffer(64, 64);
        await DecodeAsync(ctx, fb, packet);
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                Assert.Equal(x < 8 && y < 8 ? ((byte)128, (byte)128, (byte)128) : ((byte)255, (byte)255, (byte)255), Pixel(fb, x, y));
            }
        }
    }

    [Fact]
    public async Task A_DCT_block_can_be_reused_for_the_next_tile()
    {
        byte[] packet = BasePass([(0, 1), (5, 3), (0, 1), (5, 3), (0, 1), (0x6d, 8)], [.. OneAcTerm, (1, 1)]);
        using var ctx = new DecodeContext();
        var fb = new Framebuffer(16, 8);
        await DecodeAsync(ctx, fb, packet);
        Assert.Equal([159, 154, 145, 134, 122, 111, 102, 97], RowRed(fb, 0));
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++) Assert.Equal(Pixel(fb, x, y), Pixel(fb, x + 8, y));
        }
        Assert.All(AllPixels(fb), p => Assert.True(p.R == p.G && p.G == p.B));
    }

    [Fact]
    public async Task Rice_terms_from_the_limit_on_are_scaled_by_eight_above_a_limit_of_14()
    {
        var operands = new List<(uint, int)> { (0, 1), (3, 2), (0, 1), (0, 1) };
        for (int scan = 1; scan < 18; scan++) operands.AddRange([(0, 1), (0, 2)]);
        operands.AddRange([(0, 1), (2, 2), (0, 1), (1, 2), (0, 1)]); // +1 at scan 18, then end
        byte[] packet = BasePass([(0, 1), (5, 3), (0, 1), (0x6d, 8)], [.. operands], lumaLimit: 15, chromaLimit: 15);
        using var ctx = new DecodeContext();
        var fb = new Framebuffer(8, 8);
        await DecodeAsync(ctx, fb, packet);

        // Scan 18 (natural index 26) is past the limit of 15, so the term is 1 << 3.
        var c = new short[64];
        c[26] = 8;
        var expected = new int[64];
        MvsDecoder.InverseDct(c, ctx.Mvs.LuminanceQuantization, expected);
        for (int i = 0; i < 64; i++) Assert.Equal(expected[i], Pixel(fb, i % 8, i / 8).R);
    }

    private static byte[] RiceBaseline() => BasePass([(0, 1), (5, 3), (0, 1), (0x6d, 8)], OneAcTerm, lumaLimit: 2, chromaLimit: 2);

    private static readonly (uint, int)[] RefineOneTerm =
    [
        (1, 2), // a differential DCT tile
        (1, 6), // two luma terms
        (1, 3), // the AC term up by one
        (0, 1), // Cr DC unchanged
        (0, 2), // Huffman 0x01 (code 00)
        (1, 1), // +1 Cr AC term
        (0b1010, 4), // Cr end of block
        (0, 1), // Cb DC unchanged
        (0b1010, 4), // Cb end of block
    ];

    [Fact]
    public async Task Refinements_sharpen_from_the_base_block_and_both_cache_recalls_repeat_them()
    {
        using var ctx = new DecodeContext();
        var fb = new Framebuffer(8, 8);
        await DecodeAsync(ctx, fb, RiceBaseline());
        await DecodeAsync(ctx, fb, Refinement(64, 64, RefineOneTerm));
        byte[] expectedRow = [149, 147, 141, 132, 124, 115, 109, 107];
        Assert.Equal(expectedRow, RowRed(fb, 0));
        (byte, byte, byte)[] expectedPixels =
        [
            (149, 143, 145), (147, 141, 143), (141, 137, 138), (132, 130, 131),
            (124, 126, 125), (115, 119, 118), (109, 115, 113), (107, 113, 111),
        ];
        Assert.Equal(expectedPixels, Enumerable.Range(0, 8).Select(x => Pixel(fb, x, 0)).ToArray());

        // A second refinement is still relative to the base block, not to the first refinement.
        await DecodeAsync(ctx, fb, Refinement(64, 64, RefineOneTerm));
        Assert.Equal(expectedRow, RowRed(fb, 0));

        byte[] white = BasePass([(0, 1), (0, 3), (0, 1), (0x6d, 8)]);
        await DecodeAsync(ctx, fb, white);
        await DecodeAsync(ctx, fb, BasePass([(0, 1), (6, 3), (0, 1), (0x6d, 8)], [(0, 8), (1, 8)]));
        Assert.Equal(expectedRow, RowRed(fb, 0));

        await DecodeAsync(ctx, fb, white);
        await DecodeAsync(ctx, fb, Refinement(0, 0, (3, 2), (0, 1), (0, 8), (1, 8)));
        Assert.Equal(expectedRow, RowRed(fb, 0));
    }

    [Fact]
    public async Task A_cache_recall_drops_high_chroma_terms_like_Apples_cache()
    {
        var withHighCr = new List<(uint, int)> { (1, 2), (1, 6), (0, 4), (0, 1), (0, 1), (0, 1) };
        withHighCr.Add(Huffman(0xf0)); // skip 16 scans
        withHighCr.Add(Huffman(0x01)); // +1 at scan 17
        withHighCr.Add((1, 1));
        withHighCr.Add(Huffman(0x00)); // Cr end of block
        withHighCr.Add((0, 1)); // Cb DC unchanged
        withHighCr.Add(Huffman(0x00)); // Cb end of block
        var withoutCr = new List<(uint, int)> { (1, 2), (1, 6), (0, 4), (0, 1), (0, 1), (0, 1) };
        withoutCr.Add(Huffman(0x00));
        withoutCr.Add((0, 1));
        withoutCr.Add(Huffman(0x00));
        byte[] baseline = BasePass([(0, 1), (5, 3), (0, 1), (0x6d, 8)], OneAcTerm);
        byte[] recall = BasePass([(0, 1), (6, 3), (0, 1), (0x6d, 8)], [(0, 8), (1, 8)]);

        using var first = new DecodeContext();
        var fb = new Framebuffer(8, 8);
        await DecodeAsync(first, fb, baseline);
        await DecodeAsync(first, fb, Refinement(64, 64, [.. withHighCr]));
        byte[] fullRender = (byte[])fb.Pixels.Clone();
        await DecodeAsync(first, fb, recall);
        byte[] truncatedRecall = (byte[])fb.Pixels.Clone();
        Assert.NotEqual(fullRender, truncatedRecall);

        using var second = new DecodeContext();
        var reference = new Framebuffer(8, 8);
        await DecodeAsync(second, reference, baseline);
        await DecodeAsync(second, reference, Refinement(64, 64, [.. withoutCr]));
        await DecodeAsync(second, reference, recall);
        Assert.Equal(reference.Pixels, truncatedRecall);
    }

    [Fact]
    public async Task Sequential_cache_recall_follows_the_last_reference_not_the_last_insertion()
    {
        using var ctx = new DecodeContext();
        var fb = new Framebuffer(8, 8);
        await DecodeAsync(ctx, fb, RiceBaseline());
        await DecodeAsync(ctx, fb, Refinement(64, 64, (1, 2), (1, 6), (1, 3), (0, 1), (0b1010, 4), (0, 1), (0b1010, 4)));
        byte[] expected = (byte[])fb.Pixels.Clone();
        byte[] white = BasePass([(0, 1), (0, 3), (0, 1), (0x6d, 8)]);
        await DecodeAsync(ctx, fb, white);
        Assert.NotEqual(expected, fb.Pixels);
        await DecodeAsync(ctx, fb, BasePass([(0, 1), (7, 3), (0, 1), (0x6d, 8)]));
        Assert.Equal(expected, fb.Pixels);

        // A later insertion moves only the write cursor; the next sequential recall goes 1 → 2.
        await DecodeAsync(ctx, fb, Refinement(64, 64, (1, 2), (1, 6), (1, 3), (0, 1), (0b1010, 4), (0, 1), (0b1010, 4)));
        expected = (byte[])fb.Pixels.Clone();
        await DecodeAsync(ctx, fb, white);
        await DecodeAsync(ctx, fb, Refinement(0, 0, (3, 2), (1, 1)));
        Assert.Equal(expected, fb.Pixels);
    }

    [Fact]
    public async Task A_recall_of_an_empty_cache_slot_leaves_the_tile_alone()
    {
        using var ctx = new DecodeContext();
        var fb = new Framebuffer(8, 8);
        await DecodeAsync(ctx, fb, RiceBaseline());
        await DecodeAsync(ctx, fb, Refinement(64, 64, (1, 2), (1, 6), (1, 3), (0, 1), (0b1010, 4), (0, 1), (0b1010, 4)));
        await DecodeAsync(ctx, fb, BasePass([(0, 1), (6, 3), (0, 1), (0x6d, 8)], [(0, 8), (1, 8)]));
        byte[] before = (byte[])fb.Pixels.Clone();
        await DecodeAsync(ctx, fb, Refinement(0, 0, (3, 2), (1, 1))); // asks for entry 2, never written
        Assert.Equal(before, fb.Pixels);
    }

    [Fact]
    public async Task An_empty_baseline_refines_the_DC_term()
    {
        using var ctx = new DecodeContext();
        var fb = new Framebuffer(8, 8);
        await DecodeAsync(ctx, fb, BasePass([(0, 1), (5, 3), (0, 1), (0x6d, 8)], OneAcTerm));
        await DecodeAsync(ctx, fb, Refinement(0, 0, (1, 2), (0, 6), (0, 2), (0, 1), (0, 1)));
        Assert.All(AllPixels(fb), p => Assert.Equal((128, 128, 128), p));
    }

    [Fact]
    public async Task Refinements_of_never_drawn_tiles_start_from_zero()
    {
        using var ctx = new DecodeContext();
        var fb = new Framebuffer(8, 8);
        await DecodeAsync(ctx, fb, Refinement(1, 1, (1, 2), (0, 6), (0, 1), (0, 1), (0, 1), (0b1010, 4), (0, 1), (0b1010, 4)));
        await DecodeAsync(ctx, fb, Refinement(0, 0, (1, 2), (0, 6), (0, 1), (0, 1), (0, 1), (0, 1)));
        Assert.All(AllPixels(fb), p => Assert.Equal((128, 128, 128), p));
    }

    [Fact]
    public async Task A_refinement_repeats_a_copy_from_what_the_source_shows_now()
    {
        using var ctx = new DecodeContext();
        var fb = new Framebuffer(16, 8);
        await DecodeAsync(ctx, fb, BasePass(
            [(0, 1), (4, 3), (0, 1), (1, 3), (0, 1), (0x6d, 8)], // a solid tile, then a copy of it to its right
            [(0, 1), (0, 1), (80, 8), (32, 6), (32, 6)]));
        // Something else repaints the source tile (B,G,R = 180, 90, 20).
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++) new byte[] { 180, 90, 20, 0 }.CopyTo(fb.Pixels, (y * 16 + x) * 4);
        }
        await DecodeAsync(ctx, fb, Refinement(0, 0, (0, 2), (2, 2)));
        Assert.All(AllPixels(fb), p => Assert.Equal((20, 90, 180), p));
    }

    [Fact]
    public async Task Refining_a_copied_tile_keeps_its_copy_source()
    {
        using var ctx = new DecodeContext();
        var fb = new Framebuffer(16, 8);
        await DecodeAsync(ctx, fb, BasePass([(0, 1), (5, 3), (0, 1), (5, 3), (0, 1), (0x6d, 8)], [.. OneAcTerm, (1, 1)], 2, 2));
        await DecodeAsync(ctx, fb, BasePass([(0, 1), (0, 3), (0, 1), (1, 3), (0, 1), (0x6d, 8)]));
        await DecodeAsync(ctx, fb, Refinement(64, 64, [(0, 2), .. RefineOneTerm]));
        await DecodeAsync(ctx, fb, Refinement(0, 0, (0, 2), (2, 2)));
        Assert.All(AllPixels(fb), p => Assert.Equal((255, 255, 255), p));
    }

    [Fact]
    public async Task Colours_round_through_Apples_tables()
    {
        // Negative chroma: red is 21 here, not the 20 a plain shift would give.
        using var ctx = new DecodeContext();
        var fb = new Framebuffer(8, 8);
        await DecodeAsync(ctx, fb, BasePass([(0, 1), (4, 3), (0, 1), (0x6d, 8)], [(0, 1), (0, 1), (200, 8), (0, 6), (0, 6)]));
        Assert.All(AllPixels(fb), p => Assert.Equal((21, 255, 0), p));
    }

    [Fact]
    public async Task Repeats_and_left_copies_cover_their_tiles()
    {
        using var ctx = new DecodeContext();
        var fb = new Framebuffer(16, 1);
        await DecodeAsync(ctx, fb, BasePass([(0, 1), (0, 3), (1, 1), (0, 4), (0x6d, 8)]));
        Assert.All(AllPixels(fb), p => Assert.Equal((255, 255, 255), p));

        fb = new Framebuffer(16, 1);
        await DecodeAsync(ctx, fb, BasePass([(0, 1), (3, 3), (0, 1), (1, 3), (0, 1), (0x6d, 8)], [(0xff, 8)]));
        Assert.All(AllPixels(fb), p => Assert.Equal((255, 255, 255), p));
    }

    [Fact]
    public async Task Copies_name_tiles_across_the_whole_screen()
    {
        byte[] first = BasePass([(0, 1), (0, 3), (0, 1), (0x6d, 8)]);
        using (var ctx = new DecodeContext())
        {
            var fb = new Framebuffer(16, 8);
            await DecodeAsync(ctx, fb, first, 0, 0, 8, 8);
            await DecodeAsync(ctx, fb, BasePass([(0, 1), (1, 3), (0, 1), (0x6d, 8)]), 8, 0, 8, 8);
            Assert.All(AllPixels(fb), p => Assert.Equal((255, 255, 255), p));
        }
        using (var ctx = new DecodeContext())
        {
            var fb = new Framebuffer(8, 16);
            await DecodeAsync(ctx, fb, first, 0, 0, 8, 8);
            await DecodeAsync(ctx, fb, BasePass([(0, 1), (2, 3), (0, 1), (0x6d, 8)]), 0, 8, 8, 8);
            Assert.All(AllPixels(fb), p => Assert.Equal((255, 255, 255), p));
        }
    }

    [Fact]
    public async Task Copies_without_a_source_or_with_a_stale_one_change_nothing()
    {
        using (var ctx = new DecodeContext())
        {
            var fb = new Framebuffer(8, 8);
            fb.Pixels.AsSpan().Fill(0x5a);
            await DecodeAsync(ctx, fb, Refinement(0, 0, (2, 2)));
            Assert.All(fb.Pixels, b => Assert.Equal(0x5a, b));
        }
        using (var ctx = new DecodeContext())
        {
            var fb = new Framebuffer(16, 8);
            await DecodeAsync(ctx, fb, BasePass([(0, 1), (0, 3), (0, 1), (1, 3), (0, 1), (0x6d, 8)]));
            await DecodeAsync(ctx, fb, BasePass([(0, 1), (0, 3), (0, 1), (0x6d, 8)]), 0, 0, 8, 8);
            await DecodeAsync(ctx, fb, Refinement(0, 0, (0, 2), (2, 2)));
            Assert.All(AllPixels(fb), p => Assert.Equal((255, 255, 255), p));
        }
    }

    [Fact]
    public async Task Reusing_a_block_before_any_block_is_refused()
    {
        using var ctx = new DecodeContext();
        var fb = new Framebuffer(16, 1);
        await Assert.ThrowsAsync<ProtocolException>(() =>
            DecodeAsync(ctx, fb, BasePass([(0, 1), (0, 3), (0, 1), (5, 3), (0, 1), (0x6d, 8)], [(1, 1)])));
    }

    [Fact]
    public async Task Skipped_tiles_and_the_end_marker_leave_the_screen_alone()
    {
        using var ctx = new DecodeContext();
        var fb = new Framebuffer(16, 8);
        fb.Pixels.AsSpan().Fill(0x5a);
        await DecodeAsync(ctx, fb, Refinement(64, 64, (0, 2), (0, 2)));
        Assert.All(fb.Pixels, b => Assert.Equal(0x5a, b));
    }

    [Fact]
    public async Task Broken_streams_are_refused()
    {
        using var ctx = new DecodeContext();
        var fb = new Framebuffer(8, 8);
        // A wrong end marker.
        await Assert.ThrowsAsync<ProtocolException>(() => DecodeAsync(ctx, fb, BasePass([(0, 1), (0, 3), (0, 1), (0x6e, 8)])));
        // A run past the rectangle.
        await Assert.ThrowsAsync<ProtocolException>(() => DecodeAsync(ctx, fb, BasePass([(0, 1), (0, 3), (1, 1), (0, 4), (0x6d, 8)])));
        // An operand stream offset outside the body.
        byte[] bad = BasePass([(0, 1), (0, 3), (0, 1), (0x6d, 8)]);
        bad[9] = 0xff;
        await Assert.ThrowsAsync<ProtocolException>(() => DecodeAsync(ctx, fb, bad));
        // A tile outside the screen.
        await Assert.ThrowsAsync<ProtocolException>(() => DecodeAsync(ctx, fb, BasePass([(0, 1), (0, 3), (0, 1), (0x6d, 8)]), 8, 8, 8, 8));
    }
}
