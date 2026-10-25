using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;

namespace Vesper.FakeMac;

/// <summary>
/// A synthetic macOS-looking desktop, rendered with GDI+ at the Mac's native pixel density times
/// the viewer's requested server scale. It doubles as an input test bench: typed text, modifier
/// keys, clicks, scrolls and clipboard pastes all show up on screen. Without GDI+ (on Linux) it
/// paints a plainer scene straight into the frame: the same panels and changes, without text.
/// </summary>
public sealed class Desktop : IDisposable
{
    private readonly object _sync = new();
    private readonly int _pointsWidth, _pointsHeight;
    private readonly double _density;
    private readonly bool _animate;
    private Bitmap? _bitmap;
    private Graphics? _graphics;
    private double _scale = 1.0;

    // Scene state.
    private readonly StringBuilder _typed = new("Type here — every key Vesper sends lands in this window.\n");
    private readonly HashSet<string> _modifiers = [];
    private int _pointerX, _pointerY;
    private byte _buttons;
    private (int X, int Y)? _lastClick;
    private string _lastButton = "–";
    private double _scrollX, _scrollY;
    private string _clipboard = "(nothing yet)";
    private string _lastKey = "–";
    private string _status = "Waiting for a viewer";
    private int _frame;

    public Desktop(int pointsWidth, int pointsHeight, double density, bool animate)
    {
        _pointsWidth = pointsWidth;
        _pointsHeight = pointsHeight;
        _density = density;
        _animate = animate;
        (_bitmap, _graphics) = CreateSurface();
        Frame = new byte[Width * Height * 4];
        RenderAll();
    }

    public int PointsWidth => _pointsWidth;
    public int PointsHeight => _pointsHeight;
    public double Density => _density;
    public double Scale => _scale;
    public int Width => (int)Math.Round(_pointsWidth * _density * _scale);
    public int Height => (int)Math.Round(_pointsHeight * _density * _scale);
    public int NativeWidth => (int)Math.Round(_pointsWidth * _density);
    public int NativeHeight => (int)Math.Round(_pointsHeight * _density);

    /// <summary>The current frame, B,G,R,x. Read under <see cref="Sync"/>.</summary>
    public byte[] Frame { get; private set; }

    /// <summary>
    /// What a correct viewer shows, when the codec in use is lossy (Apple's adaptive codec), as
    /// the connection encoded it. Null while the picture is sent losslessly. Guarded by <see cref="Sync"/>.
    /// </summary>
    public byte[]? ViewerFrame { get; set; }

    public object Sync => _sync;

    /// <summary>Raised (outside the lock) with the changed rectangle, in framebuffer pixels.</summary>
    public event Action<Rectangle>? Changed;

    /// <summary>Raised when the framebuffer size changes (server scaling).</summary>
    public event Action? Resized;

    public void SetScale(double scale)
    {
        scale = Math.Clamp(scale, 0.1, 1.0);
        lock (_sync)
        {
            if (Math.Abs(scale - _scale) < 0.0001) return;
            _scale = scale;
            _graphics?.Dispose();
            _bitmap?.Dispose();
            (_bitmap, _graphics) = CreateSurface();
            Frame = new byte[Width * Height * 4];
            RenderAll();
        }
        Resized?.Invoke();
    }

    public void SetStatus(string status) => Update(() => _status = status, Panels.Info);

    public void Pointer(int nativeX, int nativeY, byte buttons)
    {
        Update(() =>
        {
            if (buttons != _buttons && buttons != 0)
            {
                _lastClick = (nativeX, nativeY);
                _lastButton = (buttons & 1) != 0 ? "left" : (buttons & 2) != 0 ? "right" : (buttons & 4) != 0 ? "middle" : $"mask {buttons:x2}";
            }
            _pointerX = nativeX;
            _pointerY = nativeY;
            _buttons = buttons;
        }, Panels.Info | (buttons != 0 ? Panels.Click : Panels.None));
    }

