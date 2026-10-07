using Microsoft.UI.Xaml;
using Vesper.Services;
using Vesper.Views;

namespace Vesper;

public partial class App : Application
{
    private MainWindow? _main;
    private DispatcherTimer? _lockTimer;
    private DateTime _lastSessionActivity = DateTime.Now;

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
    internal List<SessionWindow> Sessions { get; } = [];

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Vault = new SecretVault(Data.VaultPath);
        Connections = new ConnectionService(Data);
        _main = new MainWindow();
        _main.Activate();

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
    }

    internal void ShowMain()
    {
        if (_main is null)
        {
            _main = new MainWindow();
        }
        _main.Activate();
    }

    internal void MainClosed() => _main = null;

    internal void SessionOpened(SessionWindow window) => Sessions.Add(window);

    internal void SessionClosed(SessionWindow window)
    {
        Sessions.Remove(window);
        _lastSessionActivity = DateTime.Now;
        _main?.Refresh();
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

/// <summary>An in-memory diagnostic log (never written to disk), shown in Settings → About.</summary>
internal static class Diagnostics
{
    private static readonly Queue<string> Lines = new();

    public static void Add(string line)
    {
        lock (Lines)
        {
            Lines.Enqueue($"{DateTime.Now:HH:mm:ss}  {line}");
            while (Lines.Count > 200) Lines.Dequeue();
        }
#if DEBUG
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "debug-diagnostics.log"), $"{DateTime.Now:HH:mm:ss.fff}  {line}{Environment.NewLine}"); } catch { }
#endif
    }

    public static string Text
    {
        get { lock (Lines) return string.Join(Environment.NewLine, Lines); }
    }
}
