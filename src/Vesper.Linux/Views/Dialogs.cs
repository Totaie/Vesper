using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace Vesper.Linux.Views;

internal enum DialogResult { None, Primary, Secondary }

internal enum DialogButton { None, Primary, Secondary, Close }

/// <summary>A window that can show <see cref="Dialog"/>s over its content.</summary>
internal interface IDialogHost
{
    DialogLayer DialogLayer { get; }
}

/// <summary>The top layer of a window, where its dialogs open; newest on top.</summary>
internal sealed class DialogLayer : Panel
{
    private readonly List<Dialog> _open = [];

    public DialogLayer() => IsVisible = false;

    public bool HasOpenDialog => _open.Count > 0;

    internal void Add(Dialog dialog, Control visual)
    {
        _open.Add(dialog);
        Children.Add(visual);
        IsVisible = true;
    }

    internal void Remove(Dialog dialog, Control visual)
    {
        _open.Remove(dialog);
        Children.Remove(visual);
        IsVisible = Children.Count > 0;
    }

    /// <summary>Closes every open dialog with the safe answer (as Cancel would), e.g. when the window closes.</summary>
    public void CloseAll()
    {
        foreach (var dialog in _open.ToArray()) dialog.Close(DialogResult.None);
    }
}

/// <summary>
/// An in-window dialog in the manner of WinUI's ContentDialog: a title, content, and up to three
/// buttons in a footer, over a dimmed window. Escape is Cancel; Enter presses the default button.
/// </summary>
internal sealed class Dialog
{
    private Button? _primary, _secondary;
    private bool _primaryEnabled = true, _secondaryEnabled = true;
    private TaskCompletionSource<DialogResult>? _done;
    private DialogLayer? _layer;
    private Control? _visual;
    private IInputElement? _previousFocus;
    private Window? _window;

    public string Title { get; init; } = "";
    public object? Content { get; init; }
    public string? PrimaryButtonText { get; init; }
    public string? SecondaryButtonText { get; init; }
    public string? CloseButtonText { get; init; } = "Cancel";
    public DialogButton DefaultButton { get; init; } = DialogButton.Primary;

    /// <summary>The content's width; the dialog adds its own padding.</summary>
    public double ContentWidth { get; init; } = 440;

    /// <summary>What gets focus when the dialog opens (else the first text box, else the default button).</summary>
    public Control? InitialFocus { get; init; }

    /// <summary>Raised as a button closes the dialog, before the result is returned.</summary>
    public event Action<DialogResult>? Closing;

    public bool IsPrimaryButtonEnabled
    {
        get => _primaryEnabled;
        set
        {
            _primaryEnabled = value;
            if (_primary is not null) _primary.IsEnabled = value;
        }
    }

    public bool IsSecondaryButtonEnabled
    {
        get => _secondaryEnabled;
        set
        {
            _secondaryEnabled = value;
            if (_secondary is not null) _secondary.IsEnabled = value;
        }
    }

    public Task<DialogResult> ShowAsync(Visual anywhereInWindow)
    {
        if (_done is not null) throw new InvalidOperationException("This dialog is already showing.");
        _window = TopLevel.GetTopLevel(anywhereInWindow) as Window;
        if (_window is not IDialogHost host) throw new InvalidOperationException("Dialogs open only in Vesper's own windows.");
        _layer = host.DialogLayer;
        _done = new TaskCompletionSource<DialogResult>();
        _previousFocus = _window.FocusManager?.GetFocusedElement();

        var title = new TextBlock
        {
            Text = Title,
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24, 22, 24, 0),
        };
        var body = Content as Control ?? new TextBlock { Text = Content?.ToString(), TextWrapping = TextWrapping.Wrap };
        var scroller = new ScrollViewer
        {
            Content = body,
            Padding = new Thickness(24, 14, 24, 24),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var buttons = new Grid { ColumnSpacing = 8 };
        Button? defaultButton = null;
        void AddButton(string text, DialogResult result, bool enabled, bool isDefault, ref Button? field)
        {
            var button = new Button
            {
                Content = text,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                IsEnabled = enabled,
                Padding = new Thickness(12, 6),
            };
            if (isDefault)
            {
                button.Classes.Add("accent");
                defaultButton = button;
            }
            button.Click += (_, _) => Close(result);
            Grid.SetColumn(button, buttons.ColumnDefinitions.Count);
            buttons.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            buttons.Children.Add(button);
            field = button;
        }
        Button? closeField = null;
        if (PrimaryButtonText is { Length: > 0 }) AddButton(PrimaryButtonText, DialogResult.Primary, _primaryEnabled, DefaultButton == DialogButton.Primary, ref _primary);
        if (SecondaryButtonText is { Length: > 0 }) AddButton(SecondaryButtonText, DialogResult.Secondary, _secondaryEnabled, DefaultButton == DialogButton.Secondary, ref _secondary);
        if (CloseButtonText is { Length: > 0 }) AddButton(CloseButtonText, DialogResult.None, true, DefaultButton == DialogButton.Close, ref closeField);

        var footer = new Border { Classes = { "dialog-footer" }, Child = buttons };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        layout.Children.Add(title);
        Grid.SetRow(scroller, 1);
        layout.Children.Add(scroller);
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);

