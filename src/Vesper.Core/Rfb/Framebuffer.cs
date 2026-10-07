namespace Vesper.Core.Rfb;

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public long Area => (long)Width * Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public PixelRect Union(PixelRect o)
    {
        if (IsEmpty) return o;
        if (o.IsEmpty) return this;
        int x = Math.Min(X, o.X), y = Math.Min(Y, o.Y);
        return new PixelRect(x, y, Math.Max(Right, o.Right) - x, Math.Max(Bottom, o.Bottom) - y);
    }
}

/// <summary>
/// The client-side copy of the Mac's screen, 32bpp B,G,R,x. The decode thread writes into it and the
/// render thread copies changed regions out; both hold <see cref="Sync"/> while touching pixels.
/// </summary>
public sealed class Framebuffer
{
    /// <summary>Refuse absurd sizes from a hostile server instead of allocating gigabytes.</summary>
    public const int MaxDimension = 16384;

    /// <summary>
    /// 2^27 pixels (512 MiB): room for every display of a fully loaded Mac Studio side by side at
    /// native resolution, but not for 16384 × 16384.
    /// </summary>
    public const long MaxPixels = 1L << 27;

    private const int MaxDirtyRects = 48;

    private readonly List<PixelRect> _dirty = [];
    private long _dirtyArea;

    public Framebuffer(int width, int height) => Allocate(width, height);

    public object Sync { get; } = new();

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Stride => Width * 4;
    public byte[] Pixels { get; private set; } = [];

    /// <summary>Bumped on every resize so the renderer knows to recreate its texture.</summary>
    public int Generation { get; private set; }

    /// <summary>Raised (outside the lock) after pixels change. The renderer uses it to wake up.</summary>
    public event Action? Changed;

    public void Resize(int width, int height)
    {
        lock (Sync)
        {
            if (width == Width && height == Height) return;
            Allocate(width, height);
        }
        Changed?.Invoke();
    }

    private void Allocate(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension || (long)width * height > MaxPixels)
        {
            throw new ProtocolException($"The Mac reported an unusable screen size ({width}×{height}).");
        }
        Width = width;
        Height = height;
        Pixels = GC.AllocateUninitializedArray<byte>(checked(width * height * 4));
        Pixels.AsSpan().Clear();
        Generation++;
        _dirty.Clear();
        _dirty.Add(new PixelRect(0, 0, width, height));
        _dirtyArea = (long)width * height;
    }

    public bool Contains(int x, int y, int w, int h) =>
        x >= 0 && y >= 0 && w >= 0 && h >= 0 && (long)x + w <= Width && (long)y + h <= Height;

    /// <summary>Records a changed region. Caller holds <see cref="Sync"/>.</summary>
    public void MarkDirty(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        var r = new PixelRect(x, y, w, h);
        _dirtyArea += r.Area;
        if (_dirty.Count >= MaxDirtyRects || _dirtyArea >= (long)Width * Height / 2)
        {
            // Many small rects or most of the screen: one bounding box is cheaper to upload.
            var all = r;
            foreach (var d in _dirty) all = all.Union(d);
            _dirty.Clear();
            _dirty.Add(all);
            _dirtyArea = all.Area;
            return;
        }
        _dirty.Add(r);
    }

    /// <summary>Called by the decoder once a whole FramebufferUpdate has been applied.</summary>
    public void Commit() => Changed?.Invoke();

    /// <summary>Moves the pending dirty rects into <paramref name="into"/>. Caller holds <see cref="Sync"/>.</summary>
    public bool TakeDirty(List<PixelRect> into)
    {
        if (_dirty.Count == 0) return false;
        into.AddRange(_dirty);
        _dirty.Clear();
        _dirtyArea = 0;
        return true;
    }

    public void InvalidateAll()
    {
        lock (Sync)
        {
            _dirty.Clear();
            _dirty.Add(new PixelRect(0, 0, Width, Height));
            _dirtyArea = (long)Width * Height;
        }
        Changed?.Invoke();
    }
}
