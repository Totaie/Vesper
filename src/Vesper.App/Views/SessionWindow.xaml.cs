using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Vesper.Core.Rfb;
using Vesper.Core.Transport;
using Vesper.Interop;
using Vesper.Models;
using Vesper.Rendering;
using Vesper.Services;
using Windows.Foundation;
using Windows.System;

namespace Vesper.Views;

/// <summary>One Mac on screen: connection, drawing, input, and the controls around it.</summary>
public sealed partial class SessionWindow : Window, ITrustPrompts
{
    private readonly MacProfile _profile;
    private readonly ObservableCollection<StepItem> _steps = [];
    private readonly SessionRenderer _renderer;
    private readonly CursorFactory _cursors = new();
    private readonly KeyMapper _keys = new();
    private readonly KeyboardHook _hook = new();
    private readonly List<(uint Keysym, bool Down)> _keyBuffer = [];
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _floatingTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly nint _hwnd;
    private string? _password;
    private ConnectedMac? _connection;
    private ClipboardBridge? _clipboard;
    private CancellationTokenSource? _cts;
    private bool _fullScreen;
    private bool _surfaceFocused;
    private bool _active;
    private bool _closing;
    private bool _captureShortcutsForSession;
    private CursorShape? _cursorShape;
    private double _rasterScale = 1;
    private bool _promptedThisAttempt;

    internal static void Open(MacProfile profile, string? password)
    {
        var existing = App.Current.Sessions.FirstOrDefault(s => s._profile.Id == profile.Id);
        if (existing is not null)
        {
            existing.Activate();
            return;
        }
        var window = new SessionWindow(profile, password);
        App.Current.SessionOpened(window);
        window.Activate();
    }

    private SessionWindow(MacProfile profile, string? password)
    {
        InitializeComponent();
        _profile = profile;
        _password = password;
        _hwnd = WindowChrome.Handle(this);
        WindowChrome.Prepare(this, DragRegion, width: 1440, height: 900, minWidth: 640, minHeight: 420);
        AppWindow.Title = $"{MacCardItem.DisplayName(profile)} — Vesper";
        MacName.Text = MacCardItem.DisplayName(profile);
        FloatingName.Text = MacName.Text;
        StepsList.ItemsSource = _steps;
        ApplyTheme();

        _renderer = new SessionRenderer(Surface.Panel);
        _renderer.SetScaling(profile.Scaling);
        _keys.Layout = profile.Modifiers;
        _hook.Handler = OnHookKey;

        ApplyCaptureProtection(App.Current.Data.Settings.HideSessionsFromCapture);
        ClipboardToggle.IsChecked = profile.ShareClipboard;

        Surface.SizeChanged += (_, _) => UpdateView();
        Surface.Panel.CompositionScaleChanged += (_, _) => UpdateView();
        Surface.GotFocus += (_, _) => { _surfaceFocused = true; UpdateHook(); };
        Surface.LostFocus += (_, _) => { _surfaceFocused = false; ReleaseKeys(); UpdateHook(); };
        Surface.PointerMoved += OnPointer;
        Surface.PointerPressed += OnPointerPressed;
        Surface.PointerReleased += OnPointer;
        Surface.PointerCaptureLost += OnPointer;
        Surface.PointerWheelChanged += OnWheel;
        Surface.PreviewKeyDown += OnKeyDown;
        Surface.PreviewKeyUp += OnKeyUp;
        Surface.ProcessKeyboardAccelerators += (_, e) => e.Handled = true;

        Activated += OnActivated;
        Closed += OnClosed;
        _statsTimer.Tick += (_, _) => UpdateStats();
        _floatingTimer.Tick += (_, _) => { _floatingTimer.Stop(); if (_fullScreen) FloatingBar.Visibility = Visibility.Collapsed; };

        Root.Loaded += async (_, _) =>
        {
            UpdateView();
            await ConnectAsync();
        };
    }

    private SessionInfo? Info => _connection?.Session.Info;

