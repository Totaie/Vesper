using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Vesper.Interop;
using WinRT.Interop;

namespace Vesper.Views;

/// <summary>Shared window setup: Mica, a custom title bar, the icon, and a sensible size.</summary>
internal static class WindowChrome
{
    public static void Prepare(Window window, UIElement titleBar, int width, int height, int minWidth, int minHeight)
    {
        window.ExtendsContentIntoTitleBar = true;
        window.SetTitleBar(titleBar);
        if (MicaController.IsSupported())
        {
            window.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        }
        else
        {
            window.SystemBackdrop = new DesktopAcrylicBackdrop();
        }

        var appWindow = window.AppWindow;
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "Vesper.ico");
        if (File.Exists(icon)) appWindow.SetIcon(icon);
        appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        double scale = Native.GetDpiForWindow(WindowNative.GetWindowHandle(window)) / 96.0;
        if (scale <= 0) scale = 1;
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(minWidth * scale);
            presenter.PreferredMinimumHeight = (int)(minHeight * scale);
        }
        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int w = Math.Min((int)(width * scale), area.Width - 40);
        int h = Math.Min((int)(height * scale), area.Height - 40);
        appWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
    }

    public static nint Handle(Window window) => WindowNative.GetWindowHandle(window);
}