    public void Scroll(int dxPoints, int dyPoints) => Update(() => { _scrollX += dxPoints; _scrollY += dyPoints; }, Panels.Info);

    public void Clipboard(string text) => Update(() => _clipboard = text.Length > 120 ? text[..120] + "…" : text, Panels.Info);

    public string ClipboardText => _clipboard;

    public void Key(uint keysym, bool down)
    {
        Update(() =>
        {
            string? modifier = keysym switch
            {
                0xFFE1 or 0xFFE2 => "⇧ Shift",
                0xFFE3 or 0xFFE4 => "⌃ Control",
                0xFFE7 or 0xFFE8 => "⌥ Option",
                0xFFE9 or 0xFFEA or 0xFFEB or 0xFFEC => "⌘ Command",
                _ => null,
            };
            if (modifier is not null)
            {
                if (down) _modifiers.Add(modifier); else _modifiers.Remove(modifier);
                return;
            }
            if (!down) return;
            _lastKey = $"0x{keysym:X4}";
            if (_modifiers.Contains("⌘ Command") || _modifiers.Contains("⌃ Control"))
            {
                _lastKey += " (shortcut)";
                return;
            }
            switch (keysym)
            {
                case 0xFF08: if (_typed.Length > 0) _typed.Length--; break;
                case 0xFF0D or 0xFF8D: _typed.Append('\n'); break;
                case 0xFF09: _typed.Append("    "); break;
                case >= 0x20 and <= 0x7E or >= 0xA0 and <= 0xFF: _typed.Append((char)keysym); break;
                case >= 0x01000000: _typed.Append(char.ConvertFromUtf32((int)(keysym - 0x01000000))); break;
            }
            if (_typed.Length > 4000) _typed.Remove(0, _typed.Length - 4000);
        }, Panels.Editor | Panels.Info);
    }

    /// <summary>Advances clock and animation. Called ~30 times a second.</summary>
    public void Tick()
    {
        var panels = Panels.Clock;
        if (_animate) panels |= Panels.Animation;
        Update(() => _frame++, panels);
    }

    [Flags]
    private enum Panels { None = 0, Clock = 1, Editor = 2, Info = 4, Animation = 8, Click = 16 }

    // Layout, in points.
    private static readonly RectangleF MenuBar = new(0, 0, 10000, 28);
    private RectangleF EditorWindow => new(110, 96, Math.Min(940, _pointsWidth * 0.55f), Math.Min(600, _pointsHeight * 0.62f));
    private RectangleF InfoWindow => new(EditorWindow.Right + 40, 128, 480, 520);
    private RectangleF ClockArea => new(_pointsWidth - 230, 0, 230, 28);
    private RectangleF AnimationArea => new(InfoWindow.X + 24, InfoWindow.Bottom - 70, InfoWindow.Width - 48, 40);

    private void Update(Action change, Panels panels)
    {
        var dirty = new List<Rectangle>();
        lock (_sync)
        {
            change();
            if (panels.HasFlag(Panels.Clock)) dirty.Add(Redraw(ClockArea));
            if (panels.HasFlag(Panels.Editor)) dirty.Add(Redraw(EditorWindow));
            if (panels.HasFlag(Panels.Info)) dirty.Add(Redraw(InfoWindow));
            else if (panels.HasFlag(Panels.Animation)) dirty.Add(Redraw(AnimationArea));
            if (panels.HasFlag(Panels.Click) && _lastClick is var (cx, cy))
            {
                float px = (float)(cx / _density), py = (float)(cy / _density);
                dirty.Add(Redraw(new RectangleF(px - 30, py - 30, 60, 60)));
            }
        }
        foreach (var r in dirty) if (!r.IsEmpty) Changed?.Invoke(r);
    }

