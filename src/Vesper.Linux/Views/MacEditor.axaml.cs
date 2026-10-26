using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Vesper.Core.Rfb;
using Vesper.Models;
using Vesper.Services;

namespace Vesper.Linux.Views;

/// <summary>What the Mac editor decided.</summary>
internal sealed record MacEdit(MacProfile Profile, string? Password, bool RememberPassword, bool ConnectAfterSaving);

/// <summary>Adds or edits a Mac (in a dialog over the window that asked).</summary>
public sealed partial class MacEditor : UserControl
{
    private readonly MacProfile _profile;
    private readonly bool _isNew;
    private readonly bool _canSave;
    private Dialog? _dialog;

    public MacEditor() : this(null)
    {
    }

    private MacEditor(MacProfile? existing)
    {
        InitializeComponent();
        _isNew = existing is null;
        _profile = existing?.Clone() ?? new MacProfile();
        _canSave = App.Current.Vault.CanSave;

        NameBox.Text = _profile.Name;
        HostBox.Text = _profile.Host;
        UserBox.Text = _profile.Username;
        RememberBox.IsChecked = _isNew || _profile.HasSavedPassword;
        if (!_isNew && _profile.HasSavedPassword) PasswordBox.PlaceholderText = "Saved — type to change";
        if (!_canSave)
        {
            // Without a keyring there's nowhere safe to keep it: say so instead of offering.
            RememberBox.IsVisible = false;
            NoVaultNote.IsVisible = true;
            NoVaultText.Text = App.Current.Vault.UnavailableReason;
        }
        DirectChoice.IsChecked = _profile.Transport != TransportMode.Ssh;
        SshChoice.IsChecked = _profile.Transport == TransportMode.Ssh;
        SshPortBox.Value = _profile.SshPort;
        PortBox.Value = _profile.Port;
        ResolutionBox.SelectedIndex = (int)_profile.Resolution;
        ColorBox.SelectedIndex = (int)_profile.Quality;
        ModifierBox.SelectedIndex = (int)_profile.Modifiers;
        CodecBox.SelectedIndex = (int)_profile.Codec;
        ClipboardSwitch.IsChecked = _profile.ShareClipboard;
        ViewOnlySwitch.IsChecked = _profile.ViewOnly;
        LegacySwitch.IsChecked = _profile.AllowLegacySignIn;
        RsaRefusedPanel.IsVisible = _profile.RsaSignInUnsupported;
        SshPortPanel.IsVisible = SshChoice.IsChecked == true;
    }

    private MacEdit? Result { get; set; }

    /// <summary>Shows the editor; null if cancelled.</summary>
    internal static async Task<MacEdit?> ShowAsync(Visual owner, MacProfile? existing)
    {
        var editor = new MacEditor(existing);
        var dialog = new Dialog
        {
            Title = existing is null ? "Add a Mac" : $"Edit {MacNames.DisplayName(existing)}",
            Content = editor,
            ContentWidth = 560,
            PrimaryButtonText = existing is null ? "Add and connect" : "Save",
            SecondaryButtonText = existing is null ? "Add" : null,
            CloseButtonText = "Cancel",
            DefaultButton = DialogButton.Primary,
        };
        editor._dialog = dialog;
        editor.Validate();
        dialog.Closing += result =>
        {
            if (result != DialogResult.None) editor.Commit(connect: result == DialogResult.Primary);
        };
        return await dialog.ShowAsync(owner) == DialogResult.None ? null : editor.Result;
    }

    private void Commit(bool connect)
    {
        _profile.Name = (NameBox.Text ?? "").Trim();
        _profile.Host = NormalizeHost(HostBox.Text ?? "");
        _profile.Username = (UserBox.Text ?? "").Trim();
        _profile.Transport = SshChoice.IsChecked == true ? TransportMode.Ssh : TransportMode.Direct;
        _profile.SshPort = PortOrDefault(SshPortBox.Value, 22);
        _profile.Port = PortOrDefault(PortBox.Value, 5900);
        _profile.Resolution = (ResolutionMode)Math.Max(0, ResolutionBox.SelectedIndex);
        _profile.Quality = (ColorQuality)Math.Max(0, ColorBox.SelectedIndex);
        _profile.Modifiers = (ModifierLayout)Math.Max(0, ModifierBox.SelectedIndex);
        _profile.Codec = (CodecPreference)Math.Max(0, CodecBox.SelectedIndex);
        _profile.ShareClipboard = ClipboardSwitch.IsChecked == true;
        _profile.ViewOnly = ViewOnlySwitch.IsChecked == true;
        _profile.AllowLegacySignIn = LegacySwitch.IsChecked == true;
        if (string.IsNullOrEmpty(_profile.Name)) _profile.Name = MacNames.FriendlyName(_profile.Host);
        string? password = PasswordBox.Text is { Length: > 0 } typed ? typed : null;
        // Without a keyring nothing can be saved, and a password saved earlier stays as it was.
        bool remember = _canSave ? RememberBox.IsChecked == true : _profile.HasSavedPassword;
        Result = new MacEdit(_profile, password, remember, _isNew && connect);
        PasswordBox.Text = "";
    }

    /// <summary>Accepts "vnc://host:port" and "host:port" as people paste them.</summary>
    private string NormalizeHost(string raw)
    {
        var (host, port) = MacNames.ParseAddress(raw);
        if (port is { } p) PortBox.Value = p;
        return host;
    }

    private static int PortOrDefault(decimal? value, int fallback) =>
        value is not { } v || v < 1 || v > 65535 ? fallback : (int)v;

    private void Validate()
    {
        if (_dialog is null) return;
        bool ok = (HostBox.Text ?? "").Trim().Length > 0 && (UserBox.Text ?? "").Trim().Length > 0;
        _dialog.IsPrimaryButtonEnabled = ok;
        _dialog.IsSecondaryButtonEnabled = ok;
    }

    private void Field_Changed(object? sender, TextChangedEventArgs e) => Validate();

    private void Transport_Changed(object? sender, RoutedEventArgs e) =>
        SshPortPanel.IsVisible = SshChoice.IsChecked == true;

    private void RetryRsa_Click(object? sender, RoutedEventArgs e)
    {
        _profile.RsaSignInUnsupported = false;
        RsaRefusedPanel.IsVisible = false;
    }
}
