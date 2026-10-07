using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Vesper.Core.Rfb;
using Vesper.Interop;
using Windows.ApplicationModel.DataTransfer;

namespace Vesper.Services;

/// <summary>
/// Text clipboard sharing between Windows and the Mac, off unless turned on for a Mac.
///
/// To keep it from leaking:
/// - Windows' clipboard only goes to the Mac while the session window is in front (on
///   activation, and on change while it stays in front), never from the background.
/// - Anything the copying app marked private (password managers do) stays on Windows.
/// - Text from the Mac only lands on Windows' clipboard while the window is in front, so the Mac
///   can't swap what you copied in another app; and it's kept out of clipboard history, cloud
///   clipboard sync and third-party clipboard managers.
/// </summary>
internal sealed class ClipboardBridge : IDisposable
{
    private const int MaxChars = 1 << 20;

    /// <summary>Text the Mac sends just after you switch away is still the copy you made there.</summary>
    private static readonly TimeSpan JustLeft = TimeSpan.FromSeconds(1);

    private readonly DispatcherQueue _ui;
    private readonly RfbSession _session;
    private string? _lastFromMac;
    private string? _lastToMac;
    private bool _active;
    private bool _enabled;
    private bool _disposed;
    private long _leftAt;
    private uint _ownSequence;
    private uint _reportedPrivate;

    public ClipboardBridge(RfbSession session, bool enabled)
    {
        _ui = DispatcherQueue.GetForCurrentThread();
        _session = session;
        _enabled = enabled;
        session.ClipboardReceived += OnMacClipboard;
        Clipboard.ContentChanged += OnWindowsClipboardChanged;
    }

    /// <summary>Raised (UI thread) when Mac text lands on the Windows clipboard, for a quiet toast.</summary>
    public event Action<int>? ReceivedFromMac;

    public event Action<int>? SentToMac;

    /// <summary>Raised (UI thread) once per copy that stayed on Windows because its app marked it private.</summary>
    public event Action? KeptPrivate;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            _session.SetClipboardFromMac(value);
            if (value && _active) _ = SendWindowsClipboardAsync();
        }
    }

    private bool MaySend => _enabled && _active && !_disposed;

    /// <summary>The session window came to the front (or left it).</summary>
    public void SetActive(bool active)
    {
        if (_active && !active) _leftAt = Stopwatch.GetTimestamp();
        _active = active;
        if (active && _enabled) _ = SendWindowsClipboardAsync();
    }

    private void OnWindowsClipboardChanged(object? sender, object e)
    {
        if (MaySend) _ui.TryEnqueue(() => _ = SendWindowsClipboardAsync());
    }

    private async Task SendWindowsClipboardAsync()
    {
        try
        {
            if (!MaySend) return;
            uint sequence = Native.GetClipboardSequenceNumber();
            if (sequence == _ownSequence) return; // the Mac's own text, which Vesper put there
            switch (ClipboardPrivacy.Check())
            {
                case ClipboardMark.Busy:
                    return; // another app has it open; the next change or activation tries again
                case ClipboardMark.Private:
                    if (sequence != _reportedPrivate)
                    {
                        _reportedPrivate = sequence;
                        KeptPrivate?.Invoke();
                    }
                    return;
            }

            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text)) return;
            string text = await content.GetTextAsync();

            // While the text was being read the window may have left the front, sharing may have
            // been switched off, or something new may have been copied (and checked on its own).
            if (!MaySend || Native.GetClipboardSequenceNumber() != sequence) return;
            if (text.Length > MaxChars || text == _lastFromMac || text == _lastToMac) return;
            _lastToMac = text;
            _session.SendClipboard(text);
            SentToMac?.Invoke(text.Length);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // Another app holds the clipboard; the next change or activation tries again.
        }
    }

    private void OnMacClipboard(string text)
    {
        if (!_enabled || text.Length > MaxChars) return;
        _ui.TryEnqueue(() =>
        {
            bool inFront = _active || Stopwatch.GetElapsedTime(_leftAt) < JustLeft;
            if (_disposed || !_enabled || !inFront) return;
            if (text == _lastFromMac || text == _lastToMac) return;
            _lastFromMac = text;
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            try
            {
                // Recognised by clipboard managers that honour it (Ditto, ClipboardFusion, …).
                package.SetData("ExcludeClipboardContentFromMonitorProcessing", new Windows.Storage.Streams.InMemoryRandomAccessStream());
            }
            catch (Exception)
            {
            }
            try
            {
                Clipboard.SetContentWithOptions(package, new ClipboardContentOptions { IsAllowedInHistory = false, IsRoamable = false });
                _ownSequence = Native.GetClipboardSequenceNumber();
                ReceivedFromMac?.Invoke(text.Length);
            }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
            }
        });
    }

    public void Dispose()
    {
        _disposed = true;
        _session.ClipboardReceived -= OnMacClipboard;
        Clipboard.ContentChanged -= OnWindowsClipboardChanged;
    }
}
