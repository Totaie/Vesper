using Microsoft.UI.Dispatching;
using Windows.Devices.Enumeration;

namespace Vesper.Services;

public sealed record DiscoveredMac(string Id, string Name, string Host, int Port);

/// <summary>
/// Finds Macs advertising Screen Sharing (_rfb._tcp) on the local network, through Windows' own
/// DNS-SD support. It only listens to local multicast announcements and opens no connections.
/// </summary>
internal sealed class MacDiscovery : IDisposable
{
    private const string DnsSdProtocol = "{4526e8c1-8aac-4153-9b16-55e86ada0e54}";
    private static readonly string[] Properties =
    [
        "System.Devices.Dnssd.HostName",
        "System.Devices.Dnssd.InstanceName",
        "System.Devices.Dnssd.PortNumber",
        "System.Devices.IpAddress",
    ];

    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private readonly Dictionary<string, DiscoveredMac> _found = [];
    private DeviceWatcher? _watcher;

    public event Action? Changed;

    public IReadOnlyCollection<DiscoveredMac> Found => _found.Values;

    public void Start()
    {
        if (_watcher is not null) return;
        string aqs = $"System.Devices.AepService.ProtocolId:={DnsSdProtocol} AND System.Devices.Dnssd.Domain:=\"local\" AND System.Devices.Dnssd.ServiceName:=\"_rfb._tcp\"";
        try
        {
            _watcher = DeviceInformation.CreateWatcher(aqs, Properties, DeviceInformationKind.AssociationEndpointService);
            _watcher.Added += (_, info) => Upsert(info.Id, info.Properties);
            _watcher.Updated += (_, update) => Upsert(update.Id, update.Properties);
            _watcher.Removed += (_, update) => _ui.TryEnqueue(() =>
            {
                if (_found.Remove(update.Id)) Changed?.Invoke();
            });
            _watcher.Start();
        }
        catch (Exception)
        {
            _watcher = null; // discovery is a convenience; never fatal
        }
    }

    private void Upsert(string id, IReadOnlyDictionary<string, object> properties)
    {
        string? host = properties.TryGetValue("System.Devices.Dnssd.HostName", out var h) ? h as string : null;
        string? name = properties.TryGetValue("System.Devices.Dnssd.InstanceName", out var n) ? n as string : null;
        int port = properties.TryGetValue("System.Devices.Dnssd.PortNumber", out var p) && p is uint u ? (int)u : 5900;
        if (string.IsNullOrWhiteSpace(host) && properties.TryGetValue("System.Devices.IpAddress", out var ip) && ip is string[] { Length: > 0 } ips)
        {
            host = ips[0];
        }
        if (string.IsNullOrWhiteSpace(host)) return;
        host = host.TrimEnd('.');
        var mac = new DiscoveredMac(id, string.IsNullOrWhiteSpace(name) ? host : name, host, port);
        _ui.TryEnqueue(() =>
        {
            _found[id] = mac;
            Changed?.Invoke();
        });
    }

    public void Dispose()
    {
        if (_watcher is { Status: DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted })
        {
            _watcher.Stop();
        }
        _watcher = null;
    }
}
