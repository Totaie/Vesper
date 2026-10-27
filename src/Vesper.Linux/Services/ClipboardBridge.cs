using System.Diagnostics;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Vesper.Core.Rfb;

namespace Vesper.Linux.Services;

/// <summary>
/// Text clipboard sharing between this computer and the Mac, off unless turned on for a Mac.
///
/// To keep it from leaking (the Windows app's rules):
/// - This computer's clipboard only goes to the Mac while the session window is in front (when
///   it comes to the front, and on a poll while it stays there), never from the background.
/// - Anything a password manager marked secret (x-kde-passwordManagerHint, as KeePassXC and
///   KDE do) stays here, and isn't even read.
/// - Text from the Mac only lands on this clipboard while the window is in front or has just left
///   it, so the Mac can't swap what you copied in another app; it's marked so clipboard history
///   managers that honour the hint don't keep it.
/// - The Mac's own text is never sent back to it, and nothing over 1 MiB crosses.
/// </summary>
internal sealed class ClipboardBridge : IDisposable
{
    private const int MaxChars = 1 << 20;
    private const string SecretHint = "x-kde-passwordManagerHint";

    /// <summary>Text the Mac sends just after you switch away is still the copy you made there.</summary>
    private static readonly TimeSpan JustLeft = TimeSpan.FromSeconds(1);

    private static readonly DataFormat<byte[]> SecretHintFormat = DataFormat.CreateBytesPlatformFormat(SecretHint);
    private static readonly DataFormat<byte[]> FromMacFormat = DataFormat.CreateBytesPlatformFormat("application/x-vesper-from-mac");

    private readonly RfbSession _session;
    private readonly Func<IClipboard?> _clipboard;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private string? _lastFromMac;
    private string? _lastToMac;
    private bool _active;
    private bool _enabled;
    private bool _disposed;
    private bool _busy;
    private bool _wasPrivate;
    private long _leftAt;

    public ClipboardBridge(RfbSession session, bool enabled, Func<IClipboard?> clipboard)
    {
        _session = session;
        _enabled = enabled;
        _clipboard = clipboard;
        session.ClipboardReceived += OnMacClipboard;
        _poll.Tick += (_, _) => _ = SendLocalClipboardAsync();
    }

    /// <summary>Raised (UI thread) when Mac text lands on this clipboard, for a quiet notice.</summary>
    public event Action<int>? ReceivedFromMac;

    /// <summary>Raised (UI thread) when a copy stays here because its app marked it secret.</summary>
    public event Action? KeptPrivate;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            _session.SetClipboardFromMac(value);
            UpdatePolling();
            if (value && _active) _ = SendLocalClipboardAsync();
        }
    }

    private bool MaySend => _enabled && _active && !_disposed;

    /// <summary>The session window came to the front (or left it).</summary>
    public void SetActive(bool active)
    {
        if (_active && !active) _leftAt = Stopwatch.GetTimestamp();
        _active = active;
        UpdatePolling();
        if (active && _enabled) _ = SendLocalClipboardAsync();
    }

    private void UpdatePolling()
    {
        if (MaySend) _poll.Start();
        else _poll.Stop();
    }

    private async Task SendLocalClipboardAsync()
    {
        if (!MaySend || _busy || _clipboard() is not { } clipboard) return;
        _busy = true;
        try
        {
            using var data = await clipboard.TryGetDataAsync();
            if (data is null || !MaySend) return;
            var formats = data.Formats;
            if (formats.Contains(FromMacFormat)) return; // the Mac's own text, which Vesper put there
            if (formats.Contains(SecretHintFormat))
            {
                if (!_wasPrivate) KeptPrivate?.Invoke();
                _wasPrivate = true;
                return;
            }
            _wasPrivate = false;
            if (!formats.Contains(DataFormat.Text)) return;
            string? text = await data.TryGetTextAsync();

            // While the text was being read the window may have left the front, or sharing may
            // have been switched off.
            if (text is null || !MaySend) return;
            if (text.Length == 0 || text.Length > MaxChars || text == _lastFromMac || text == _lastToMac) return;
            _lastToMac = text;
            _session.SendClipboard(text);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Another app holds the clipboard, or went away mid-read; the next poll tries again.
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Raised on the receive thread.</summary>
    private void OnMacClipboard(string text)
    {
        if (!_enabled || text.Length > MaxChars) return;
        Dispatcher.UIThread.Post(async () =>
        {
            bool inFront = _active || Stopwatch.GetElapsedTime(_leftAt) < JustLeft;
            if (_disposed || !_enabled || !inFront) return;
            if (text == _lastFromMac || text == _lastToMac) return;
            if (_clipboard() is not { } clipboard) return;
            _lastFromMac = text;
            // What was last sent is no longer on the Mac's clipboard: sending it again is a new copy.
            _lastToMac = null;
            var item = new DataTransferItem();
            item.SetText(text);
            // Clipboard history managers that honour it (Klipper, and others) don't keep this.
            item.Set(SecretHintFormat, "secret"u8.ToArray());
            item.Set(FromMacFormat, [1]);
            var transfer = new DataTransfer();
            transfer.Add(item);
            try
            {
                await clipboard.SetDataAsync(transfer);
                ReceivedFromMac?.Invoke(text.Length);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Diagnostics.Add($"The Mac's clipboard couldn't be placed here: {e.GetType().Name}");
            }
        });
    }

    public void Dispose()
    {
        _disposed = true;
        _poll.Stop();
        _session.ClipboardReceived -= OnMacClipboard;
    }
}
