using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vesper.Core.Rfb.Decoders;

/// <summary>
/// Apple's adaptive codec, encoding 1011 (0x3F3), which Apple's viewer uses for Adaptive quality
/// and Remote Desktop Manager calls MVS ("Multi-Variant Stream"). It's what makes a Mac's screen
/// feel live: the picture is coded in 8×8 tiles, most of them a few bits (white, a copy of a
/// neighbour, one or two colours, a recently seen tile from a cache), the rest JPEG-like DCT
/// blocks that arrive coarse and are then sharpened by refinement passes.
///
/// A rectangle is a u32 length and a body whose first byte says what it is:
/// <list type="bullet">
/// <item>0, a base pass over every tile: a command stream (3-bit command, repeat count) from byte
/// 6 and an operand stream from the u24 offset in bytes 3–5, each ending in 0x6d;</item>
/// <item>1, a refinement pass: a 2-bit selector per tile that sharpens its DCT coefficients,
/// repeats its copy, or recalls a cached tile, ending in "mvs";</item>
/// <item>2, new quantization tables (129 bytes, in a 0×0 rectangle).</item>
/// </list>
///
/// Ported from ard-rs (github.com/weidix/ard-rs, crates/ard-core/src/mvs.rs; MIT OR Apache-2.0),
/// whose author matched it to Apple's own decoder bit for bit, including its integer inverse DCT
/// and the quirks of its cache. The structure follows remotex's description (MIT). See
/// THIRD-PARTY-NOTICES.md. Unlike ard-rs, tiles are drawn as they're decoded, and a tile copy
/// copies the pixels the source tile shows.
/// </summary>
internal sealed class MvsDecoder
{
    public const int QuantizationUpdateLength = 129;
    private const int CacheLastIndex = 64_999;
    private const int CachedTileBytes = 3 * 64;
    // A base-pass block: 64 luma terms, then the Cb and Cr DC terms (its chroma has no AC terms).
    private const int RiceBlockLength = 66;

    private static readonly ushort[] DefaultLuminanceQuantization =
    [
        0x10, 0x0b, 0x0b, 0x0e, 0x18, 0x16, 0x18, 0x21, 0x0b, 0x0c, 0x0b, 0x0b, 0x1a, 0x12, 0x13, 0x17,
        0x0d, 0x0d, 0x0e, 0x18, 0x14, 0x15, 0x16, 0x1c, 0x0d, 0x11, 0x0e, 0x15, 0x17, 0x19, 0x22, 0x22,
        0x11, 0x16, 0x12, 0x19, 0x1f, 0x20, 0x22, 0x2c, 0x18, 0x14, 0x11, 0x1b, 0x23, 0x28, 0x2c, 0x36,
        0x1e, 0x23, 0x1c, 0x1e, 0x22, 0x2c, 0x36, 0x40, 0x23, 0x1e, 0x21, 0x26, 0x2d, 0x37, 0x41, 0x4b,
    ];

    private static readonly ushort[] DefaultChrominanceQuantization =
    [
        0x13, 0x13, 0x18, 0x2f, 0x4c, 0x63, 0x63, 0x63, 0x13, 0x15, 0x1a, 0x42, 0x63, 0x63, 0x63, 0x63,
        0x18, 0x1a, 0x38, 0x63, 0x63, 0x63, 0x63, 0x63, 0x2f, 0x42, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63,
        0x4c, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63,
        0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63,
    ];

    internal static ReadOnlySpan<byte> Zigzag =>
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20,
        13, 6, 7, 14, 21, 28, 35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59,
        52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    // The refinement pass codes both chroma components' AC terms with JPEG's standard luminance
    // AC Huffman table (Apple's decoder builds that one table and uses it for both).
    private static ReadOnlySpan<byte> LuminanceAcBits =>
        [0x00, 0x00, 0x02, 0x01, 0x03, 0x03, 0x02, 0x04, 0x03, 0x05, 0x05, 0x04, 0x04, 0x00, 0x00, 0x01, 0x7d];

    private static ReadOnlySpan<byte> LuminanceAcValues =>
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

    private const uint White = 0xFFFFFFFF, Black = 0xFF000000;

    // YCbCr → RGB is JPEG's full-range conversion, in Apple's 16.16 fixed point.
    private static readonly int[] RedFromCr = new int[256], BlueFromCb = new int[256], GreenFromCb = new int[256], GreenFromCr = new int[256];

    static MvsDecoder()
    {
        for (int i = 0; i < 256; i++)
        {
            int c = i - 128;
            RedFromCr[i] = (91_881 * c + 32_768) >> 16;
            BlueFromCb[i] = (116_130 * c + 32_768) >> 16;
            GreenFromCb[i] = -22_554 * c;
            GreenFromCr[i] = -46_802 * c;
        }
    }

    private readonly ushort[] _luminanceQuantization = (ushort[])DefaultLuminanceQuantization.Clone();
    private readonly ushort[] _chrominanceQuantization = (ushort[])DefaultChrominanceQuantization.Clone();

    // Per-tile state over the whole framebuffer (tiles on its 8-pixel grid).
    private int _width, _height, _tilesWide, _tileCount;
    private int[] _generation = [];
    private int[] _copySource = [];
    private byte[] _baselineCount = [];
    private short[] _baseline = [];
    private int _generationCounter;

    // The DCT cache: a ring of up to 64,999 tiles, numbered from 1, each 3 × 64 signed bytes.
    private sbyte[] _cache = [];
    private int _cacheWriteIndex;
    private int _lastCacheReference;
    private long _cacheInsertions;