        var card = new Border
        {
            Classes = { "dialog-card" },
            Width = ContentWidth + 48,
            Margin = new Thickness(24),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = layout,
            RenderTransform = TransformOperations.Parse("scale(1.03)"),
            Transitions = [new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(160), Easing = new CubicEaseOut() }],
        };
        KeyboardNavigation.SetTabNavigation(card, KeyboardNavigationMode.Cycle);

        var smoke = new Border
        {
            Classes = { "dialog-smoke" },
            Child = card,
            Opacity = 0,
            Transitions = [new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(140) }],
        };
        smoke.KeyDown += (_, e) =>
        {
            if (e.Handled) return;
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close(DialogResult.None);
            }
            else if (e.Key == Key.Enter && e.Source is not Button && e.Source is not TextBox { AcceptsReturn: true }
                     && defaultButton is { IsEffectivelyEnabled: true })
            {
                e.Handled = true;
                Close(defaultButton == _primary ? DialogResult.Primary : defaultButton == _secondary ? DialogResult.Secondary : DialogResult.None);
            }
        };
        // Clicks on the dimmed window do nothing (as with ContentDialog); keys stay in the dialog.
        smoke.PointerPressed += (_, e) => e.Handled = true;

        _visual = smoke;
        _layer.Add(this, smoke);
        var focus = InitialFocus ?? FirstTextBox(body) ?? (Control?)defaultButton ?? closeField;
        Dispatcher.UIThread.Post(() =>
        {
            smoke.Opacity = 1;
            card.RenderTransform = TransformOperations.Parse("scale(1)");
            focus?.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
        return _done.Task;
    }

    /// <summary>Closes the dialog as if this button were pressed.</summary>
    public void Close(DialogResult result)
    {
        if (_done is null || _done.Task.IsCompleted) return;
        try
        {
            Closing?.Invoke(result);
        }
        finally
        {
            if (_layer is not null && _visual is not null) _layer.Remove(this, _visual);
            if (_previousFocus is Control { IsEffectivelyEnabled: true } previous && TopLevel.GetTopLevel(previous) is not null && _layer?.HasOpenDialog != true)
            {
                previous.Focus();
            }
            _done.TrySetResult(result);
        }
    }

    private static Control? FirstTextBox(Control root)
    {
        if (root is TextBox { IsEffectivelyEnabled: true, IsReadOnly: false } box) return box;
        if (root is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child.IsVisible && FirstTextBox(child) is { } found) return found;
            }
        }
        if (root is Decorator { Child: { } decorated }) return FirstTextBox(decorated);
        if (root is ContentControl { Content: Control content }) return FirstTextBox(content);
        return null;
    }
}

internal static class Dialogs
{
    public static Task InfoAsync(Visual anchor, string title, string message) =>
        new Dialog
        {
            Title = title,
            Content = Text(message),
            CloseButtonText = "OK",
            DefaultButton = DialogButton.Close,
        }.ShowAsync(anchor);

    public static async Task<bool> ConfirmAsync(Visual anchor, string title, string message, string confirm) =>
        await new Dialog
        {
            Title = title,
            Content = Text(message),
            PrimaryButtonText = confirm,
            CloseButtonText = "Cancel",
            DefaultButton = DialogButton.Close,
        }.ShowAsync(anchor) == DialogResult.Primary;

    private static TextBlock Text(string message) => new() { Text = message, TextWrapping = TextWrapping.Wrap };
}
