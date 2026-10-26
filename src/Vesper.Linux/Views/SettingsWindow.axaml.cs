using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Vesper.Models;
using Vesper.Updates;

namespace Vesper.Linux.Views;

public sealed partial class SettingsWindow : Window, IDialogHost
{
    private static SettingsWindow? s_open;
    private bool _loading = true;

    public static SettingsWindow ShowSingle()
    {
        if (s_open is not null)
        {
            s_open.Activate();
            return s_open;
        }
        s_open = new SettingsWindow();
        s_open.Show();
        return s_open;
    }

    public SettingsWindow()
    {
        InitializeComponent();
        Closed += (_, _) =>
        {
            App.Current.Updates.Changed -= OnUpdateChanged;
            Layer.CloseAll();
            s_open = null;
        };
        App.Current.Updates.Changed += OnUpdateChanged;

        var vault = App.Current.Vault;
        LockBox.SelectedItem = LockBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == Settings.LockAfterMinutes.ToString())
            ?? LockBox.Items[1];
        ScrollSlider.Value = Settings.ScrollSpeed;
        ThemeBox.SelectedIndex = (int)Settings.Theme;
        StatsSwitch.IsChecked = Settings.ShowConnectionStats;
        UpdatesCheck.IsChecked = Settings.CheckForUpdates;
        RefreshUpdate();
        if (!vault.CanSave)
        {
            NoVaultCard.IsVisible = true;
            NoVaultText.Text = vault.UnavailableReason;
            ForgetAllButton.IsEnabled = vault.Exists;
        }
        VersionText.Text = $"Version {typeof(App).Assembly.GetName().Version?.ToString(3)} · .NET {Environment.Version}";
        PrivacyText.Text = "No accounts, no telemetry, no crash reports. Vesper connects only to the Macs you add, and to GitHub to look for updates (unless you turn that off above). " +
            $"Its settings live in {Tilde(App.Current.Data.Directory)}, readable only by you, with passwords sealed by AES-256-GCM " +
            "under a key kept in your login keyring.";
        RefreshDiagnostics();
        _loading = false;
    }

    DialogLayer IDialogHost.DialogLayer => Layer;

    private static AppSettings Settings => App.Current.Data.Settings;

    private static string Tilde(string path)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !OperatingSystem.IsWindows() && home.Length > 1 && path.StartsWith(home + "/", StringComparison.Ordinal)
            ? "~" + path[home.Length..]
            : path;
    }

    private static void Save() => App.Current.Data.SaveSettings();

    private void Lock_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || LockBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        Settings.LockAfterMinutes = int.Parse(tag, System.Globalization.CultureInfo.InvariantCulture);
        Save();
    }

    private async void ForgetAll_Click(object? sender, RoutedEventArgs e)
    {
        bool ok = await Dialogs.ConfirmAsync(this, "Forget every saved password?",
            "The encrypted vault is deleted. You'll be asked for each Mac's password the next time you connect.",
            "Forget passwords");
        if (!ok) return;
        try
        {
            App.Current.Vault.ForgetAll();
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync(this, "Couldn't delete the vault", ex.Message);
            return;
        }
        foreach (var p in App.Current.Data.Profiles) p.HasSavedPassword = false;
        App.Current.Data.SaveProfiles();
        if (!App.Current.Vault.CanSave) ForgetAllButton.IsEnabled = false;
    }

    private void Scroll_Changed(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        Settings.ScrollSpeed = ScrollSlider.Value;
        Save();
    }

    private void Theme_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeBox.SelectedIndex < 0) return;
        Settings.Theme = (ThemeChoice)ThemeBox.SelectedIndex;
        Save();
        App.Current.ApplyTheme();
    }

    private void Stats_Toggled(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.ShowConnectionStats = StatsSwitch.IsChecked == true;
        Save();
    }

    private async void CheckUpdates_Click(object? sender, RoutedEventArgs e) => await App.Current.Updates.CheckAsync(manual: true);

    private async void InstallUpdate_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await App.Current.RestartToUpdateAsync();
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync(this, "Update not installed", ex.Message);
        }
    }

    private void OnUpdateChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshUpdate);

    private void RefreshUpdate()
    {
        var u = App.Current.Updates;
        string version = u.CurrentVersion.ToString(3);
        string? offered = u.Offer?.Version;
        (UpdateTitle.Text, UpdateDetail.Text) = u.Status switch
        {
            UpdateStatus.Checking => ("Checking for updates…", $"You have Vesper {version}."),
            UpdateStatus.Downloading => ($"Downloading Vesper {offered}…", "It's checked against Vesper's release signature before it's used."),
            UpdateStatus.Ready => ($"Vesper {offered} is ready to install", InstallInfo.Kind == InstallKind.LinuxPackage
                ? "Installing asks for your password (pacman), then Vesper restarts."
                : "It installs when you restart Vesper, or the next time you quit it."),
            UpdateStatus.Available => ($"Vesper {offered} is available", "This copy isn't running from Vesper's AppImage, so it can't update itself. Download the new version from its releases page."),
            UpdateStatus.Failed => ("Couldn't check for updates", u.Error ?? ""),
            UpdateStatus.UpToDate => ("Vesper is up to date", $"Version {version}" + (u.LastChecked is { } t ? $", checked at {t:HH:mm}." : ".")),
            _ => ("Updates", $"You have Vesper {version}."),
        };
        UpdateProgress.IsVisible = u.Status == UpdateStatus.Downloading;
        UpdateProgress.Value = u.Progress;
        InstallUpdateButton.IsVisible = u.Status == UpdateStatus.Ready;
        CheckUpdatesButton.IsEnabled = u.Status is not (UpdateStatus.Checking or UpdateStatus.Downloading);
    }

    private void Updates_Toggled(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.CheckForUpdates = UpdatesCheck.IsChecked == true;
        Save();
    }

    private void Diagnostics_Expanding(object? sender, CancelRoutedEventArgs e) => RefreshDiagnostics();

    private void RefreshDiagnostics() =>
        DiagnosticsBox.Text = Diagnostics.Text.Length > 0 ? Diagnostics.Text : "Nothing recorded yet.";

    private async void CopyDiagnostics_Click(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(Diagnostics.Text);
    }
}
