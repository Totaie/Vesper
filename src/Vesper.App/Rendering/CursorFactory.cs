using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Vesper.Core.Rfb;
using Vesper.Interop;
using WinRT;

namespace Vesper.Rendering;

/// <summary>
/// Turns the Mac's pointer images into real Windows hardware cursors, so the pointer moves at the
/// mouse's own rate with no network round trip. Uses WinAppSDK's IInputCursorStaticsInterop to
/// wrap an HCURSOR built from the image.
/// </summary>
internal sealed unsafe class CursorFactory : IDisposable
{
    private static readonly Guid InteropIid = new("ac6f5065-90c4-46ce-beb7-05e138e54117");
    private readonly Dictionary<(CursorShape Shape, int Size), (InputCursor Cursor, nint Handle)> _cache = [];
    private InputCursor? _hidden;
    private nint _hiddenHandle;

    public InputCursor Arrow { get; } = InputSystemCursor.Create(InputSystemCursorShape.Arrow);

    public InputCursor Hidden
    {
        get
        {
            if (_hidden is null)
            {
                _hiddenHandle = CreateHCursor(new byte[4], 1, 1, 0, 0);
                _hidden = Wrap(_hiddenHandle);
            }
            return _hidden;
        }
    }

    /// <summary>The cursor for a Mac pointer image, scaled so one Mac point is <paramref name="pixelsPerPoint"/> screen pixels.</summary>
    public InputCursor For(CursorShape shape, double pixelsPerPoint)
    {
        if (shape.IsSystemDefault) return Arrow;
        if (shape.IsHidden) return Hidden;
        double scale = Math.Clamp(pixelsPerPoint, 0.5, 4.0);
        int width = Math.Max(1, (int)Math.Round(shape.Width * scale));
        if (_cache.TryGetValue((shape, width), out var hit)) return hit.Cursor;

        int height = Math.Max(1, (int)Math.Round(shape.Height * scale));
        byte[] pixels = width == shape.Width && height == shape.Height ? shape.Bgra : Resample(shape, width, height);
        nint handle = CreateHCursor(pixels, width, height,
            (int)Math.Round(shape.HotspotX * scale), (int)Math.Round(shape.HotspotY * scale));
        var cursor = Wrap(handle);
        if (_cache.Count > 64) Clear();
        _cache[(shape, width)] = (cursor, handle);
        return cursor;
    }

    private static InputCursor Wrap(nint hcursor)
    {
        using var factory = ActivationFactory.Get("Microsoft.UI.Input.InputCursor", InteropIid);
        nint thisPtr = factory.ThisPtr;
        // IInputCursorStaticsInterop: IUnknown (3) + IInspectable (3), then CreateFromHCursor.
        var createFromHCursor = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)(*(nint**)thisPtr)[6];
        nint abi;
        Marshal.ThrowExceptionForHR(createFromHCursor(thisPtr, hcursor, &abi));
        try
        {
            return InputCursor.FromAbi(abi);
        }
        finally
        {
            Marshal.Release(abi);
        }
    }

    /// <summary>A 32-bit alpha cursor from straight-alpha B,G,R,A pixels.</summary>
    private static nint CreateHCursor(byte[] bgra, int width, int height, int hotX, int hotY)
    {
        var header = new Native.BitmapV5Header
        {
            Size = sizeof(Native.BitmapV5Header),
            Width = width,
            Height = -height, // top-down
            Planes = 1,
            BitCount = 32,
            Compression = 3, // BI_BITFIELDS
            RedMask = 0x00FF0000,
            GreenMask = 0x0000FF00,
            BlueMask = 0x000000FF,
            AlphaMask = 0xFF000000,
        };
        void* bits;
        nint color = Native.CreateDIBSection(0, &header, 0, &bits, 0, 0);
        if (color == 0) throw new InvalidOperationException("Couldn't create the pointer bitmap.");
        nint mask = Native.CreateBitmap(width, height, 1, 1, null);
        try
        {
            new ReadOnlySpan<byte>(bgra, 0, width * height * 4).CopyTo(new Span<byte>(bits, width * height * 4));
            var info = new Native.IconInfo
            {
                IsIcon = 0,
                HotspotX = Math.Clamp(hotX, 0, width - 1),
                HotspotY = Math.Clamp(hotY, 0, height - 1),
                MaskBitmap = mask,
                ColorBitmap = color,
            };
            nint cursor = Native.CreateIconIndirect(&info);
            if (cursor == 0) throw new InvalidOperationException("Couldn't create the pointer.");
            return cursor;
        }
        finally
        {
            Native.DeleteObject(color);
            Native.DeleteObject(mask);
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

    private void Clear()
    {
        foreach (var (cursor, handle) in _cache.Values)
        {
            cursor.Dispose();
            Native.DestroyCursor(handle);
        }
        _cache.Clear();
    }

    public void Dispose()
    {
        Clear();
        _hidden?.Dispose();
        if (_hiddenHandle != 0) Native.DestroyCursor(_hiddenHandle);
    }
}