    internal ReadOnlySpan<ushort> LuminanceQuantization => _luminanceQuantization;
    internal ReadOnlySpan<ushort> ChrominanceQuantization => _chrominanceQuantization;

    /// <summary>Reads one 1011 rectangle (u32 length, body) and draws it.</summary>
    public async ValueTask DecodeAsync(RfbReader r, Framebuffer fb, PixelRect rc, DecodeContext ctx, CancellationToken ct)
    {
        uint length = await r.ReadU32Async(ct).ConfigureAwait(false);
        // The worst case is DCT tiles with every coefficient coded long: well under 16 bytes a pixel.
        long limit = Math.Min(100_000_000, rc.Area * 16 + 65_536);
        if (length == 0 || length > limit)
        {
            throw new ProtocolException("An adaptive-codec rectangle has an implausible length.");
        }
        byte[] buffer = ctx.Compressed((int)length);
        await r.ReadExactlyAsync(buffer.AsMemory(0, (int)length), ct).ConfigureAwait(false);
        Decode(buffer.AsSpan(0, (int)length), fb, rc);
    }

    internal void Decode(ReadOnlySpan<byte> body, Framebuffer fb, PixelRect rc)
    {
        switch (body[0])
        {
            case 2:
                ReadQuantization(body);
                return;
            case 0 or 1 when rc.IsEmpty:
                throw new ProtocolException("An adaptive-codec picture came in an empty rectangle.");
            case 0 or 1:
                EnsureTiles(fb);
                _opCount = _dctCount = 0;
                if (body[0] == 0) DecodeBasePass(body, fb, rc);
                else DecodeRefinement(body, fb, rc);
                Flush(fb);
                lock (fb.Sync) fb.MarkDirty(rc.X, rc.Y, rc.Width, rc.Height);
                return;
            default:
                throw new ProtocolException($"The Mac sent an adaptive-codec update of unknown type {body[0]}.");
        }
    }

    private void ReadQuantization(ReadOnlySpan<byte> body)
    {
        if (body.Length != QuantizationUpdateLength)
        {
            throw new ProtocolException("The Mac sent adaptive-codec quantization tables of the wrong size.");
        }
        for (int i = 0; i < 64; i++)
        {
            _luminanceQuantization[i] = body[1 + i];
            _chrominanceQuantization[i] = body[65 + i];
        }
    }

    /// <summary>Tile state follows the framebuffer's size; the cache outlives a resize.</summary>
    private void EnsureTiles(Framebuffer fb)
    {
        if (fb.Width == _width && fb.Height == _height) return;
        _width = fb.Width;
        _height = fb.Height;
        _tilesWide = (_width + 7) / 8;
        _tileCount = _tilesWide * ((_height + 7) / 8);
        _generation = new int[_tileCount];
        _copySource = new int[_tileCount];
        _copySource.AsSpan().Fill(-1);
        _baselineCount = new byte[_tileCount];
        _baseline = new short[_tileCount * RiceBlockLength];
        _generationCounter = 0;
    }

    // ───────────────────────────── Base pass (body type 0) ─────────────────────────────

    // Fully optimized from the first call: a session decodes few rectangles, each of thousands of tiles.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void DecodeBasePass(ReadOnlySpan<byte> body, Framebuffer fb, PixelRect rc)
    {
        if (body.Length < 6) throw Truncated();
        int secondaryOffset = body[3] << 16 | body[4] << 8 | body[5];
        if (secondaryOffset < 6 || secondaryOffset > body.Length)
        {
            throw new ProtocolException("An adaptive-codec update points outside itself.");
        }
        var commands = new MvsBits(body[6..secondaryOffset]);
        var operands = new MvsBits(body[secondaryOffset..]);
        commands.Read(1); // an initial state bit, which Apple's decoder skips

        int tilesWide = (rc.Width + 7) / 8;
        int total = tilesWide * ((rc.Height + 7) / 8);
        byte lumaLimit = body[1], chromaLimit = body[2];
        uint firstColour = White;
        // Apple's default second colour is RGB (181, 213, 254).
        uint secondColour = Bgrx(0xB5, 0xD5, 0xFE);
        uint solidColour = Black;
        int generation = ++_generationCounter;

        // A DCT block may reuse the previous one in this rectangle, and predicts its DC terms from it.
        // Two buffers in turn, so the previous block is never copied.
        Span<short> blocks = stackalloc short[2 * RiceBlockLength];
        int previousBlock = -1;
        byte previousLimit = 0;

        int tile = 0, localX = 0, localY = 0;
        while (tile < total)
        {
            int command = (int)commands.Read(3);
            long run = commands.RepeatCount() + 1L;
            if (tile + run > total)
            {
                throw new ProtocolException("An adaptive-codec run goes past its rectangle.");
            }
            for (long i = 0; i < run; i++, tile++, localX += 8)
            {
                if (localX >= rc.Width)
                {
                    localX = 0;
                    localY += 8;
                }
                int w = Math.Min(8, rc.Width - localX), h = Math.Min(8, rc.Height - localY);
                int x = rc.X + localX, y = rc.Y + localY;
                // Tiles are numbered over the whole framebuffer, not this rectangle: a copy "from the
                // left" at the start of a row takes the last tile of the row above.
                int global = y / 8 * _tilesWide + x / 8;
                if ((uint)global >= (uint)_tileCount)
                {
                    throw new ProtocolException("An adaptive-codec tile lies outside the screen.");
                }
                int copySource = -1;
                switch (command)
                {
                    case 0:
                        QueueFill(fb, x, y, w, h, White);
                        break;
                    case 1:
                        copySource = global - 1;
                        if (copySource >= 0) QueueCopy(fb, copySource, x, y, w, h);
                        break;
                    case 2:
                        copySource = global - _tilesWide;
                        if (copySource >= 0) QueueCopy(fb, copySource, x, y, w, h);
                        else copySource = -1;
                        break;
                    case 3:
                        QueueBilevel(ref operands, fb, x, y, w, h, uniform: White, zero: Black, one: White);
                        break;
                    case 4:
                        if (operands.Read(1) != 0)
                        {
                            if (operands.Read(1) == 0)
                            {
                                firstColour = ReadColour(ref operands);
                                secondColour = ReadColour(ref operands);
                            }
                            QueueBilevel(ref operands, fb, x, y, w, h, uniform: firstColour, zero: secondColour, one: firstColour);
                        }
                        else
                        {
                            if (operands.Read(1) == 0) solidColour = ReadColour(ref operands);
                            QueueFill(fb, x, y, w, h, solidColour);
                        }
                        break;
                    case 5:
                        byte limit;
                        int block;
                        if (operands.Read(1) != 0)
                        {
                            if (previousBlock < 0) throw new ProtocolException("An adaptive-codec block reuses a block that doesn't exist.");
                            block = previousBlock;
                            limit = previousLimit;
                        }
                        else
                        {
                            block = previousBlock == 0 ? 1 : 0;
                            limit = DecodeRiceBlock(ref operands, blocks.Slice(block * RiceBlockLength, RiceBlockLength),
                                previousBlock < 0 ? default : blocks.Slice(previousBlock * RiceBlockLength, RiceBlockLength), lumaLimit, chromaLimit);
                        }
                        previousBlock = block;
                        previousLimit = limit;
                        var coefficients = blocks.Slice(block * RiceBlockLength, RiceBlockLength);
                        QueueCoefficients(fb, OpKind.RiceDct, coefficients, x, y, w, h);
                        // The block is this tile's baseline for refinements.
                        coefficients.CopyTo(_baseline.AsSpan(global * RiceBlockLength, RiceBlockLength));
                        _baselineCount[global] = limit;
                        break;
                    case 6 or 7:
                        int index = command == 6
                            ? (int)(operands.Read(8) << 8 | operands.Read(8))
                            : NextCacheIndex(_lastCacheReference);
                        // An index that isn't in the cache leaves the tile as it is, as Apple's decoder does.
                        if (TryCached(index, out int cached)) QueueCached(fb, cached, x, y, w, h);
                        break;
                }
                _generation[global] = generation;
                _copySource[global] = copySource;
            }
        }
        if (commands.Read(8) != 0x6d || operands.Read(8) != 0x6d)
        {
            throw new ProtocolException("An adaptive-codec update didn't end where it should.");
        }
    }

