using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Vesper.Linux.Controls;

/// <summary>
/// A line icon drawn from a 16×16 path in the text colour around it. Linux has no Segoe Fluent
/// Icons, so Vesper draws its own (see <see cref="Icons"/>): crisp at any scale, and they follow
/// the theme like text does.
/// </summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(Size), 16);

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(StrokeThickness), 1.5);

    /// <summary>Filled instead of stroked (dots, solid glyphs).</summary>
    public static readonly StyledProperty<bool> IsFilledProperty =
        AvaloniaProperty.Register<Icon, bool>(nameof(IsFilled));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<Icon>();

    static Icon()
    {
        AffectsRender<Icon>(DataProperty, StrokeThicknessProperty, IsFilledProperty, ForegroundProperty);
        AffectsMeasure<Icon>(SizeProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<Icon>(false);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public bool IsFilled
    {
        get => GetValue(IsFilledProperty);
        set => SetValue(IsFilledProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (Data is not { } data || Foreground is not { } brush) return;
        double side = Math.Min(Bounds.Width, Bounds.Height);
        double scale = side / 16;
        var offset = new Point((Bounds.Width - side) / 2, (Bounds.Height - side) / 2);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y)))
        {
            if (IsFilled)
            {
                context.DrawGeometry(brush, null, data);
            }
            else
            {
                var pen = new Pen(brush, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                context.DrawGeometry(null, pen, data);
            }
        }
    }
}