    private (Bitmap?, Graphics?) CreateSurface()
    {
        // VESPER_FAKEMAC_PLAIN=1 tries the plain painter on Windows too.
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("VESPER_FAKEMAC_PLAIN") == "1") return (null, null);
        var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppRgb);
        var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        return (bmp, g);
    }

    private void RenderAll() => Redraw(new RectangleF(0, 0, _pointsWidth, _pointsHeight));

    /// <summary>Redraws everything intersecting <paramref name="points"/>; returns the pixel rect changed.</summary>
    private Rectangle Redraw(RectangleF points)
    {
        double k = _density * _scale;
        var px = Rectangle.Intersect(
            Rectangle.FromLTRB((int)Math.Floor(points.Left * k) - 2, (int)Math.Floor(points.Top * k) - 2,
                               (int)Math.Ceiling(points.Right * k) + 2, (int)Math.Ceiling(points.Bottom * k) + 2),
            new Rectangle(0, 0, Width, Height));
        if (px.IsEmpty) return px;
        if (_graphics is not { } g || _bitmap is null)
        {
            PaintPlain(px, k);
            return px;
        }

        g.ResetTransform();
        g.SetClip(px);
        g.ScaleTransform((float)k, (float)k);
        DrawScene(g);
        g.ResetClip();

        var data = _bitmap.LockBits(px, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (int row = 0; row < px.Height; row++)
            {
                Marshal.Copy(data.Scan0 + row * data.Stride, Frame, ((px.Y + row) * Width + px.X) * 4, px.Width * 4);
            }
        }
        finally
        {
            _bitmap.UnlockBits(data);
        }
        return px;
    }

    /// <summary>
    /// The scene without GDI+: sky gradient, menu bar, the two windows, the clock, the animation
    /// and the last click, each changing colour with the state GDI+ would have written as text.
    /// </summary>
    private void PaintPlain(Rectangle px, double k)
    {
        Rectangle Scaled(RectangleF r) => Rectangle.FromLTRB((int)(r.Left * k), (int)(r.Top * k), (int)(r.Right * k), (int)(r.Bottom * k));
        var editor = Scaled(EditorWindow);
        var info = Scaled(InfoWindow);
        var clock = Scaled(ClockArea);
        var bar = Scaled(AnimationArea);
        int menu = (int)(MenuBar.Height * k);
        int typed = _typed.Length, second = DateTime.Now.Second;
        int infoShade = (_pointerX + _pointerY + (int)_scrollY + _clipboard.Length + _modifiers.Count * 7 + _lastKey.Length) & 63;
        int barX = bar.Left + (_frame * 7 % Math.Max(1, bar.Width - 40));
        var click = _lastClick is var (cx, cy)
            ? new Rectangle((int)(cx / _density * k) - 12, (int)(cy / _density * k) - 12, 24, 24)
            : Rectangle.Empty;
        for (int y = px.Top; y < px.Bottom; y++)
        {
            int t = Height > 1 ? y * 255 / (Height - 1) : 0;
            for (int x = px.Left; x < px.Right; x++)
            {
                (int r, int g, int b) = (14 + t * 226 / 255, 18 + t * 132 / 255, 52 + t * 44 / 255);
                if (y < menu) (r, g, b) = (232, 232, 238);
                if (clock.Contains(x, y)) (r, g, b) = (200, 200 + second, 210);
                if (editor.Contains(x, y)) (r, g, b) = (248, 248 - (typed & 31), 250 - ((x / 8 + typed) & 15));
                if (info.Contains(x, y)) (r, g, b) = (236 - infoShade, 238, 244);
                if (_animate && bar.Contains(x, y) && x >= barX && x < barX + 40) (r, g, b) = (240, 150, 96);
                if (click.Contains(x, y)) (r, g, b) = (255, 90, 90);
                int i = (y * Width + x) * 4;
                Frame[i] = (byte)b;
                Frame[i + 1] = (byte)g;
                Frame[i + 2] = (byte)r;
                Frame[i + 3] = 0;
            }
        }
    }

    private void DrawScene(Graphics g)
    {
        // Wallpaper: an evening sky.
        var full = new RectangleF(0, 0, _pointsWidth, _pointsHeight);
        using (var sky = new LinearGradientBrush(full, Color.FromArgb(20, 24, 66), Color.FromArgb(233, 122, 82), 90f))
        {
            var blend = new ColorBlend
            {
                Colors = [Color.FromArgb(14, 18, 52), Color.FromArgb(58, 44, 112), Color.FromArgb(176, 86, 120), Color.FromArgb(240, 150, 96)],
                Positions = [0f, 0.45f, 0.78f, 1f],
            };
            sky.InterpolationColors = blend;
            g.FillRectangle(sky, full);
        }
        using (var star = new SolidBrush(Color.FromArgb(255, 236, 190)))
        {
            g.FillEllipse(star, _pointsWidth * 0.82f, _pointsHeight * 0.18f, 7, 7);
        }

        // Menu bar.
        using (var bar = new SolidBrush(Color.FromArgb(150, 245, 245, 250)))
        {
            g.FillRectangle(bar, 0, 0, _pointsWidth, MenuBar.Height);
        }
        using var menuFont = new Font("Segoe UI Semibold", 12.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var menuFontRegular = new Font("Segoe UI", 12.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Color.FromArgb(30, 30, 36));
        g.DrawString("●   FakeMac", menuFont, ink, 16, 6);
        float mx = 130;
        foreach (string item in new[] { "File", "Edit", "View", "Go", "Window", "Help" })
        {
            g.DrawString(item, menuFontRegular, ink, mx, 6);
            mx += g.MeasureString(item, menuFontRegular).Width + 14;
        }
        g.DrawString(DateTime.Now.ToString("ddd d MMM  HH:mm:ss"), menuFontRegular, ink, _pointsWidth - 190, 6);

        DrawWindow(g, EditorWindow, "Untitled — TextEdit", DrawEditor);
        DrawWindow(g, InfoWindow, "What the Mac received", DrawInfo);
        DrawDock(g);

        if (_lastClick is var (cx, cy))
        {
            float x = (float)(cx / _density), y = (float)(cy / _density);
            using var ring = new Pen(Color.FromArgb(220, 255, 214, 102), 2.5f);
            g.DrawEllipse(ring, x - 14, y - 14, 28, 28);
        }
    }

    private static void DrawWindow(Graphics g, RectangleF r, string title, Action<Graphics, RectangleF> content)
    {
        using (var shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
        using (var shadowPath = RoundedRect(new RectangleF(r.X + 2, r.Y + 8, r.Width, r.Height), 12))
        {
            g.FillPath(shadow, shadowPath);
        }
        using (var body = new SolidBrush(Color.FromArgb(252, 252, 253)))
        using (var path = RoundedRect(r, 12))
        {
            g.FillPath(body, path);
        }
        using (var titleBar = new SolidBrush(Color.FromArgb(236, 236, 240)))
        using (var titlePath = RoundedRect(new RectangleF(r.X, r.Y, r.Width, 34), 12))
        {
            g.FillPath(titleBar, titlePath);
            g.FillRectangle(titleBar, r.X, r.Y + 20, r.Width, 14);
        }
        Color[] lights = [Color.FromArgb(255, 95, 87), Color.FromArgb(254, 188, 46), Color.FromArgb(40, 200, 64)];
        for (int i = 0; i < 3; i++)
        {
            using var b = new SolidBrush(lights[i]);
            g.FillEllipse(b, r.X + 14 + i * 20, r.Y + 11, 12, 12);
        }
        using var font = new Font("Segoe UI Semibold", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Color.FromArgb(60, 60, 68));
        var size = g.MeasureString(title, font);
        g.DrawString(title, font, ink, r.X + (r.Width - size.Width) / 2, r.Y + 9);
        content(g, new RectangleF(r.X + 20, r.Y + 48, r.Width - 40, r.Height - 64));
    }

    private void DrawEditor(Graphics g, RectangleF area)
    {
        using var font = new Font("Cascadia Mono", 15f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Color.FromArgb(28, 28, 32));
        string text = _typed.ToString() + (_frame / 15 % 2 == 0 ? "▏" : " ");
        g.DrawString(text, font, ink, area);
    }

    private void DrawInfo(Graphics g, RectangleF area)
    {
        using var label = new Font("Segoe UI", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var value = new Font("Segoe UI Semibold", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var dim = new SolidBrush(Color.FromArgb(120, 120, 130));
        using var ink = new SolidBrush(Color.FromArgb(28, 28, 32));
        (string, string)[] rows =
        [
            ("Viewer", _status),
            ("Pointer (native px)", $"{_pointerX}, {_pointerY}"),
            ("Buttons held", _buttons == 0 ? "none" : $"0x{_buttons:X2}"),
            ("Last click", _lastClick is var (clickX, clickY) ? $"{_lastButton} at {clickX}, {clickY}" : "–"),
            ("Modifiers held", _modifiers.Count == 0 ? "none" : string.Join("  ", _modifiers.Order())),
            ("Last key", _lastKey),
            ("Scrolled (points)", $"x {_scrollX:+0;-0;0}   y {_scrollY:+0;-0;0}"),
            ("Clipboard from viewer", _clipboard.ReplaceLineEndings(" ⏎ ")),
            ("Framebuffer", $"{Width}×{Height} (scale {_scale:0.##}, {_density:0.#}x display)"),
        ];
        float y = area.Y;
        foreach (var (k, v) in rows)
        {
            g.DrawString(k, label, dim, area.X, y);
            g.DrawString(v, value, ink, new RectangleF(area.X + 170, y, area.Width - 170, 40));
            y += 38;
        }
        // A moving bar, so continuous updates are visible.
        var track = AnimationArea;
        using var trackBrush = new SolidBrush(Color.FromArgb(232, 232, 238));
        using var trackPath = RoundedRect(track, 8);
        g.FillPath(trackBrush, trackPath);
        if (_animate)
        {
            float t = (float)(Math.Sin(_frame / 20.0) * 0.5 + 0.5);
            var pill = new RectangleF(track.X + 4 + t * (track.Width - 128), track.Y + 4, 120, track.Height - 8);
            using var pillBrush = new LinearGradientBrush(pill, Color.FromArgb(255, 186, 92), Color.FromArgb(255, 120, 120), 0f);
            using var pillPath = RoundedRect(pill, 6);
            g.FillPath(pillBrush, pillPath);
        }
        else
        {
            g.DrawString("Animation off (start with --animate)", label, dim, track.X + 12, track.Y + 11);
        }
    }

    private void DrawDock(Graphics g)
    {
        Color[] icons = [Color.FromArgb(34, 120, 255), Color.FromArgb(255, 149, 0), Color.FromArgb(52, 199, 89),
            Color.FromArgb(255, 59, 48), Color.FromArgb(175, 82, 222), Color.FromArgb(90, 200, 250),
            Color.FromArgb(255, 204, 0), Color.FromArgb(142, 142, 147), Color.FromArgb(255, 45, 85)];
        float size = 54, gap = 12;
        float width = icons.Length * size + (icons.Length + 1) * gap;
        var dock = new RectangleF((_pointsWidth - width) / 2, _pointsHeight - size - 22, width, size + 16);
        using (var glass = new SolidBrush(Color.FromArgb(110, 250, 250, 255)))
        using (var path = RoundedRect(dock, 22))
        {
            g.FillPath(glass, path);
        }
        for (int i = 0; i < icons.Length; i++)
        {
            var r = new RectangleF(dock.X + gap + i * (size + gap), dock.Y + 8, size, size);
            using var b = new LinearGradientBrush(r, ControlPaint.Light(icons[i]), icons[i], 90f);
            using var p = RoundedRect(r, 13);
            g.FillPath(b, p);
        }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        _graphics?.Dispose();
        _bitmap?.Dispose();
    }

    private static class ControlPaint
    {
        public static Color Light(Color c) => Color.FromArgb(Math.Min(255, c.R + 60), Math.Min(255, c.G + 60), Math.Min(255, c.B + 60));
    }
}
