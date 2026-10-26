using Avalonia;
using Avalonia.Controls;

namespace Vesper.Linux.Controls;

/// <summary>
/// Lays cards out in as many equal columns as fit at <see cref="MinItemWidth"/>, stretched to fill
/// the row (WinUI's UniformGridLayout with ItemsStretch="Fill").
/// </summary>
public sealed class CardGrid : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<CardGrid, double>(nameof(MinItemWidth), 330);

    public static readonly StyledProperty<double> ItemHeightProperty =
        AvaloniaProperty.Register<CardGrid, double>(nameof(ItemHeight), 318);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<CardGrid, double>(nameof(Spacing), 18);

    static CardGrid() => AffectsMeasure<CardGrid>(MinItemWidthProperty, ItemHeightProperty, SpacingProperty);

    public double MinItemWidth
    {
        get => GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private (int Columns, double ItemWidth) Columns(double width)
    {
        if (double.IsInfinity(width) || width <= 0) return (1, MinItemWidth);
        int columns = Math.Max(1, (int)((width + Spacing) / (MinItemWidth + Spacing)));
        return (columns, (width - (columns - 1) * Spacing) / columns);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (columns, itemWidth) = Columns(availableSize.Width);
        int count = Children.Count(c => c.IsVisible);
        foreach (var child in Children) child.Measure(new Size(itemWidth, ItemHeight));
        int rows = (count + columns - 1) / columns;
        double width = double.IsInfinity(availableSize.Width) ? columns * itemWidth + (columns - 1) * Spacing : availableSize.Width;
        return new Size(width, rows == 0 ? 0 : rows * ItemHeight + (rows - 1) * Spacing);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (columns, itemWidth) = Columns(finalSize.Width);
        int i = 0;
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            int row = i / columns, column = i % columns;
            child.Arrange(new Rect(column * (itemWidth + Spacing), row * (ItemHeight + Spacing), itemWidth, ItemHeight));
            i++;
        }
        return finalSize;
    }
}
