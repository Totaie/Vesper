using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Vesper.Linux.Services;
using Vesper.Linux.Views;
using Vesper.Models;
using Vesper.Security;
using Vesper.Services;
using Vesper.Updates;

namespace Vesper.Linux;

public sealed partial class App : Application
{
    private MainWindow? _main;
    private DispatcherTimer? _lockTimer;
    private DateTime _lastSessionActivity = DateTime.Now;
    private bool _showingProblem;

    public static new App Current => (App)Application.Current!;

    internal AppData Data { get; private set; } = null!;
    internal SecretVault Vault { get; private set; } = null!;
    internal ConnectionService Connections { get; private set; } = null!;
    internal UpdateService Updates { get; private set; } = null!;
    internal List<SessionWindow> Sessions { get; } = [];

    /// <summary>Something found at startup that the user needs to hear about, shown by the home window once.</summary>
    internal string? StartupWarning { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Nothing is written to disk about a failure: it goes to the in-memory diagnostics and is
        // shown in the app.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Diagnostics.Add("Unhandled: " + e.Exception.GetType().Name + ": " + e.Exception.Message);
            e.Handled = true;
            ShowProblem(e.Exception.Message);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Diagnostics.Add("Unobserved: " + e.Exception.GetBaseException().GetType().Name + ": " + e.Exception.GetBaseException().Message);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Diagnostics.Add("Fatal: " + (e.ExceptionObject as Exception)?.GetType().Name + ": " + (e.ExceptionObject as Exception)?.Message);

        Hardening.Report();
        Data = new AppData();
        ApplyTheme();
        Vault = new SecretVault(Data.VaultPath, IVaultKeyProtector.ForThisComputer());
        CheckVault();
        Connections = new ConnectionService(Data, Vault);
        if (!Vault.CanSave) Diagnostics.Add("Saved passwords are off: " + Vault.UnavailableReason);
        Updates = new UpdateService(Data, typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0));
        Updates.Start();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _main = new MainWindow();
            desktop.MainWindow = _main;
        }

        // Lock the vault after a quiet spell with no session open.
        _lockTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _lockTimer.Tick += (_, _) =>
        {
            if (Sessions.Count > 0) _lastSessionActivity = DateTime.Now;
            else if (Vault.IsUnlocked && DateTime.Now - _lastSessionActivity > TimeSpan.FromMinutes(Data.Settings.LockAfterMinutes))
            {
                Vault.Lock();
            }
        };
        _lockTimer.Start();

#if DEBUG
        DevHooks.Start(this);
#endif
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// A vault that needs Windows Hello can't have been made by Vesper on Linux, and can't be opened
    /// here: it's set aside unopened rather than trusted.
    /// </summary>
    private void CheckVault()
    {
        if (!Vault.UsesHello) return;
        Vault.SetAside();
        foreach (var p in Data.Profiles) p.HasSavedPassword = false;
        Data.SaveProfiles();
        Diagnostics.Add("The password vault needed Windows Hello, which Vesper doesn't use on Linux; it was set aside.");
        StartupWarning = "Vesper's password vault was one that needs Windows Hello, which Vesper doesn't make on Linux. " +
            "It was set aside unopened, so Vesper will ask for your Macs' passwords again. If you didn't copy it here yourself, " +
            "software on this computer may have tampered with it: consider changing those passwords.";
    }

    internal MainWindow? Main => _main;

    internal void MainClosed() => _main = null;

    /// <summary>
    /// Installs the downloaded update (pacman, after your password through polkit), then starts
    /// the new version and quits this one. Throws with a message if it wasn't installed.
    /// </summary>
    internal async Task RestartToUpdateAsync()
    {
        if (Updates is not { Status: UpdateStatus.Ready, Offer: { } offer, DownloadedFile: { } file }) return;
        await UpdateInstaller.InstallAsync(offer, file, relaunch: true);
        foreach (var session in Sessions.ToList()) session.Close();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }

    internal void SessionOpened(SessionWindow window) => Sessions.Add(window);

    internal void SessionClosed(SessionWindow window)
    {
        Sessions.Remove(window);
        _lastSessionActivity = DateTime.Now;
        _main?.Refresh();
    }

    /// <summary>Applies the chosen theme to every window.</summary>
    internal void ApplyTheme() => RequestedThemeVariant = ThemeFor(Data.Settings.Theme);

    internal static ThemeVariant ThemeFor(ThemeChoice choice) => choice switch
    {
        ThemeChoice.Dark => ThemeVariant.Dark,
        ThemeChoice.Light => ThemeVariant.Light,
        _ => ThemeVariant.Default,
    };

    /// <summary>Shows an unexpected error in the window in front, once at a time.</summary>
    private async void ShowProblem(string message)
    {
        if (_showingProblem) return;
        Window? target = Sessions.FirstOrDefault(s => s.IsActive) ?? (Window?)_main ?? Sessions.FirstOrDefault();
        if (target is null) return;
        _showingProblem = true;
        try
        {
            await Dialogs.InfoAsync(target, "Something went wrong",
                message + "\n\nVesper carried on. Details are in Settings › About › Diagnostics (kept in memory only).");
        }
        catch (Exception e)
        {
            Diagnostics.Add("Couldn't show a problem: " + e.Message);
        }
        finally
        {
            _showingProblem = false;
        }
    }
}