    /// <summary>A DCT block with Rice-coded coefficients: luma in full, chroma as DC terms only.</summary>
    private static byte DecodeRiceBlock(ref MvsBits bits, scoped Span<short> current, scoped ReadOnlySpan<short> previous, byte lumaLimit, byte chromaLimit)
    {
        current[..64].Clear();
        uint predictorMode = bits.Read(2);
        short previousLumaDc = previous.IsEmpty ? (short)0 : previous[0];
        short previousCbDc = previous.IsEmpty ? (short)0 : previous[64];
        short previousCrDc = previous.IsEmpty ? (short)0 : previous[65];
        if ((predictorMode & 2) != 0)
        {
            current[64] = previousCbDc;
            current[65] = previousCrDc;
        }
        else
        {
            current[64] = PredictChromaDc(previousCbDc, DecodeDcRice(ref bits));
            current[65] = PredictChromaDc(previousCrDc, DecodeDcRice(ref bits));
        }
        current[0] = unchecked((short)(previousLumaDc - DecodeDcRice(ref bits)));
        byte limit = (predictorMode & 1) != 0 ? chromaLimit : lumaLimit;
        DecodeAcRice(ref bits, current[..64], limit);
        return limit;
    }

    /// <summary>
    /// Apple halves the signed predictor (truncating toward zero), applies the delta, then doubles it.
    /// </summary>
    internal static short PredictChromaDc(short predictor, short delta) => unchecked((short)((predictor / 2 - delta) * 2));

    private static void DecodeAcRice(ref MvsBits bits, scoped Span<short> luma, byte limit)
    {
        int scan = 1;
        while (scan < 64)
        {
            if (bits.Read(1) != 0)
            {
                bool compact = scan < 6;
                int prefix = bits.ReadOnes(compact ? 33 : 19);
                uint magnitude, sign;
                if (compact)
                {
                    if (prefix < 4)
                    {
                        uint suffix = bits.Read(3);
                        magnitude = (uint)(prefix * 4 + 2) + (suffix >> 1);
                        sign = suffix & 1;
                    }
                    else
                    {
                        uint suffix = bits.Read(4);
                        magnitude = (uint)(prefix * 8 - 14) + (suffix >> 1);
                        sign = suffix & 1;
                    }
                }
                else if (prefix == 0)
                {
                    uint suffix = bits.Read(2);
                    magnitude = 2 + (suffix >> 1);
                    sign = suffix & 1;
                }
                else if (prefix == 1)
                {
                    uint suffix = bits.Read(3);
                    magnitude = 4 + (suffix >> 1);
                    sign = suffix & 1;
                }
                else
                {
                    uint suffix = bits.Read(4);
                    magnitude = (uint)(prefix * 8 - 8) + (suffix >> 1);
                    sign = suffix & 1;
                }
                short value = (short)(magnitude << CoefficientShift(scan, limit));
                luma[Zigzag[scan]] = sign == 0 ? value : (short)-value;
                scan++;
                continue;
            }

            switch (bits.Read(2))
            {
                case 0:
                    scan++;
                    break;
                case 2:
                    luma[Zigzag[scan]] = (short)(1 << CoefficientShift(scan, limit));
                    scan++;
                    break;
                case 3:
                    luma[Zigzag[scan]] = (short)-(1 << CoefficientShift(scan, limit));
                    scan++;
                    break;
                default: // 1: end of block, or a run of zeros
                    if (bits.Read(1) == 0) return;
                    uint first = bits.Read(2);
                    uint skip = first + 3;
                    if (first == 3)
                    {
                        uint extension;
                        do
                        {
                            extension = bits.Read(3);
                            skip += extension;
                        }
                        while (extension == 7);
                    }
                    if (scan + skip > 64) throw new ProtocolException("An adaptive-codec run of zeros goes past its block.");
                    scan += (int)skip;
                    break;
            }
        }
    }

