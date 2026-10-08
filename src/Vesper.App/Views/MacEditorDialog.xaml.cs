using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vesper.Core.Rfb;
using Vesper.Models;
using Vesper.Services;

namespace Vesper.Views;

/// <summary>Adds or edits a Mac.</summary>
public sealed partial class MacEditorDialog : ContentDialog
{
    private readonly MacProfile _profile;
    private readonly bool _isNew;

    internal MacEditorDialog(MacProfile? existing, DiscoveredMac? discovered)
    {
        InitializeComponent();
        _isNew = existing is null;
        _profile = existing?.Clone() ?? new MacProfile();
        if (discovered is not null)
        {
            _profile.Name = discovered.Name;
            _profile.Host = discovered.Host;
            _profile.Port = discovered.Port;
        }

        Title = _isNew ? "Add a Mac" : $"Edit {MacCardItem.DisplayName(_profile)}";
        PrimaryButtonText = _isNew ? "Add and connect" : "Save";
        SecondaryButtonText = _isNew ? "Add" : "";

        NameBox.Text = _profile.Name;
        HostBox.Text = _profile.Host;
        UserBox.Text = _profile.Username;
        RememberBox.IsChecked = _isNew || _profile.HasSavedPassword;
        if (!_isNew && _profile.HasSavedPassword) PasswordBox.PlaceholderText = "Saved — type to change";
        TransportChoice.SelectedIndex = _profile.Transport == TransportMode.Ssh ? 1 : 0;
        SshPortBox.Value = _profile.SshPort;
        PortBox.Value = _profile.Port;
        ResolutionBox.SelectedIndex = (int)_profile.Resolution;
        ColorBox.SelectedIndex = (int)_profile.Quality;
        ModifierBox.SelectedIndex = (int)_profile.Modifiers;
        CodecBox.SelectedIndex = (int)_profile.Codec;
        ClipboardSwitch.IsOn = _profile.ShareClipboard;
        ViewOnlySwitch.IsOn = _profile.ViewOnly;
        LegacySwitch.IsOn = _profile.AllowLegacySignIn;
        RsaRefusedPanel.Visibility = _profile.RsaSignInUnsupported ? Visibility.Visible : Visibility.Collapsed;

        PrimaryButtonClick += (_, _) => Commit(connect: true);
        SecondaryButtonClick += (_, _) => Commit(connect: false);
        Validate();
    }

    internal MacProfile? Result { get; private set; }
    internal string? Password { get; private set; }
    internal bool RememberPassword => RememberBox.IsChecked == true;
    internal bool ConnectAfterSaving { get; private set; }

    private void Commit(bool connect)
    {
        _profile.Name = NameBox.Text.Trim();
        _profile.Host = NormalizeHost(HostBox.Text);
        _profile.Username = UserBox.Text.Trim();
        _profile.Transport = TransportChoice.SelectedIndex == 1 ? TransportMode.Ssh : TransportMode.Direct;
        _profile.SshPort = PortOrDefault(SshPortBox.Value, 22);
        _profile.Port = PortOrDefault(PortBox.Value, 5900);
        _profile.Resolution = (ResolutionMode)Math.Max(0, ResolutionBox.SelectedIndex);
        _profile.Quality = (ColorQuality)Math.Max(0, ColorBox.SelectedIndex);
        _profile.Modifiers = (ModifierLayout)Math.Max(0, ModifierBox.SelectedIndex);
        _profile.Codec = (CodecPreference)Math.Max(0, CodecBox.SelectedIndex);
        _profile.ShareClipboard = ClipboardSwitch.IsOn;
        _profile.ViewOnly = ViewOnlySwitch.IsOn;
        _profile.AllowLegacySignIn = LegacySwitch.IsOn;
        if (string.IsNullOrEmpty(_profile.Name)) _profile.Name = FriendlyName(_profile.Host);
        Password = PasswordBox.Password.Length > 0 ? PasswordBox.Password : null;
        ConnectAfterSaving = _isNew && connect;
        Result = _profile;
    }

    /// <summary>Accepts "vnc://host:port" and "host:port" as people paste them.</summary>
    private string NormalizeHost(string raw)
    {
        string host = raw.Trim();
        foreach (string scheme in new[] { "vnc://", "ard://", "ssh://" })
        {
            if (host.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) host = host[scheme.Length..];
        }
        if (host.Contains('@')) host = host[(host.IndexOf('@') + 1)..];
        host = host.TrimEnd('/');
        int colon = host.LastIndexOf(':');
        if (colon > 0 && host.Count(c => c == ':') == 1 && int.TryParse(host[(colon + 1)..], out int port))
        {
            PortBox.Value = port;
            host = host[..colon];
        }
        return host;
    }

    private static string FriendlyName(string host)
    {
        string name = host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? host[..^6] : host;
        return name.Length > 0 && char.IsLetter(name[0]) ? char.ToUpperInvariant(name[0]) + name[1..] : name;
    }

    private static int PortOrDefault(double value, int fallback) =>
        double.IsNaN(value) || value < 1 || value > 65535 ? fallback : (int)value;

    private void Validate()
    {
        bool ok = HostBox.Text.Trim().Length > 0 && UserBox.Text.Trim().Length > 0;
        IsPrimaryButtonEnabled = ok;
        IsSecondaryButtonEnabled = ok;
    }

    private void Field_Changed(object sender, TextChangedEventArgs e) => Validate();

    private void Password_Changed(object sender, RoutedEventArgs e) => Validate();

    private void Transport_Changed(object sender, SelectionChangedEventArgs e) =>
        SshPortBox.Visibility = TransportChoice.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;

    private void RetryRsa_Click(object sender, RoutedEventArgs e)
    {
        _profile.RsaSignInUnsupported = false;
        RsaRefusedPanel.Visibility = Visibility.Collapsed;
    }
}
