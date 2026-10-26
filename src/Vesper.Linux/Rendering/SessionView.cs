using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Vesper.Core.Rfb;
using Vesper.Models;
using PixelRect = Vesper.Core.Rfb.PixelRect;

namespace Vesper.Linux.Rendering;

/// <summary>
/// The Mac's screen. Copies into bitmaps only the rectangles the Mac changed, at most once per
/// displayed frame however fast updates arrive, so an idle session costs nothing. The picture is
/// kept as tiles, because a bitmap that changes is copied and uploaded to the GPU whole: a
/// blinking cursor on a 5K screen re-sends one tile, not 30 MB. Each tile carries a margin of its
/// neighbours' pixels, so filtering across tile edges blends the true pixels and no seams show.
/// It's drawn one framebuffer pixel per screen pixel (nearest-neighbour) when that's the scale,
/// and with high-quality filtering otherwise. Input is mapped back through the same
/// <see cref="Placement"/>, so a click lands on exactly the pixel drawn under it.
/// </summary>
public sealed class SessionView : Control
{
    private const int TileSize = 512;
    private const int Apron = 8;

    private static readonly IBrush Backdrop = new SolidColorBrush(Color.FromRgb(12, 12, 16));

    private sealed class Tile(WriteableBitmap bitmap, PixelRect inner, PixelRect covered)
    {
        public WriteableBitmap Bitmap { get; } = bitmap;

        /// <summary>The framebuffer pixels this tile shows.</summary>
        public PixelRect Inner { get; } = inner;

        /// <summary>The framebuffer pixels its bitmap holds: the inner ones and a margin around them.</summary>
        public PixelRect Covered { get; } = covered;

        public List<PixelRect> Pending { get; } = [];
    }

    private readonly List<PixelRect> _dirty = [];
    private readonly List<Tile> _touched = [];
    private Tile[] _tiles = [];
    private int _columns, _rows, _width, _height;
    private Framebuffer? _framebuffer;
    private Framebuffer? _tilesSource;
    private int _tilesGeneration = -1;
    private int _frameRequested;
    private ViewScaling _scaling = ViewScaling.Fit;
    private Point _pan;

    // Frames per second over a rolling second.
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _windowStart;
    private int _frames;

    public SessionView()
    {
        Focusable = true;
        IsTabStop = false;
        ClipToBounds = true;
    }

    /// <summary>Frames shown in the last second.</summary>
    public double FramesPerSecond { get; private set; }

    public ViewScaling Scaling
    {
        get => _scaling;
        set
        {
            _scaling = value;
            InvalidateVisual();
        }
    }

    /// <summary>In 1:1 mode, which part of a larger screen is visible (offset in framebuffer pixels).</summary>
    public Point Pan
    {
        get => _pan;
        set
        {
            if (_pan == value) return;
            _pan = value;
            InvalidateVisual();
        }
    }

    /// <summary>Physical pixels per DIP where the view is shown.</summary>
    public double RenderScale => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

    /// <summary>Draws this framebuffer from now on (a reconnect brings a new one).</summary>
    public void Attach(Framebuffer framebuffer)
    {
        Detach();
        _framebuffer = framebuffer;
        framebuffer.Changed += OnFramebufferChanged;
        RequestFrame();
    }

    /// <summary>Stops drawing the framebuffer; what was last shown stays until the next one.</summary>
    public void Detach()
    {
        if (_framebuffer is not null) _framebuffer.Changed -= OnFramebufferChanged;
        _framebuffer = null;
    }

    /// <summary>Forgets the picture altogether (the window is closing).</summary>
    public void Clear()
    {
        Detach();
        DisposeTiles();
        _tilesSource = null;
        _tilesGeneration = -1;
        InvalidateVisual();
    }

    /// <summary>Raised on the decode thread: ask for one UI-thread copy before the next frame, however many changes come in.</summary>
    private void OnFramebufferChanged() => RequestFrame();

