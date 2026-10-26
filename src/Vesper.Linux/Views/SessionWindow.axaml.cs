using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Vesper.Core.Rfb;
using Vesper.Core.Transport;
using Vesper.Linux.Controls;
using Vesper.Linux.Rendering;
using Vesper.Linux.Services;
using Vesper.Models;
using Vesper.Services;

namespace Vesper.Linux.Views;

internal enum NoticeKind { Info, Success, Warning, Error }

/// <summary>One Mac on screen: connection, drawing, input, and the controls around it.</summary>
public sealed partial class SessionWindow : Window, ITrustPrompts, IDialogHost
{
    private const double ToolbarHeight = 46;

    private readonly MacProfile _profile;
    private readonly ObservableCollection<StepItem> _steps = [];
    private readonly CursorFactory _cursors = new();
    private readonly KeyMapper _keys = new();
    private readonly List<(uint Keysym, bool Down)> _keyBuffer = [];
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _floatingTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly DispatcherTimer _refitTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _noticeTimer = new();
    private string? _password;
    private (string Password, bool? Remember)? _typedPassword;
    private string? _approvedIdentity;
    private string? _approvedSshKey;
    private ConnectedMac? _connection;
    private ClipboardBridge? _clipboard;
    private CancellationTokenSource? _cts;
    private bool _fullScreen;
    private WindowState _stateBeforeFullScreen = WindowState.Normal;
    private bool _active;
    private bool _closing;
    private CursorShape? _cursorShape;
    private bool _promptedThisAttempt;
    private bool _sizedToMac;
    private string _loggedCodec = "";
    private (int Width, int Height) _fitTarget;
    private PointerButtons _lastButtons;
    private int _lastX, _lastY;

    /// <summary>The Mac has proven itself: only now are its screen shown and input sent.</summary>
    private bool _secured;

    internal static void Open(MacProfile profile, string? password)
    {
        var existing = App.Current.Sessions.FirstOrDefault(s => s._profile.Id == profile.Id);
        if (existing is not null)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var window = new SessionWindow(profile, password);
        App.Current.SessionOpened(window);
        window.Show();
    }

    /// <summary>For the XAML previewer only: a window with no Mac, which never connects.</summary>
    public SessionWindow() : this(new MacProfile(), null)
    {
    }

