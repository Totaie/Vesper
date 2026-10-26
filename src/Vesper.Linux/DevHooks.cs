#if DEBUG
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vesper.Linux.Views;
using Vesper.Services;

namespace Vesper.Linux;

/// <summary>
/// Development builds only (none of this exists in Release): lets a developer look at screens
/// without clicking through them, against a dev data folder (VESPER_DEV_DATA, required).
/// <list type="bullet">
/// <item>VESPER_DEV_AUTOCONNECT=&lt;Mac name&gt;[;&lt;Mac name&gt;…] opens those Macs' sessions at
/// startup, with the password from VESPER_DEV_PASSWORD if given (otherwise the usual prompt).</item>
/// <item>VESPER_DEV_SHOW=add | editor | editor-open | settings | confirm opens that screen at
/// startup; fullscreen | notice | menu | security act on the first session once it's connected.</item>
/// </list>
/// </summary>
internal static class DevHooks
{
    public static void Start(App app)
    {
        if (Environment.GetEnvironmentVariable("VESPER_DEV_DATA") is not { Length: > 0 }) return; // never against real data
        string? connect = Environment.GetEnvironmentVariable("VESPER_DEV_AUTOCONNECT");
        string? show = Environment.GetEnvironmentVariable("VESPER_DEV_SHOW");
        if (connect is null && show is null) return;
        DispatcherTimer.RunOnce(async () =>
        {
            var main = app.Main;
            foreach (string name in (connect ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                if (app.Data.Profiles.FirstOrDefault(p => MacNames.DisplayName(p) == name) is { } profile)
                {
                    SessionWindow.Open(profile, Environment.GetEnvironmentVariable("VESPER_DEV_PASSWORD"));
                }
            }
            if (main is null) return;
            switch (show)
            {
                case "add":
                    await main.EditAsync(null);
                    break;
                case "editor" when app.Data.Profiles.FirstOrDefault() is { } first:
                    await main.EditAsync(first);
                    break;
                case "editor-open" when app.Data.Profiles.FirstOrDefault() is { } first:
                    DispatcherTimer.RunOnce(() =>
                    {
                        foreach (var expander in main.GetVisualDescendants().OfType<Expander>()) expander.IsExpanded = true;
                    }, TimeSpan.FromMilliseconds(400));
                    await main.EditAsync(first);
                    break;
                case "settings":
                    SettingsWindow.ShowSingle();
                    break;
                case "settings-about":
                    var settings = SettingsWindow.ShowSingle();
                    DispatcherTimer.RunOnce(() =>
                    {
                        foreach (var expander in settings.GetVisualDescendants().OfType<Expander>()) expander.IsExpanded = true;
                        DispatcherTimer.RunOnce(() => settings.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.ScrollToEnd(),
                            TimeSpan.FromMilliseconds(300));
                    }, TimeSpan.FromMilliseconds(500));
                    break;
                case "confirm":
                    await Dialogs.ConfirmAsync(main, "Forget this Mac's identity?",
                        "Vesper will ask you to verify the Mac's identity key (and SSH host key) again on the next connection.", "Forget identity");
                    break;
                case "fullscreen" or "notice" or "menu" or "security" or "typing" or "fullscreen-keys":
                    DispatcherTimer.RunOnce(() => app.Sessions.FirstOrDefault()?.DevShow(show), TimeSpan.FromSeconds(4));
                    break;
            }
        }, TimeSpan.FromMilliseconds(600));
    }
}
#endif
