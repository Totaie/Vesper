using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Vesper.Models;
using Vesper.Services;
using Windows.ApplicationModel.DataTransfer;

namespace Vesper.Views;

public sealed partial class SettingsWindow : Window
{
    private static SettingsWindow? s_open;
    private bool _loading = true;

    public static void Show()
    {
        if (s_open is not null)
        {
            s_open.Activate();
            return;
        }
        s_open = new SettingsWindow();
        s_open.Activate();
    }

    private SettingsWindow()
    {
        InitializeComponent();
        WindowChrome.Prepare(this, TitleBar, width: 900, height: 860, minWidth: 560, minHeight: 480);
        Closed += (_, _) => s_open = null;
        Root.RequestedTheme = App.ThemeFor(Settings.Theme);

        CaptureSwitch.IsOn = Settings.HideSessionsFromCapture;
        HelloSwitch.IsOn = Settings.RequireWindowsHello;
        LockBox.SelectedItem = LockBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == Settings.LockAfterMinutes.ToString())
            ?? LockBox.Items[1];
        ShortcutsBox.SelectedIndex = (int)Settings.WindowsShortcuts;
        ScrollSlider.Value = Settings.ScrollSpeed;
        ThemeBox.SelectedIndex = (int)Settings.Theme;
        StatsSwitch.IsOn = Settings.ShowConnectionStats;
        DiscoverSwitch.IsOn = Settings.FindMacsNearby;
        VersionText.Text = $"Version {typeof(App).Assembly.GetName().Version?.ToString(3)} · .NET {Environment.Version}";
        DiagnosticsBox.Text = Diagnostics.Text.Length > 0 ? Diagnostics.Text : "Nothing recorded yet.";
        _loading = false;
        _ = CheckHelloAsync();
    }

    private static AppSettings Settings => App.Current.Data.Settings;

    private async Task CheckHelloAsync()
    {
        if (!await WindowsHello.IsAvailableAsync())
        {
            HelloSwitch.IsEnabled = false;
            HelloDescription.Text = "Windows Hello isn't set up on this PC. Saved passwords are encrypted to your Windows account.";
        }
    }

    private void Save() => App.Current.Data.SaveSettings();

    private void Capture_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.HideSessionsFromCapture = CaptureSwitch.IsOn;
        Save();
    }

    private async void Hello_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var vault = App.Current.Vault;
        try
        {
            if (HelloSwitch.IsOn && !vault.UsesHello)
            {
                await Dialogs.InfoAsync(Content.XamlRoot, "Set up Windows Hello for Vesper",
                    "Windows Hello will ask twice: once to create Vesper's key and once to confirm it works.");
                if (!await vault.EnableHelloAsync()) Revert(false);
            }
            else if (!HelloSwitch.IsOn && vault.UsesHello)
            {
                if (!vault.IsUnlocked && !await vault.UnlockAsync()) Revert(true);
                else vault.DisableHello();
            }
            Settings.RequireWindowsHello = vault.UsesHello;
            vault.RequireHello = vault.UsesHello;
            Save();
        }
        catch (Exception ex)
        {
            Revert(vault.UsesHello);
            await Dialogs.InfoAsync(Content.XamlRoot, "Windows Hello", ex.Message);
        }

        void Revert(bool value)
        {
            _loading = true;
            HelloSwitch.IsOn = value;
            _loading = false;
        }
    }

    private void Lock_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LockBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        Settings.LockAfterMinutes = int.Parse(tag);
        Save();
    }

    private async void ForgetAll_Click(object sender, RoutedEventArgs e)
    {
        bool ok = await Dialogs.ConfirmAsync(Content.XamlRoot, "Forget every saved password?",
            "The encrypted vault is deleted. You'll be asked for each Mac's password the next time you connect." +
            (Settings.RequireWindowsHello ? " Windows Hello stays required: the next password you save creates a new vault behind it." : ""),
            "Forget passwords");
        if (!ok) return;
        App.Current.Vault.ForgetAll();
        foreach (var p in App.Current.Data.Profiles) p.HasSavedPassword = false;
        App.Current.Data.SaveProfiles();
    }

    private void Shortcuts_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        Settings.WindowsShortcuts = (ShortcutCapture)ShortcutsBox.SelectedIndex;
        Save();
    }

    private void Scroll_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        Settings.ScrollSpeed = ScrollSlider.Value;
        Save();
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        Settings.Theme = (ThemeChoice)ThemeBox.SelectedIndex;
        Save();
        Root.RequestedTheme = App.ThemeFor(Settings.Theme);
        App.Current.ApplyTheme();
    }

    private void Stats_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.ShowConnectionStats = StatsSwitch.IsOn;
        Save();
    }

    private void Discover_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.FindMacsNearby = DiscoverSwitch.IsOn;
        Save();
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(Diagnostics.Text);
        Clipboard.SetContent(package);
    }
}