    private SessionWindow(MacProfile profile, string? password)
    {
        InitializeComponent();
        _profile = profile;
        _password = password;
        Title = $"{MacNames.DisplayName(profile)} — Vesper";
        MacName.Text = MacNames.DisplayName(profile);
        FloatingName.Text = MacName.Text;
        StepsList.ItemsSource = _steps;
        Surface.Scaling = profile.Scaling;
        _keys.Layout = profile.Modifiers;
        ClipboardToggle.IsChecked = profile.ShareClipboard;

        Surface.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty) UpdateView();
        };
        Surface.LostFocus += (_, _) => ReleaseKeys();
        Surface.PointerMoved += OnPointer;
        Surface.PointerPressed += OnPointerPressed;
        Surface.PointerReleased += OnPointer;
        Surface.PointerCaptureLost += (_, _) => ReleaseButtons();
        Surface.PointerWheelChanged += OnWheel;
        // Tunnelling, and even if something above marked them handled: while the Mac's screen has
        // focus every key is the Mac's (no Tab focus moves, no Alt menu access, no accelerators).
        Surface.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        Surface.AddHandler(KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        Activated += (_, _) => SetActive(true);
        Deactivated += (_, _) => SetActive(false);
        ScalingChanged += (_, _) => UpdateView();
        PositionChanged += (_, _) => ScheduleRefit();
        Closed += OnClosed;
        _statsTimer.Tick += (_, _) => UpdateStats();
        _refitTimer.Tick += (_, _) => Refit();
        _floatingTimer.Tick += (_, _) => { _floatingTimer.Stop(); if (_fullScreen) FloatingBar.IsVisible = false; };
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); HideNotice(); };

        Opened += async (_, _) =>
        {
            UpdateView();
            if (_profile.Host.Length > 0) await ConnectAsync();
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // The window manager can take a window out of full screen itself (a desktop shortcut, say):
        // the toolbar comes back with it.
        if (change.Property == WindowStateProperty && _fullScreen && WindowState != WindowState.FullScreen)
        {
            _fullScreen = false;
            ApplyFullScreenChrome(false);
        }
    }

    DialogLayer IDialogHost.DialogLayer => Layer;

    private SessionInfo? Info => _connection?.Session.Info;

    private static App Vesper => App.Current;

    // ───────────────────────────── Connecting ─────────────────────────────

    private async Task ConnectAsync()
    {
        ShowConnecting();
        string? password = _password;
        _password = null;
        string? problem = null;
        if (password is null && _profile.HasSavedPassword && await UnlockVaultAsync())
        {
            switch (Vesper.Vault.TryGetPassword(_profile, out string saved))
            {
                case VaultLookup.Found:
                    password = saved;
                    _typedPassword = null;
                    break;
                case VaultLookup.Mismatch:
                    Diagnostics.Add($"The saved password for {_profile.Host} is sealed for different settings; not using it.");
                    problem = "This Mac's address, account or security settings changed since its password was saved, so Vesper won't send the saved password without asking. " +
                              "If you didn't change them in Vesper, something else on this computer did: check them before connecting.";
                    break;
            }
        }
        if (password is null)
        {
            _typedPassword = await AskPasswordAsync(problem);
            password = _typedPassword?.Password;
        }
        if (password is null)
        {
            ShowProblem("Not connected", "No password was entered.", retry: true);
            return;
        }
        if (_closing) return;

        _cts = new CancellationTokenSource();
        _promptedThisAttempt = false;
        var progress = new Progress<ConnectStage>(SetStage);
        try
        {
            _fitTarget = FitTarget();
            var connection = await Vesper.Connections.ConnectAsync(_profile, password, _fitTarget, this, progress, _cts.Token);
            if (_closing)
            {
                // The window closed while the last step finished: don't leave a session running.
                await connection.DisposeAsync();
                return;
            }
            _connection = connection;
        }
        catch (AuthenticationFailedException e) when (e.CredentialsRejected)
        {
            if (_closing) return;
            _typedPassword = await AskPasswordAsync(e.Message);
            if (_typedPassword is null)
            {
                ShowProblem("Not connected", e.Message, retry: true, edit: true);
                return;
            }
            _password = _typedPassword.Value.Password;
            await ConnectAsync();
            return;
        }
        catch (OperationCanceledException) when (_closing)
        {
            return;
        }
        catch (Exception e) when (_promptedThisAttempt && !_closing && e is ConnectionClosedException or IOException or TimeoutException)
        {
            // The Mac gave up on the half-finished sign-in while the trust prompt was being read.
            // The key was approved, so the next attempt goes straight through.
            Diagnostics.Add($"Connection to {_profile.Host} timed out during a trust prompt; retrying.");
            _password = password;
            await ConnectAsync();
            return;
        }
        catch (Exception e)
        {
            if (_closing) return;
            Diagnostics.Add($"Connect to {_profile.Host} failed: {e.GetType().Name}: {e.Message}" +
                (e is ConnectionRefusedException refused ? $" The Mac said: \"{refused.ServerReason}\"" : ""));
            bool stopped = e is IdentityRejectedException or SecurityRefusedException;
            ShowProblem(stopped ? "Connection stopped" : "Couldn't connect", Explain(e), retry: true, edit: e is not IdentityRejectedException);
            return;
        }
        finally
        {
            password = null;
        }
        OnConnected();
    }

    /// <summary>
    /// A typed password is remembered (or a saved one forgotten) only once the Mac has accepted it
    /// and proven itself: an impersonator can say "accepted" to anything.
    /// </summary>
    private async Task SaveTypedPasswordAsync()
    {
        if (_typedPassword is not { } typed) return;
        _typedPassword = null;
        if (typed.Remember is not { } remember) return; // nowhere to keep it on this computer
        try
        {
            if (remember && await UnlockVaultAsync())
            {
                Vesper.Vault.SetPassword(_profile, typed.Password);
                Remember(p => p.HasSavedPassword = true);
            }
            else if (!remember && _profile.HasSavedPassword)
            {
                if (await UnlockVaultAsync()) Vesper.Vault.Forget(_profile.Id);
                Remember(p => p.HasSavedPassword = false);
            }
        }
        catch (Exception e)
        {
            ShowNotice("The password couldn't be saved: " + e.Message, NoticeKind.Warning);
        }
    }

    private void OnConnected()
    {
        var connection = _connection!;
        var session = connection.Session;
        Diagnostics.Add($"Connected to {_profile.Host}: {session.Info.SignIn}, encrypted={session.Info.Encrypted}, Apple={session.Info.AppleExtensions}");
        session.CursorChanged += shape => Dispatcher.UIThread.Post(() => ApplyCursor(shape));
        session.LayoutChanged += layout => Dispatcher.UIThread.Post(() => OnLayout(layout));
        session.Resized += (_, _) => Dispatcher.UIThread.Post(UpdateTitle);
        session.ModelIdentified += model => Dispatcher.UIThread.Post(() =>
        {
            if (_profile.MacModel != model) Remember(p => p.MacModel = model);
        });
        session.Notice += text => Dispatcher.UIThread.Post(() => ShowNotice(text, NoticeKind.Info));

        // The screen, input and the security badge wait until the Mac's first encrypted message
        // has proven who's at the other end.
        session.Secured += () => Dispatcher.UIThread.Post(() =>
        {
            if (_connection == connection) OnSecured(connection);
        });
        _ = RunAsync(connection);
    }

    private void OnSecured(ConnectedMac connection)
    {
        var session = connection.Session;
        _secured = true;
        Surface.Attach(session.Framebuffer);
        _clipboard = new ClipboardBridge(session, _profile.ShareClipboard, () => Clipboard);
        _clipboard.ReceivedFromMac += chars => ShowNotice($"Copied from the Mac ({chars:N0} characters)", NoticeKind.Success, quiet: true);
        _clipboard.KeptPrivate += () => ShowNotice("Not shared with the Mac: the app you copied from marked it secret, as password managers do.", NoticeKind.Info, quiet: true);
        _clipboard.SetActive(_active);

        ShowSecurity(connection.Security, session.Info);
        Remember(p => p.LastConnected = DateTimeOffset.Now);
        Overlay.IsVisible = false;
        Surface.Focus();
        UpdateTitle();
        StatsText.IsVisible = Vesper.Data.Settings.ShowConnectionStats;
        _loggedCodec = "";
        _statsTimer.Start();
        if (_cursorShape is not null) ApplyCursor(_cursorShape);
        _ = SaveTypedPasswordAsync();
    }

    private async Task RunAsync(ConnectedMac connection)
    {
        string title = "Disconnected", reason = "The Mac closed the connection.";
        try
        {
            await connection.Session.RunAsync(_cts!.Token);
        }
        catch (OperationCanceledException) when (_closing)
        {
            return;
        }
        catch (Exception e)
        {
            if (e is SecurityRefusedException) title = "Connection stopped";
            reason = Explain(e, duringSession: true);
            Diagnostics.Add($"Session with {_profile.Host} ended: {e.GetType().Name}: {e.Message}");
        }
        if (_closing) return;
        await TearDownAsync();
        ShowProblem(title, reason, retry: true);
    }

    private async Task TearDownAsync()
    {
        _refitTimer.Stop();
        if (_connection is { } ended) Diagnostics.Add($"Picture from {_profile.Host}: {ended.Session.DescribePicture()}");
        ReleaseKeys();
        ReleaseButtons();
        _secured = false;
        _statsTimer.Stop();
        _clipboard?.Dispose();
        _clipboard = null;
        _cts?.Cancel();
        Surface.Clear(); // the last picture of the Mac doesn't linger in memory behind the overlay
        if (_connection is { } c)
        {
            _connection = null;
            await c.DisposeAsync();
        }
        Surface.Cursor = _cursors.Arrow;
    }

    private static string Explain(Exception e, bool duringSession = false) => e switch
    {
        IdentityRejectedException or AuthenticationFailedException or SecurityRefusedException or ConnectionRefusedException or TimeoutException => e.Message,
        ConnectionClosedException => "The Mac closed the connection. It may have been restarted, put to sleep, or had Screen Sharing turned off.",
        IOException or SocketException when duringSession =>
            "The connection to the Mac was lost. It may have gone to sleep, restarted, or left the network.",
        IOException { InnerException: SocketException } => e.Message, // already worded by the connector
        IOException => e.Message,
        SocketException se => $"Network error ({se.SocketErrorCode}).",
        ProtocolException => "The Mac sent something Vesper didn't expect, so Vesper stopped to be safe. (" + e.Message + ")",
        _ => e.Message,
    };

    private void ShowConnecting()
    {
        _steps.Clear();
        _steps.Add(new StepItem(ConnectStage.Reaching, $"Reaching {_profile.Host}"));
        if (_profile.Transport == TransportMode.Ssh) _steps.Add(new StepItem(ConnectStage.SecuringTunnel, "Opening the SSH tunnel"));
        _steps.Add(new StepItem(ConnectStage.VerifyingIdentity, "Checking the Mac's identity"));
        _steps.Add(new StepItem(ConnectStage.SigningIn, $"Signing in as {_profile.Username}"));
        _steps.Add(new StepItem(ConnectStage.Encrypting, "Encrypting the session"));
        _steps.Add(new StepItem(ConnectStage.Ready, "Bringing up the screen"));
        SetStage(ConnectStage.Reaching);
        OverlayTitle.Text = $"Connecting to {MacNames.DisplayName(_profile)}";
        OverlayMessage.IsVisible = false;
        StepsList.IsVisible = true;
        RetryButton.IsVisible = false;
        EditButton.IsVisible = false;
        CancelButton.Content = "Cancel";
        Overlay.IsVisible = true;
    }

    private void SetStage(ConnectStage stage)
    {
        foreach (var step in _steps)
        {
            step.State = step.Stage < stage ? StepState.Done : step.Stage == stage ? StepState.Active : StepState.Pending;
        }
    }

    private void ShowProblem(string title, string message, bool retry, bool edit = false)
    {
        OverlayTitle.Text = title;
        OverlayMessage.Text = message;
        OverlayMessage.IsVisible = true;
        StepsList.IsVisible = false;
        RetryButton.IsVisible = retry;
        EditButton.IsVisible = edit;
        CancelButton.Content = "Close";
        Overlay.IsVisible = true;
        SecurityPill.IsVisible = false;
        StatsText.IsVisible = false;
        if (_fullScreen) SetFullScreen(false);
        (retry ? RetryButton : CancelButton).Focus();
    }

    private async void Retry_Click(object? sender, RoutedEventArgs e) => await ConnectAsync();

    private async void EditMac_Click(object? sender, RoutedEventArgs e)
    {
        if (await MacEditor.ShowAsync(this, _profile) is not { } edit) return;
        var before = _profile.Clone();
        CopyProfile(edit.Profile, _profile);
        try
        {
            await SavedPasswords.ApplyEditAsync(Vesper.Vault, before, _profile, edit.Password, edit.RememberPassword, UnlockVaultAsync);
        }
        catch (Exception ex)
        {
            ShowNotice("The password couldn't be saved: " + ex.Message, NoticeKind.Warning);
        }
        if (edit.Password is { Length: > 0 } pw) _password = pw;
        _typedPassword = null;
        Vesper.Data.Upsert(_profile);
        MacName.Text = MacNames.DisplayName(_profile);
        FloatingName.Text = MacName.Text;
        _keys.Layout = _profile.Modifiers;
        Surface.Scaling = _profile.Scaling;
        ClipboardToggle.IsChecked = _profile.ShareClipboard;
        await ConnectAsync();
    }

    private static void CopyProfile(MacProfile from, MacProfile to)
    {
        foreach (var property in typeof(MacProfile).GetProperties().Where(p => p.CanWrite))
        {
            property.SetValue(to, property.GetValue(from));
        }
    }

    // ───────────────────────────── Passwords and trust ─────────────────────────────

    private async Task<bool> UnlockVaultAsync()
    {
        try
        {
            return await Vesper.Vault.UnlockAsync();
        }
        catch (Exception e)
        {
            ShowNotice(e.Message, NoticeKind.Warning);
            return false;
        }
    }

    /// <summary>
    /// Asks for the password. Whether to remember it is decided here but applied only after the
    /// Mac proves itself; null "remember" means this computer can't save it, so nothing changes.
    /// </summary>
    private async Task<(string Password, bool? Remember)?> AskPasswordAsync(string? problem)
    {
        bool canSave = Vesper.Vault.CanSave;
        var password = new TextBox
        {
            PasswordChar = '•',
            Classes = { "revealPasswordButton" },
            PlaceholderText = "Password",
        };
        var remember = new CheckBox { Content = "Remember on this computer (encrypted)", IsChecked = _profile.HasSavedPassword || problem is null };
        var panel = new StackPanel { Spacing = 14 };
        if (problem is not null) panel.Children.Add(InlineNotice(problem, NoticeKind.Error));
        panel.Children.Add(new TextBlock
        {
            Text = $"Sign in to {MacNames.DisplayName(_profile)} ({_profile.Host}) with your Mac account.",
            TextWrapping = TextWrapping.Wrap,
            Classes = { "secondary" },
        });
        panel.Children.Add(new StackPanel
        {
            Spacing = 6,
            Children = { new TextBlock { Text = $"Password for {_profile.Username}" }, password },
        });
        if (canSave) panel.Children.Add(remember);
        else panel.Children.Add(new TextBlock { Text = Vesper.Vault.UnavailableReason, TextWrapping = TextWrapping.Wrap, FontSize = 12, Classes = { "secondary" } });

        var dialog = new Dialog
        {
            Title = "Sign in",
            Content = panel,
            ContentWidth = 380,
            PrimaryButtonText = "Connect",
            CloseButtonText = "Cancel",
            DefaultButton = DialogButton.Primary,
            InitialFocus = password,
        };
        var result = await dialog.ShowAsync(this);
        string typed = password.Text ?? "";
        password.Text = "";
        if (result != DialogResult.Primary || typed.Length == 0) return null;
        return (typed, canSave ? remember.IsChecked == true : null);
    }

    // A key approved in this window counts as approved for the rest of its life: a Mac that gave up
    // while the prompt was open gets the retry without asking twice. It's pinned only once the
    // Mac proves it holds the key.

    Task<bool> ITrustPrompts.TrustMacIdentityAsync(MacProfile profile, MacIdentity identity, string? previous) =>
        OnUiAsync(async () =>
        {
            if (identity.Fingerprint == _approvedIdentity) return true;
            _promptedThisAttempt = true;
            bool trusted = await TrustDialogAsync(
                previous is null ? $"First connection to {MacNames.DisplayName(profile)}" : "This Mac's identity has changed",
                previous is null
                    ? "Vesper hasn't met this Mac before. It will remember the Mac's identity key and warn you if it ever changes, so nobody can quietly pretend to be your Mac later. If you can, check this fingerprint against one you trust."
                    : $"The identity key {profile.Host} presented doesn't match the one you trusted. That happens after reinstalling macOS or resetting Screen Sharing, but it's also exactly what someone intercepting the connection would look like. If you didn't expect it, disconnect.",
                $"RSA {identity.KeySizeBits}-bit key",
                identity.Fingerprint, previous, verifyCommand: null);
            if (trusted) _approvedIdentity = identity.Fingerprint;
            return trusted;
        });

    Task<bool> ITrustPrompts.TrustSshHostKeyAsync(MacProfile profile, SshHostKey key, string? previous) =>
        OnUiAsync(async () =>
        {
            if (key.Fingerprint == _approvedSshKey) return true;
            _promptedThisAttempt = true;
            bool trusted = await TrustDialogAsync(
                previous is null ? $"First SSH connection to {MacNames.DisplayName(profile)}" : "This Mac's SSH host key has changed",
                previous is null
                    ? "Vesper will pin this SSH host key and refuse to send your password if it ever changes. To be certain it's your Mac, compare it with what Terminal on the Mac prints for:"
                    : $"The SSH host key {profile.Host} presented doesn't match the one you trusted. Unless you reinstalled macOS or regenerated its SSH keys, someone may be intercepting the connection. Compare it with what Terminal on the Mac prints for:",
                $"{key.FriendlyAlgorithm} host key",
                key.Fingerprint, previous, verifyCommand: key.VerifyCommand);
            if (trusted) _approvedSshKey = key.Fingerprint;
            return trusted;
        });

    private async Task<bool> TrustDialogAsync(string title, string body, string keyKind, string fingerprint, string? previous, string? verifyCommand)
    {
        if (_closing) return false;
        bool changed = previous is not null;
        var panel = new StackPanel { Spacing = 14 };
        if (changed) panel.Children.Add(InlineNotice("Vesper hasn't sent your password.", NoticeKind.Error, "Possible interception"));
        panel.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap });
        if (verifyCommand is not null)
        {
            panel.Children.Add(new Border { Classes = { "mono-box" }, Child = Mono(verifyCommand) });
        }
        var keyBox = new StackPanel { Spacing = 4 };
        keyBox.Children.Add(new TextBlock { Text = keyKind, FontSize = 12, Classes = { "secondary" } });
        keyBox.Children.Add(Mono(fingerprint));
        if (changed)
        {
            keyBox.Children.Add(new TextBlock { Text = "Previously trusted", FontSize = 12, Margin = new Thickness(0, 8, 0, 0), Classes = { "secondary" } });
            keyBox.Children.Add(Mono(previous!));
        }
        panel.Children.Add(new Border { Classes = { "mono-box" }, Child = keyBox });
        CheckBox? understand = null;
        if (changed)
        {
            understand = new CheckBox { Content = "I know why this changed" };
            panel.Children.Add(understand);
        }
        var dialog = new Dialog
        {
            Title = title,
            Content = panel,
            ContentWidth = 460,
            PrimaryButtonText = changed ? "Trust the new key" : "Trust and connect",
            CloseButtonText = changed ? "Disconnect" : "Cancel",
            // A changed key defaults to the safe answer, and can't be trusted without the tick.
            DefaultButton = changed ? DialogButton.Close : DialogButton.Primary,
            IsPrimaryButtonEnabled = !changed,
        };
        if (understand is not null)
        {
            understand.IsCheckedChanged += (_, _) => dialog.IsPrimaryButtonEnabled = understand.IsChecked == true;
        }
        return await dialog.ShowAsync(this) == DialogResult.Primary;

        static SelectableTextBlock Mono(string text) => new()
        {
            Text = text,
            Classes = { "mono" },
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        };
    }

    private static Border InlineNotice(string message, NoticeKind kind, string? heading = null)
    {
        var text = new StackPanel { Spacing = 2 };
        if (heading is not null) text.Children.Add(new TextBlock { Text = heading, FontWeight = FontWeight.SemiBold });
        text.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        var icon = new Icon { Name = "NoticeIcon", Data = IconFor(kind), Size = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var border = new Border { Classes = { "notice", "inline" }, Child = grid };
        border.Classes.Add(ClassFor(kind));
        return border;
    }

    private Task<T> OnUiAsync<T>(Func<Task<T>> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                tcs.SetResult(await work());
            }
            catch (Exception e)
            {
                tcs.SetException(e);
            }
        });
        return tcs.Task;
    }

    private void Remember(Action<MacProfile> change)
    {
        change(_profile);
        if (Vesper.Data.Find(_profile.Id) is { } stored)
        {
            if (!ReferenceEquals(stored, _profile)) change(stored);
            Vesper.Data.SaveProfiles();
        }
    }

    // ───────────────────────────── Security indicator ─────────────────────────────

    private void ShowSecurity(SecuritySummary security, SessionInfo info)
    {
        var (icon, tone) = security.Level switch
        {
            SecurityLevel.Verified => (Icons.Lock, "verified"),
            SecurityLevel.Protected => (Icons.Lock, "protected"),
            SecurityLevel.Unverified => (Icons.ShieldAlert, "warning"),
            _ => (Icons.Warning, "danger"),
        };
        foreach (var target in new StyledElement[] { SecurityIcon, FloatingSecurityIcon, SecurityText })
        {
            target.Classes.Remove("verified");
            target.Classes.Remove("protected");
            target.Classes.Remove("warning");
            target.Classes.Remove("danger");
            target.Classes.Add(tone);
        }
        SecurityIcon.Data = icon;
        FloatingSecurityIcon.Data = icon;
        SecurityText.Text = security.Headline;
        SecurityFlyoutTitle.Text = security.Headline;
        SecurityFlyoutDetail.Text = security.Detail;
        SecurityFlyoutIdentity.Text = info.Identity is { } id ? $"Mac identity\n{id.Fingerprint}" : "";
        SecurityFlyoutIdentity.IsVisible = info.Identity is not null;
        SecurityPill.IsVisible = true;
        if (security.Level == SecurityLevel.Unverified)
        {
            ShowNotice("This connection is encrypted, but the Mac's identity can't be verified on this network. Consider an SSH tunnel or Tailscale.", NoticeKind.Warning);
        }
        else if (security.Level == SecurityLevel.Unencrypted)
        {
            ShowNotice(security.Detail, NoticeKind.Error);
        }
    }

    // ───────────────────────────── Screen and view ─────────────────────────────

    private void UpdateView()
    {
        if (_cursorShape is not null) ApplyCursor(_cursorShape);
        ScheduleRefit();
    }

    private void UpdateTitle()
    {
        if (_connection is null) return;
        var fb = _connection.Session.Framebuffer;
        Title = $"{MacNames.DisplayName(_profile)} — {fb.Width}×{fb.Height} — Vesper";
    }

    private void OnLayout(AppleDisplayLayout layout)
    {
        UpdateTitle();
        // The first layout comes at native size; wait for the one with the scale Vesper asked for.
        if (_connection?.Session.ScalePending != true) SizeToMac(layout.FramebufferWidth, layout.FramebufferHeight);
        DisplaysMenu.Items.Clear();
        bool many = layout.Displays.Count > 1;
        DisplaysMenu.IsVisible = many;
        DisplaysSeparator.IsVisible = many;
        if (!many) return;
        var all = new MenuItem { Header = "All displays", ToggleType = MenuItemToggleType.Radio, GroupName = "display", IsChecked = layout.CurrentDisplay is null };
        all.Click += (_, _) => _connection?.Session.SelectDisplay(null);
        DisplaysMenu.Items.Add(all);
        foreach (var display in layout.Displays)
        {
            var item = new MenuItem
            {
                Header = $"{display.Label}{(display.IsMain ? " (main)" : "")} — {display.PointsWidth}×{display.PointsHeight}",
                ToggleType = MenuItemToggleType.Radio,
                GroupName = "display",
                IsChecked = layout.CurrentDisplay == display.Id,
            };
            uint id = display.Id;
            item.Click += (_, _) => _connection?.Session.SelectDisplay(id);
            DisplaysMenu.Items.Add(item);
        }
    }

    /// <summary>
    /// Opens the window at the Mac's own shape: one framebuffer pixel per screen pixel when that
    /// fits (the sharpest text), otherwise as large as fits in 90% of the work area. Only once per
    /// window, so a size the user chooses is left alone.
    /// </summary>
    private void SizeToMac(int fbWidth, int fbHeight)
    {
        if (_sizedToMac || _fullScreen || fbWidth <= 0 || fbHeight <= 0) return;
        if (WindowState != WindowState.Normal) return;
        if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is not { } screen) return;
        _sizedToMac = true;
        var area = screen.WorkingArea;
        double scaling = RenderScaling;
        // The window manager's frame, where it says (X11 frame extents); otherwise none.
        var frame = FrameSize is { } f && f.Width >= ClientSize.Width ? f : ClientSize;
        double chromeW = (frame.Width - ClientSize.Width) * scaling;
        double chromeH = (frame.Height - ClientSize.Height) * scaling + ToolbarHeight * scaling;
        double scale = Math.Min(1.0, Math.Min((area.Width * 0.9 - chromeW) / fbWidth, (area.Height * 0.9 - chromeH) / fbHeight));
        double clientW = Math.Round(fbWidth * scale), clientH = Math.Round(fbHeight * scale) + ToolbarHeight * scaling;
        Width = clientW / scaling;
        Height = clientH / scaling;
        double outerW = clientW + chromeW, outerH = clientH + (frame.Height - ClientSize.Height) * scaling;
        Position = new PixelPoint(area.X + (int)((area.Width - outerW) / 2), area.Y + (int)((area.Height - outerH) / 2));
    }

    private void ApplyCursor(CursorShape shape)
    {
        _cursorShape = shape;
        if (_connection is null || !_secured) return; // shown from the moment the Mac is proven
        var fb = _connection.Session.Framebuffer;
        var place = Surface.CurrentPlacement(fb.Width, fb.Height);
        double screenPixelsPerFramebufferPixel = fb.Width > 0 ? place.Width * Surface.RenderScale / fb.Width : 1;
        double pixelsPerPoint = screenPixelsPerFramebufferPixel * _connection.Session.EffectiveDensity;
        Surface.Cursor = _cursors.For(shape, pixelsPerPoint);
    }

    private void ViewMenu_Opening(object? sender, EventArgs e)
    {
        FitItem.IsChecked = _profile.Scaling == ViewScaling.Fit;
        ActualItem.IsChecked = _profile.Scaling == ViewScaling.ActualSize;
        MatchItem.IsChecked = _profile.Resolution == ResolutionMode.MatchThisDisplay;
        NativeItem.IsChecked = _profile.Resolution == ResolutionMode.Native;
        var color = _connection?.Session.Color ?? _profile.Quality;
        AdaptiveItem.IsChecked = color == ColorQuality.Adaptive;
        FullColorItem.IsChecked = color == ColorQuality.Full;
        ThousandsItem.IsChecked = color == ColorQuality.Thousands;
        GrayItem.IsChecked = color == ColorQuality.Grayscale;
    }

    private void Scaling_Click(object? sender, RoutedEventArgs e)
    {
        var scaling = (string?)((Control)sender!).Tag == "ActualSize" ? ViewScaling.ActualSize : ViewScaling.Fit;
        Remember(p => p.Scaling = scaling);
        Surface.Scaling = scaling;
        if (_cursorShape is not null) ApplyCursor(_cursorShape);
        ScheduleRefit();
        FocusSurface();
    }

    private void Resolution_Click(object? sender, RoutedEventArgs e)
    {
        var mode = (string?)((Control)sender!).Tag == "Native" ? ResolutionMode.Native : ResolutionMode.MatchThisDisplay;
        Remember(p => p.Resolution = mode);
        _fitTarget = FitTarget();
        _connection?.Session.FitToPixels(mode == ResolutionMode.MatchThisDisplay ? _fitTarget : null);
        FocusSurface();
    }

    /// <summary>
    /// What "match this display" asks the Mac to fill, in physical pixels. Fitted to the window, it's
    /// the picture area itself, so the picture is shown one pixel per pixel at any window size and
    /// nothing is resampled (shrinking even 4% visibly softens text). At 1:1 it's the screen, so
    /// there's detail to pan around.
    /// </summary>
    private (int Width, int Height) FitTarget()
    {
        double scale = Surface.RenderScale;
        if (_profile.Scaling == ViewScaling.ActualSize || Surface.Bounds.Width < 1 || Surface.Bounds.Height < 1)
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            var bounds = screen?.Bounds ?? new Avalonia.PixelRect(0, 0, 1920, 1080);
            return (Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
        }
        // A window made tiny for a moment shouldn't make the Mac render a postage stamp.
        return (Math.Max(320, (int)Math.Round(Surface.Bounds.Width * scale)),
            Math.Max(200, (int)Math.Round(Surface.Bounds.Height * scale)));
    }

    /// <summary>
    /// Asks the Mac for a picture of the new size once the window has settled: every new size
    /// costs the Mac a fresh layout and a repaint, so a drag-resize asks once, at the end.
    /// </summary>
    private void ScheduleRefit()
    {
        _refitTimer.Stop();
        _refitTimer.Start();
    }

    private void Refit()
    {
        _refitTimer.Stop();
        if (_connection is not { } c || _profile.Resolution != ResolutionMode.MatchThisDisplay) return;
        var target = FitTarget();
        if (target == _fitTarget) return;
        _fitTarget = target;
        c.Session.FitToPixels(target);
    }

    private void Color_Click(object? sender, RoutedEventArgs e)
    {
        var color = Enum.Parse<ColorQuality>((string)((Control)sender!).Tag!);
        Remember(p => p.Quality = color);
        _connection?.Session.SetColorQuality(color);
        FocusSurface();
    }

    // ───────────────────────────── Pointer ─────────────────────────────

    private bool ToFramebuffer(Point position, out int x, out int y)
    {
        x = y = 0;
        if (_connection is null) return false;
        var fb = _connection.Session.Framebuffer;
        var place = Surface.CurrentPlacement(fb.Width, fb.Height);
        if (place.Width <= 0 || place.Height <= 0) return false;
        x = (int)Math.Clamp(Math.Floor((position.X - place.X) / place.Width * fb.Width), 0, fb.Width - 1);
        y = (int)Math.Clamp(Math.Floor((position.Y - place.Y) / place.Height * fb.Height), 0, fb.Height - 1);

        // In 1:1 mode a screen larger than the window pans as the pointer nears an edge.
        double viewW = Surface.Bounds.Width, viewH = Surface.Bounds.Height, scale = Surface.RenderScale;
        if (Surface.Scaling == ViewScaling.ActualSize && (place.Width > viewW || place.Height > viewH))
        {
            double fx = Math.Clamp(position.X / viewW, 0, 1), fy = Math.Clamp(position.Y / viewH, 0, 1);
            Surface.Pan = new Point(fx * Math.Max(0, fb.Width - viewW * scale), fy * Math.Max(0, fb.Height - viewH * scale));
        }
        return true;
    }

    private static PointerButtons Buttons(PointerPointProperties p) =>
        (p.IsLeftButtonPressed ? PointerButtons.Left : 0)
        | (p.IsRightButtonPressed ? PointerButtons.Right : 0)
        | (p.IsMiddleButtonPressed ? PointerButtons.Middle : 0);

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Surface.Focus(NavigationMethod.Pointer);
        e.Pointer.Capture(Surface);
        OnPointer(sender, e);
    }

    private void OnPointer(object? sender, PointerEventArgs e)
    {
        var point = e.GetCurrentPoint(Surface);
        if (_fullScreen && point.Position.Y < 4) RevealFloatingBar();
        if (_connection is null || !_secured || !ToFramebuffer(point.Position, out int x, out int y)) return;
        _lastButtons = Buttons(point.Properties);
        (_lastX, _lastY) = (x, y);
        _connection.Session.SendPointer(x, y, _lastButtons);
        e.Handled = true;
    }

    /// <summary>A lost capture (another window grabbed the mouse) lets go of the buttons on the Mac too.</summary>
    private void ReleaseButtons()
    {
        if (_lastButtons == PointerButtons.None || _connection is null || !_secured) return;
        _lastButtons = PointerButtons.None;
        _connection.Session.SendPointer(_lastX, _lastY, PointerButtons.None);
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_connection is null || !_secured) return;
        var point = e.GetCurrentPoint(Surface);
        var buttons = Buttons(point.Properties);
        // A notch (Delta 1) scrolls about 40 points; touchpads send smaller steps for smooth scrolling.
        double speed = Vesper.Data.Settings.ScrollSpeed;
        if (ToFramebuffer(point.Position, out int x, out int y))
        {
            _connection.Session.SendPointer(x, y, buttons);
        }
        if (e.Delta.X != 0) _connection.Session.SendScroll(-e.Delta.X * 40 * speed, 0, buttons);
        if (e.Delta.Y != 0) _connection.Session.SendScroll(0, -e.Delta.Y * 40 * speed, buttons);
        e.Handled = true;
    }

    // ───────────────────────────── Keyboard ─────────────────────────────

    private void SetActive(bool active)
    {
        _active = active;
        if (!active) ReleaseKeys();
        _clipboard?.SetActive(active);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Route == RoutingStrategies.Bubble && e.Handled) return; // seen on the way down already
        if (_connection is null || !_secured || Layer.HasOpenDialog) return;
        e.Handled = true;
        if (HandleLocalShortcut(e)) return;
        Send(e.PhysicalKey, e.Key, e.KeySymbol, down: true);
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Route == RoutingStrategies.Bubble && e.Handled) return;
        if (_connection is null || !_secured) return;
        e.Handled = true;
        Send(e.PhysicalKey, e.Key, e.KeySymbol, down: false);
    }

    /// <summary>Ctrl+Alt+Enter toggles full screen, here rather than on the Mac.</summary>
    private bool HandleLocalShortcut(KeyEventArgs e)
    {
        if (!_keys.CtrlDown || !_keys.AltDown) return false;
        if (e.PhysicalKey is PhysicalKey.Enter or PhysicalKey.NumPadEnter || e.Key == Key.Enter)
        {
            ReleaseKeys();
            Dispatcher.UIThread.Post(() => SetFullScreen(!_fullScreen));
            return true;
        }
        return false;
    }

    private void Send(PhysicalKey physical, Key key, string? symbol, bool down)
    {
        _keyBuffer.Clear();
        _keys.Translate(physical, key, symbol, down, _keyBuffer);
        if (_connection is not { } c) return;
        foreach (var (keysym, isDown) in _keyBuffer) c.Session.SendKey(keysym, isDown);
    }

    /// <summary>Lets go of everything the Mac thinks is held, so nothing sticks when focus moves away.</summary>
    private void ReleaseKeys()
    {
        _keyBuffer.Clear();
        _keys.ReleaseAll(_keyBuffer);
        if (_connection is not { } c) return;
        foreach (var (keysym, isDown) in _keyBuffer) c.Session.SendKey(keysym, isDown);
    }

    private void KeysMenu_Opening(object? sender, EventArgs e) => MacStyleItem.IsChecked = _keys.Layout == ModifierLayout.MacStyle;

    private void Shortcut_Click(object? sender, RoutedEventArgs e)
    {
        if (_connection is not { } c) return;
        const uint Cmd = 0xFFEB, Ctrl = 0xFFE3, Opt = 0xFFE7, Shift = 0xFFE1;
        uint[] combo = (string?)((Control)sender!).Tag switch
        {
            "spotlight" => [Cmd, ' '],
            "apps" => [Cmd, 0xFF09],
            "mission" => [Ctrl, 0xFF52],
            "forcequit" => [Opt, Cmd, 0xFF1B],
            "screenshot" => [Shift, Cmd, '4'],
            "lock" => [Ctrl, Cmd, 'q'],
            _ => [],
        };
        foreach (uint k in combo) c.Session.SendKey(k, true);
        foreach (uint k in combo.Reverse()) c.Session.SendKey(k, false);
        FocusSurface();
    }

    private void MacStyle_Click(object? sender, RoutedEventArgs e)
    {
        ReleaseKeys();
        _keys.Layout = MacStyleItem.IsChecked ? ModifierLayout.MacStyle : ModifierLayout.Literal;
        Remember(p => p.Modifiers = _keys.Layout);
        FocusSurface();
    }

    // ───────────────────────────── Toolbar ─────────────────────────────

    private void FocusSurface()
    {
        if (_connection is not null && !Overlay.IsVisible) Dispatcher.UIThread.Post(() => Surface.Focus(), DispatcherPriority.Input);
    }

    private void Clipboard_Click(object? sender, RoutedEventArgs e)
    {
        bool on = ClipboardToggle.IsChecked == true;
        Remember(p => p.ShareClipboard = on);
        if (_clipboard is not null) _clipboard.Enabled = on;
        ShowNotice(on ? "Clipboard sharing is on, and only works while this window is in front." : "Clipboard sharing is off.",
            NoticeKind.Info, quiet: true);
        FocusSurface();
    }

    private void FullScreen_Click(object? sender, RoutedEventArgs e) => SetFullScreen(!_fullScreen);

    private void SetFullScreen(bool on)
    {
        if (on == _fullScreen) return;
        _fullScreen = on;
        if (on)
        {
            _stateBeforeFullScreen = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState;
            WindowState = WindowState.FullScreen;
        }
        else
        {
            WindowState = _stateBeforeFullScreen;
        }
        ApplyFullScreenChrome(on);
    }

    private void ApplyFullScreenChrome(bool on)
    {
        Root.RowDefinitions[0].Height = new GridLength(on ? 0 : ToolbarHeight);
        TitleBarArea.IsVisible = !on;
        FullScreenIcon.Data = on ? Icons.ExitFullScreen : Icons.FullScreen;
        if (on) RevealFloatingBar();
        else FloatingBar.IsVisible = false;
        ReleaseKeys();
        FocusSurface();
    }

    private void RevealFloatingBar()
    {
        FloatingBar.IsVisible = true;
        _floatingTimer.Stop();
        _floatingTimer.Start();
    }

    private void FloatingBar_PointerExited(object? sender, PointerEventArgs e)
    {
        _floatingTimer.Stop();
        _floatingTimer.Start();
    }

    private void Disconnect_Click(object? sender, RoutedEventArgs e) => Close();

    // ───────────────────────────── Notices ─────────────────────────────

    private static Geometry IconFor(NoticeKind kind) => kind switch
    {
        NoticeKind.Success => Icons.Success,
        NoticeKind.Warning => Icons.Warning,
        NoticeKind.Error => Icons.Error,
        _ => Icons.Info,
    };

    private static string ClassFor(NoticeKind kind) => kind switch
    {
        NoticeKind.Success => "success",
        NoticeKind.Warning => "warning",
        NoticeKind.Error => "error",
        _ => "info",
    };

    private void ShowNotice(string message, NoticeKind kind, bool quiet = false)
    {
        Notice.Classes.Remove("success");
        Notice.Classes.Remove("warning");
        Notice.Classes.Remove("error");
        Notice.Classes.Remove("info");
        Notice.Classes.Add(ClassFor(kind));
        NoticeIcon.Data = IconFor(kind);
        NoticeText.Text = message;
        Notice.IsVisible = true;
        Notice.Opacity = 1;
        _noticeTimer.Stop();
        // Warnings and errors stay until closed; the rest fade.
        if (quiet || kind is not (NoticeKind.Warning or NoticeKind.Error))
        {
            _noticeTimer.Interval = TimeSpan.FromSeconds(quiet ? 3 : 6);
            _noticeTimer.Start();
        }
    }

    private void HideNotice()
    {
        Notice.Opacity = 0;
        DispatcherTimer.RunOnce(() =>
        {
            if (Notice.Opacity == 0) Notice.IsVisible = false;
        }, TimeSpan.FromMilliseconds(250), DispatcherPriority.Background);
    }

    private void NoticeClose_Click(object? sender, RoutedEventArgs e)
    {
        _noticeTimer.Stop();
        HideNotice();
        FocusSurface();
    }

    private void UpdateStats()
    {
        if (_connection is null) return;
        var stats = _connection.Session.Stats;
        if (stats.Codec != _loggedCodec)
        {
            // Which codec the Mac chose is the first question when a session is slow.
            _loggedCodec = stats.Codec;
            Diagnostics.Add($"Picture from {_profile.Host}: {stats.Codec}, {_connection.Session.Framebuffer.Width}×{_connection.Session.Framebuffer.Height}");
        }
        if (!StatsText.IsVisible) return;
        string rtt = TcpRtt.Milliseconds(_connection.Socket) switch
        {
            double ms when ms < 1 => " · <1 ms",
            double ms => $" · {ms:0} ms",
            _ => "",
        };
        string codec = stats.Codec.Length > 0 ? $" · {stats.Codec}" : "";
        StatsText.Text = $"{Surface.FramesPerSecond:0} fps · {stats.UpdatesPerSecond:0} upd/s · {stats.MegabitsPerSecond:0.0} Mb/s{rtt}{codec}";
    }

