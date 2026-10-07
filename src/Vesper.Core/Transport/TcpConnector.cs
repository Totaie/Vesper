using System.Net;
using System.Net.Sockets;

namespace Vesper.Core.Transport;

public static class TcpConnector
{
    /// <summary>
    /// Opens a TCP connection tuned for interactive remote desktop traffic: Nagle off so key and
    /// pointer events leave immediately, generous buffers for screen updates, and keepalives so a
    /// dead network is noticed instead of hanging forever.
    /// </summary>
    public static async Task<Socket> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(host, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (SocketException e)
        {
            throw new IOException($"Couldn't find \"{host}\" on the network ({e.SocketErrorCode}).", e);
        }
        if (addresses.Length == 0)
        {
            throw new IOException($"Couldn't find \"{host}\" on the network.");
        }

        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
                ReceiveBufferSize = 4 << 20,
                SendBufferSize = 256 << 10,
            };
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 10);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 3);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 4);
                await socket.ConnectAsync(new IPEndPoint(address, port), timeoutCts.Token).ConfigureAwait(false);
                return socket;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                socket.Dispose();
                last = new TimeoutException($"{host} didn't answer on port {port}.");
            }
            catch (SocketException e)
            {
                socket.Dispose();
                last = e;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw last is SocketException se
            ? new IOException(Describe(se, host, port), se)
            : last ?? new IOException($"Couldn't connect to {host}:{port}.");
    }

    private static string Describe(SocketException e, string host, int port) => e.SocketErrorCode switch
    {
        SocketError.ConnectionRefused => $"{host} refused the connection on port {port}. Is the service turned on?",
        SocketError.HostUnreachable or SocketError.NetworkUnreachable => $"{host} isn't reachable from this network.",
        SocketError.TimedOut => $"{host} didn't answer on port {port}.",
        _ => $"Couldn't connect to {host}:{port} ({e.SocketErrorCode}).",
    };
}
