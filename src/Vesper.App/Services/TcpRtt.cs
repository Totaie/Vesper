using System.Net.Sockets;

namespace Vesper.Services;

/// <summary>The kernel's smoothed round-trip estimate for a TCP connection (SIO_TCP_INFO).</summary>
internal static class TcpRtt
{
    private const int SioTcpInfo = unchecked((int)0xD8000027);

    public static double? Milliseconds(Socket socket)
    {
        try
        {
            byte[] output = new byte[128];
            int n = socket.IOControl(SioTcpInfo, BitConverter.GetBytes(0), output);
            // TCP_INFO_v0: State, Mss, ConnectionTimeMs (u64), TimestampsEnabled, then RttUs at 20.
            return n >= 24 ? BitConverter.ToUInt32(output, 20) / 1000.0 : null;
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or PlatformNotSupportedException)
        {
            return null;
        }
    }
}
