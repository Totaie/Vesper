using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Vesper.Core.Rfb;
using Vesper.Models;
using Vesper.Views;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace Vesper.Rendering;

/// <summary>
/// Draws the Mac's framebuffer into a composition surface on its own thread. Only the rectangles
/// the Mac changed are uploaded to the GPU; scaling to the window happens on the GPU (high-quality
/// cubic when shrinking, exact pixels at 1:1), and the picture is redrawn only when something
/// changed, so an idle session costs nothing. Windows composes the surface with the rest of the
/// window (see <see cref="SessionSurface"/> for why it isn't a swap chain).
/// </summary>
internal sealed class SessionRenderer : IDisposable
{
    private static readonly Windows.UI.Color Background = Windows.UI.Color.FromArgb(255, 12, 12, 16);
    private readonly CompositionGraphicsDevice _graphics;
    private readonly CompositionDrawingSurface _surface;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private readonly List<PixelRect> _dirty = [];
    private readonly object _viewLock = new();
    private volatile bool _running = true;
    private CanvasDevice _device;
    private int _surfaceWidth, _surfaceHeight;
    private CanvasBitmap? _bitmap;
    private int _bitmapGeneration = -1;
    private Framebuffer? _bitmapSource;
    private Framebuffer? _framebuffer;
    private byte[] _staging = [];
    private int _redraw = 1; // set by any thread, taken by the render thread

    // The view, written by the UI thread.
    private double _viewWidth, _viewHeight, _rasterScale = 1;
    private bool _viewChanged;
    private ViewScaling _scaling = ViewScaling.Fit;
    private Point _pan;