#if DEBUG
    /// <summary>Development builds only: shows a state that otherwise needs clicking (see DevHooks).</summary>
    internal void DevShow(string what)
    {
        switch (what)
        {
            case "fullscreen":
                SetFullScreen(true);
                break;
            case "notice":
                ShowNotice("Clipboard sharing is on, and only works while this window is in front.", NoticeKind.Info);
                break;
            case "menu":
                DispatcherTimer.RunOnce(() => ViewButton.Flyout?.ShowAt(ViewButton), TimeSpan.FromSeconds(1));
                break;
            case "security":
                DispatcherTimer.RunOnce(() => SecurityPill.Flyout?.ShowAt(SecurityPill), TimeSpan.FromSeconds(1));
                break;
            case "typing":
                // Key events raised inside this window only (no system input): checks the routing
                // and mapping end to end against FakeMac.
                Surface.Focus();
                Key(PhysicalKey.ShiftLeft, Avalonia.Input.Key.LeftShift, null, true);
                Key(PhysicalKey.H, Avalonia.Input.Key.H, "H", true);
                Key(PhysicalKey.H, Avalonia.Input.Key.H, "H", false);
                Key(PhysicalKey.ShiftLeft, Avalonia.Input.Key.LeftShift, null, false);
                Key(PhysicalKey.I, Avalonia.Input.Key.I, "i", true);
                Key(PhysicalKey.I, Avalonia.Input.Key.I, "i", false);
                Key(PhysicalKey.Tab, Avalonia.Input.Key.Tab, "\t", true);
                Key(PhysicalKey.Tab, Avalonia.Input.Key.Tab, "\t", false);
                Key(PhysicalKey.Digit4, Avalonia.Input.Key.D4, "€", true);
                Key(PhysicalKey.Digit4, Avalonia.Input.Key.D4, "€", false);
                Key(PhysicalKey.ControlLeft, Avalonia.Input.Key.LeftCtrl, null, true);
                break;
            case "fullscreen-keys":
                Surface.Focus();
                Key(PhysicalKey.ControlLeft, Avalonia.Input.Key.LeftCtrl, null, true);
                Key(PhysicalKey.AltLeft, Avalonia.Input.Key.LeftAlt, null, true);
                Key(PhysicalKey.Enter, Avalonia.Input.Key.Enter, "\r", true);
                Key(PhysicalKey.Enter, Avalonia.Input.Key.Enter, "\r", false);
                Key(PhysicalKey.AltLeft, Avalonia.Input.Key.LeftAlt, null, false);
                Key(PhysicalKey.ControlLeft, Avalonia.Input.Key.LeftCtrl, null, false);
                break;
        }

        void Key(PhysicalKey physical, Key key, string? symbol, bool down) => Surface.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = down ? KeyDownEvent : KeyUpEvent,
            PhysicalKey = physical,
            Key = key,
            KeySymbol = symbol,
            Source = Surface,
        });
    }
#endif

    private async void OnClosed(object? sender, EventArgs e)
    {
        _closing = true;
        _floatingTimer.Stop();
        _noticeTimer.Stop();
        Layer.CloseAll();
        await TearDownAsync();
        Surface.Clear();
        _cursors.Dispose();
        App.Current.SessionClosed(this);
    }
}

public enum StepState { Pending, Active, Done }

/// <summary>One line of the connecting screen.</summary>
public sealed class StepItem(ConnectStage stage, string text) : INotifyPropertyChanged
{
    private StepState _state;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ConnectStage Stage { get; } = stage;
    public string Text { get; } = text;

    public StepState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            Changed(nameof(IsActive));
            Changed(nameof(IsDone));
            Changed(nameof(IsPending));
            Changed(nameof(Opacity));
        }
    }

    public bool IsActive => _state == StepState.Active;
    public bool IsDone => _state == StepState.Done;
    public bool IsPending => _state == StepState.Pending;
    public double Opacity => _state == StepState.Pending ? 0.5 : 1.0;

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
