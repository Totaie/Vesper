using System.Collections.ObjectModel;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Vesper.Models;
using Vesper.Services;
using Vesper.Updates;

namespace Vesper.Views;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<MacCardItem> _macs = [];
    private readonly ObservableCollection<DiscoveredMac> _nearby = [];
    private MacDiscovery? _discovery;
    private string? _dismissedUpdate;

    public MainWindow()
    {
        InitializeComponent();
        WindowChrome.Prepare(this, TitleBar, width: 1200, height: 820, minWidth: 720, minHeight: 560);
        MacsRepeater.ItemsSource = _macs;
        NearbyRepeater.ItemsSource = _nearby;
        App.Current.Data.ProfilesChanged += () => DispatcherQueue.TryEnqueue(Refresh);
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated) UpdateGreeting();
        };
        App.Current.Updates.Changed += OnUpdateChanged;
        Closed += (_, _) =>
        {
            App.Current.Updates.Changed -= OnUpdateChanged;
            _discovery?.Dispose();
            App.Current.MainClosed();
        };
        ApplyTheme();
        Refresh();
        UpdateGreeting();
        RefreshUpdateBar();
        if (App.Current.Data.Settings.FindMacsNearby) StartDiscovery();
        Root.Loaded += async (_, _) =>
        {
            if (App.Current.StartupWarning is { } warning)
            {
                App.Current.StartupWarning = null;
                await Dialogs.InfoAsync(Content.XamlRoot, "Saved passwords set aside", warning);
            }
        };
    }

    internal void ApplyTheme() => Root.RequestedTheme = App.ThemeFor(App.Current.Data.Settings.Theme);

    private void UpdateGreeting()
    {
        int hour = DateTime.Now.Hour;
        Greeting.Text = hour switch
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
        var profiles = App.Current.Data.Profiles.OrderByDescending(p => p.LastConnected ?? p.Added).ToList();
        _macs.Clear();
        foreach (var p in profiles) _macs.Add(new MacCardItem(p));
        EmptyState.Visibility = profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MacsRepeater.Visibility = profiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        Subtitle.Text = profiles.Count switch
        {
            0 => "Let's get your Mac on screen. It only takes a minute.",
            1 => "Your Mac, a click away — fast and encrypted end to end.",
            _ => "Your Macs, a click away — fast and encrypted end to end.",
        };
        RefreshNearby();
    }

    private void StartDiscovery()
    {
        _discovery = new MacDiscovery();
        _discovery.Changed += RefreshNearby;
        _discovery.Start();
    }

    private void RefreshNearby()
    {
        if (_discovery is null) return;
        var saved = App.Current.Data.Profiles.Select(p => p.Host.TrimEnd('.').ToLowerInvariant()).ToHashSet();
        var fresh = _discovery.Found.Where(d => !saved.Contains(d.Host.ToLowerInvariant())).OrderBy(d => d.Name).ToList();
        _nearby.Clear();
        foreach (var d in fresh) _nearby.Add(d);
        NearbySection.Visibility = fresh.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private MacProfile? ProfileFrom(object sender) =>
        sender is FrameworkElement { Tag: Guid id } ? App.Current.Data.Find(id) : null;

    private async void AddMac_Click(object sender, RoutedEventArgs e) => await EditAsync(null, null);

    private async void AddNearby_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id } && _discovery?.Found.FirstOrDefault(d => d.Id == id) is { } found)
        {
            await EditAsync(null, found);
        }
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileFrom(sender) is { } profile) await EditAsync(profile, null);
    }

    private async Task EditAsync(MacProfile? existing, DiscoveredMac? discovered)
    {
        var dialog = new MacEditorDialog(existing, discovered) { XamlRoot = Content.XamlRoot, RequestedTheme = Root.ActualTheme };
        if (await dialog.ShowAsync() != ContentDialogResult.None && dialog.Result is { } result)
        {
            try
            {
                await SavedPasswords.ApplyEditAsync(App.Current.Vault, existing, result, dialog.Password, dialog.RememberPassword, EnsureVaultAsync);
            }
            catch (Exception e)
            {
                await Dialogs.InfoAsync(Content.XamlRoot, "Password not saved", e.Message);
            }
            App.Current.Data.Upsert(result);
            if (dialog.ConnectAfterSaving) SessionWindow.Open(result, dialog.Password);
        }
    }

    internal async Task<bool> EnsureVaultAsync()
    {
        try
        {
            return await App.Current.Vault.UnlockAsync();
        }
        catch (Exception e)
        {
            await Dialogs.InfoAsync(Content.XamlRoot, "Saved passwords unavailable", e.Message);
            return false;
        }
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileFrom(sender) is { } profile) SessionWindow.Open(profile, null);
    }

    private void Card_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ProfileFrom(sender) is { } profile) SessionWindow.Open(profile, null);
    }

    private async void ForgetPassword_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileFrom(sender) is not { } profile) return;
        try
        {
            if (await EnsureVaultAsync()) App.Current.Vault.Forget(profile.Id);
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync(Content.XamlRoot, "Couldn't update saved passwords", ex.Message);
        }
        profile.HasSavedPassword = false;
        App.Current.Data.Upsert(profile);
    }

    private async void ForgetIdentity_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileFrom(sender) is not { } profile) return;
        bool ok = await Dialogs.ConfirmAsync(Content.XamlRoot, "Forget this Mac's identity?",
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
                App.Current.Vault.Rebind(profile.Id, before, SecretVault.Binding(profile));
            }
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync(Content.XamlRoot, "Couldn't update saved passwords", ex.Message);
        }
        App.Current.Data.Upsert(profile);
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileFrom(sender) is not { } profile) return;
        bool ok = await Dialogs.ConfirmAsync(Content.XamlRoot, $"Remove {MacCardItem.DisplayName(profile)}?",
            "Its settings, trusted identity and saved password are deleted from this PC.", "Remove");
        if (!ok) return;
        try
        {
            if (profile.HasSavedPassword && await EnsureVaultAsync()) App.Current.Vault.Forget(profile.Id);
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync(Content.XamlRoot, "Couldn't update saved passwords", ex.Message);
        }
        App.Current.Data.Remove(profile.Id);
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsWindow.Show();

    private void OnUpdateChanged() => DispatcherQueue.TryEnqueue(RefreshUpdateBar);

    private void RefreshUpdateBar()
    {
        var updates = App.Current.Updates;
        if (updates.Offer is not { } offer || offer.Version == _dismissedUpdate
            || updates.Status is not (UpdateStatus.Ready or UpdateStatus.Available))
        {
            UpdateBar.IsOpen = false;
            return;
        }
        string summary = offer.Summary.Length > 0 ? offer.Summary + " " : "";
        if (updates.Status == UpdateStatus.Ready)
        {
            UpdateBar.Title = $"Vesper {offer.Version} is ready";
            UpdateBar.Message = summary + "It installs when you restart Vesper, or the next time you quit it.";
            UpdateBarButton.Content = "Restart now";
        }
        else
        {
            UpdateBar.Title = $"Vesper {offer.Version} is available";
            UpdateBar.Message = summary + "This copy of Vesper can't update itself; download the new version from its releases page.";
            UpdateBarButton.Content = "Open releases page";
        }
        UpdateBar.IsOpen = true;
    }

    private void UpdateBar_Closed(InfoBar sender, object args) => _dismissedUpdate = App.Current.Updates.Offer?.Version;

    private async void UpdateBar_Click(object sender, RoutedEventArgs e)
    {
        var updates = App.Current.Updates;
        if (updates.Status != UpdateStatus.Ready)
        {
            await Windows.System.Launcher.LaunchUriAsync(UpdateService.ReleasesPage);
            return;
        }
        int sessions = App.Current.Sessions.Count;
        if (sessions > 0 && !await Dialogs.ConfirmAsync(Content.XamlRoot, "Restart Vesper now?",
                sessions == 1 ? "Your Mac will be disconnected while Vesper updates." : $"Your {sessions} Macs will be disconnected while Vesper updates.",
                "Restart and update"))
        {
            return;
        }
        try
        {
            await App.Current.RestartToUpdateAsync();
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync(Content.XamlRoot, "Update not installed", ex.Message);
        }
    }

    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid card)
        {
            card.BorderBrush = (Brush)Application.Current.Resources["VesperCardHoverBorderBrush"];
            card.Translation = new Vector3(0, -3, 0);
        }
    }

    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid card)
        {
            card.BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
            card.Translation = Vector3.Zero;
        }
    }
}

/// <summary>One Mac as its card shows it.</summary>
public sealed class MacCardItem(MacProfile profile)
{
    public Guid Id => profile.Id;
    public string Name => DisplayName(profile);
    public string Address => string.IsNullOrEmpty(profile.Username) ? profile.Address : $"{profile.Username} @ {profile.Address}";
    public string ModelName => MacNames.Model(profile.MacModel);
    public bool HasSavedPassword => profile.HasSavedPassword;

    public string SecurityText => profile.Transport == TransportMode.Ssh
        ? "SSH tunnel"
        : profile.PinnedIdentityFingerprint is null ? "Verify on first connect" : "Identity pinned";

    public string SecurityGlyph => profile.PinnedIdentityFingerprint is null && profile.PinnedSshKey is null ? "" : "";

    public Brush SecurityBrush => (Brush)Application.Current.Resources[
        profile.PinnedIdentityFingerprint is null && profile.PinnedSshKey is null ? "TextFillColorSecondaryBrush" : "VesperVerifiedBrush"];

    public string LastConnected => MacNames.LastConnected(profile);

    public static string DisplayName(MacProfile p) => MacNames.DisplayName(p);
}
