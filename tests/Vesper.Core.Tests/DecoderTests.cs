using System.Buffers.Binary;
using Vesper.Core.Rfb;
using Vesper.Core.Rfb.Decoders;
using Vesper.FakeMac;

namespace Vesper.Core.Tests;

public class DecoderTests
{
    private static byte[] Pattern(int w, int h, int seed, bool noisy)
    {
        var rng = new Random(seed);
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (noisy)
                {
                    rng.NextBytes(px.AsSpan(i, 3));
                }
                else
                {
                    // Bands, flat areas and a few colours: palette, RLE and solid tiles.
                    byte v = (byte)((x / 7 + y / 11) % 5 * 50);
                    px[i] = v;
                    px[i + 1] = (byte)(x < w / 2 ? 255 - v : v);
                    px[i + 2] = (byte)(y < h / 3 ? 30 : 200);
                }
            }
        }
        return px;
    }

    private static async Task<Framebuffer> DecodeAsync(int w, int h, int encoding, IEnumerable<byte[]> rectBodies, PixelRect[] rects)
    {
        var stream = new MemoryStream();
        foreach (var body in rectBodies) stream.Write(body);
        stream.Position = 0;
        var reader = new RfbReader(stream);
        var fb = new Framebuffer(w, h);
        using var ctx = new DecodeContext();
        foreach (var rc in rects)
        {
            switch (encoding)
            {
                case 6: await StandardDecoders.ZlibAsync(reader, fb, rc, ctx, default); break;
                case 16: await ZrleDecoder.DecodeAsync(reader, fb, rc, ctx, default); break;
                case 0x3ea: await AppleZlibDecoders.Rgb555Async(reader, fb, rc, ctx, default); break;
                case 0x3e9: await AppleZlibDecoders.Gray4Async(reader, fb, rc, ctx, default); break;
            }
        }
        return fb;
    }

    private static void AssertSameColour(byte[] expected, Framebuffer fb, int tolerance = 0)
    {
        for (int i = 0; i < expected.Length; i += 4)
        {
            for (int c = 0; c < 3; c++)
            {
                if (Math.Abs(expected[i + c] - fb.Pixels[i + c]) > tolerance)
                {
                    int px = i / 4;
                    Assert.Fail($"pixel {px % fb.Width},{px / fb.Width} channel {c}: expected {expected[i + c]}, got {fb.Pixels[i + c]}");
                }
            }
        }
    }

    [Theory]
    [InlineData(16, false)]
    [InlineData(16, true)]
    [InlineData(6, false)]
    [InlineData(6, true)]
    public async Task Lossless_codecs_reproduce_the_screen_across_rectangles(int encoding, bool noisy)
    {
        const int W = 301, H = 197; // not multiples of 64 or 16: partial tiles everywhere
        byte[] screen = Pattern(W, H, 42, noisy);
        using var enc = new Encoders();
        var rects = new[] { new PixelRect(0, 0, W, 100), new PixelRect(0, 100, 150, 97), new PixelRect(150, 100, 151, 97) };
        var bodies = rects.Select(r => encoding == 16
            ? enc.Zrle(screen, W * 4, r.X, r.Y, r.Width, r.Height)
            : enc.Zlib(screen, W * 4, r.X, r.Y, r.Width, r.Height)).ToList();
        var fb = await DecodeAsync(W, H, encoding, bodies, rects);
        AssertSameColour(screen, fb);
    }

    [Fact]
    public async Task Rgb555_keeps_five_bits_per_channel()
    {
        const int W = 64, H = 40;
        byte[] screen = Pattern(W, H, 7, noisy: true);
        using var enc = new Encoders();
        var rect = new PixelRect(0, 0, W, H);
        var fb = await DecodeAsync(W, H, 0x3ea, [enc.Rgb555(screen, W * 4, 0, 0, W, H)], [rect]);
        AssertSameColour(screen, fb, tolerance: 8);
    }

    [Fact]
    public async Task Gray4_decodes_nibbles_high_first()
    {
        const int W = 33, H = 5; // odd width: the last nibble of each row is padding
        byte[] screen = Pattern(W, H, 3, noisy: true);
        using var enc = new Encoders();
        var fb = await DecodeAsync(W, H, 0x3e9, [enc.Gray4(screen, W * 4, 0, 0, W, H)], [new PixelRect(0, 0, W, H)]);
        for (int i = 0; i < W * H; i++)
        {
            int grey = (5 * screen[i * 4 + 2] + 9 * screen[i * 4 + 1] + 2 * screen[i * 4]) / 16 >> 4;
            Assert.Equal((byte)(grey * 17), fb.Pixels[i * 4]);
        }
    }

    [Fact]
    public void Zrle_rejects_runs_that_overflow_a_tile()
    {
        var fb = new Framebuffer(64, 64);
        // Plain RLE tile: one CPIXEL, then a run of 255+255+...: far past 4096 pixels.
        byte[] tile = [128, 1, 2, 3, .. Enumerable.Repeat((byte)255, 20), 0];
        Assert.Throws<ProtocolException>(() => ZrleDecoder.Parse(tile, fb, new PixelRect(0, 0, 64, 64)));
    }

    [Fact]
    public void Zrle_rejects_palette_indices_past_the_palette()
    {
        var fb = new Framebuffer(4, 1);
        // Packed palette of 2 colours, 1 bit per pixel; that's fine. Palette RLE of 2 with index 5 is not.
        byte[] tile = [130, 0, 0, 0, 255, 255, 255, 5];
        Assert.Throws<ProtocolException>(() => ZrleDecoder.Parse(tile, fb, new PixelRect(0, 0, 4, 1)));
    }

    [Fact]
    public async Task Zlib_bomb_is_stopped_at_the_rectangle_bound()
    {
        // A 4×4 rectangle may inflate to 64 bytes; send a stream that inflates to a megabyte.
        var sink = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(sink, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(new byte[1 << 20]);
            z.Flush();
        }
        byte[] body = new byte[4 + sink.Length];
        BinaryPrimitives.WriteUInt32BigEndian(body, (uint)sink.Length);
        sink.ToArray().CopyTo(body, 4);
        await Assert.ThrowsAsync<ProtocolException>(() => DecodeAsync(4, 4, 6, [body], [new PixelRect(0, 0, 4, 4)]));
    }

    [Fact]
    public void Framebuffer_refuses_absurd_sizes()
    {
        Assert.Throws<ProtocolException>(() => new Framebuffer(0, 10));
        Assert.Throws<ProtocolException>(() => new Framebuffer(20000, 10));
        Assert.Throws<ProtocolException>(() => new Framebuffer(16384, 16384));
    }

    [Fact]
    public async Task Rre_refuses_to_repaint_its_rectangle_over_and_over()
    {
        // 8×8 rectangle, 64 subrectangles (allowed: no more than its pixels), each the whole 8×8.
        var body = new MemoryStream();
        var u32 = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(u32, 64); body.Write(u32);
        body.Write(new byte[4]); // background
        for (int i = 0; i < 64; i++)
        {
            body.Write(new byte[4]);                    // colour
            body.Write([0, 0, 0, 0, 0, 8, 0, 8]);       // x 0, y 0, w 8, h 8
        }
        body.Position = 0;
        var reader = new RfbReader(body);
        var fb = new Framebuffer(8, 8);
        await Assert.ThrowsAsync<ProtocolException>(() => StandardDecoders.RreAsync(reader, fb, new PixelRect(0, 0, 8, 8), default).AsTask());
    }

    [Fact]
    public void Display_layout_parses_the_bytes_a_real_Mac_sent()
    {
        // Header captured from a Mac in remotex's tests: version 5, 2880×900 points, 4480×1800
        // pixels, all displays, state 4, 2 displays. Records added: a 2x and a 1x screen.
        var payload = new byte[0x14 + 2 * 0x38];
        Convert.FromHexString("00050b40038411800708ffffffff0000000400 02".Replace(" ", "")).CopyTo(payload, 0);
        WriteRecord(payload.AsSpan(0x14), density: 2.0, id: 4, points: (1440, 900), pixelsAt: 0, pixels: (2880, 1800), main: true);
        WriteRecord(payload.AsSpan(0x14 + 0x38), density: 1.0, id: 1, points: (1440, 900), pixelsAt: 2880, pixels: (1600, 1000), main: false);
        var layout = AppleDisplayLayout.Parse(payload);
        Assert.Equal(4480, layout.FramebufferWidth);
        Assert.Equal(1800, layout.FramebufferHeight);
        Assert.Null(layout.CurrentDisplay);
        Assert.Equal(2, layout.Displays.Count);
        Assert.True(layout.Displays[0].IsMain);
        Assert.Equal(2.0, layout.NativeDensity);

        static void WriteRecord(Span<byte> r, double density, uint id, (int W, int H) points, int pixelsAt, (int W, int H) pixels, bool main)
        {
            BinaryPrimitives.WriteDoubleBigEndian(r, density);
            BinaryPrimitives.WriteDoubleBigEndian(r[8..], 1.0);
            BinaryPrimitives.WriteUInt32BigEndian(r[0x10..], id);
            BinaryPrimitives.WriteInt16BigEndian(r[0x16..], 0);
            BinaryPrimitives.WriteInt16BigEndian(r[0x18..], (short)points.H);
            BinaryPrimitives.WriteInt16BigEndian(r[0x1a..], (short)points.W);
            BinaryPrimitives.WriteInt16BigEndian(r[0x1e..], (short)pixelsAt);
            BinaryPrimitives.WriteInt16BigEndian(r[0x20..], (short)pixels.H);
            BinaryPrimitives.WriteInt16BigEndian(r[0x22..], (short)(pixelsAt + pixels.W));
            BinaryPrimitives.WriteUInt32BigEndian(r[0x24..], main ? 1u : 0u);
        }
    }

    [Fact]
    public void Pasteboard_archive_round_trips_text()
    {
        byte[] message = ApplePasteboard.BuildSendMessage("héllo, Mac 🍎", 9);
        uint uncompressed = BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(8));
        Assert.Equal("héllo, Mac 🍎", ApplePasteboard.ExtractText(message.AsSpan(16), uncompressed));
        // Clearing sends one item with no flavours; read back, that is "no text", which leaves the
        // other side's clipboard alone. Only an empty archive reads as "cleared".
        byte[] empty = ApplePasteboard.BuildSendMessage("", 1);
        Assert.Null(ApplePasteboard.ExtractText(empty.AsSpan(16), BinaryPrimitives.ReadUInt32BigEndian(empty.AsSpan(8))));
        Assert.Equal("", ApplePasteboard.ExtractText([], 0));
    }

    [Theory]
    [InlineData("RFB 003.889\n", 3, 889)]
    [InlineData("RFB 003.008\n", 3, 8)]
    public void Version_banner_parses(string banner, int major, int minor) =>
        Assert.Equal((major, minor), RfbSession.ParseVersion(System.Text.Encoding.ASCII.GetBytes(banner)));

    [Theory]
    [InlineData("SSH-2.0-Open")]
    [InlineData("HTTP/1.1 200")]
    public void Non_rfb_banners_are_refused(string banner) =>
        Assert.Throws<ProtocolException>(() => RfbSession.ParseVersion(System.Text.Encoding.ASCII.GetBytes(banner.PadRight(12)[..12])));
}
