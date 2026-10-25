using System.IO.Compression;

namespace Vesper.Core.Rfb;

/// <summary>
/// A pointer image: straight (not premultiplied) B,G,R,A pixels plus the hotspot. Drawn locally so
/// the pointer moves at the client's frame rate instead of waiting for the Mac to repaint it.
/// </summary>
public sealed record CursorShape(int Width, int Height, int HotspotX, int HotspotY, byte[] Bgra)
{
    public const int MaxDimension = 512;

    /// <summary>The pointer is hidden (e.g. while typing, or in a full-screen video).</summary>
    public static CursorShape Hidden { get; } = new(0, 0, 0, 0, []);

    /// <summary>One of macOS's built-in cursors, named by id only; shown as the local arrow.</summary>
    public static CursorShape SystemDefault { get; } = new(-1, -1, 0, 0, []);

    public bool IsHidden => Width == 0 || Height == 0;

    public bool IsSystemDefault => ReferenceEquals(this, SystemDefault);
}

/// <summary>
/// Apple's cursor cache (encoding 0x450): a shape is stored once with pixels and then re-selected
/// by id. Each stored shape is its own zlib stream holding w·h·4 B,G,R,x bytes followed by a
/// separate w·h alpha plane.
/// </summary>
internal sealed class AppleCursorCache
{
    private const int MaxShapes = 256;

    /// <summary>A Mac's real pointers are small; a server filling the cache with 512² shapes is capped at this.</summary>
    private const long MaxBytes = 32L << 20;

    private readonly Dictionary<uint, CursorShape> _shapes = [];
    private long _bytes;

    /// <summary>Returns the shape to show, or null to leave the pointer as it is.</summary>
    public CursorShape? Accept(uint id, int hotX, int hotY, int width, int height, ReadOnlySpan<byte> deflated)
    {
        if (deflated.IsEmpty)
        {
            return _shapes.GetValueOrDefault(id);
        }
        if (width <= 0 || height <= 0 || width > CursorShape.MaxDimension || height > CursorShape.MaxDimension)
        {
            return null;
        }
        int pixels = width * height;
        byte[] raw = new byte[pixels * 5];
        try
        {
            using var input = new MemoryStream(deflated.ToArray(), writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            zlib.ReadExactly(raw);
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
        {
            return null; // a bad shape is skipped; it carries no state the next one depends on
        }

        byte[] bgra = new byte[pixels * 4];
        for (int i = 0; i < pixels; i++)
        {
            bgra[i * 4] = raw[i * 4];
            bgra[i * 4 + 1] = raw[i * 4 + 1];
            bgra[i * 4 + 2] = raw[i * 4 + 2];
            bgra[i * 4 + 3] = raw[pixels * 4 + i];
        }
        var shape = new CursorShape(width, height, Math.Clamp(hotX, 0, width - 1), Math.Clamp(hotY, 0, height - 1), bgra);
        if (_shapes.Remove(id, out var replaced)) _bytes -= replaced.Bgra.Length;
        if (_shapes.Count >= MaxShapes || _bytes + bgra.Length > MaxBytes)
        {
            _shapes.Clear();
            _bytes = 0;
        }
        _shapes[id] = shape;
        _bytes += bgra.Length;
        return shape;
    }

    /// <summary>
    /// The standard RFB Cursor pseudo-encoding (-239): w·h 32bpp pixels, then a 1-bit mask with rows
    /// padded to whole bytes.
    /// </summary>
    public static CursorShape FromRfbCursor(int hotX, int hotY, int width, int height, ReadOnlySpan<byte> pixels, ReadOnlySpan<byte> mask)
    {
        if (width == 0 || height == 0) return CursorShape.Hidden;
        int maskStride = (width + 7) / 8;
        byte[] bgra = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                bool opaque = (mask[y * maskStride + x / 8] & (0x80 >> (x % 8))) != 0;
                bgra[i] = pixels[i];
                bgra[i + 1] = pixels[i + 1];
                bgra[i + 2] = pixels[i + 2];
                bgra[i + 3] = (byte)(opaque ? 255 : 0);
            }
        }
        return new CursorShape(width, height, Math.Clamp(hotX, 0, width - 1), Math.Clamp(hotY, 0, height - 1), bgra);
    }
}
