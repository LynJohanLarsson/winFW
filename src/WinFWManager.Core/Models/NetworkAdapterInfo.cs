using System.Net;

namespace WinFWManager.Core.Models;

public class NetworkAdapterInfo
{
    public string Name { get; set; } = string.Empty;
    public string InterfaceAlias { get; set; } = string.Empty;
    public Guid InterfaceGuid { get; set; }
    public long InterfaceLuid { get; set; }
    public AdapterType AdapterType { get; set; }
    public string Status { get; set; } = "Unknown";
    public List<IPAddress> IpAddresses { get; set; } = new();
    public List<IpSubnet> Subnets { get; set; } = new();
    public string? MacAddress { get; set; }
    public FirewallProfile AssignedProfile { get; set; }
    public string? VSwitchName { get; set; }
    public int InterfaceIndex { get; set; }

    public bool IsVirtual => AdapterType is AdapterType.Virtual
        or AdapterType.VSwitch or AdapterType.WSL or AdapterType.HyperV;
}