    private bool CaptureShortcuts => _captureShortcutsForSession || App.Current.Data.Settings.WindowsShortcuts switch
    {
        ShortcutCapture.Always => true,
        ShortcutCapture.FullScreenOnly => _fullScreen,
        _ => false,
    };

    internal void ApplyTheme() => Root.RequestedTheme = App.ThemeFor(App.Current.Data.Settings.Theme);

    // ───────────────────────────── Connecting ─────────────────────────────

    private async Task ConnectAsync()
    {
        ShowConnecting();
        string? password = _password;
        _password = null;
        if (password is null && _profile.HasSavedPassword && await UnlockVaultAsync())
        {
            if (App.Current.Vault.TryGetPassword(_profile.Id, out string saved)) password = saved;
        }
        password ??= await AskPasswordAsync(null);
        if (password is null)
        {
            ShowProblem("Not connected", "No password was entered.", retry: true);
            return;
        }

        _cts = new CancellationTokenSource();
        _promptedThisAttempt = false;
        var progress = new Progress<ConnectStage>(SetStage);
        try
        {
            _connection = await App.Current.Connections.ConnectAsync(_profile, password, _rasterScale, this, progress, _cts.Token);
        }
        catch (AuthenticationFailedException e) when (e.CredentialsRejected)
        {
            string? again = await AskPasswordAsync(e.Message);
            if (again is null)
            {
                ShowProblem("Not connected", e.Message, retry: true, edit: true);
                return;
            }
            _password = again;
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
            // The key is pinned now, so the next attempt goes straight through.
            Diagnostics.Add($"Connection to {_profile.Host} timed out during a trust prompt; retrying.");
            _password = password;
            await ConnectAsync();
            return;
        }
        catch (Exception e)
        {
            Diagnostics.Add($"Connect to {_profile.Host} failed: {e.GetType().Name}: {e.Message}");
            ShowProblem(e is IdentityRejectedException ? "Connection stopped" : "Couldn't connect", Explain(e), retry: true, edit: e is not IdentityRejectedException);
            return;
        }
        finally
        {
            password = null;
        }
        OnConnected();
    }

    private void OnConnected()
    {
        var connection = _connection!;
        var session = connection.Session;
        Diagnostics.Add($"Connected to {_profile.Host}: {session.Info.SignIn}, encrypted={session.Info.Encrypted}, Apple={session.Info.AppleExtensions}");
        _renderer.Attach(session.Framebuffer);
        session.CursorChanged += shape => DispatcherQueue.TryEnqueue(() => ApplyCursor(shape));
        session.LayoutChanged += layout => DispatcherQueue.TryEnqueue(() => OnLayout(layout));
        session.Resized += (_, _) => DispatcherQueue.TryEnqueue(UpdateTitle);
        session.ModelIdentified += model => DispatcherQueue.TryEnqueue(() => Remember(p => p.MacModel = model));
        session.Notice += text => DispatcherQueue.TryEnqueue(() => ShowNotice(text, InfoBarSeverity.Informational));

        _clipboard = new ClipboardBridge(session, _profile.ShareClipboard);
        _clipboard.ReceivedFromMac += chars => ShowNotice($"Copied from the Mac ({chars:N0} characters)", InfoBarSeverity.Success, quiet: true);
        _clipboard.SetActive(_active);

        ShowSecurity(connection.Security, session.Info);
        Remember(p => p.LastConnected = DateTimeOffset.Now);
        Overlay.Visibility = Visibility.Collapsed;
        Surface.Focus(FocusState.Programmatic);
        UpdateHook();
        UpdateTitle();
        if (App.Current.Data.Settings.ShowConnectionStats)
        {
            StatsText.Visibility = Visibility.Visible;
            _statsTimer.Start();
        }
        _ = RunAsync(connection);
    }

