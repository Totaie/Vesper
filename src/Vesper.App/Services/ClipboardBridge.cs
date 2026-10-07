using Microsoft.UI.Dispatching;
using Vesper.Core.Rfb;
using Windows.ApplicationModel.DataTransfer;

namespace Vesper.Services;

/// <summary>
/// Text clipboard sharing between Windows and the Mac, off unless turned on for a Mac.
///
/// To keep it from leaking: Windows' clipboard only goes to the Mac when the session window is in
/// front (on activation, and on change while it stays in front), never in the background; and
/// text from the Mac is placed on the Windows clipboard excluded from clipboard history, cloud
/// clipboard sync and third-party clipboard managers.
/// </summary>
internal sealed class ClipboardBridge : IDisposable
{
    private const int MaxChars = 1 << 20;
    private readonly DispatcherQueue _ui;
    private readonly RfbSession _session;
    private string? _lastFromMac;
    private string? _lastToMac;
    private bool _active;
    private bool _enabled;

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

    /// <summary>The session window came to the front (or left it).</summary>
    public void SetActive(bool active)
    {
        _active = active;
        if (active && _enabled) _ = SendWindowsClipboardAsync();
    }

    private void OnWindowsClipboardChanged(object? sender, object e)
    {
        if (_enabled && _active) _ui.TryEnqueue(() => _ = SendWindowsClipboardAsync());
    }

    private async Task SendWindowsClipboardAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text)) return;
            string text = await content.GetTextAsync();
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
                ReceivedFromMac?.Invoke(text.Length);
            }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
            }
        });
    }

    public void Dispose()
    {
        _session.ClipboardReceived -= OnMacClipboard;
        Clipboard.ContentChanged -= OnWindowsClipboardChanged;
    }
}
