using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Vesper.Linux.Controls;

/// <summary>A numbered step of the first-run instructions.</summary>
public sealed class SetupStep : Grid
{
    public static readonly StyledProperty<string?> NumberProperty =
        AvaloniaProperty.Register<SetupStep, string?>(nameof(Number));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<SetupStep, string?>(nameof(Text));

    private readonly TextBlock _number = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        FontSize = 12,
        FontWeight = FontWeight.SemiBold,
    };

    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

    public SetupStep()
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*");
        ColumnSpacing = 12;
        var circle = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            VerticalAlignment = VerticalAlignment.Top,
            Child = _number,
            Classes = { "step-number" },
        };
        Children.Add(circle);
        SetColumn(_text, 1);
        Children.Add(_text);
    }

    public string? Number
    {
        get => GetValue(NumberProperty);
        set => SetValue(NumberProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == NumberProperty) _number.Text = Number;
        else if (change.Property == TextProperty) _text.Text = Text;
    }
}