/// <summary>Vesper's icons, on a 16×16 grid.</summary>
public static class Icons
{
    public static Geometry Plus { get; } = Parse("M8,3 L8,13 M3,8 L13,8");
    public static Geometry Settings { get; } = Parse(
        "M2,4.5 L5,4.5 M9,4.5 L14,4.5 M5,4.5 A2,2 0 1 0 9,4.5 A2,2 0 1 0 5,4.5 Z " +
        "M2,11.5 L8,11.5 M12,11.5 L14,11.5 M8,11.5 A2,2 0 1 0 12,11.5 A2,2 0 1 0 8,11.5 Z");
    public static Geometry More { get; } = Parse(
        "M2.4,8 A1.2,1.2 0 1 0 4.8,8 A1.2,1.2 0 1 0 2.4,8 Z M6.8,8 A1.2,1.2 0 1 0 9.2,8 A1.2,1.2 0 1 0 6.8,8 Z " +
        "M11.2,8 A1.2,1.2 0 1 0 13.6,8 A1.2,1.2 0 1 0 11.2,8 Z");
    public static Geometry Edit { get; } = Parse("M10.6,2.6 L13.4,5.4 L5.6,13.2 L2.5,13.5 L2.8,10.4 Z M9.2,4 L12,6.8");
    public static Geometry Key { get; } = Parse(
        "M2.5,10.5 A3,3 0 1 0 8.5,10.5 A3,3 0 1 0 2.5,10.5 Z M7.6,8.4 L13.5,2.5 M11.2,4.8 L12.9,6.5 M9.6,6.4 L11,7.8");
    public static Geometry Shield { get; } = Parse("M8,1.8 L13,3.7 L13,7.7 C13,10.8 10.9,13.1 8,14.2 C5.1,13.1 3,10.8 3,7.7 L3,3.7 Z");
    public static Geometry ShieldCheck { get; } = Parse(
        "M8,1.8 L13,3.7 L13,7.7 C13,10.8 10.9,13.1 8,14.2 C5.1,13.1 3,10.8 3,7.7 L3,3.7 Z M5.7,8.1 L7.3,9.7 L10.4,6.5");
    public static Geometry ShieldAlert { get; } = Parse(
        "M8,1.8 L13,3.7 L13,7.7 C13,10.8 10.9,13.1 8,14.2 C5.1,13.1 3,10.8 3,7.7 L3,3.7 Z M8,5.2 L8,8.4 M8,10.6 L8,10.7");
    public static Geometry Lock { get; } = Parse(
        "M4.5,7 L11.5,7 C12.3,7 12.8,7.5 12.8,8.3 L12.8,12.7 C12.8,13.5 12.3,14 11.5,14 L4.5,14 C3.7,14 3.2,13.5 3.2,12.7 " +
        "L3.2,8.3 C3.2,7.5 3.7,7 4.5,7 Z M5.3,7 L5.3,5 C5.3,3.3 6.5,2.1 8,2.1 C9.5,2.1 10.7,3.3 10.7,5 L10.7,7");
    public static Geometry Trash { get; } = Parse("M2.5,4.5 L13.5,4.5 M6,4.5 L6,2.6 L10,2.6 L10,4.5 M4,4.5 L4.7,13.5 L11.3,13.5 L12,4.5 M6.7,7 L6.7,11 M9.3,7 L9.3,11");
    public static Geometry ArrowRight { get; } = Parse("M3,8 L13,8 M9,4 L13,8 L9,12");
    public static Geometry Display { get; } = Parse("M3,2.5 L13,2.5 C13.8,2.5 14.3,3 14.3,3.8 L14.3,9.7 C14.3,10.5 13.8,11 13,11 L3,11 C2.2,11 1.7,10.5 1.7,9.7 L1.7,3.8 C1.7,3 2.2,2.5 3,2.5 Z M5.5,14 L10.5,14 M8,11 L8,14");
    public static Geometry Keyboard { get; } = Parse(
        "M2.8,3.8 L13.2,3.8 C14,3.8 14.5,4.3 14.5,5.1 L14.5,10.9 C14.5,11.7 14,12.2 13.2,12.2 L2.8,12.2 C2,12.2 1.5,11.7 1.5,10.9 " +
        "L1.5,5.1 C1.5,4.3 2,3.8 2.8,3.8 Z M4.2,6.5 L4.6,6.5 M7.2,6.5 L7.6,6.5 M10.2,6.5 L10.6,6.5 M5,9.5 L11,9.5");
    public static Geometry Clipboard { get; } = Parse(
        "M5.6,3.2 L4,3.2 C3.4,3.2 3,3.6 3,4.2 L3,13 C3,13.6 3.4,14 4,14 L12,14 C12.6,14 13,13.6 13,13 L13,4.2 C13,3.6 12.6,3.2 12,3.2 L10.4,3.2 " +
        "M6.2,2 L9.8,2 C10.2,2 10.4,2.2 10.4,2.6 L10.4,3.8 C10.4,4.2 10.2,4.4 9.8,4.4 L6.2,4.4 C5.8,4.4 5.6,4.2 5.6,3.8 L5.6,2.6 C5.6,2.2 5.8,2 6.2,2 Z " +
        "M5.5,8 L10.5,8 M5.5,10.8 L9,10.8");
    public static Geometry FullScreen { get; } = Parse("M2.5,6 L2.5,2.5 L6,2.5 M10,2.5 L13.5,2.5 L13.5,6 M13.5,10 L13.5,13.5 L10,13.5 M6,13.5 L2.5,13.5 L2.5,10");
    public static Geometry ExitFullScreen { get; } = Parse("M6,2.5 L6,6 L2.5,6 M13.5,6 L10,6 L10,2.5 M10,13.5 L10,10 L13.5,10 M2.5,10 L6,10 L6,13.5");
    public static Geometry Power { get; } = Parse("M8,2 L8,7.6 M4.9,4.3 A5,5 0 1 0 11.1,4.3");
    public static Geometry Check { get; } = Parse("M3,8.6 L6.4,12 L13,4.6");
    public static Geometry Close { get; } = Parse("M4,4 L12,12 M12,4 L4,12");
    public static Geometry Info { get; } = Parse("M2,8 A6,6 0 1 0 14,8 A6,6 0 1 0 2,8 Z M8,7.2 L8,11.2 M8,4.9 L8,5");
    public static Geometry Success { get; } = Parse("M2,8 A6,6 0 1 0 14,8 A6,6 0 1 0 2,8 Z M5.4,8.2 L7.2,10 L10.6,6.3");
    public static Geometry Warning { get; } = Parse("M8,2.2 L14.3,13.3 L1.7,13.3 Z M8,6.4 L8,9.6 M8,11.4 L8,11.5");
    public static Geometry Error { get; } = Parse("M2,8 A6,6 0 1 0 14,8 A6,6 0 1 0 2,8 Z M5.9,5.9 L10.1,10.1 M10.1,5.9 L5.9,10.1");
    public static Geometry Copy { get; } = Parse(
        "M5.5,5.5 L12.5,5.5 C13.1,5.5 13.5,5.9 13.5,6.5 L13.5,13 C13.5,13.6 13.1,14 12.5,14 L6.5,14 C5.9,14 5.5,13.6 5.5,13 Z " +
        "M10.5,5.5 L10.5,3 C10.5,2.4 10.1,2 9.5,2 L3.5,2 C2.9,2 2.5,2.4 2.5,3 L2.5,9.5 C2.5,10.1 2.9,10.5 3.5,10.5 L5.5,10.5");
    public static Geometry Moon { get; } = Parse("M13.2,9.6 A5.6,5.6 0 1 1 6.4,2.8 A4.6,4.6 0 0 0 13.2,9.6 Z");
    public static Geometry Chart { get; } = Parse("M2.5,13.5 L13.5,13.5 M4.5,11 L4.5,8 M8,11 L8,4.5 M11.5,11 L11.5,6.5");
    public static Geometry Mouse { get; } = Parse("M8,2 C10.5,2 12,3.8 12,6 L12,10 C12,12.2 10.5,14 8,14 C5.5,14 4,12.2 4,10 L4,6 C4,3.8 5.5,2 8,2 Z M8,4.4 L8,6.4");
    public static Geometry Refresh { get; } = Parse("M13,8 A5,5 0 1 1 11.4,4.3 M11.8,1.6 L11.8,4.6 L8.8,4.6");
    public static Geometry Star { get; } = Parse("M 8,0.5 Q 8.9,7.1 15.5,8 Q 8.9,8.9 8,15.5 Q 7.1,8.9 0.5,8 Q 7.1,7.1 8,0.5 Z");
    public static Geometry ChevronDown { get; } = Parse("M4.5,6.2 L8,9.7 L11.5,6.2");

    private static Geometry Parse(string data) => Geometry.Parse(data);
}
