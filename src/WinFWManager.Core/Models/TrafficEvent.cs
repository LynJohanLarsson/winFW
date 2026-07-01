using System.Net;

namespace WinFWManager.Core.Models;

public class TrafficEvent
{
    public DateTime Timestamp { get; set; }
    public TrafficDirection Direction { get; set; }
    public TransportProtocol Protocol { get; set; }
    public IPAddress? SourceAddress { get; set; }
    public int SourcePort { get; set; }
    public IPAddress? DestinationAddress { get; set; }
    public int DestinationPort { get; set; }
    public TrafficAction Action { get; set; }
    public int ProcessId { get; set; }
    public string? ProcessName { get; set; }
    public string? InterfaceName { get; set; }
    public long InterfaceLuid { get; set; }
    public AdapterType AdapterType { get; set; } = AdapterType.Unknown;
    public FirewallProfile Profile { get; set; }
    public string? Country { get; set; }
    public string? City { get; set; }
    public string? Asn { get; set; }
    public string? Hostname { get; set; }
    public long FilterId { get; set; }

    public bool IsWslTraffic =>
        AdapterType == AdapterType.WSL
        || InterfaceName?.Contains("WSL", StringComparison.OrdinalIgnoreCase) == true;

    public bool IsHyperVTraffic =>
        !IsWslTraffic
        && (AdapterType is AdapterType.HyperV or AdapterType.VSwitch
            || InterfaceName?.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase) == true);

    public bool IsDestinationPrivate =>
        DestinationAddress != null && IsPrivateAddress(DestinationAddress);

    public bool IsSourcePrivate =>
        SourceAddress != null && IsPrivateAddress(SourceAddress);

    private static bool IsPrivateAddress(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return false;
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 127);
    }
}
