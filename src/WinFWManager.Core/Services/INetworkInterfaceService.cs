using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

public interface INetworkInterfaceService
{
    Task<IReadOnlyList<NetworkAdapterInfo>> GetAllAdaptersAsync();
    Task RefreshAsync();
    string? ResolveInterfaceName(long interfaceLuid);
    string? ResolveInterfaceByIp(System.Net.IPAddress address);
    NetworkAdapterInfo? ResolveAdapter(System.Net.IPAddress? local, System.Net.IPAddress? remote);
    NetworkAdapterInfo? ResolveByIfIndex(int ifIndex);
    AdapterType ClassifyAdapter(string interfaceName);
}
