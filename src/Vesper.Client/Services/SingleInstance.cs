using System.Net.Sockets;

namespace Vesper.Services;

/// <summary>
/// One Vesper per user. Two copies would each keep the Macs and settings in memory and overwrite
/// each other's changes when they save, so a second launch asks the first to show its window and
/// quits. They talk over a Unix domain socket in Vesper's private data folder (Windows has them
/// too), which only this user can reach.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string Show = "show\n";
    private readonly Socket _listener;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(Socket listener, Action showRequested)
    {
        _listener = listener;
        _ = AcceptAsync(showRequested);
    }

    /// <summary>
    /// Becomes the running copy, or, if one is already running, asks it to come forward and
    /// returns null (this copy should quit). <paramref name="showRequested"/> is called on a
    /// background thread.
    /// </summary>
    public static SingleInstance? Claim(string dataDirectory, Action showRequested)
    {
        string path = Path.Combine(dataDirectory, "instance.sock");
        var endpoint = new UnixDomainSocketEndPoint(path);
        if (File.Exists(path))
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                probe.Connect(endpoint);
                probe.Send(System.Text.Encoding.ASCII.GetBytes(Show));
                return null;
            }
            catch (SocketException)
            {
                // Left behind by a copy that didn't exit cleanly.
                try { File.Delete(path); } catch (IOException) { }
            }
        }
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(endpoint);
            listener.Listen(4);
        }
        catch (SocketException)
        {
            // Couldn't claim it (another copy won a race, or the platform refused): run anyway
            // rather than not at all.
            listener.Dispose();
            return new SingleInstance(new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified), showRequested);
        }
        return new SingleInstance(listener, showRequested);
    }

    private async Task AcceptAsync(Action showRequested)
    {
        if (!_listener.IsBound) return;
        byte[] buffer = new byte[16];
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var client = await _listener.AcceptAsync(_stop.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                int n = await client.ReceiveAsync(buffer, SocketFlags.None, timeout.Token).ConfigureAwait(false);
                if (n > 0 && System.Text.Encoding.ASCII.GetString(buffer, 0, n) == Show) showRequested();
            }
            catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
            {
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException or ObjectDisposedException)
            {
                if (_stop.IsCancellationRequested) return;
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        string? path = (_listener.LocalEndPoint as UnixDomainSocketEndPoint)?.ToString();
        _listener.Dispose();
        if (path is not null) try { File.Delete(path); } catch (IOException) { }
    }
}
