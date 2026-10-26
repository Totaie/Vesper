using Avalonia;
using Avalonia.Controls;
using Vesper.Linux.Services;

namespace Vesper.Linux;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Before anything else: no core dumps, no debugger attach, no .NET diagnostics endpoint.
        Hardening.Apply(args);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnLastWindowClose);
    }

    /// <summary>Also what the XAML previewer uses.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new X11PlatformOptions
            {
                WmClass = "vesper",
                // The X session manager would be told how to restart Vesper; it has nothing to restore.
                EnableSessionManagement = false,
            });
}
