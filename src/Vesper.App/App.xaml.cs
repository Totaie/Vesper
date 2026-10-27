using Microsoft.UI.Xaml;
using Vesper.Services;
using Vesper.Updates;
using Vesper.Views;

namespace Vesper;

public partial class App : Application
{
    private MainWindow? _main;
    private DispatcherTimer? _lockTimer;
    private DateTime _lastSessionActivity = DateTime.Now;
    private bool _installStarted;

    public App()
    {
#if DEBUG
        // Development builds only: first-chance exceptions to a file beside the exe.
        string log = Path.Combine(AppContext.BaseDirectory, "debug-exceptions.log");
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            try { File.AppendAllText(log, $"{DateTime.Now:HH:mm:ss.fff} [thread {Environment.CurrentManagedThreadId}] {e.Exception.GetType().Name}: {e.Exception.Message}{Environment.NewLine}{Environment.StackTrace}{Environment.NewLine}{Environment.NewLine}"); } catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try { File.AppendAllText(log, $"{DateTime.Now:HH:mm:ss.fff} UNHANDLED {e.ExceptionObject}{Environment.NewLine}{Environment.NewLine}"); } catch { }
        };
#endif
        Hardening.Apply();
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            // Log nothing to disk; show the message in the app instead of a crash dialog.
            Diagnostics.Add("Unhandled: " + e.Exception.GetType().Name + ": " + e.Exception.Message);
            e.Handled = true;
        };
    }

    public static new App Current => (App)Application.Current;

    internal AppData Data { get; } = new();
    internal SecretVault Vault { get; private set; } = null!;
    internal ConnectionService Connections { get; private set; } = null!;
    internal UpdateService Updates { get; private set; } = null!;
    internal List<SessionWindow> Sessions { get; } = [];

    /// <summary>Something found at startup that the user needs to hear about, shown by the main window once.</summary>
    internal string? StartupWarning { get; set; }

    private SingleInstance? _instance;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // One Vesper: launching it again brings this one's window forward instead.
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _instance = SingleInstance.Claim(Data.Directory, () => dispatcher.TryEnqueue(ShowMain));
        if (_instance is null)
        {
            Exit();
            return;
        }
        Vault = new SecretVault(Data.VaultPath, new Security.DpapiProtector(), new WindowsHello()) { RequireHello = Data.Settings.RequireWindowsHello };
        CheckVault();
        Connections = new ConnectionService(Data, Vault);
        Updates = new UpdateService(Data, typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0));
        Updates.Start();
        _ = Task.Run(UpdateInstaller.CleanUpAfterUpdate);
        _main = new MainWindow();
        _main.Activate();

        // Lock the vault after a quiet spell with no session open.
        _lockTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _lockTimer.Tick += (_, _) =>
        {
            // A session window left on "Disconnected" doesn't keep saved passwords unlocked.
            if (Sessions.Any(s => s.IsConnected)) _lastSessionActivity = DateTime.Now;
            else if (Vault.IsUnlocked && DateTime.Now - _lastSessionActivity > TimeSpan.FromMinutes(Data.Settings.LockAfterMinutes))
            {
                Vault.Lock();
            }
        };
        _lockTimer.Start();
    }

    /// <summary>
    /// The vault's protection must be what the user chose. settings.json and vault.bin can both be
    /// rewritten by any program running as the user, so a vault that doesn't need Windows Hello
    /// when Hello is required isn't one Vesper made: it's set aside rather than trusted with new
    /// passwords.
    /// </summary>
    private void CheckVault()
    {
        if (Vault.UsesHello && !Data.Settings.RequireWindowsHello)
        {
            // A Hello vault can't be opened without Hello whatever the setting says; show it truthfully.
            Data.Settings.RequireWindowsHello = true;
            Data.SaveSettings();
            Vault.RequireHello = true;
        }
        else if (Data.Settings.RequireWindowsHello && Vault.Exists && !Vault.UsesHello)
        {
            Vault.SetAside();
            foreach (var p in Data.Profiles) p.HasSavedPassword = false;
            Data.SaveProfiles();
            Diagnostics.Add("The password vault didn't require Windows Hello although Vesper does; it was set aside.");
            StartupWarning = "Vesper's password vault had been replaced with one that doesn't need Windows Hello, which Vesper doesn't do. " +
                "It was set aside unopened, so Vesper will ask for your Macs' passwords again. Software on this PC may have tampered with it: " +
                "consider scanning for malware and changing those passwords.";
        }
    }

    internal void ShowMain()
    {
        if (_main is null)
        {
            _main = new MainWindow();
        }
        _main.Activate();
    }

    internal void MainClosed()
    {
        _main = null;
        LastWindowCheck();
    }

    /// <summary>When the last of Vesper's windows has closed, the app is quitting.</summary>
    internal void LastWindowCheck()
    {
        if (_main is null && Sessions.Count == 0 && !SettingsWindow.IsOpen) InstallOnExit();
    }

    internal void SessionOpened(SessionWindow window) => Sessions.Add(window);

    internal void SessionClosed(SessionWindow window)
    {
        Sessions.Remove(window);
        _lastSessionActivity = DateTime.Now;
        _main?.Refresh();
        LastWindowCheck();
    }

    /// <summary>Installs the downloaded update and quits; Vesper opens again on the new version.</summary>
    internal async Task RestartToUpdateAsync()
    {
        if (Updates is not { Status: UpdateStatus.Ready, Offer: { } offer, DownloadedFile: { } file }) return;
        _installStarted = true;
        try
        {
            await UpdateInstaller.InstallAsync(offer, file, relaunch: true);
        }
        catch
        {
            _installStarted = false;
            throw;
        }
        foreach (var session in Sessions.ToList()) session.Close();
        Exit();
    }

    /// <summary>
    /// Quitting with an update downloaded installs it, quietly, so the next start is the new
    /// version. (On Windows nothing needs asking: Setup installs for this user only.)
    /// </summary>
    private void InstallOnExit()
    {
        if (_installStarted || Updates is not { Status: UpdateStatus.Ready, Offer: { } offer, DownloadedFile: { } file }) return;
        _installStarted = true;
        try
        {
            UpdateInstaller.InstallOnExit(offer, file);
        }
        catch (Exception e)
        {
            Diagnostics.Add($"Couldn't install the update on exit: {e.Message}");
        }
    }

    /// <summary>Applies the chosen theme to every open window.</summary>
    internal void ApplyTheme()
    {
        _main?.ApplyTheme();
        foreach (var s in Sessions) s.ApplyTheme();
    }

    internal static ElementTheme ThemeFor(Models.ThemeChoice choice) => choice switch
    {
        Models.ThemeChoice.Dark => ElementTheme.Dark,
        Models.ThemeChoice.Light => ElementTheme.Light,
        _ => ElementTheme.Default,
    };
}