    /// <summary>
    /// The descale Apple applies to Rice-coded AC terms: with a limit above 14, ×2 below the limit
    /// and ×8 from it; otherwise ×8 below and ×16 from it.
    /// </summary>
    private static int CoefficientShift(int scan, byte limit) =>
        limit > 14 ? (scan < limit ? 1 : 3) : (scan < limit ? 3 : 4);

    private static short DecodeDcRice(ref MvsBits bits)
    {
        int prefix = bits.ReadOnes(39);
        uint magnitude, sign;
        switch (prefix)
        {
            case 0:
                if (bits.Read(1) == 0) return 0;
                magnitude = 1;
                sign = bits.Read(1);
                break;
            case 1:
            {
                uint suffix = bits.Read(2);
                magnitude = 2 + (suffix >> 1);
                sign = suffix & 1;
                break;
            }
            case 2:
            {
                uint suffix = bits.Read(3);
                magnitude = 4 + (suffix >> 1);
                sign = suffix & 1;
                break;
            }
            default:
            {
                uint suffix = bits.Read(4);
                magnitude = (uint)(prefix * 8 - 16) + (suffix >> 1);
                sign = suffix & 1;
                break;
            }
        }
        return sign == 0 ? (short)magnitude : (short)-(short)magnitude;
    }

    // ───────────────────────────── Refinement pass (body type 1) ─────────────────────────────

    // Fully optimized from the first call: a session decodes few rectangles, each of thousands of tiles.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void DecodeRefinement(ReadOnlySpan<byte> body, Framebuffer fb, PixelRect rc)
    {
        if (body.Length < 3) throw Truncated();
        var bits = new MvsBits(body[3..]);
        byte lumaLimit = Math.Min(body[1], (byte)64), chromaLimit = Math.Min(body[2], (byte)64);
        int tilesWide = (rc.Width + 7) / 8;
        int total = tilesWide * ((rc.Height + 7) / 8);
        Span<short> coefficients = stackalloc short[CachedTileBytes];

        for (int tile = 0; tile < total; tile++)
        {
            int localX = tile % tilesWide * 8, localY = tile / tilesWide * 8;
            int w = Math.Min(8, rc.Width - localX), h = Math.Min(8, rc.Height - localY);
            int x = rc.X + localX, y = rc.Y + localY;
            int global = y / 8 * _tilesWide + x / 8;
            if ((uint)global >= (uint)_tileCount)
            {
                throw new ProtocolException("An adaptive-codec tile lies outside the screen.");
            }
            switch (bits.Read(2))
            {
                case 0:
                    break;
                case 1:
                    DecodeDifferential(ref bits, global, lumaLimit, chromaLimit, coefficients);
                    QueueCoefficients(fb, OpKind.Dct, coefficients, x, y, w, h);
                    InsertCached(coefficients);
                    break;
                case 2:
                    // Repeats the copy the base pass made, if it was made in this tile's latest pass.
                    int source = _copySource[global];
                    if (source >= 0 && _generation[source] == _generation[global]) QueueCopy(fb, source, x, y, w, h);
                    break;
                case 3:
                    int index = bits.Read(1) != 0
                        ? NextCacheIndex(_lastCacheReference)
                        : (int)(bits.Read(8) << 8 | bits.Read(8));
                    if (TryCached(index, out int cached)) QueueCached(fb, cached, x, y, w, h);
                    break;
            }
        }
        if (bits.Read(8) != 0x6d || bits.Read(8) != 0x76 || bits.Read(8) != 0x73)
        {
            throw new ProtocolException("An adaptive-codec refinement didn't end where it should.");
        }
    }