    private async Task RunAsync(ConnectedMac connection)
    {
        string reason = "The Mac closed the connection.";
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
            reason = Explain(e, duringSession: true);
            Diagnostics.Add($"Session with {_profile.Host} ended: {e.GetType().Name}: {e.Message}");
        }
        if (_closing) return;
        await TearDownAsync();
        ShowProblem("Disconnected", reason, retry: true);
    }

    private async Task TearDownAsync()
    {
        ReleaseKeys();
        _hook.Uninstall();
        _statsTimer.Stop();
        _clipboard?.Dispose();
        _clipboard = null;
        _cts?.Cancel();
        if (_connection is { } c)
        {
            _connection = null;
            await c.DisposeAsync();
        }
        Surface.SetCursor(_cursors.Arrow);
    }

    private static string Explain(Exception e, bool duringSession = false) => e switch
    {
        IdentityRejectedException or AuthenticationFailedException or TimeoutException => e.Message,
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
        OverlayTitle.Text = $"Connecting to {MacCardItem.DisplayName(_profile)}";
        OverlayMessage.Visibility = Visibility.Collapsed;
        StepsList.Visibility = Visibility.Visible;
        RetryButton.Visibility = Visibility.Collapsed;
        EditButton.Visibility = Visibility.Collapsed;
        CancelButton.Content = "Cancel";
        Overlay.Visibility = Visibility.Visible;
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
        OverlayMessage.Visibility = Visibility.Visible;
        StepsList.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        EditButton.Visibility = edit ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Content = "Close";
        Overlay.Visibility = Visibility.Visible;
        SecurityPill.Visibility = Visibility.Collapsed;
        if (_fullScreen) SetFullScreen(false);
    }

    private async void Retry_Click(object sender, RoutedEventArgs e) => await ConnectAsync();

    private async void EditMac_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new MacEditorDialog(_profile, null) { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme };
        if (await dialog.ShowAsync() == ContentDialogResult.None || dialog.Result is not { } result) return;
        CopyProfile(result, _profile);
        if (dialog.Password is { Length: > 0 } pw)
        {
            _password = pw;
            if (dialog.RememberPassword && await UnlockVaultAsync())
            {
                App.Current.Vault.SetPassword(_profile.Id, pw);
                _profile.HasSavedPassword = true;
            }
        }
        App.Current.Data.Upsert(_profile);
        MacName.Text = MacCardItem.DisplayName(_profile);
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
            return await App.Current.Vault.UnlockAsync();
        }
        catch (InvalidOperationException e)
        {
            ShowNotice(e.Message, InfoBarSeverity.Warning);
            return false;
        }
    }

    private async Task<string?> AskPasswordAsync(string? problem)
    {
        var password = new PasswordBox { Header = $"Password for {_profile.Username}", PasswordRevealMode = PasswordRevealMode.Peek };
        var remember = new CheckBox { Content = "Remember on this PC (encrypted)", IsChecked = _profile.HasSavedPassword || problem is null };
        var panel = new StackPanel { Spacing = 14, Width = 380 };
        if (problem is not null)
        {
            panel.Children.Add(new InfoBar { Severity = InfoBarSeverity.Error, IsOpen = true, IsClosable = false, Message = problem });
        }
        panel.Children.Add(new TextBlock
        {
            Text = $"Sign in to {MacCardItem.DisplayName(_profile)} ({_profile.Host}) with your Mac account.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        panel.Children.Add(password);
        panel.Children.Add(remember);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            Title = "Sign in",
            Content = panel,
            PrimaryButtonText = "Connect",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        password.Loaded += (_, _) => password.Focus(FocusState.Programmatic);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || password.Password.Length == 0) return null;
        string value = password.Password;
        if (remember.IsChecked == true && await UnlockVaultAsync())
        {
            App.Current.Vault.SetPassword(_profile.Id, value);
            Remember(p => p.HasSavedPassword = true);
        }
        else if (remember.IsChecked != true && _profile.HasSavedPassword && await UnlockVaultAsync())
        {
            App.Current.Vault.Forget(_profile.Id);
            Remember(p => p.HasSavedPassword = false);
        }
        return value;
    }

    Task<bool> ITrustPrompts.TrustMacIdentityAsync(MacProfile profile, MacIdentity identity, string? previous) =>
        OnUiAsync(() => { _promptedThisAttempt = true; return TrustDialogAsync(
            previous is null ? $"First connection to {MacCardItem.DisplayName(profile)}" : "This Mac's identity has changed",
            previous is null
                ? "Vesper hasn't met this Mac before. It will remember the Mac's identity key and warn you if it ever changes, so nobody can quietly pretend to be your Mac later. If you can, check this fingerprint against one you trust."
                : $"The identity key {profile.Host} presented doesn't match the one you trusted. That happens after reinstalling macOS or resetting Screen Sharing, but it's also exactly what someone intercepting the connection would look like. If you didn't expect it, disconnect.",
            $"RSA {identity.KeySizeBits}-bit key",
            identity.Fingerprint, previous, verifyCommand: null); });

    Task<bool> ITrustPrompts.TrustSshHostKeyAsync(MacProfile profile, SshHostKey key, string? previous) =>
        OnUiAsync(() => { _promptedThisAttempt = true; return TrustDialogAsync(
            previous is null ? $"First SSH connection to {MacCardItem.DisplayName(profile)}" : "This Mac's SSH host key has changed",
            previous is null
                ? "Vesper will pin this SSH host key and refuse to send your password if it ever changes. To be certain it's your Mac, compare it with what Terminal on the Mac prints for:"
                : $"The SSH host key {profile.Host} presented doesn't match the one you trusted. Unless you reinstalled macOS or regenerated its SSH keys, someone may be intercepting the connection. Compare it with what Terminal on the Mac prints for:",
            $"{key.FriendlyAlgorithm} host key",
            key.Fingerprint, previous, verifyCommand: key.VerifyCommand); });

    private async Task<bool> TrustDialogAsync(string title, string body, string keyKind, string fingerprint, string? previous, string? verifyCommand)
    {
        bool changed = previous is not null;
        var panel = new StackPanel { Spacing = 14, Width = 460 };
        if (changed)
        {
            panel.Children.Add(new InfoBar
            {
                Severity = InfoBarSeverity.Error,
                IsOpen = true,
                IsClosable = false,
                Title = "Possible interception",
                Message = "Vesper hasn't sent your password.",
            });
        }
        panel.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap });
        if (verifyCommand is not null)
        {
            panel.Children.Add(Mono(verifyCommand, selectable: true));
        }
        var keyBox = new StackPanel { Spacing = 4, Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(8) };
        keyBox.Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"];
        keyBox.Children.Add(new TextBlock { Text = keyKind, FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        keyBox.Children.Add(Mono(fingerprint, selectable: true));
        if (changed)
        {
            keyBox.Children.Add(new TextBlock { Text = "Previously trusted", FontSize = 12, Margin = new Thickness(0, 8, 0, 0), Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
            keyBox.Children.Add(Mono(previous!, selectable: true));
        }
        panel.Children.Add(keyBox);
        CheckBox? understand = null;
        if (changed)
        {
            understand = new CheckBox { Content = "I know why this changed" };
            panel.Children.Add(understand);
        }
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            Title = title,
            Content = panel,
            PrimaryButtonText = changed ? "Trust the new key" : "Trust and connect",
            CloseButtonText = changed ? "Disconnect" : "Cancel",
            DefaultButton = changed ? ContentDialogButton.Close : ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = !changed,
        };
        if (understand is not null)
        {
            understand.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
            understand.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = false;
        }
        return await dialog.ShowAsync() == ContentDialogResult.Primary;

        static TextBlock Mono(string text, bool selectable) => new()
        {
            Text = text,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = selectable,
        };
    }

    private Task<T> OnUiAsync<T>(Func<Task<T>> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = DispatcherQueue.TryEnqueue(async () =>
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
        if (!queued) tcs.SetResult(default!);
        return tcs.Task;
    }

    private void Remember(Action<MacProfile> change)
    {
        change(_profile);
        if (App.Current.Data.Find(_profile.Id) is { } stored)
        {
            change(stored);
            App.Current.Data.SaveProfiles();
        }
    }

    // ───────────────────────────── Security indicator ─────────────────────────────

    private void ShowSecurity(SecuritySummary security, SessionInfo info)
    {
        var (glyph, brushKey) = security.Level switch
        {
            SecurityLevel.Verified => ("", "VesperVerifiedBrush"),
            SecurityLevel.Protected => ("", "VesperProtectedBrush"),
            _ => ("", "VesperWarningBrush"),
        };
        var brush = (Brush)Application.Current.Resources[brushKey];
        SecurityIcon.Glyph = glyph;
        SecurityIcon.Foreground = brush;
        FloatingSecurityIcon.Glyph = glyph;
        FloatingSecurityIcon.Foreground = brush;
        SecurityText.Text = security.Headline;
        SecurityText.Foreground = brush;
        SecurityFlyoutTitle.Text = security.Headline;
        SecurityFlyoutDetail.Text = security.Detail;
        SecurityFlyoutIdentity.Text = info.Identity is { } id ? $"Mac identity\n{id.Fingerprint}" : "";
        SecurityPill.Visibility = Visibility.Visible;
        if (security.Level == SecurityLevel.Unverified)
        {
            ShowNotice("This connection is encrypted, but the Mac's identity can't be verified on this network. Consider an SSH tunnel or a VPN.", InfoBarSeverity.Warning);
        }
    }

    // ───────────────────────────── Screen and view ─────────────────────────────

    private void UpdateView()
    {
        _rasterScale = Surface.XamlRoot?.RasterizationScale ?? 1.0;
        _renderer.SetView(Surface.ActualWidth, Surface.ActualHeight, _rasterScale);
        if (_cursorShape is not null) ApplyCursor(_cursorShape);
    }

    private void UpdateTitle()
    {
        if (_connection is null) return;
        var fb = _connection.Session.Framebuffer;
        AppWindow.Title = $"{MacCardItem.DisplayName(_profile)} — {fb.Width}×{fb.Height} — Vesper";
    }

    private void OnLayout(AppleDisplayLayout layout)
    {
        UpdateTitle();
        DisplaysMenu.Items.Clear();
        bool many = layout.Displays.Count > 1;
        DisplaysMenu.Visibility = many ? Visibility.Visible : Visibility.Collapsed;
        DisplaysSeparator.Visibility = DisplaysMenu.Visibility;
        if (!many) return;
        var all = new RadioMenuFlyoutItem { Text = "All displays", GroupName = "display", IsChecked = layout.CurrentDisplay is null };
        all.Click += (_, _) => _connection?.Session.SelectDisplay(null);
        DisplaysMenu.Items.Add(all);
        foreach (var display in layout.Displays)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = $"{display.Label}{(display.IsMain ? " (main)" : "")} — {display.PointsWidth}×{display.PointsHeight}",
                GroupName = "display",
                IsChecked = layout.CurrentDisplay == display.Id,
            };
            uint id = display.Id;
            item.Click += (_, _) => _connection?.Session.SelectDisplay(id);
            DisplaysMenu.Items.Add(item);
        }
    }

    private void ApplyCursor(CursorShape shape)
    {
        _cursorShape = shape;
        if (_connection is null) return;
        var fb = _connection.Session.Framebuffer;
        var view = _renderer.View;
        var place = SessionRenderer.Placement(view.W, view.H, view.Scale, fb.Width, fb.Height, view.Scaling, view.Pan);
        double screenPixelsPerFramebufferPixel = fb.Width > 0 ? place.Width * view.Scale / fb.Width : 1;
        double pixelsPerPoint = screenPixelsPerFramebufferPixel * _connection.Session.EffectiveDensity;
        var cursor = _cursors.For(shape, pixelsPerPoint);
        Surface.SetCursor(cursor);
#if DEBUG
        Diagnostics.Add($"cursor {shape.Width}x{shape.Height} hot {shape.HotspotX},{shape.HotspotY} at {pixelsPerPoint:0.##} px/pt -> {cursor.GetHashCode():X}");
#endif
    }

    private void ViewMenu_Opening(object? sender, object e)
    {
        FitItem.IsChecked = _profile.Scaling == ViewScaling.Fit;
        ActualItem.IsChecked = _profile.Scaling == ViewScaling.ActualSize;
        MatchItem.IsChecked = _profile.Resolution == ResolutionMode.MatchThisDisplay;
        NativeItem.IsChecked = _profile.Resolution == ResolutionMode.Native;
        var color = _connection?.Session.Color ?? _profile.Color;
        FullColorItem.IsChecked = color == ColorQuality.Full;
        ThousandsItem.IsChecked = color == ColorQuality.Thousands;
        GrayItem.IsChecked = color == ColorQuality.Grayscale;
    }

    private void Scaling_Click(object sender, RoutedEventArgs e)
    {
        var scaling = (string)((FrameworkElement)sender).Tag == "ActualSize" ? ViewScaling.ActualSize : ViewScaling.Fit;
        Remember(p => p.Scaling = scaling);
        _renderer.SetScaling(scaling);
        if (_cursorShape is not null) ApplyCursor(_cursorShape);
    }

    private void Resolution_Click(object sender, RoutedEventArgs e)
    {
        var mode = (string)((FrameworkElement)sender).Tag == "Native" ? ResolutionMode.Native : ResolutionMode.MatchThisDisplay;
        Remember(p => p.Resolution = mode);
        _connection?.Session.MatchPixelsPerPoint(mode == ResolutionMode.MatchThisDisplay ? _rasterScale : null);
    }

    private void Color_Click(object sender, RoutedEventArgs e)
    {
        var color = Enum.Parse<ColorQuality>((string)((FrameworkElement)sender).Tag);
        Remember(p => p.Color = color);
        _connection?.Session.SetColorQuality(color);
    }

    // ───────────────────────────── Pointer ─────────────────────────────

    private bool ToFramebuffer(Point position, out int x, out int y)
    {
        x = y = 0;
        if (_connection is null) return false;
        var fb = _connection.Session.Framebuffer;
        var view = _renderer.View;
        var place = SessionRenderer.Placement(view.W, view.H, view.Scale, fb.Width, fb.Height, view.Scaling, view.Pan);
        if (place.Width <= 0 || place.Height <= 0) return false;
        x = (int)Math.Clamp(Math.Floor((position.X - place.X) / place.Width * fb.Width), 0, fb.Width - 1);
        y = (int)Math.Clamp(Math.Floor((position.Y - place.Y) / place.Height * fb.Height), 0, fb.Height - 1);

        // In 1:1 mode a screen larger than the window pans as the pointer nears an edge.
        if (view.Scaling == ViewScaling.ActualSize && (place.Width > view.W || place.Height > view.H))
        {
            double fx = Math.Clamp(position.X / view.W, 0, 1), fy = Math.Clamp(position.Y / view.H, 0, 1);
            _renderer.SetPan(new Point(fx * Math.Max(0, fb.Width - view.W * view.Scale), fy * Math.Max(0, fb.Height - view.H * view.Scale)));
        }
        return true;
    }

    private static PointerButtons Buttons(PointerPointProperties p) =>
        (p.IsLeftButtonPressed ? PointerButtons.Left : 0)
        | (p.IsRightButtonPressed ? PointerButtons.Right : 0)
        | (p.IsMiddleButtonPressed ? PointerButtons.Middle : 0);

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Surface.Focus(FocusState.Pointer);
        Surface.CapturePointer(e.Pointer);
        OnPointer(sender, e);
    }

    private void OnPointer(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Surface);
        if (_fullScreen && point.Position.Y < 4) RevealFloatingBar();
        if (_connection is null || !ToFramebuffer(point.Position, out int x, out int y)) return;
        _connection.Session.SendPointer(x, y, Buttons(point.Properties));
        e.Handled = true;
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_connection is null) return;
        var point = e.GetCurrentPoint(Surface);
        var props = point.Properties;
        // A notch (120) scrolls about 40 points; trackpads send smaller steps for smooth scrolling.
        double points = props.MouseWheelDelta / 120.0 * 40 * App.Current.Data.Settings.ScrollSpeed;
        if (ToFramebuffer(point.Position, out int x, out int y))
        {
            _connection.Session.SendPointer(x, y, Buttons(props));
        }
        if (props.IsHorizontalMouseWheel) _connection.Session.SendScroll(points, 0, Buttons(props));
        else _connection.Session.SendScroll(0, -points, Buttons(props));
        e.Handled = true;
    }

    // ───────────────────────────── Keyboard ─────────────────────────────

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        _active = e.WindowActivationState != WindowActivationState.Deactivated;
        if (!_active) ReleaseKeys();
        _clipboard?.SetActive(_active);
        UpdateHook();
    }

    /// <summary>The keyboard hook exists only while shortcuts are captured and the Mac's screen has focus.</summary>
    private void UpdateHook()
    {
        bool want = _connection is not null && _active && _surfaceFocused && CaptureShortcuts && !(Info?.ObserveOnly ?? true);
        _keys.SendWindowsKey = want;
        if (want) _hook.Install();
        else _hook.Uninstall();
    }

    private bool OnHookKey(PhysicalKey key)
    {
        if (_connection is null || !_active || !_surfaceFocused || Native.GetForegroundWindow() != _hwnd) return false;
        if (HandleLocalShortcut(key.VirtualKey, key.Down)) return true;
        Send(key.VirtualKey, key.ScanCode, key.Extended, key.Down);
        return true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_connection is null || _hook.IsInstalled) return;
        if (HandleLocalShortcut((uint)e.Key, true)) { e.Handled = true; return; }
        Send((uint)e.Key, e.KeyStatus.ScanCode, e.KeyStatus.IsExtendedKey, true);
        e.Handled = true;
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (_connection is null || _hook.IsInstalled) return;
        Send((uint)e.Key, e.KeyStatus.ScanCode, e.KeyStatus.IsExtendedKey, false);
        e.Handled = true;
    }

    /// <summary>Ctrl+Alt+Enter toggles full screen; Ctrl+Alt+Home leaves it and hands shortcuts back to Windows.</summary>
    private bool HandleLocalShortcut(uint vk, bool down)
    {
        if (!down || !_keys.CtrlDown || !_keys.AltDown) return false;
        if (vk == (uint)VirtualKey.Enter)
        {
            ReleaseKeys();
            DispatcherQueue.TryEnqueue(() => SetFullScreen(!_fullScreen));
            return true;
        }
        if (vk == (uint)VirtualKey.Home)
        {
            ReleaseKeys();
            _captureShortcutsForSession = false;
            DispatcherQueue.TryEnqueue(() => { SetFullScreen(false); UpdateHook(); });
            return true;
        }
        return false;
    }

    private void Send(uint vk, uint scanCode, bool extended, bool down)
    {
        _keyBuffer.Clear();
        _keys.Translate(vk, scanCode, extended, down, _keyBuffer);
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

    private void KeysMenu_Opening(object? sender, object e)
    {
        MacStyleItem.IsChecked = _keys.Layout == ModifierLayout.MacStyle;
        CaptureShortcutsItem.IsChecked = CaptureShortcuts;
    }

    private void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        if (_connection is not { } c) return;
        const uint Cmd = 0xFFEB, Ctrl = 0xFFE3, Opt = 0xFFE7, Shift = 0xFFE1;
        uint[] combo = (string)((FrameworkElement)sender).Tag switch
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
        Surface.Focus(FocusState.Programmatic);
    }

    private void MacStyle_Click(object sender, RoutedEventArgs e)
    {
        ReleaseKeys();
        _keys.Layout = MacStyleItem.IsChecked ? ModifierLayout.MacStyle : ModifierLayout.Literal;
        Remember(p => p.Modifiers = _keys.Layout);
    }

    private void CaptureShortcuts_Click(object sender, RoutedEventArgs e)
    {
        _captureShortcutsForSession = CaptureShortcutsItem.IsChecked;
        Surface.Focus(FocusState.Programmatic);
        UpdateHook();
    }

    // ───────────────────────────── Toolbar ─────────────────────────────

    private void Clipboard_Click(object sender, RoutedEventArgs e)
    {
        bool on = ClipboardToggle.IsChecked == true;
        Remember(p => p.ShareClipboard = on);
        if (_clipboard is not null) _clipboard.Enabled = on;
        ShowNotice(on ? "Clipboard sharing is on, and only works while this window is in front." : "Clipboard sharing is off.",
            InfoBarSeverity.Informational, quiet: true);
        Surface.Focus(FocusState.Programmatic);
    }

    private void Shield_Click(object sender, RoutedEventArgs e)
    {
        bool on = ShieldToggle.IsChecked == true;
        ApplyCaptureProtection(on);
        ShowNotice(on ? "Hidden from screenshots, screen recording, screen sharing and Recall." : "This window can now be captured.",
            InfoBarSeverity.Informational, quiet: true);
        Surface.Focus(FocusState.Programmatic);
    }

    private void ApplyCaptureProtection(bool on)
    {
        Native.SetWindowDisplayAffinity(_hwnd, on ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE);
        ShieldToggle.IsChecked = on;
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e) => SetFullScreen(!_fullScreen);

    private void SetFullScreen(bool on)
    {
        _fullScreen = on;
        AppWindow.SetPresenter(on ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        TitleRow.Height = new GridLength(on ? 0 : 46);
        TitleBarArea.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        FullScreenIcon.Glyph = on ? "" : "";
        if (on) RevealFloatingBar();
        else FloatingBar.Visibility = Visibility.Collapsed;
        ReleaseKeys();
        UpdateHook();
        Surface.Focus(FocusState.Programmatic);
    }

    private void RevealFloatingBar()
    {
        FloatingBar.Visibility = Visibility.Visible;
        _floatingTimer.Stop();
        _floatingTimer.Start();
    }

    private void FloatingBar_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _floatingTimer.Stop();
        _floatingTimer.Start();
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowNotice(string message, InfoBarSeverity severity, bool quiet = false)
    {
        Notice.Severity = severity;
        Notice.Message = message;
        Notice.IsOpen = true;
        if (quiet || severity != InfoBarSeverity.Warning)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(quiet ? 3 : 6) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (Notice.Message == message) Notice.IsOpen = false;
            };
            timer.Start();
        }
    }

    private void UpdateStats()
    {
        if (_connection is null) return;
        var stats = _connection.Session.Stats;
        string rtt = TcpRtt.Milliseconds(_connection.Socket) switch
        {
            double ms when ms < 1 => " · <1 ms",
            double ms => $" · {ms:0} ms",
            _ => "",
        };
        StatsText.Text = $"{_renderer.FramesPerSecond:0} fps · {stats.UpdatesPerSecond:0} upd/s · {stats.MegabitsPerSecond:0.0} Mb/s{rtt}";
    }

    private async void OnClosed(object sender, WindowEventArgs e)
    {
        _closing = true;
        _floatingTimer.Stop();
        await TearDownAsync();
        _renderer.Dispose();
        _cursors.Dispose();
        _hook.Dispose();
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
            Changed(nameof(ActiveVisibility));
            Changed(nameof(DoneVisibility));
            Changed(nameof(PendingVisibility));
            Changed(nameof(Opacity));
        }
    }

    public bool IsActive => _state == StepState.Active;
    public Visibility ActiveVisibility => _state == StepState.Active ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DoneVisibility => _state == StepState.Done ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PendingVisibility => _state == StepState.Pending ? Visibility.Visible : Visibility.Collapsed;
    public double Opacity => _state == StepState.Pending ? 0.5 : 1.0;

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
