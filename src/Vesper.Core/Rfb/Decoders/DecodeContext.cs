using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vesper.Core.Rfb.Decoders;

/// <summary>Per-session decoder state: the persistent zlib streams and reusable scratch buffers.</summary>
internal sealed class DecodeContext : IDisposable
{
    private byte[] _scratch = new byte[1 << 20];
    private byte[] _compressed = new byte[1 << 18];

    public ZlibInflater Zlib { get; } = new();
    public ZlibInflater Zrle { get; } = new();
    public ZlibInflater Rgb555 { get; } = new();
    public ZlibInflater Gray4 { get; } = new();
    public MvsDecoder Mvs { get; } = new();

    public byte[] Scratch(int size)
    {
        if (_scratch.Length < size)
        {
            _scratch = GC.AllocateUninitializedArray<byte>(Math.Max(size, _scratch.Length * 2));
        }
        return _scratch;
    }

    public byte[] Compressed(int size)
    {
        if (_compressed.Length < size)
        {
            _compressed = GC.AllocateUninitializedArray<byte>(Math.Max(size, _compressed.Length * 2));
        }
        return _compressed;
    }

    public void Dispose()
    {
        Zlib.Dispose();
        Zrle.Dispose();
        Rgb555.Dispose();
        Gray4.Dispose();
    }
}

/// <summary>Pixel helpers for the fixed B,G,R,x framebuffer layout (little-endian 0xFFRRGGBB words).</summary>
internal static class Px
{
    public const uint Opaque = 0xFF000000;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Pixel32(ReadOnlySpan<byte> s, int i) =>
        s[i] | (uint)s[i + 1] << 8 | (uint)s[i + 2] << 16 | Opaque;

    /// <summary>ZRLE's 3-byte CPIXEL for our pixel format: the low three bytes, B,G,R.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint CPixel(ReadOnlySpan<byte> s, int i) =>
        s[i] | (uint)s[i + 1] << 8 | (uint)s[i + 2] << 16 | Opaque;

    public static Span<uint> Words(Framebuffer fb) => MemoryMarshal.Cast<byte, uint>(fb.Pixels.AsSpan());

    public static void Fill(Framebuffer fb, int x, int y, int w, int h, uint colour)
    {
        var px = Words(fb);
        for (int row = 0; row < h; row++)
        {
            px.Slice((y + row) * fb.Width + x, w).Fill(colour);
        }
    }

    /// <summary>
    /// Copies tightly packed 32bpp rows into the framebuffer. The x byte is copied as-is: the
    /// renderer treats the framebuffer as alpha-ignored, so it never has to be patched to 0xFF.
    /// </summary>
    public static void CopyRows(ReadOnlySpan<byte> src, Framebuffer fb, int x, int y, int w, int h)
    {
        var dst = fb.Pixels.AsSpan();
        int rowBytes = w * 4;
        for (int row = 0; row < h; row++)
        {
            src.Slice(row * rowBytes, rowBytes).CopyTo(dst.Slice(((y + row) * fb.Width + x) * 4, rowBytes));
        }
    }
}