    /// <summary>
    /// Sharpens a tile relative to the DCT block its last base pass sent (refinements never move
    /// that baseline): luma terms as deltas, chroma as a one-step DC change plus Huffman-coded AC.
    /// </summary>
    private void DecodeDifferential(ref MvsBits bits, int tile, byte lumaLimit, byte chromaLimit, scoped Span<short> coefficients)
    {
        var baseline = _baseline.AsSpan(tile * RiceBlockLength, RiceBlockLength);
        int oldCount = _baselineCount[tile];
        if (oldCount > 64) throw new ProtocolException("An adaptive-codec tile has an impossible baseline.");
        int newCount = (int)bits.Read(6) + 1;

        Span<sbyte> old = stackalloc sbyte[64];
        Span<sbyte> values = stackalloc sbyte[64];
        // Apple keeps the baseline as signed bytes.
        for (int scan = 0; scan < 64; scan++) old[scan] = unchecked((sbyte)baseline[Zigzag[scan]]);
        values.Clear();
        values[0] = old[0];

        int sharedEnd = Math.Min(oldCount, newCount);
        if (oldCount < 15)
        {
            for (int scan = 1; scan < sharedEnd; scan++)
            {
                values[scan] = old[scan] == 0 ? unchecked((sbyte)DecodeDcRice(ref bits)) : SignedDelta(old[scan], bits.Read(3));
            }
            // Apple's loop continues from the baseline's own count (which may be 0, rewriting the DC term).
            for (int scan = oldCount; scan < newCount; scan++)
            {
                values[scan] = old[scan] == 0 ? unchecked((sbyte)DecodeDcRice(ref bits)) : SignedDelta(old[scan], bits.Read(4));
            }
        }
        else
        {
            for (int scan = 1; scan < sharedEnd; scan++)
            {
                values[scan] = old[scan] == 0
                    ? (bits.Read(1) == 0 ? (sbyte)0 : bits.Read(1) == 0 ? (sbyte)1 : (sbyte)-1)
                    : SignedDelta(old[scan], bits.Read(1));
            }
            for (int scan = oldCount; scan < newCount; scan++)
            {
                values[scan] = old[scan] == 0 ? unchecked((sbyte)DecodeDcRice(ref bits)) : SignedDelta(old[scan], bits.Read(3));
            }
        }

        coefficients.Clear();
        for (int scan = 0; scan < newCount; scan++) coefficients[Zigzag[scan]] = values[scan];

        // Chroma comes Cr first, and Cr's AC terms run to the luma limit.
        coefficients[128] = OneStepDc(ref bits, unchecked((sbyte)baseline[65]));
        DecodeHuffmanAc(ref bits, coefficients.Slice(128, 64), lumaLimit);
        coefficients[64] = OneStepDc(ref bits, unchecked((sbyte)baseline[64]));
        DecodeHuffmanAc(ref bits, coefficients.Slice(64, 64), chromaLimit);
    }

    private static sbyte SignedDelta(sbyte old, uint magnitude) =>
        unchecked(old < 0 ? (sbyte)(old - (sbyte)magnitude) : (sbyte)(old + (sbyte)magnitude));

    private static short OneStepDc(ref MvsBits bits, sbyte old)
    {
        bool changes = bits.Read(1) != 0;
        if (old == 0)
        {
            if (!changes) return 0;
            return bits.Read(1) == 0 ? (short)1 : (short)-1;
        }
        if (!changes) return old;
        return unchecked(old < 0 ? (sbyte)(old - 1) : (sbyte)(old + 1));
    }

    private static void DecodeHuffmanAc(ref MvsBits bits, scoped Span<short> component, byte limit)
    {
        // Apple's counter starts before the first AC term and runs while below the limit; here
        // the scan position starts at 1, so the test is inclusive.
        int scan = 1;
        while (scan <= limit)
        {
            byte symbol = DecodeAcSymbol(ref bits);
            int run = symbol >> 4, size = symbol & 0x0f;
            if (size == 0)
            {
                if (run != 15) break;
                scan += 16;
                continue;
            }
            scan += run;
            if (scan >= 80) throw new ProtocolException("An adaptive-codec Huffman run goes past its block.");
            int encoded = (int)bits.Read(size);
            int value = encoded < 1 << (size - 1) ? encoded + 1 - (1 << size) : encoded;
            component[scan < 64 ? Zigzag[scan] : 63] = (short)value;
            scan++;
        }
    }

    private static byte DecodeAcSymbol(ref MvsBits bits)
    {
        int code = 0, firstCode = 0, valueIndex = 0;
        for (int length = 1; length <= 16; length++)
        {
            code = code << 1 | (int)bits.Read(1);
            int count = LuminanceAcBits[length];
            if (code >= firstCode && code < firstCode + count)
            {
                return LuminanceAcValues[valueIndex + code - firstCode];
            }
            valueIndex += count;
            firstCode = (firstCode + count) << 1;
        }
        throw new ProtocolException("An adaptive-codec Huffman code isn't in the table.");
    }

    // ───────────────────────────── The DCT cache ─────────────────────────────

    private static int NextCacheIndex(int index) => index == CacheLastIndex ? 1 : index + 1;

    /// <summary>
    /// Caches a refined tile the way Apple's decoder does: as signed bytes, with all 64 luma terms
    /// but only the first 20 Cb and 15 Cr zigzag terms, so a recall renders smoother chroma than
    /// the tile did when it arrived.
    /// </summary>
    private void InsertCached(ReadOnlySpan<short> coefficients)
    {
        int index = NextCacheIndex(_cacheWriteIndex);
        _cacheWriteIndex = index;
        _cacheInsertions++;
        int needed = (index + 1) * CachedTileBytes;
        if (_cache.Length < needed)
        {
            Array.Resize(ref _cache, Math.Min((CacheLastIndex + 1) * CachedTileBytes, Math.Max(needed, _cache.Length * 2)));
        }
        var slot = _cache.AsSpan(index * CachedTileBytes, CachedTileBytes);
        for (int i = 0; i < 64; i++) slot[i] = unchecked((sbyte)coefficients[i]);
        slot.Slice(64, 128).Clear();
        for (int scan = 0; scan < 20; scan++) slot[64 + Zigzag[scan]] = unchecked((sbyte)coefficients[64 + Zigzag[scan]]);
        for (int scan = 0; scan < 15; scan++) slot[128 + Zigzag[scan]] = unchecked((sbyte)coefficients[128 + Zigzag[scan]]);
    }

    private bool TryCached(int index, out int offset)
    {
        offset = 0;
        if (index == 0 || index > CacheLastIndex || index > _cacheInsertions || (index + 1) * CachedTileBytes > _cache.Length)
        {
            return false;
        }
        _lastCacheReference = index;
        offset = index * CachedTileBytes;
        return true;
    }

    // ───────────────────────────── Drawing ─────────────────────────────

