using System.Net;
using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

/// <summary>A packet-drop observation awaiting correlation (network-layer
/// drops carry IfIndex but no ports; transport-layer drops the inverse).</summary>
public sealed class DropObservation
{
    public required DateTime Timestamp { get; init; }
    public required IPAddress Source { get; init; }
    public required IPAddress Destination { get; init; }
    public int? IfIndex { get; init; }
    public int LocalPort { get; init; }
    public int RemotePort { get; init; }
    public bool HasPorts { get; init; }
    public required string Reason { get; init; }
    public TrafficDirection Direction { get; init; }
    public TransportProtocol Protocol { get; init; } = TransportProtocol.Other;
}

/// <summary>
/// Interprets Microsoft-Windows-TCPIP manifest events from plain field
/// dictionaries (testable without a live ETW session).
/// </summary>
public static class TcpIpEventParser
{
    /// <summary>Event names that produce an allowed-traffic TrafficEvent.</summary>
    public static readonly IReadOnlySet<string> FlowEventNames = new HashSet<string>
    {
        "TcpConnectTcbComplete", "TcpConnectionRundown", "TcpAcceptListenerComplete",
        "UdpEndpointSendMessages", "UdpEndpointReceiveMessages"
    };

    /// <summary>Event names that feed the drop correlator.</summary>
    public static readonly IReadOnlySet<string> DropEventNames = new HashSet<string>
    {
        "TcpipNetworkPacketDrops", "TcpipTransportPacketDrops"
    };

    public static TrafficEvent? Parse(string eventName, IReadOnlyDictionary<string, object?> f, DateTime timestamp)
    {
        switch (eventName)
        {
            case "TcpConnectTcbComplete":
            case "TcpConnectionRundown":
                return FromSockaddrs(f, "LocalAddress", "RemoteAddress",
                    TransportProtocol.TCP, TrafficDirection.Outbound, timestamp);
            case "TcpAcceptListenerComplete":
                return FromSockaddrs(f, "LocalAddress", "RemoteAddress",
                    TransportProtocol.TCP, TrafficDirection.Inbound, timestamp);
            case "UdpEndpointSendMessages":
                return FromSockaddrs(f, "LocalSockAddr", "RemoteSockAddr",
                    TransportProtocol.UDP, TrafficDirection.Outbound, timestamp);
            case "UdpEndpointReceiveMessages":
                return FromSockaddrs(f, "LocalSockAddr", "RemoteSockAddr",
                    TransportProtocol.UDP, TrafficDirection.Inbound, timestamp);
            default:
                return null;
        }
    }

    /// <summary>Parses a packet-drop event into a DropObservation.
    /// Note: transport-drop mapping assumes inbound flows (the verified
    /// WSL-to-host case). Outbound blocked flows surface as two standalone
    /// events (the network half's packet-perspective key never matches the
    /// transport half's local-perspective key), and the transport half's
    /// direction/endpoints reflect the inbound assumption — refine when an
    /// outbound-drop scenario is captured empirically.</summary>
    public static DropObservation? TryParseDrop(string eventName, IReadOnlyDictionary<string, object?> f, DateTime timestamp)
    {
        if (eventName == "TcpipNetworkPacketDrops")
        {
            var src = GetAddress(f, "SourceAddress");
            var dst = GetAddress(f, "DestAddress");
            if (src == null || dst == null) return null;
            return new DropObservation
            {
                Timestamp = timestamp,
                Source = src,
                Destination = dst,
                IfIndex = GetInt(f, "IfIndex"),
                Reason = DropReasonMapper.Network(GetInt(f, "Reason") ?? 0),
                Direction = GetInt(f, "PathDirection") == 1
                    ? TrafficDirection.Inbound : TrafficDirection.Outbound,
                Protocol = GetInt(f, "IPTransportProtocol") switch
                {
                    6 => TransportProtocol.TCP,
                    17 => TransportProtocol.UDP,
                    _ => TransportProtocol.Other
                }
            };
        }

        if (eventName == "TcpipTransportPacketDrops")
        {
            var (local, lport) = GetSockaddr(f, "LocalSockAddr");
            var (remote, rport) = GetSockaddr(f, "RemoteSockAddr");
            if (local == null || remote == null) return null;
            return new DropObservation
            {
                Timestamp = timestamp,
                // Transport drops are local-perspective: remote is the peer.
                Source = remote,
                Destination = local,
                LocalPort = lport,
                RemotePort = rport,
                HasPorts = lport > 0 || rport > 0,
                Reason = DropReasonMapper.Transport(GetInt(f, "Reason") ?? 0),
                Direction = TrafficDirection.Inbound,
                Protocol = GetInt(f, "IPTransportProtocol") switch
                {
                    6 => TransportProtocol.TCP,
                    17 => TransportProtocol.UDP,
                    _ => TransportProtocol.Other
                }
            };
        }

        return null;
    }

    private static TrafficEvent? FromSockaddrs(IReadOnlyDictionary<string, object?> f,
        string localField, string remoteField, TransportProtocol proto,
        TrafficDirection direction, DateTime timestamp)
    {
        var (local, lport) = GetSockaddr(f, localField);
        var (remote, rport) = GetSockaddr(f, remoteField);
        if (local == null || remote == null) return null;

        bool outbound = direction == TrafficDirection.Outbound;
        return new TrafficEvent
        {
            Timestamp = timestamp,
            Direction = direction,
            Protocol = proto,
            Action = TrafficAction.Allow,
            SourceAddress = outbound ? local : remote,
            SourcePort = outbound ? lport : rport,
            DestinationAddress = outbound ? remote : local,
            DestinationPort = outbound ? rport : lport,
            ProcessId = GetInt(f, "Pid") ?? GetInt(f, "ProcessId") ?? 0
        };
    }

    private static (IPAddress? Ip, int Port) GetSockaddr(IReadOnlyDictionary<string, object?> f, string name)
        => f.TryGetValue(name, out var v) && v is byte[] b ? SockAddrDecoder.Decode(b) : (null, 0);

    private static IPAddress? GetAddress(IReadOnlyDictionary<string, object?> f, string name)
        => f.TryGetValue(name, out var v) && v is byte[] b ? SockAddrDecoder.Decode(b).Ip : null;

    private static int? GetInt(IReadOnlyDictionary<string, object?> f, string name)
    {
        if (!f.TryGetValue(name, out var v) || v == null) return null;
        try { return Convert.ToInt32(v); } catch { return null; }
    }
}