    public SessionRenderer(SessionSurface view)
    {
        _device = CanvasDevice.GetSharedDevice();
        var compositor = view.Visual.Compositor;
        _graphics = CanvasComposition.CreateCompositionGraphicsDevice(compositor, _device);
        _graphics.RenderingDeviceReplaced += (_, _) => { Volatile.Write(ref _redraw, 1); _wake.Set(); };
        _surface = _graphics.CreateDrawingSurface(new Size(1, 1),
            Microsoft.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, Microsoft.Graphics.DirectX.DirectXAlphaMode.Premultiplied);
        var brush = compositor.CreateSurfaceBrush(_surface);
        // The surface is drawn at the view's exact physical size, so it's shown pixel for pixel.
        brush.Stretch = CompositionStretch.Fill;
        brush.BitmapInterpolationMode = CompositionBitmapInterpolationMode.NearestNeighbor;
        view.Visual.Brush = brush;
        _thread = new Thread(RenderLoop) { IsBackground = true, Name = "Vesper renderer", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Frames presented in the last second.</summary>
    public double FramesPerSecond { get; private set; }

    /// <summary>Draws this framebuffer from now on (a reconnect brings a new one).</summary>
    public void Attach(Framebuffer framebuffer)
    {
        if (_framebuffer is not null) _framebuffer.Changed -= OnFramebufferChanged;
        _framebuffer = framebuffer;
        framebuffer.Changed += OnFramebufferChanged;
        Volatile.Write(ref _redraw, 1);
        _wake.Set();
    }

    private void OnFramebufferChanged() => _wake.Set();

    /// <summary>Called from the UI thread when the panel's size or the display's scale changes.</summary>
    public void SetView(double widthDips, double heightDips, double rasterScale)
    {
        lock (_viewLock)
        {
            _viewWidth = Math.Max(1, widthDips);
            _viewHeight = Math.Max(1, heightDips);
            _rasterScale = Math.Max(0.5, rasterScale);
            _viewChanged = true;
        }
        _wake.Set();
    }

    public void SetScaling(ViewScaling scaling)
    {
        lock (_viewLock) _scaling = scaling;
        Volatile.Write(ref _redraw, 1);
        _wake.Set();
    }

    /// <summary>In 1:1 mode, which part of a larger screen is visible (offset in framebuffer pixels).</summary>
    public void SetPan(Point pan)
    {
        lock (_viewLock) _pan = pan;
        Volatile.Write(ref _redraw, 1);
        _wake.Set();
    }

    /// <summary>
    /// Where the framebuffer lands in the panel, in DIPs. Shared by drawing and by input mapping,
    /// so a click lands on exactly the pixel drawn under it.
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

    public (double W, double H, double Scale, ViewScaling Scaling, Point Pan) View
    {
        get { lock (_viewLock) return (_viewWidth, _viewHeight, _rasterScale, _scaling, _pan); }
    }

    private void RenderLoop()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int frames = 0;
        long windowStart = 0;
        while (_running)
        {
            _wake.WaitOne(500);
            if (!_running) break;
            try
            {
                if (RenderFrame()) frames++;
            }
            catch (Exception e) when (_device.IsDeviceLost(e.HResult))
            {
                RecreateDevice();
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception e)
            {
                // A bad frame must never take the app down: start the next one from a clean slate.
                Diagnostics.Add($"Renderer recovered from {e.GetType().Name}: {e.Message}");
                _bitmap?.Dispose();
                _bitmap = null;
                _bitmapGeneration = -1;
                Volatile.Write(ref _redraw, 1);
                Thread.Sleep(50);
            }
            long now = clock.ElapsedMilliseconds;
            if (now - windowStart >= 1000)
            {
                FramesPerSecond = frames * 1000.0 / (now - windowStart);
                frames = 0;
                windowStart = now;
            }
        }
    }

    private bool RenderFrame()
    {
        double viewW, viewH, rasterScale;
        bool viewChanged;
        ViewScaling scaling;
        Point pan;
        lock (_viewLock)
        {
            (viewW, viewH, rasterScale, scaling, pan) = (_viewWidth, _viewHeight, _rasterScale, _scaling, _pan);
            viewChanged = _viewChanged;
            _viewChanged = false;
        }
        if (viewW <= 1 || viewH <= 1) return false;

        int width = Math.Max(1, (int)Math.Round(viewW * rasterScale));
        int height = Math.Max(1, (int)Math.Round(viewH * rasterScale));
        if (width != _surfaceWidth || height != _surfaceHeight)
        {
            CanvasComposition.Resize(_surface, new Size(width, height));
            _surfaceWidth = width;
            _surfaceHeight = height;
            Volatile.Write(ref _redraw, 1);
        }
        if (viewChanged) Volatile.Write(ref _redraw, 1);

        var fb = _framebuffer;
        bool uploaded = fb is not null && Upload(fb);
        bool redraw = Interlocked.Exchange(ref _redraw, 0) != 0;
        if (!uploaded && !redraw) return false;

        // Drawn in physical pixels (the surface's own units).
        using (var ds = CanvasComposition.CreateDrawingSession(_surface))
        {
            ds.Clear(Background);
            if (_bitmap is not null && fb is not null)
            {
                var src = _bitmap.SizeInPixels;
                var place = Placement(viewW, viewH, rasterScale, (int)src.Width, (int)src.Height, scaling, pan);
                var dest = new Rect(place.X * rasterScale, place.Y * rasterScale, place.Width * rasterScale, place.Height * rasterScale);
                double physicalScale = dest.Width / src.Width;
                var interpolation = Math.Abs(physicalScale - 1) < 0.001 ? CanvasImageInterpolation.NearestNeighbor
                    : physicalScale < 1 ? CanvasImageInterpolation.HighQualityCubic
                    : CanvasImageInterpolation.Linear;
                ds.DrawImage(_bitmap, dest, new Rect(0, 0, src.Width, src.Height), 1.0f, interpolation);
            }
        }
        return true;
    }

    /// <summary>Copies changed regions from the framebuffer into the GPU bitmap. True if anything changed.</summary>
    private bool Upload(Framebuffer fb)
    {
        _dirty.Clear();
        int width, height;
        lock (fb.Sync)
        {
            width = fb.Width;
            height = fb.Height;
            // A new size, or a whole new framebuffer after a reconnect, needs a new GPU bitmap.
            if (_bitmap is null || !ReferenceEquals(fb, _bitmapSource) || fb.Generation != _bitmapGeneration)
            {
                _bitmap?.Dispose();
                _bitmap = CanvasBitmap.CreateFromBytes(_device, fb.Pixels, width, height,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Ignore);
                _bitmapGeneration = fb.Generation;
                _bitmapSource = fb;
                fb.TakeDirty(_dirty);
                return true;
            }
            if (!fb.TakeDirty(_dirty)) return false;

            // Pack each dirty rectangle while holding the lock (fast memcpy), upload after.
            int total = 0;
            foreach (var r in _dirty) total += r.Width * r.Height * 4;
            if (_staging.Length < total) _staging = GC.AllocateUninitializedArray<byte>(total);
            int at = 0;
            var pixels = fb.Pixels.AsSpan();
            foreach (var r in _dirty)
            {
                int rowBytes = r.Width * 4;
                for (int y = 0; y < r.Height; y++)
                {
                    pixels.Slice(((r.Y + y) * width + r.X) * 4, rowBytes).CopyTo(_staging.AsSpan(at));
                    at += rowBytes;
                }
            }
        }

        int offset = 0;
        foreach (var r in _dirty)
        {
            int bytes = r.Width * r.Height * 4;
            // A view over the staging array: no copy on the way to the GPU.
            _bitmap.SetPixelBytes(_staging.AsBuffer(offset, bytes), r.X, r.Y, r.Width, r.Height);
            offset += bytes;
        }
        return true;
    }

    private void RecreateDevice()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        _bitmapGeneration = -1;
        _bitmapSource = null;
        _device = CanvasDevice.GetSharedDevice();
        CanvasComposition.SetCanvasDevice(_graphics, _device);
        _framebuffer?.InvalidateAll();
        Volatile.Write(ref _redraw, 1);
    }

    public void Dispose()
    {
        _running = false;
        _wake.Set();
        if (_framebuffer is not null) _framebuffer.Changed -= OnFramebufferChanged;
        // A render thread still finishing a frame keeps its resources until it's done.
        if (!_thread.Join(1000)) return;
        _bitmap?.Dispose();
        _surface.Dispose();
        _graphics.Dispose();
        _wake.Dispose();
    }
}