    // A rectangle is parsed in order into a batch of tile operations; the DCT tiles in the batch
    // are then rendered across the CPU's cores, and the batch is applied to the screen in order,
    // so a copy still sees exactly what decoding tile by tile would have shown.

    private enum OpKind : byte { Fill, Pixels, Copy, Dct, RiceDct }

    private struct TileOp
    {
        public int X, Y, W, H, Source;
        public uint Colour;
        public OpKind Kind;
    }

    private const int BatchSize = 4096;
    private const int ParallelThreshold = 96;
    private const int RenderChunk = 128;
    private readonly TileOp[] _ops = new TileOp[BatchSize];
    private readonly int[] _dctOps = new int[BatchSize];
    private readonly short[] _coefficientPool = new short[BatchSize * CachedTileBytes];
    private readonly uint[] _pixelPool = new uint[BatchSize * 64];
    private int _opCount, _dctCount;

    /// <summary>The next operation's index, which is also its slot in the coefficient and pixel pools.</summary>
    private int NextOp(Framebuffer fb, OpKind kind, int x, int y, int w, int h)
    {
        if (_opCount == BatchSize) Flush(fb);
        int index = _opCount++;
        ref var op = ref _ops[index];
        op.Kind = kind;
        op.X = x;
        op.Y = y;
        op.W = w;
        op.H = h;
        return index;
    }

    private void QueueFill(Framebuffer fb, int x, int y, int w, int h, uint colour) =>
        _ops[NextOp(fb, OpKind.Fill, x, y, w, h)].Colour = colour;

    private void QueueCopy(Framebuffer fb, int source, int x, int y, int w, int h) =>
        _ops[NextOp(fb, OpKind.Copy, x, y, w, h)].Source = source;

    private void QueueCoefficients(Framebuffer fb, OpKind kind, ReadOnlySpan<short> coefficients, int x, int y, int w, int h)
    {
        int index = NextOp(fb, kind, x, y, w, h);
        coefficients.CopyTo(_coefficientPool.AsSpan(index * CachedTileBytes, coefficients.Length));
        _dctOps[_dctCount++] = index;
    }

    private void QueueCached(Framebuffer fb, int offset, int x, int y, int w, int h)
    {
        int index = NextOp(fb, OpKind.Dct, x, y, w, h);
        var slot = _cache.AsSpan(offset, CachedTileBytes);
        var destination = _coefficientPool.AsSpan(index * CachedTileBytes, CachedTileBytes);
        for (int i = 0; i < CachedTileBytes; i++) destination[i] = slot[i];
        _dctOps[_dctCount++] = index;
    }

    private void QueueBilevel(ref MvsBits bits, Framebuffer fb, int x, int y, int w, int h, uint uniform, uint zero, uint one)
    {
        int index = NextOp(fb, OpKind.Pixels, x, y, w, h);
        var pixels = _pixelPool.AsSpan(index * 64, 64);
        // All eight rows are always coded, even for a tile clipped at the screen's edge.
        uint uniformRows = bits.Read(8);
        for (int row = 0; row < 8; row++)
        {
            var line = pixels.Slice(row * 8, 8);
            if ((uniformRows & (0x80u >> row)) != 0)
            {
                line.Fill(uniform);
                continue;
            }
            uint mask = bits.Read(8);
            for (int col = 0; col < 8; col++) line[col] = (mask & (0x80u >> col)) == 0 ? zero : one;
        }
    }

    /// <summary>Renders the batch's DCT tiles (across cores when there are enough), then applies the batch.</summary>
    private void Flush(Framebuffer fb)
    {
        if (_opCount == 0) return;
        int count = _dctCount;
        if (count < ParallelThreshold)
        {
            for (int i = 0; i < count; i++) Render(_dctOps[i]);
        }
        else
        {
            Parallel.For(0, (count + RenderChunk - 1) / RenderChunk, chunk =>
            {
                int end = Math.Min(count, (chunk + 1) * RenderChunk);
                for (int i = chunk * RenderChunk; i < end; i++) Render(_dctOps[i]);
            });
        }
        lock (fb.Sync) Apply(fb);
        _opCount = _dctCount = 0;
    }

    private void Render(int index)
    {
        var coefficients = _coefficientPool.AsSpan(index * CachedTileBytes, CachedTileBytes);
        var pixels = _pixelPool.AsSpan(index * 64, 64);
        Span<int> luma = stackalloc int[64];
        InverseDct(coefficients[..64], _luminanceQuantization, luma);
        if (_ops[index].Kind == OpKind.RiceDct)
        {
            // A base-pass block: its chroma is flat, from the DC terms.
            int cb = DcSample(coefficients[64], _chrominanceQuantization[0]);
            int cr = DcSample(coefficients[65], _chrominanceQuantization[0]);
            for (int i = 0; i < 64; i++) pixels[i] = YCbCr(luma[i], cb, cr);
            return;
        }
        Span<int> cbs = stackalloc int[64], crs = stackalloc int[64];
        InverseDct(coefficients.Slice(64, 64), _chrominanceQuantization, cbs);
        InverseDct(coefficients.Slice(128, 64), _chrominanceQuantization, crs);
        for (int i = 0; i < 64; i++) pixels[i] = YCbCr(luma[i], cbs[i], crs[i]);
    }

