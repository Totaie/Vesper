using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vesper.Linux.Controls;
using Vesper.Models;
using Vesper.Services;
using Vesper.Updates;

namespace Vesper.Linux.Views;

/// <summary>Home: your Macs as cards, and the way to add one.</summary>
public sealed partial class MainWindow : Window, IDialogHost
{
    private readonly ObservableCollection<MacCardItem> _macs = [];

    public MainWindow()
    {
        InitializeComponent();
        MacsList.ItemsSource = _macs;
        App.Current.Data.ProfilesChanged += OnProfilesChanged;
        App.Current.Updates.Changed += OnUpdateChanged;
        Activated += (_, _) => UpdateGreeting();
        Closed += (_, _) =>
        {
            App.Current.Data.ProfilesChanged -= OnProfilesChanged;
            App.Current.Updates.Changed -= OnUpdateChanged;
            Layer.CloseAll();
            App.Current.MainClosed();
        };
        Opened += async (_, _) =>
        {
            if (App.Current.StartupWarning is { } warning)
            {
                App.Current.StartupWarning = null;
                await Dialogs.InfoAsync(this, "Saved passwords set aside", warning);
            }
        };
        Refresh();
        UpdateGreeting();
        RefreshUpdateBar();
    }

    private string? _dismissedUpdate;

    private void OnUpdateChanged() => Dispatcher.UIThread.Post(RefreshUpdateBar);

    private void RefreshUpdateBar()
    {
        var updates = Vesper.Updates;
        if (updates.Offer is not { } offer || offer.Version == _dismissedUpdate
            || updates.Status is not (UpdateStatus.Ready or UpdateStatus.Available))
        {
            UpdateBar.IsVisible = false;
            return;
        }
        string summary = offer.Summary.Length > 0 ? offer.Summary + " " : "";
        bool ready = updates.Status == UpdateStatus.Ready;
        UpdateTitle.Text = ready ? $"Vesper {offer.Version} is ready to install" : $"Vesper {offer.Version} is available";
        UpdateDetail.Text = summary + (ready
            ? (InstallInfo.Kind == InstallKind.LinuxPackage
                ? "Installing asks for your password (pacman), then Vesper restarts."
                : "It installs when you restart Vesper, or the next time you quit it.")
            : "This copy can't update itself (run Vesper from its AppImage for that); download the new version from its releases page.");
        UpdateActions.Children.Clear();
        var later = new Button { Content = "Later" };
        later.Click += (_, _) =>
        {
            _dismissedUpdate = offer.Version;
            UpdateBar.IsVisible = false;
        };
        var act = new Button { Content = ready ? "Restart now" : "Open releases page" };
        act.Classes.Add("accent");
        act.Click += async (_, _) => await UpdateActionAsync(ready);
        UpdateActions.Children.Add(later);
        UpdateActions.Children.Add(act);
        UpdateBar.IsVisible = true;
    }

    private async Task UpdateActionAsync(bool ready)
    {
        if (!ready)
        {
            await Launcher.LaunchUriAsync(UpdateService.ReleasesPage);
            return;
        }
        int sessions = Vesper.Sessions.Count;
        if (sessions > 0 && !await Dialogs.ConfirmAsync(this, "Restart Vesper now?",
                sessions == 1 ? "Your Mac will be disconnected while Vesper updates." : $"Your {sessions} Macs will be disconnected while Vesper updates.",
                "Install and restart"))
        {
            return;
        }
        try
        {
            await Vesper.RestartToUpdateAsync();
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync(this, "Update not installed", ex.Message);
        }
    }

    DialogLayer IDialogHost.DialogLayer => Layer;

    private static App Vesper => App.Current;

    private void OnProfilesChanged() => Dispatcher.UIThread.Post(Refresh);

    private void UpdateGreeting()
    {
        Greeting.Text = DateTime.Now.Hour switch
        {
            < 5 => "Good night",
            < 12 => "Good morning",
            < 17 => "Good afternoon",
            < 22 => "Good evening",
            _ => "Good night",
        };
    }

    internal void Refresh()
    {
        var profiles = Vesper.Data.Profiles.OrderByDescending(p => p.LastConnected ?? p.Added).ToList();
        _macs.Clear();
        foreach (var p in profiles) _macs.Add(new MacCardItem(p));
        EmptyState.IsVisible = profiles.Count == 0;
        MacsList.IsVisible = profiles.Count > 0;
        Subtitle.Text = profiles.Count switch
        {
            0 => "Let's get your Mac on screen. It only takes a minute.",
            1 => "Your Mac, a click away — fast and encrypted end to end.",
            _ => "Your Macs, a click away — fast and encrypted end to end.",
        };
    }

    private static MacProfile? ProfileFrom(object? sender) =>
        sender is Control { DataContext: MacCardItem item } ? Vesper.Data.Find(item.Id) : null;

    private async void AddMac_Click(object? sender, RoutedEventArgs e) => await EditAsync(null);

