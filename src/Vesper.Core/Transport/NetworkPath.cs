using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Vesper.Core.Transport;

public enum NetworkPathKind
{
    /// <summary>
    /// Same machine. Nothing crosses a network, but that says nothing about who's listening: a
    /// forwarded port can lead anywhere.
    /// </summary>
    Loopback,

    /// <summary>
    /// Straight to a Tailscale node's own address, through this PC's Tailscale adapter. WireGuard
    /// proves which node answers, so the path authenticates the Mac.
    /// </summary>
    TailscalePeer,

    /// <summary>Plain local network. Anyone on it can watch or redirect traffic.</summary>
    LocalNetwork,

    /// <summary>
    /// Anything else: the public internet, and also routes that only pass through a VPN (a
    /// Tailscale subnet router or exit node, a commercial VPN), where the last hop is open.
    /// </summary>
    Internet,
}

public sealed record NetworkPath(NetworkPathKind Kind, string Description)
{
    /// <summary>The path itself proves which machine is at the other end.</summary>
    public bool AuthenticatesPeer => Kind == NetworkPathKind.TailscalePeer;

    /// <summary>
    /// Classifies a connected socket by where it actually goes. Leaving through a VPN adapter isn't
    /// enough: a subnet route or exit node carries the traffic only part of the way. Only an
    /// address that is itself a Tailscale node, reached through the Tailscale adapter, counts.
    /// </summary>
    public static NetworkPath Classify(Socket socket)
    {
        var remote = (socket.RemoteEndPoint as IPEndPoint)?.Address;
        var local = (socket.LocalEndPoint as IPEndPoint)?.Address;
        if (remote is null || local is null)
        {
            return new(NetworkPathKind.Internet, "Unknown network");
        }
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        if (IPAddress.IsLoopback(remote))
        {
            return new(NetworkPathKind.Loopback, "This PC");
        }
        if (IsTailscaleNodeAddress(remote) && OnTailscaleAdapter(local))
        {
            return new(NetworkPathKind.TailscalePeer, "Tailscale");
        }
        return IsPrivate(remote)
            ? new(NetworkPathKind.LocalNetwork, "Local network")
            : new(NetworkPathKind.Internet, "Internet");
    }

    /// <summary>
    /// Whether an address is one Tailscale assigns to a node: 100.64.0.0/10 or fd7a:115c:a1e0::/48,
    /// minus Tailscale's own service address and the 4via6 range (which reaches IPv4 subnets
    /// behind a router, unencrypted on the last hop).
    /// </summary>
    public static bool IsTailscaleNodeAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        byte[] b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            bool cgnat = b[0] == 100 && (b[1] & 0xC0) == 64;
            bool service = b[0] == 100 && b[1] == 100 && b[2] == 100 && b[3] == 100;
            return cgnat && !service;
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        ReadOnlySpan<byte> ula = [0xfd, 0x7a, 0x11, 0x5c, 0xa1, 0xe0];
        if (!b.AsSpan(0, 6).SequenceEqual(ula)) return false;
        bool via = b[6] == 0x0b && b[7] == 0x1a;                       // fd7a:115c:a1e0:b1a::/64
        bool serviceV6 = b.AsSpan(6, 9).IndexOfAnyExcept((byte)0) < 0 && b[15] == 0x53; // fd7a:115c:a1e0::53
        return !via && !serviceV6;
    }

    private static bool OnTailscaleAdapter(IPAddress local)
    {
        if (local.IsIPv4MappedToIPv6) local = local.MapToIPv4();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (!(nic.Description + " " + nic.Name).Contains("Tailscale", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.Equals(local)) return true;
            }
        }
        return false;
    }

    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && (b[1] & 0xF0) == 16)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }
        return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal || address.IsIPv6SiteLocal;
    }
}