    private void Apply(Framebuffer fb)
    {
        var px = Px.Words(fb);
        int stride = fb.Width;
        for (int i = 0; i < _opCount; i++)
        {
            ref var op = ref _ops[i];
            switch (op.Kind)
            {
                case OpKind.Fill:
                    for (int row = 0; row < op.H; row++) px.Slice((op.Y + row) * stride + op.X, op.W).Fill(op.Colour);
                    break;
                case OpKind.Copy:
                    CopyTile(fb, op.Source, op.X, op.Y, op.W, op.H);
                    break;
                default:
                    var pixels = _pixelPool.AsSpan(i * 64, 64);
                    for (int row = 0; row < op.H; row++) pixels.Slice(row * 8, op.W).CopyTo(px.Slice((op.Y + row) * stride + op.X, op.W));
                    break;
            }
        }
    }

    /// <summary>A colour as 8-bit Y and the top six bits of Cb and Cr.</summary>
    private static uint ReadColour(ref MvsBits bits)
    {
        int y = (int)bits.Read(8);
        int cb = (int)bits.Read(6) * 4;
        int cr = (int)bits.Read(6) * 4;
        return YCbCr(y, cb, cr);
    }

    /// <summary>Copies what the source tile shows. The base pass only names tiles before this one, and the copy covers this tile's own size.</summary>
    private void CopyTile(Framebuffer fb, int source, int x, int y, int w, int h)
    {
        int sx = source % _tilesWide * 8, sy = source / _tilesWide * 8;
        int cw = Math.Min(w, fb.Width - sx), ch = Math.Min(h, fb.Height - sy);
        var px = Px.Words(fb);
        for (int row = 0; row < ch; row++)
        {
            px.Slice((sy + row) * fb.Width + sx, cw).CopyTo(px.Slice((y + row) * fb.Width + x, cw));
        }
    }

    /// <summary>One pixel from YCbCr samples, each already within 0–255.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint YCbCr(int y, int cb, int cr)
    {
        ref int red = ref MemoryMarshal.GetArrayDataReference(RedFromCr);
        ref int blue = ref MemoryMarshal.GetArrayDataReference(BlueFromCb);
        ref int greenCb = ref MemoryMarshal.GetArrayDataReference(GreenFromCb);
        ref int greenCr = ref MemoryMarshal.GetArrayDataReference(GreenFromCr);
        int r = Clamp(y + Unsafe.Add(ref red, cr & 0xff));
        int g = Clamp(y + ((32_768 + Unsafe.Add(ref greenCb, cb & 0xff) + Unsafe.Add(ref greenCr, cr & 0xff)) >> 16));
        int b = Clamp(y + Unsafe.Add(ref blue, cb & 0xff));
        return (uint)b | (uint)g << 8 | (uint)r << 16 | 0xFF000000;
    }

    private static uint Bgrx(byte r, byte g, byte b) => b | (uint)g << 8 | (uint)r << 16 | 0xFF000000;

    internal static int DcSample(short coefficient, ushort quantization) =>
        Math.Clamp(((coefficient * quantization + 4) >> 3) + 128, 0, 255);

    // ───────────────────────────── Inverse DCT ─────────────────────────────

    // libjpeg's jpeg_idct_islow, exactly as Screen Sharing runs it: a column pass rounding through
    // (x + 1024) >> 11 and a row pass through (x + 131072) >> 18, all integer arithmetic wrapping
    // at 32 bits. A single exact pass differs by one level on a few samples, which shows as faint
    // speckle at the edges of text.
    private const int Fix0_298631336 = 2446, Fix0_390180644 = 3196, Fix0_541196100 = 4433, Fix0_765366865 = 6270,
        Fix0_899976223 = 7373, Fix1_175875602 = 9633, Fix1_501321110 = 12_299, Fix1_847759065 = 15_137,
        Fix1_961570560 = 16_069, Fix2_053119869 = 16_819, Fix2_562915447 = 20_995, Fix3_072711026 = 25_172;

