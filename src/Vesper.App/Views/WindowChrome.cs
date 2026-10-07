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

/// <summary>Friendly names for Mac model identifiers (hw.model).</summary>
internal static class MacModels
{
    public static string Describe(string? model)
    {
        if (string.IsNullOrEmpty(model)) return "Mac";
        string m = model;
        if (m.StartsWith("MacBookPro", StringComparison.Ordinal)) return "MacBook Pro";
        if (m.StartsWith("MacBookAir", StringComparison.Ordinal)) return "MacBook Air";
        if (m.StartsWith("Macmini", StringComparison.Ordinal)) return "Mac mini";
        if (m.StartsWith("MacPro", StringComparison.Ordinal)) return "Mac Pro";
        if (m.StartsWith("iMac", StringComparison.Ordinal)) return "iMac";
        return m switch
        {
            "Mac13,1" or "Mac13,2" or "Mac14,13" or "Mac14,14" or "Mac15,14" or "Mac16,9" => "Mac Studio",
            "Mac14,3" or "Mac14,12" or "Mac16,10" or "Mac16,11" => "Mac mini",
            "Mac14,8" => "Mac Pro",
            "Mac15,4" or "Mac15,5" or "Mac16,2" or "Mac16,3" => "iMac",
            "Mac14,2" or "Mac14,15" or "Mac15,12" or "Mac15,13" or "Mac16,12" or "Mac16,13" => "MacBook Air",
            "Mac14,5" or "Mac14,6" or "Mac14,9" or "Mac14,10" or "Mac14,7" or "Mac15,3" or "Mac15,6" or "Mac15,7" or "Mac15,8"
                or "Mac15,9" or "Mac15,10" or "Mac15,11" or "Mac16,1" or "Mac16,5" or "Mac16,6" or "Mac16,7" or "Mac16,8" => "MacBook Pro",
            _ => "Mac",
        };
    }
}
