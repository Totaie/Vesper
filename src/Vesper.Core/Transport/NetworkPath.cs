using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Vesper.Core.Transport;

public enum NetworkPathKind
{
    /// <summary>Same machine. Nothing crosses a network.</summary>
    Loopback,

    /// <summary>The socket is bound to a WireGuard-based VPN interface (Tailscale or WireGuard).</summary>
    WireGuardVpn,

    /// <summary>Plain local network. Anyone on it can watch unencrypted traffic.</summary>
    LocalNetwork,

    /// <summary>The public internet.</summary>
    Internet,
}

public sealed record NetworkPath(NetworkPathKind Kind, string Description)
{
    public bool IsEncrypted => Kind is NetworkPathKind.Loopback or NetworkPathKind.WireGuardVpn;

    /// <summary>
    /// Classifies a connected socket by the interface it actually left through, rather than by
    /// the destination address alone. A 100.64.0.0/10 address only counts as Tailscale when our
    /// own end of the connection sits on the Tailscale adapter.
    /// </summary>
    public static NetworkPath Classify(Socket socket)
    {
        var remote = (socket.RemoteEndPoint as IPEndPoint)?.Address;
        var local = (socket.LocalEndPoint as IPEndPoint)?.Address;
        if (remote is null || local is null)
        {
            return new(NetworkPathKind.Internet, "Unknown network");
        }
        if (IPAddress.IsLoopback(remote))
        {
            return new(NetworkPathKind.Loopback, "This PC");
        }

        string? vpn = WireGuardAdapterOwning(local);
        if (vpn is not null)
        {
            return new(NetworkPathKind.WireGuardVpn, vpn);
        }
        return IsPrivate(remote)
            ? new(NetworkPathKind.LocalNetwork, "Local network")
            : new(NetworkPathKind.Internet, "Internet");
    }

    private static string? WireGuardAdapterOwning(IPAddress local)
    {
        if (local.IsIPv4MappedToIPv6) local = local.MapToIPv4();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            string label = nic.Description + " " + nic.Name;
            string? name =
                label.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) ? "Tailscale (WireGuard)" :
                label.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ? "WireGuard" :
                null;
            if (name is null) continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.Equals(local)) return name;
            }
        }
        return null;
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
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && (b[1] & 0xC0) == 64);
        }
        return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal || address.IsIPv6SiteLocal;
    }
}
