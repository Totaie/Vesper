using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Vesper.Core.Rfb;

namespace Vesper.Linux.Rendering;

/// <summary>
/// Turns the Mac's pointer images into real system cursors (X11 cursors on Linux), so the pointer
/// moves at the mouse's own rate with no network round trip. Images are scaled so one Mac point
/// is as many screen pixels as the picture shows it at; anything unusable falls back to the arrow.
/// </summary>
internal sealed class CursorFactory : IDisposable
{
    private readonly Dictionary<(CursorShape Shape, int Size), (Cursor Cursor, Bitmap Image)> _cache = [];
    private readonly List<(Cursor Cursor, Bitmap Image)> _retired = [];
    private Cursor? _hidden;
    private Cursor? _lastMade;

    public Cursor Arrow { get; } = new(StandardCursorType.Arrow);

    private Cursor Hidden => _hidden ??= new Cursor(StandardCursorType.None);

    /// <summary>The cursor for a Mac pointer image, scaled so one Mac point is <paramref name="pixelsPerPoint"/> screen pixels.</summary>
    public Cursor For(CursorShape shape, double pixelsPerPoint)
    {
        if (shape.IsSystemDefault) return Arrow;
        if (shape.IsHidden) return Hidden;
        double scale = Math.Clamp(pixelsPerPoint, 0.5, 4.0);
        int width = Math.Max(1, (int)Math.Round(shape.Width * scale));
        if (_cache.TryGetValue((shape, width), out var hit)) return _lastMade = hit.Cursor;

        int height = Math.Max(1, (int)Math.Round(shape.Height * scale));
        try
        {
            byte[] pixels = width == shape.Width && height == shape.Height ? shape.Bgra : Resample(shape, width, height);
            var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Unpremul);
            using (var target = bitmap.Lock())
            {
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(pixels, y * width * 4, target.Address + y * target.RowBytes, width * 4);
                }
            }
            var hotspot = new PixelPoint(
                Math.Clamp((int)Math.Round(shape.HotspotX * scale), 0, width - 1),
                Math.Clamp((int)Math.Round(shape.HotspotY * scale), 0, height - 1));
            var cursor = new Cursor(bitmap, hotspot);
            if (_cache.Count > 64) Clear();
            _cache[(shape, width)] = (cursor, bitmap);
            return _lastMade = cursor;
        }
        catch (Exception e)
        {
            Diagnostics.Add($"Pointer image not usable ({e.GetType().Name}); showing the arrow.");
            return Arrow;
        }
    }

    /// <summary>Bilinear resample of a straight-alpha image, weighting colour by alpha so edges stay clean.</summary>
    private static byte[] Resample(CursorShape shape, int width, int height)
    {
        var src = shape.Bgra;
        int sw = shape.Width, sh = shape.Height;
        var dst = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            double fy = Math.Clamp((y + 0.5) * sh / height - 0.5, 0, sh - 1);
            int y0 = (int)fy, y1 = Math.Min(y0 + 1, sh - 1);
            double ty = fy - y0;
            for (int x = 0; x < width; x++)
            {
                double fx = Math.Clamp((x + 0.5) * sw / width - 0.5, 0, sw - 1);
                int x0 = (int)fx, x1 = Math.Min(x0 + 1, sw - 1);
                double tx = fx - x0;
                double a = 0, b = 0, g = 0, r = 0;
                Accumulate(x0, y0, (1 - tx) * (1 - ty));
                Accumulate(x1, y0, tx * (1 - ty));
                Accumulate(x0, y1, (1 - tx) * ty);
                Accumulate(x1, y1, tx * ty);
                int o = (y * width + x) * 4;
                if (a > 0)
                {
                    dst[o] = (byte)Math.Round(b / a);
                    dst[o + 1] = (byte)Math.Round(g / a);
                    dst[o + 2] = (byte)Math.Round(r / a);
                }
                dst[o + 3] = (byte)Math.Round(a);

                void Accumulate(int px, int py, double w)
                {
                    int i = (py * sw + px) * 4;
                    double alpha = src[i + 3] * w;
                    a += alpha;
                    b += src[i] * alpha;
                    g += src[i + 1] * alpha;
                    r += src[i + 2] * alpha;
                }
            }
        }
        return dst;
    }

    /// <summary>Drops the cache; the cursor on screen now is kept until the factory goes.</summary>
    private void Clear()
    {
        foreach (var entry in _cache.Values)
        {
            if (ReferenceEquals(entry.Cursor, _lastMade))
            {
                _retired.Add(entry);
                continue;
            }
            entry.Cursor.Dispose();
            entry.Image.Dispose();
        }
        _cache.Clear();
    }

    public void Dispose()
    {
        foreach (var (cursor, image) in _cache.Values.Concat(_retired))
        {
            cursor.Dispose();
            image.Dispose();
        }
        _cache.Clear();
        _retired.Clear();
        _hidden?.Dispose();
        Arrow.Dispose();
    }
}
