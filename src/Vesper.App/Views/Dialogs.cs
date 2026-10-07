using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Vesper.Views;

internal static class Dialogs
{
    public static async Task InfoAsync(XamlRoot root, string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = (root.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
        };
        await dialog.ShowAsync();
    }

    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string message, string confirm)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = confirm,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = (root.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
