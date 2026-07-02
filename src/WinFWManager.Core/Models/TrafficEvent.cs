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

    /// <summary>Human-readable drop reason; null for allowed traffic.</summary>
    public string? DropReason { get; set; }

    /// <summary>True when the adapter was resolved from an ETW IfIndex
    /// (authoritative); false when derived by IP/subnet matching.</summary>
    public bool IsInterfaceExact { get; set; }

    /// <summary>ETW interface index when the event carried one; resolved to an
    /// adapter during enrichment.</summary>
    public int? InterfaceIndexHint { get; set; }

    /// <summary>Compact flow path, e.g. "WSL guest → vEthernet (WSL) ⛔".</summary>
    public string FlowDescription
    {
        get
        {
            string nic = InterfaceName ?? "?";
            string sym = Action == TrafficAction.Allow ? "✓" : "⛔";
            if (Direction == TrafficDirection.Inbound)
            {
                string src = IsWslTraffic ? "WSL guest"
                    : IsHyperVTraffic ? "Hyper-V guest"
                    : IsSourcePrivate ? "LAN" : "internet";
                return $"{src} → {nic} {sym}";
            }
            string dst = IsWslTraffic ? "WSL guest"
                : IsHyperVTraffic ? "Hyper-V guest"
                : IsDestinationPrivate ? "LAN" : "internet";
            return $"{nic} → {dst} {sym}";
        }
    }

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