    internal async Task EditAsync(MacProfile? existing)
    {
        if (await MacEditor.ShowAsync(this, existing) is not { } edit) return;
        try
        {
            await SavedPasswords.ApplyEditAsync(Vesper.Vault, existing, edit.Profile, edit.Password, edit.RememberPassword, EnsureVaultAsync);
        }
        catch (Exception e)
        {
            await Dialogs.InfoAsync(this, "Password not saved", e.Message);
        }
        Vesper.Data.Upsert(edit.Profile);
        if (edit.ConnectAfterSaving) SessionWindow.Open(edit.Profile, edit.Password);
    }

    internal async Task<bool> EnsureVaultAsync()
    {
        try
        {
            return await Vesper.Vault.UnlockAsync();
        }
        catch (Exception e)
        {
            await Dialogs.InfoAsync(this, "Saved passwords unavailable", e.Message);
            return false;
        }
    }

    private void Connect_Click(object? sender, RoutedEventArgs e)
    {
        if (ProfileFrom(sender) is { } profile) SessionWindow.Open(profile, null);
    }

    private void Card_DoubleTapped(object? sender, TappedEventArgs e)
    {
        // A double click on the card's buttons is theirs, not the card's.
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        if (ProfileFrom(sender) is { } profile) SessionWindow.Open(profile, null);
    }

    private void More_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not MacCardItem item) return;
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        menu.Items.Add(Item("Edit", Icons.Edit, async () => { if (Vesper.Data.Find(item.Id) is { } p) await EditAsync(p); }));
        menu.Items.Add(Item("Forget saved password", Icons.Key, () => ForgetPasswordAsync(item.Id), item.HasSavedPassword));
        menu.Items.Add(Item("Forget trusted identity", Icons.Shield, () => ForgetIdentityAsync(item.Id)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Remove", Icons.Trash, () => RemoveAsync(item.Id)));
        menu.ShowAt(button);

        static MenuItem Item(string text, Geometry icon, Func<Task> action, bool enabled = true)
        {
            var menuItem = new MenuItem
            {
                Header = text,
                Icon = new Icon { Data = icon, Size = 15 },
                IsEnabled = enabled,
            };
            menuItem.Click += async (_, _) => await action();
            return menuItem;
        }
    }

    private async Task ForgetPasswordAsync(Guid id)
    {
        if (Vesper.Data.Find(id) is not { } profile) return;
        try
        {
            if (await EnsureVaultAsync()) Vesper.Vault.Forget(profile.Id);
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync(this, "Couldn't update saved passwords", ex.Message);
        }
        profile.HasSavedPassword = false;
        Vesper.Data.Upsert(profile);
    }

    private async Task ForgetIdentityAsync(Guid id)
    {
        if (Vesper.Data.Find(id) is not { } profile) return;
        bool ok = await Dialogs.ConfirmAsync(this, "Forget this Mac's identity?",
            "Vesper will ask you to verify the Mac's identity key (and SSH host key) again on the next connection, and will try its identity-key sign-in again. Do this after reinstalling macOS or resetting Screen Sharing.",
            "Forget identity");
        if (!ok) return;
        byte[] before = SecretVault.Binding(profile);
        profile.PinnedIdentityFingerprint = null;
        profile.PinnedSshKey = null;
        profile.PinnedSshKeyAlgorithm = null;
        profile.RsaSignInUnsupported = false;
        try
        {
            if (profile.HasSavedPassword && await EnsureVaultAsync())
            {
                Vesper.Vault.Rebind(profile.Id, before, SecretVault.Binding(profile));
            }
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync(this, "Couldn't update saved passwords", ex.Message);
        }
        Vesper.Data.Upsert(profile);
    }

    private async Task RemoveAsync(Guid id)
    {
        if (Vesper.Data.Find(id) is not { } profile) return;
        bool ok = await Dialogs.ConfirmAsync(this, $"Remove {MacNames.DisplayName(profile)}?",
            "Its settings, trusted identity and saved password are deleted from this computer.", "Remove");
        if (!ok) return;
        try
        {
            if (profile.HasSavedPassword && await EnsureVaultAsync()) Vesper.Vault.Forget(profile.Id);
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync(this, "Couldn't update saved passwords", ex.Message);
        }
        Vesper.Data.Remove(profile.Id);
    }

    private void Settings_Click(object? sender, RoutedEventArgs e) => SettingsWindow.ShowSingle();
}

/// <summary>One Mac as its card shows it.</summary>
public sealed class MacCardItem(MacProfile profile)
{
    public Guid Id => profile.Id;
    public string Name => MacNames.DisplayName(profile);
    public string Address => string.IsNullOrEmpty(profile.Username) ? profile.Address : $"{profile.Username} @ {profile.Address}";
    public string ModelName => MacNames.Model(profile.MacModel);
    public bool HasSavedPassword => profile.HasSavedPassword;

    public string SecurityText => profile.Transport == TransportMode.Ssh
        ? "SSH tunnel"
        : profile.PinnedIdentityFingerprint is null ? "Verify on first connect" : "Identity pinned";

    public bool IsPinned => profile.PinnedIdentityFingerprint is not null || profile.PinnedSshKey is not null;

    public Geometry SecurityIcon => IsPinned ? Icons.ShieldCheck : Icons.Shield;

    public string LastConnected => MacNames.LastConnected(profile);
}
