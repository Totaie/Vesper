using System.Net.Sockets;

namespace Vesper.Services;

/// <summary>The kernel's smoothed round-trip estimate for a TCP connection.</summary>
public static class TcpRtt
{
    private const int SioTcpInfo = unchecked((int)0xD8000027);
    private const int IpProtoTcp = 6, LinuxTcpInfo = 11;

    public static double? Milliseconds(Socket socket)
    {
        try
        {
            byte[] output = new byte[256];
            if (OperatingSystem.IsWindows())
            {
                int n = socket.IOControl(SioTcpInfo, BitConverter.GetBytes(0), output);
                // TCP_INFO_v0: State, Mss, ConnectionTimeMs (u64), TimestampsEnabled, then RttUs at 20.
                return n >= 24 ? BitConverter.ToUInt32(output, 20) / 1000.0 : null;
            }
            if (OperatingSystem.IsLinux())
            {
                // struct tcp_info: eight u8 fields, then u32s; tcpi_rtt (µs) is at 68.
                int n = socket.GetRawSocketOption(IpProtoTcp, LinuxTcpInfo, output);
                return n >= 72 ? BitConverter.ToUInt32(output, 68) / 1000.0 : null;
            }
            return null;
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or PlatformNotSupportedException)
        {
            return null;
        }
    }
}