    private void RequestFrame()
    {
        if (Interlocked.Exchange(ref _frameRequested, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (TopLevel.GetTopLevel(this) is { } top) top.RequestAnimationFrame(_ => OnFrame());
            else OnFrame();
        }, DispatcherPriority.Render);
    }

    private void OnFrame()
    {
        // Cleared first: a change arriving during the copy asks for the next frame.
        Volatile.Write(ref _frameRequested, 0);
        if (_framebuffer is { } fb && Upload(fb))
        {
            _frames++;
            InvalidateVisual();
        }
        long now = _clock.ElapsedMilliseconds;
        if (now - _windowStart >= 1000)
        {
            FramesPerSecond = _frames * 1000.0 / (now - _windowStart);
            _frames = 0;
            _windowStart = now;
        }
    }

    /// <summary>Copies what changed from the framebuffer into the tiles. True if anything did.</summary>
    private bool Upload(Framebuffer fb)
    {
        _dirty.Clear();
        lock (fb.Sync)
        {
            int width = fb.Width, height = fb.Height;
            if (_tiles.Length == 0 || !ReferenceEquals(fb, _tilesSource) || fb.Generation != _tilesGeneration)
            {
                // A new size, or a whole new framebuffer after a reconnect, needs new tiles.
                CreateTiles(width, height);
                _tilesGeneration = fb.Generation;
                _tilesSource = fb;
                fb.TakeDirty(_dirty);
                _dirty.Clear();
                _dirty.Add(new PixelRect(0, 0, width, height));
            }
            else if (!fb.TakeDirty(_dirty))
            {
                return false;
            }

            // Which parts of which tiles each change touches (margins included).
            foreach (var r in _dirty)
            {
                int x0 = Math.Max(0, r.X), y0 = Math.Max(0, r.Y);
                int x1 = Math.Min(width, r.Right), y1 = Math.Min(height, r.Bottom);
                if (x1 <= x0 || y1 <= y0) continue;
                int c0 = Math.Max(0, (x0 - Apron) / TileSize), c1 = Math.Min(_columns - 1, (x1 - 1 + Apron) / TileSize);
                int r0 = Math.Max(0, (y0 - Apron) / TileSize), r1 = Math.Min(_rows - 1, (y1 - 1 + Apron) / TileSize);
                for (int row = r0; row <= r1; row++)
                {
                    for (int column = c0; column <= c1; column++)
                    {
                        var tile = _tiles[row * _columns + column];
                        var c = tile.Covered;
                        int ix0 = Math.Max(x0, c.X), iy0 = Math.Max(y0, c.Y), ix1 = Math.Min(x1, c.Right), iy1 = Math.Min(y1, c.Bottom);
                        if (ix1 <= ix0 || iy1 <= iy0) continue;
                        if (tile.Pending.Count == 0) _touched.Add(tile);
                        tile.Pending.Add(new PixelRect(ix0, iy0, ix1 - ix0, iy1 - iy0));
                    }
                }
            }

            var source = fb.Pixels.AsSpan();
            foreach (var tile in _touched)
            {
                Copy(source, width, tile);
                tile.Pending.Clear();
            }
            _touched.Clear();
        }
        return true;
    }

    private static unsafe void Copy(ReadOnlySpan<byte> source, int sourceWidth, Tile tile)
    {
        using var target = tile.Bitmap.Lock();
        byte* destination = (byte*)target.Address;
        int sourceStride = sourceWidth * 4, destinationStride = target.RowBytes;
        var c = tile.Covered;
        foreach (var part in tile.Pending)
        {
            int rowBytes = part.Width * 4;
            for (int y = part.Y; y < part.Bottom; y++)
            {
                source.Slice(y * sourceStride + part.X * 4, rowBytes)
                    .CopyTo(new Span<byte>(destination + (long)(y - c.Y) * destinationStride + (part.X - c.X) * 4, rowBytes));
            }
        }
    }

