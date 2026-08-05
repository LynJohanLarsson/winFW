using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

public class NetworkInterfaceService : INetworkInterfaceService
{
    private List<NetworkAdapterInfo> _adapters = new();
    private readonly ConcurrentDictionary<long, string> _luidToName = new();
    private readonly ConcurrentDictionary<string, string> _ipToName = new(StringComparer.Ordinal);

    public NetworkInterfaceService()
    {
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged +=
            (_, _) => { try { RefreshAsync(); } catch { } };
    }

    public async Task<IReadOnlyList<NetworkAdapterInfo>> GetAllAdaptersAsync()
    {
        await RefreshAsync();
        return _adapters.AsReadOnly();
    }

    public Task RefreshAsync()
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces();
        var adapters = new List<NetworkAdapterInfo>();

        foreach (var ni in interfaces)
        {
            var props = ni.GetIPProperties();
            var unicast = props.UnicastAddresses
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                         || a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                .ToList();

            var addresses = unicast.Select(a => a.Address).ToList();
            var subnets = unicast
                .Where(a => a.PrefixLength > 0)
                .Select(a => new IpSubnet(a.Address, a.PrefixLength))
                .ToList();

            int interfaceIndex = 0;
            try
            {
                var ipv4Props = props.GetIPv4Properties();
                interfaceIndex = ipv4Props?.Index ?? 0;
            }
            catch
            {
                // Some adapters don't support IPv4 properties
            }

            var adapter = new NetworkAdapterInfo
            {
                Name = ni.Name,
                InterfaceAlias = ni.Description,
                AdapterType = ClassifyAdapter(ni.Name),
                Status = ni.OperationalStatus.ToString(),
                IpAddresses = addresses,
                Subnets = subnets,
                MacAddress = FormatMac(ni.GetPhysicalAddress()),
                InterfaceIndex = interfaceIndex
            };

            adapters.Add(adapter);
        }

        _adapters = adapters;

        // Build IP → adapter name lookup
        _ipToName.Clear();
        foreach (var a in adapters)
        {
            foreach (var ip in a.IpAddresses)
                _ipToName[ip.ToString()] = a.Name;
        }

        return Task.CompletedTask;
    }

    public string? ResolveInterfaceName(long interfaceLuid)
    {
        if (_luidToName.TryGetValue(interfaceLuid, out var name))
            return name;

        return null;
    }

    public string? ResolveInterfaceByIp(IPAddress address)
    {
        if (_ipToName.TryGetValue(address.ToString(), out var name))
            return name;

        return null;
    }

    /// <summary>
    /// Resolves the adapter a connection traverses, given its local and remote
    /// endpoints. Kernel TCP/IP ETW events carry no interface identifier, so we
    /// attribute traffic by IP: an exact match on the local endpoint, then a
    /// most-specific subnet match on the local endpoint, then on the remote
    /// endpoint. The last step is what catches host&lt;-&gt;WSL/Hyper-V traffic,
    /// where the peer (guest) IP lives in a virtual adapter's subnet.
    /// </summary>
    public NetworkAdapterInfo? ResolveAdapter(IPAddress? local, IPAddress? remote)
        => ResolveAdapterFrom(_adapters, local, remote);

    /// <summary>Pure resolution logic over a supplied adapter list (unit-testable).</summary>
    public static NetworkAdapterInfo? ResolveAdapterFrom(
        IReadOnlyList<NetworkAdapterInfo> adapters, IPAddress? local, IPAddress? remote)
    {
        // 1. Exact match: the local endpoint is one of an adapter's own addresses.
        if (local != null)
        {
            foreach (var a in adapters)
                if (a.IpAddresses.Any(ip => ip.Equals(local)))
                    return a;
        }

        // 2. Most-specific subnet containing the local endpoint.
        var byLocal = MatchSubnet(adapters, local);
        if (byLocal != null) return byLocal;

        // 3. Most-specific subnet containing the remote endpoint (host<->VM peer).
        return MatchSubnet(adapters, remote);
    }

    /// <summary>
    /// Resolves an adapter by its interface index, as carried in TCPIP-provider
    /// ETW drop events. Returns null when the index is unknown or non-positive.
    /// </summary>
    public NetworkAdapterInfo? ResolveByIfIndex(int ifIndex)
        => ResolveByIfIndexFrom(_adapters, ifIndex);

    /// <summary>Pure resolution logic over a supplied adapter list (unit-testable).</summary>
    public static NetworkAdapterInfo? ResolveByIfIndexFrom(
        IReadOnlyList<NetworkAdapterInfo> adapters, int ifIndex)
        => ifIndex <= 0 ? null : adapters.FirstOrDefault(a => a.InterfaceIndex == ifIndex);

    private static NetworkAdapterInfo? MatchSubnet(
        IReadOnlyList<NetworkAdapterInfo> adapters, IPAddress? address)
    {
        if (address == null) return null;

        NetworkAdapterInfo? best = null;
        int bestPrefix = -1;
        foreach (var a in adapters)
            foreach (var s in a.Subnets)
                if (s.PrefixLength > bestPrefix && s.Contains(address))
                {
                    best = a;
                    bestPrefix = s.PrefixLength;
                }

        return best;
    }

    public AdapterType ClassifyAdapter(string interfaceName)
    {
        if (string.IsNullOrEmpty(interfaceName))
            return AdapterType.Unknown;

        if (interfaceName.Contains("Loopback", StringComparison.OrdinalIgnoreCase))
            return AdapterType.Loopback;

        if (interfaceName.Contains("WSL", StringComparison.OrdinalIgnoreCase))
            return AdapterType.WSL;

        if (interfaceName.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase))
            return AdapterType.VSwitch;

        if (interfaceName.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase))
            return AdapterType.HyperV;

        if (interfaceName.StartsWith("vSwitch", StringComparison.OrdinalIgnoreCase))
            return AdapterType.VSwitch;

        if (interfaceName.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
            || interfaceName.Contains("VPN", StringComparison.OrdinalIgnoreCase)
            || interfaceName.Contains("TAP", StringComparison.OrdinalIgnoreCase))
            return AdapterType.Virtual;

        return AdapterType.Physical;
    }

    private static string? FormatMac(PhysicalAddress mac)
    {
        var bytes = mac.GetAddressBytes();
        if (bytes.Length == 0) return null;
        return string.Join(":", bytes.Select(b => b.ToString("X2")));
    }
}
