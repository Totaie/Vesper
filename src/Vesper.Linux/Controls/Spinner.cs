using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Vesper.Linux.Controls;

/// <summary>
/// A small indeterminate progress ring (Avalonia has no ProgressRing): a three-quarter arc that
/// Styles.axaml spins. Style animations stop while a control is hidden, so it costs nothing then.
/// </summary>
public sealed class Spinner : Control
{
    public static readonly StyledProperty<IBrush?> BrushProperty =
        AvaloniaProperty.Register<Spinner, IBrush?>(nameof(Brush));

    static Spinner() => AffectsRender<Spinner>(BrushProperty);

    /// <summary>Each spinner turns its own transform (the style animates its angle).</summary>
    public Spinner() => RenderTransform = new RotateTransform();

    public IBrush? Brush
    {
        get => GetValue(BrushProperty);
        set => SetValue(BrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Brush is not { } brush) return;
        double side = Math.Min(Bounds.Width, Bounds.Height);
        double thickness = Math.Max(1.5, side / 8);
        double r = (side - thickness) / 2;
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        const double sweep = 1.5 * Math.PI;
        var from = new Point(c.X + r, c.Y);
        var to = new Point(c.X + r * Math.Cos(sweep), c.Y + r * Math.Sin(sweep));
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(from, false);
            g.ArcTo(to, new Size(r, r), 0, isLargeArc: true, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(brush, thickness, lineCap: PenLineCap.Round), geometry);
    }
}