    private void CreateTiles(int width, int height)
    {
        DisposeTiles();
        _width = width;
        _height = height;
        _columns = (width + TileSize - 1) / TileSize;
        _rows = (height + TileSize - 1) / TileSize;
        _tiles = new Tile[_columns * _rows];
        for (int row = 0; row < _rows; row++)
        {
            for (int column = 0; column < _columns; column++)
            {
                int x = column * TileSize, y = row * TileSize;
                var inner = new PixelRect(x, y, Math.Min(TileSize, width - x), Math.Min(TileSize, height - y));
                int cx = Math.Max(0, x - Apron), cy = Math.Max(0, y - Apron);
                var covered = new PixelRect(cx, cy, Math.Min(width, inner.Right + Apron) - cx, Math.Min(height, inner.Bottom + Apron) - cy);
                var bitmap = new WriteableBitmap(new PixelSize(covered.Width, covered.Height), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Opaque);
                _tiles[row * _columns + column] = new Tile(bitmap, inner, covered);
            }
        }
    }

    private void DisposeTiles()
    {
        foreach (var tile in _tiles) tile.Bitmap.Dispose();
        _tiles = [];
        _columns = _rows = _width = _height = 0;
    }

    /// <summary>
    /// Where the framebuffer lands in the view, in DIPs. Shared by drawing and by input mapping, so
    /// a click lands on exactly the pixel drawn under it. (The Windows app's maths, unchanged.)
    /// </summary>
    public static Rect Placement(double viewW, double viewH, double rasterScale, int fbW, int fbH, ViewScaling scaling, Point pan)
    {
        if (fbW <= 0 || fbH <= 0) return default;
        if (scaling == ViewScaling.ActualSize)
        {
            // One framebuffer pixel per physical pixel, centred when smaller, panned when larger.
            double w = fbW / rasterScale, h = fbH / rasterScale;
            double x = w <= viewW ? (viewW - w) / 2 : -Math.Clamp(pan.X / rasterScale, 0, w - viewW);
            double y = h <= viewH ? (viewH - h) / 2 : -Math.Clamp(pan.Y / rasterScale, 0, h - viewH);
            return new Rect(Snap(x, rasterScale), Snap(y, rasterScale), w, h);
        }
        double scale = Math.Min(viewW / fbW, viewH / fbH);
        double dw = fbW * scale, dh = fbH * scale;
        return new Rect(Snap((viewW - dw) / 2, rasterScale), Snap((viewH - dh) / 2, rasterScale), dw, dh);
    }

    private static double Snap(double dips, double rasterScale) => Math.Round(dips * rasterScale) / rasterScale;

    /// <summary>Where the picture is now, for input mapping and cursor scaling.</summary>
    public Rect CurrentPlacement(int fbWidth, int fbHeight) =>
        Placement(Bounds.Width, Bounds.Height, RenderScale, fbWidth, fbHeight, _scaling, _pan);

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Backdrop, bounds);
        if (_tiles.Length == 0) return;
        double scale = RenderScale;
        var place = Placement(bounds.Width, bounds.Height, scale, _width, _height, _scaling, _pan);
        if (place.Width <= 0 || place.Height <= 0) return;
        double sx = place.Width / _width, sy = place.Height / _height;
        double physicalScale = sx * scale;
        var interpolation = Math.Abs(physicalScale - 1) < 0.001 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality;
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = interpolation, EdgeMode = EdgeMode.Aliased }))
        {
            foreach (var tile in _tiles)
            {
                var inner = tile.Inner;
                var destination = new Rect(place.X + inner.X * sx, place.Y + inner.Y * sy, inner.Width * sx, inner.Height * sy);
                if (!destination.Intersects(bounds)) continue; // panned out of view at 1:1
                var c = tile.Covered;
                context.DrawImage(tile.Bitmap, new Rect(inner.X - c.X, inner.Y - c.Y, inner.Width, inner.Height), destination);
            }
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Clear();
    }
}