    [SkipLocalsInit]
    internal static unsafe void InverseDct(ReadOnlySpan<short> coefficients, ReadOnlySpan<ushort> quantization, Span<int> output)
    {
        if (coefficients.Length < 64 || quantization.Length < 64 || output.Length < 64)
        {
            throw new ArgumentException("A DCT block is 64 terms.");
        }
        unchecked
        {
            // Only a DC term: both passes take their zero-AC shortcuts, so the block is one level.
            if (coefficients[1..64].IndexOfAnyExcept((short)0) < 0)
            {
                output[..64].Fill(Clamp((((coefficients[0] * quantization[0]) << 2) + 16 >> 5) + 128));
                return;
            }

            int* ws = stackalloc int[64];
            fixed (short* c = coefficients)
            fixed (ushort* q = quantization)
            fixed (int* o = output)
            {
                for (int x = 0; x < 8; x++)
                {
                    int d0 = c[x] * q[x], d1 = c[8 + x] * q[8 + x], d2 = c[16 + x] * q[16 + x], d3 = c[24 + x] * q[24 + x];
                    int d4 = c[32 + x] * q[32 + x], d5 = c[40 + x] * q[40 + x], d6 = c[48 + x] * q[48 + x], d7 = c[56 + x] * q[56 + x];
                    if ((d1 | d2 | d3 | d4 | d5 | d6 | d7) == 0)
                    {
                        int dc = d0 << 2;
                        ws[x] = dc; ws[8 + x] = dc; ws[16 + x] = dc; ws[24 + x] = dc;
                        ws[32 + x] = dc; ws[40 + x] = dc; ws[48 + x] = dc; ws[56 + x] = dc;
                        continue;
                    }
                    Pass(d0, d1, d2, d3, d4, d5, d6, d7, out int o0, out int o1, out int o2, out int o3, out int o4, out int o5, out int o6, out int o7);
                    ws[x] = o0 + 1024 >> 11; ws[8 + x] = o1 + 1024 >> 11; ws[16 + x] = o2 + 1024 >> 11; ws[24 + x] = o3 + 1024 >> 11;
                    ws[32 + x] = o4 + 1024 >> 11; ws[40 + x] = o5 + 1024 >> 11; ws[48 + x] = o6 + 1024 >> 11; ws[56 + x] = o7 + 1024 >> 11;
                }

                for (int y = 0; y < 8; y++)
                {
                    int* r = ws + y * 8;
                    int* d = o + y * 8;
                    if ((r[1] | r[2] | r[3] | r[4] | r[5] | r[6] | r[7]) == 0)
                    {
                        int sample = Clamp((r[0] + 16 >> 5) + 128);
                        d[0] = sample; d[1] = sample; d[2] = sample; d[3] = sample;
                        d[4] = sample; d[5] = sample; d[6] = sample; d[7] = sample;
                        continue;
                    }
                    Pass(r[0], r[1], r[2], r[3], r[4], r[5], r[6], r[7], out int o0, out int o1, out int o2, out int o3, out int o4, out int o5, out int o6, out int o7);
                    d[0] = Clamp((o0 + 131_072 >> 18) + 128); d[1] = Clamp((o1 + 131_072 >> 18) + 128);
                    d[2] = Clamp((o2 + 131_072 >> 18) + 128); d[3] = Clamp((o3 + 131_072 >> 18) + 128);
                    d[4] = Clamp((o4 + 131_072 >> 18) + 128); d[5] = Clamp((o5 + 131_072 >> 18) + 128);
                    d[6] = Clamp((o6 + 131_072 >> 18) + 128); d[7] = Clamp((o7 + 131_072 >> 18) + 128);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Clamp(int value) => value < 0 ? 0 : value > 255 ? 255 : value;

    /// <summary>One 1-D stage of jpeg_idct_islow (jidctint.c), wrapping at 32 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Pass(int i0, int i1, int i2, int i3, int i4, int i5, int i6, int i7,
        out int o0, out int o1, out int o2, out int o3, out int o4, out int o5, out int o6, out int o7)
    {
        unchecked
        {
            int z1 = (i2 + i6) * Fix0_541196100;
            int tmp2 = z1 + i6 * -Fix1_847759065;
            int tmp3 = z1 + i2 * Fix0_765366865;
            int tmp0 = (i0 + i4) << 13;
            int tmp1 = (i0 - i4) << 13;
            int tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3, tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;

            int a1 = i7 + i1, a2 = i5 + i3, a3 = i7 + i3, a4 = i5 + i1;
            int z5 = (a3 + a4) * Fix1_175875602;
            int odd0 = i7 * Fix0_298631336, odd1 = i5 * Fix2_053119869, odd2 = i3 * Fix3_072711026, odd3 = i1 * Fix1_501321110;
            a1 *= -Fix0_899976223;
            a2 *= -Fix2_562915447;
            a3 = a3 * -Fix1_961570560 + z5;
            a4 = a4 * -Fix0_390180644 + z5;
            odd0 += a1 + a3;
            odd1 += a2 + a4;
            odd2 += a2 + a3;
            odd3 += a1 + a4;

            o0 = tmp10 + odd3;
            o1 = tmp11 + odd2;
            o2 = tmp12 + odd1;
            o3 = tmp13 + odd0;
            o4 = tmp13 - odd0;
            o5 = tmp12 - odd1;
            o6 = tmp11 - odd2;
            o7 = tmp10 - odd3;
        }
    }

    private static ProtocolException Truncated() => new("The Mac's adaptive-codec data ended early.");
}

/// <summary>Reads an MVS stream most significant bit first.</summary>
internal ref struct MvsBits(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _next;
    private ulong _buffer; // unread bits, left-aligned
    private int _count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Read(int bits)
    {
        if (bits == 0) return 0;
        if (_count < bits) Refill(bits);
        uint value = (uint)(_buffer >> (64 - bits));
        _buffer <<= bits;
        _count -= bits;
        return value;
    }

    private void Refill(int needed)
    {
        while (_count <= 56 && _next < _data.Length)
        {
            _buffer |= (ulong)_data[_next++] << (56 - _count);
            _count += 8;
        }
        if (_count < needed) throw new ProtocolException("The Mac's adaptive-codec data ended early.");
    }

    /// <summary>
    /// Reads a run of 1 bits and the 0 that ends it, returning the run's length. A run longer than
    /// <paramref name="maximum"/> is refused, as Apple's decoder refuses an overlong Rice prefix.
    /// </summary>
    public int ReadOnes(int maximum)
    {
        int total = 0;
        while (true)
        {
            if (_count == 0) Refill(1);
            int ones = Math.Min(BitOperations.LeadingZeroCount(~_buffer), _count);
            total += ones;
            if (total > maximum) throw new ProtocolException("An adaptive-codec value is coded too long.");
            if (ones < _count)
            {
                int used = ones + 1;
                _buffer = used == 64 ? 0 : _buffer << used;
                _count -= used;
                return total;
            }
            _buffer = 0;
            _count = 0;
        }
    }

    /// <summary>
    /// Apple's repeat count: 0 for a clear bit; else a 4-bit value for 1–15; 15 switches to up to
    /// three 7-bit groups, least significant first, with the top bit continuing, plus 16.
    /// </summary>
    public uint RepeatCount()
    {
        if (Read(1) == 0) return 0;
        uint shortForm = Read(4);
        if (shortForm != 15) return shortForm + 1;
        uint value = 0;
        for (int shift = 0; shift <= 14; shift += 7)
        {
            uint group = Read(8);
            value |= (group & 0x7f) << shift;
            if ((group & 0x80) == 0) break;
        }
        return value + 16;
    }
}
