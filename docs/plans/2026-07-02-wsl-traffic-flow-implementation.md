# WSL Traffic Flow & TCPIP Provider Migration — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make WSL→host traffic visible with real Action (Allow/Drop + reason) and exact interface attribution by migrating ETW capture to the `Microsoft-Windows-TCPIP` manifest provider.

**Architecture:** A testable parser layer (`SockAddrDecoder`, `TcpIpEventParser`, `DropCorrelator`, `DropReasonMapper`) sits between raw ETW events and `TrafficEvent`. `EtwTrafficMonitor` is rewritten to enable the TCPIP manifest provider, name-filter events, and feed the parser. `NetworkInterfaceService` gains authoritative IfIndex→adapter resolution. `WslNetworkModeDetector` classifies NAT/Mirrored/Bridged. UI gains a real Action column (with drop-reason tooltip), a Flow column, WSL-guest highlighting in the dashboard graph, and a WSL-mode badge.

**Tech Stack:** .NET 8, WPF, Microsoft.Diagnostics.Tracing.TraceEvent 3.2.4, xunit + FluentAssertions.

**Repo/branch:** `C:\Claude\WinFWManager-clone`, branch `fix/wsl-hyperv-identification`.

**Design doc:** `docs/plans/2026-07-02-wsl-traffic-flow-design.md` — read it first.

**Empirical facts (from live ETW spikes — trust these over docs):**
- Provider GUID: `2f07e2ee-15db-40f1-90ef-9d7ba282188a` (`Microsoft-Windows-TCPIP`), enable with `TraceEventLevel.Verbose`, keywords `ulong.MaxValue`.
- Sockaddr fields (`LocalAddress`, `RemoteAddress`, `LocalSockAddr`, `RemoteSockAddr`) arrive as `byte[]`: family = `b[0] | b[1]<<8` (2 = AF_INET, 23 = AF_INET6), port = `(b[2]<<8) | b[3]` (network order), IPv4 addr = bytes 4–7, IPv6 addr = bytes 8–23.
- `TcpipNetworkPacketDrops`: has `IfIndex`, `SourceAddress`/`DestAddress` (byte[]), `Reason` (256 observed for WFP firewall drop), `PathDirection` (1 = inbound/RX, 0 = outbound/TX), no ports.
- `TcpipTransportPacketDrops`: has `LocalSockAddr`/`RemoteSockAddr` with ports, `Reason` (4 observed for WFP firewall drop), no IfIndex.
- `TcpConnectTcbComplete` / `TcpConnectionRundown` / `TcpDisconnectTcbComplete`: `LocalAddress`/`RemoteAddress` (byte[]), `Pid` field. Rundown fires for existing connections when the session starts.
- `UdpEndpointSendMessages` / `UdpEndpointReceiveMessages`: `LocalSockAddr`/`RemoteSockAddr`, `Pid`.
- Dual-stack addresses appear as `::ffff:10.64.9.103` — map to IPv4.
- The provider is chatty (~300–500 events/s): filter on event NAME before touching payloads.

---

## Phase 1 — Core parsing infrastructure (pure, unit-tested)

### Task 1: Extend TrafficEvent model (DropReason, IsInterfaceExact, FlowDescription)

**Files:**
- Modify: `src/WinFWManager.Core/Models/TrafficEvent.cs`
- Test: `tests/WinFWManager.Tests/Models/TrafficEventTests.cs`

**Step 1: Write failing tests** — append to `TrafficEventTests.cs`:

```csharp
    [Fact]
    public void FlowDescription_DroppedInboundWsl_ShowsGuestArrowNicBlocked()
    {
        var evt = new TrafficEvent
        {
            Direction = TrafficDirection.Inbound,
            Action = TrafficAction.Drop,
            AdapterType = AdapterType.WSL,
            InterfaceName = "vEthernet (WSL)"
        };
        evt.FlowDescription.Should().Be("WSL guest → vEthernet (WSL) ⛔");
    }

    [Fact]
    public void FlowDescription_AllowedOutboundPublic_ShowsNicArrowInternet()
    {
        var evt = new TrafficEvent
        {
            Direction = TrafficDirection.Outbound,
            Action = TrafficAction.Allow,
            InterfaceName = "Ethernet",
            DestinationAddress = System.Net.IPAddress.Parse("8.8.8.8")
        };
        evt.FlowDescription.Should().Be("Ethernet → internet ✓");
    }

    [Fact]
    public void FlowDescription_AllowedOutboundPrivate_ShowsNicArrowLan()
    {
        var evt = new TrafficEvent
        {
            Direction = TrafficDirection.Outbound,
            Action = TrafficAction.Allow,
            InterfaceName = "Ethernet",
            DestinationAddress = System.Net.IPAddress.Parse("10.0.0.5")
        };
        evt.FlowDescription.Should().Be("Ethernet → LAN ✓");
    }

    [Fact]
    public void FlowDescription_InboundNonWsl_ShowsRemoteArrowNic()
    {
        var evt = new TrafficEvent
        {
            Direction = TrafficDirection.Inbound,
            Action = TrafficAction.Allow,
            InterfaceName = "Ethernet",
            SourceAddress = System.Net.IPAddress.Parse("192.168.1.50")
        };
        evt.FlowDescription.Should().Be("LAN → Ethernet ✓");
    }
```

**Step 2: Run — verify fail**

```
cd C:\Claude\WinFWManager-clone && dotnet test tests/WinFWManager.Tests --filter "FlowDescription" -v n
```
Expected: FAIL — `FlowDescription` not defined.

**Step 3: Implement.** In `TrafficEvent.cs`, add properties after `FilterId`:

```csharp
    /// <summary>Human-readable drop reason; null for allowed traffic.</summary>
    public string? DropReason { get; set; }

    /// <summary>True when the adapter was resolved from an ETW IfIndex
    /// (authoritative); false when derived by IP/subnet matching.</summary>
    public bool IsInterfaceExact { get; set; }

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
```

**Step 4: Run — verify pass** (same command). Expected: 4 PASS, all existing tests still green.

**Step 5: Commit**

```bash
git add src/WinFWManager.Core/Models/TrafficEvent.cs tests/WinFWManager.Tests/Models/TrafficEventTests.cs
git commit -m "feat: add DropReason, IsInterfaceExact and FlowDescription to TrafficEvent"
```

### Task 2: SockAddrDecoder

**Files:**
- Create: `src/WinFWManager.Core/Services/SockAddrDecoder.cs`
- Test: `tests/WinFWManager.Tests/Services/SockAddrDecoderTests.cs`

**Step 1: Write failing tests**

```csharp
using System.Net;
using FluentAssertions;
using WinFWManager.Core.Services;

namespace WinFWManager.Tests.Services;

public class SockAddrDecoderTests
{
    [Fact]
    public void Decode_Ipv4Sockaddr_ReturnsAddressAndPort()
    {
        // AF_INET (2), port 9099 (0x238B), 172.26.112.1
        var b = new byte[] { 2, 0, 0x23, 0x8B, 172, 26, 112, 1 };
        var (ip, port) = SockAddrDecoder.Decode(b);
        ip.Should().Be(IPAddress.Parse("172.26.112.1"));
        port.Should().Be(9099);
    }

    [Fact]
    public void Decode_Ipv6Sockaddr_ReturnsAddressAndPort()
    {
        var b = new byte[28];
        b[0] = 23; // AF_INET6
        b[2] = 0x01; b[3] = 0xBB; // port 443
        b[8] = 0xfe; b[9] = 0x80; b[23] = 0x01; // fe80::1
        var (ip, port) = SockAddrDecoder.Decode(b);
        ip.Should().Be(IPAddress.Parse("fe80::1"));
        port.Should().Be(443);
    }

    [Fact]
    public void Decode_DualStackMapped_NormalizesToIpv4()
    {
        var b = new byte[28];
        b[0] = 23;
        b[2] = 0x01; b[3] = 0xBB;
        // ::ffff:10.64.9.103
        b[18] = 0xFF; b[19] = 0xFF; b[20] = 10; b[21] = 64; b[22] = 9; b[23] = 103;
        var (ip, port) = SockAddrDecoder.Decode(b);
        ip.Should().Be(IPAddress.Parse("10.64.9.103"));
    }

    [Fact]
    public void Decode_BareIpv4Bytes_ReturnsAddressNoPort()
    {
        var (ip, port) = SockAddrDecoder.Decode(new byte[] { 172, 26, 127, 184 });
        ip.Should().Be(IPAddress.Parse("172.26.127.184"));
        port.Should().Be(0);
    }

    [Fact]
    public void Decode_TooShort_ReturnsNull()
    {
        SockAddrDecoder.Decode(new byte[] { 2, 0 }).Ip.Should().BeNull();
    }

    [Fact]
    public void DecodeIpv4Uint_LittleEndianHostOrder_ReturnsAddress()
    {
        // 172.26.112.1 as little-endian uint (as ETW SourceIPv4Address fields arrive)
        uint v = BitConverter.ToUInt32(new byte[] { 172, 26, 112, 1 });
        SockAddrDecoder.DecodeIpv4Uint(v).Should().Be(IPAddress.Parse("172.26.112.1"));
    }
}
```

**Step 2: Run — verify fail** (`--filter "SockAddrDecoderTests"`).

**Step 3: Implement**

```csharp
using System.Net;

namespace WinFWManager.Core.Services;

/// <summary>
/// Decodes SOCKADDR byte payloads from Microsoft-Windows-TCPIP ETW events.
/// Layout (verified empirically): family = b[0]|b[1]&lt;&lt;8 (2=AF_INET, 23=AF_INET6),
/// port = big-endian at b[2..3], IPv4 addr at b[4..7], IPv6 addr at b[8..23].
/// </summary>
public static class SockAddrDecoder
{
    public static (IPAddress? Ip, int Port) Decode(byte[] bytes)
    {
        if (bytes.Length == 4)
            return (new IPAddress(bytes), 0);
        if (bytes.Length < 8)
            return (null, 0);

        int family = bytes[0] | (bytes[1] << 8);
        int port = (bytes[2] << 8) | bytes[3];

        if (family == 2)
            return (new IPAddress(bytes[4..8]), port);

        if (family == 23 && bytes.Length >= 24)
        {
            var ip = new IPAddress(bytes[8..24]);
            if (ip.IsIPv4MappedToIPv6)
                ip = ip.MapToIPv4();
            return (ip, port);
        }

        return (null, 0);
    }

    /// <summary>Decodes ETW uint IPv4 fields (SourceIPv4Address etc.), which
    /// carry the address bytes in memory order.</summary>
    public static IPAddress DecodeIpv4Uint(uint value)
        => new(BitConverter.GetBytes(value));
}
```

**Step 4: Run — verify pass.**

**Step 5: Commit**

```bash
git add src/WinFWManager.Core/Services/SockAddrDecoder.cs tests/WinFWManager.Tests/Services/SockAddrDecoderTests.cs
git commit -m "feat: add SockAddrDecoder for TCPIP ETW sockaddr payloads"
```

### Task 3: DropReasonMapper

**Files:**
- Create: `src/WinFWManager.Core/Services/DropReasonMapper.cs`
- Test: `tests/WinFWManager.Tests/Services/DropReasonMapperTests.cs`

**Step 1: Write failing tests**

```csharp
using FluentAssertions;
using WinFWManager.Core.Services;

namespace WinFWManager.Tests.Services;

public class DropReasonMapperTests
{
    [Fact]
    public void NetworkReason_256_IsWfpFirewall()
        => DropReasonMapper.Network(256).Should().Be("Firewall (WFP filter)");

    [Fact]
    public void TransportReason_4_IsWfpFirewall()
        => DropReasonMapper.Transport(4).Should().Be("Firewall (WFP filter)");

    [Fact]
    public void UnknownReason_FallsBackToNumeric()
        => DropReasonMapper.Network(9999).Should().Be("Drop (reason 9999)");
}
```

**Step 2: Run — verify fail.**

**Step 3: Implement**

```csharp
namespace WinFWManager.Core.Services;

/// <summary>
/// Maps TCPIP packet-drop Reason codes to readable text. Only empirically
/// verified codes are named (observed on live captures); everything else
/// falls back to a numeric label. Extend as new codes are confirmed.
/// </summary>
public static class DropReasonMapper
{
    public static string Network(int reason) => reason switch
    {
        256 => "Firewall (WFP filter)",   // verified: WSL->host SYN dropped by Hyper-V firewall
        _ => $"Drop (reason {reason})"
    };

    public static string Transport(int reason) => reason switch
    {
        4 => "Firewall (WFP filter)",     // verified: same drop at transport layer
        _ => $"Drop (reason {reason})"
    };
}
```

**Step 4: Run — verify pass.**

**Step 5: Commit**

```bash
git add src/WinFWManager.Core/Services/DropReasonMapper.cs tests/WinFWManager.Tests/Services/DropReasonMapperTests.cs
git commit -m "feat: add DropReasonMapper with verified TCPIP reason codes"
```

### Task 4: TcpIpEventParser

**Files:**
- Create: `src/WinFWManager.Core/Services/TcpIpEventParser.cs`
- Test: `tests/WinFWManager.Tests/Services/TcpIpEventParserTests.cs`

The parser takes an event name + field dictionary (never a `TraceEvent`) and returns a `TrafficEvent?`. Drop events return `null` here — they go through the correlator instead (Task 5); the parser only classifies them via `TryParseDrop`.

**Step 1: Write failing tests**

```csharp
using System.Net;
using FluentAssertions;
using WinFWManager.Core.Models;
using WinFWManager.Core.Services;

namespace WinFWManager.Tests.Services;

public class TcpIpEventParserTests
{
    private static byte[] V4(string ip, int port)
    {
        var a = IPAddress.Parse(ip).GetAddressBytes();
        return new byte[] { 2, 0, (byte)(port >> 8), (byte)(port & 0xFF), a[0], a[1], a[2], a[3] };
    }

    [Fact]
    public void Parse_TcpConnectTcbComplete_OutboundAllow()
    {
        var fields = new Dictionary<string, object?>
        {
            ["LocalAddress"] = V4("10.64.9.103", 62926),
            ["RemoteAddress"] = V4("51.105.69.5", 443),
            ["Pid"] = 9972
        };
        var evt = TcpIpEventParser.Parse("TcpConnectTcbComplete", fields, DateTime.UtcNow);

        evt.Should().NotBeNull();
        evt!.Direction.Should().Be(TrafficDirection.Outbound);
        evt.Action.Should().Be(TrafficAction.Allow);
        evt.Protocol.Should().Be(TransportProtocol.TCP);
        evt.SourceAddress.Should().Be(IPAddress.Parse("10.64.9.103"));
        evt.SourcePort.Should().Be(62926);
        evt.DestinationAddress.Should().Be(IPAddress.Parse("51.105.69.5"));
        evt.DestinationPort.Should().Be(443);
        evt.ProcessId.Should().Be(9972);
    }

    [Fact]
    public void Parse_UdpReceive_InboundWithSwappedEndpoints()
    {
        var fields = new Dictionary<string, object?>
        {
            ["LocalSockAddr"] = V4("10.64.9.103", 55080),
            ["RemoteSockAddr"] = V4("10.13.37.11", 53),
            ["Pid"] = 2708
        };
        var evt = TcpIpEventParser.Parse("UdpEndpointReceiveMessages", fields, DateTime.UtcNow);

        evt!.Direction.Should().Be(TrafficDirection.Inbound);
        evt.Protocol.Should().Be(TransportProtocol.UDP);
        // Inbound: remote is the source
        evt.SourceAddress.Should().Be(IPAddress.Parse("10.13.37.11"));
        evt.SourcePort.Should().Be(53);
        evt.DestinationAddress.Should().Be(IPAddress.Parse("10.64.9.103"));
        evt.DestinationPort.Should().Be(55080);
    }

    [Fact]
    public void Parse_UnknownEvent_ReturnsNull()
        => TcpIpEventParser.Parse("TcpTcbStartTimer", new(), DateTime.UtcNow).Should().BeNull();

    [Fact]
    public void TryParseDrop_NetworkDrop_ExtractsIfIndexReasonDirection()
    {
        var fields = new Dictionary<string, object?>
        {
            ["SourceAddress"] = IPAddress.Parse("172.26.127.184").GetAddressBytes(),
            ["DestAddress"] = IPAddress.Parse("172.26.112.1").GetAddressBytes(),
            ["IfIndex"] = 33,
            ["Reason"] = 256,
            ["PathDirection"] = 1
        };
        var drop = TcpIpEventParser.TryParseDrop("TcpipNetworkPacketDrops", fields, DateTime.UtcNow);

        drop.Should().NotBeNull();
        drop!.Source.Should().Be(IPAddress.Parse("172.26.127.184"));
        drop.Destination.Should().Be(IPAddress.Parse("172.26.112.1"));
        drop.IfIndex.Should().Be(33);
        drop.Reason.Should().Be("Firewall (WFP filter)");
        drop.Direction.Should().Be(TrafficDirection.Inbound);
        drop.HasPorts.Should().BeFalse();
    }

    [Fact]
    public void TryParseDrop_TransportDrop_ExtractsPorts()
    {
        var fields = new Dictionary<string, object?>
        {
            ["LocalSockAddr"] = V4("172.26.112.1", 9099),
            ["RemoteSockAddr"] = V4("172.26.127.184", 44216),
            ["Reason"] = 4
        };
        var drop = TcpIpEventParser.TryParseDrop("TcpipTransportPacketDrops", fields, DateTime.UtcNow);

        drop!.HasPorts.Should().BeTrue();
        drop.LocalPort.Should().Be(9099);
        drop.RemotePort.Should().Be(44216);
        drop.IfIndex.Should().BeNull();
        drop.Reason.Should().Be("Firewall (WFP filter)");
    }
}
```

**Step 2: Run — verify fail.**

**Step 3: Implement**

```csharp
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
                    ? TrafficDirection.Inbound : TrafficDirection.Outbound
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
                Direction = TrafficDirection.Inbound
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
```

**Step 4: Run — verify pass.**

**Step 5: Commit**

```bash
git add src/WinFWManager.Core/Services/TcpIpEventParser.cs tests/WinFWManager.Tests/Services/TcpIpEventParserTests.cs
git commit -m "feat: add TcpIpEventParser for TCPIP manifest events"
```

### Task 5: DropCorrelator

**Files:**
- Create: `src/WinFWManager.Core/Services/DropCorrelator.cs`
- Test: `tests/WinFWManager.Tests/Services/DropCorrelatorTests.cs`

Merges network-layer drops (IfIndex, no ports) with transport-layer drops (ports, no IfIndex) on the (source, destination) address pair within a 2-second window. Injected clock for determinism.

**Step 1: Write failing tests**

```csharp
using System.Net;
using FluentAssertions;
using WinFWManager.Core.Models;
using WinFWManager.Core.Services;

namespace WinFWManager.Tests.Services;

public class DropCorrelatorTests
{
    private static readonly IPAddress Guest = IPAddress.Parse("172.26.127.184");
    private static readonly IPAddress Host = IPAddress.Parse("172.26.112.1");
    private DateTime _now = new(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc);

    private DropCorrelator NewCorrelator() => new(() => _now, TimeSpan.FromSeconds(2));

    private DropObservation NetworkDrop() => new()
    {
        Timestamp = _now, Source = Guest, Destination = Host,
        IfIndex = 33, Reason = "Firewall (WFP filter)", Direction = TrafficDirection.Inbound
    };

    private DropObservation TransportDrop() => new()
    {
        Timestamp = _now, Source = Guest, Destination = Host,
        LocalPort = 9099, RemotePort = 44216, HasPorts = true,
        Reason = "Firewall (WFP filter)", Direction = TrafficDirection.Inbound
    };

    [Fact]
    public void NetworkThenTransport_WithinWindow_EmitsMergedEvent()
    {
        var c = NewCorrelator();
        c.Add(NetworkDrop()).Should().BeNull("first half waits for its sibling");
        var merged = c.Add(TransportDrop());

        merged.Should().NotBeNull();
        merged!.Action.Should().Be(TrafficAction.Drop);
        merged.SourceAddress.Should().Be(Guest);
        merged.DestinationAddress.Should().Be(Host);
        merged.DestinationPort.Should().Be(9099);
        merged.SourcePort.Should().Be(44216);
        merged.InterfaceIndexHint.Should().Be(33);
        merged.DropReason.Should().Be("Firewall (WFP filter)");
    }

    [Fact]
    public void TransportThenNetwork_AlsoMerges()
    {
        var c = NewCorrelator();
        c.Add(TransportDrop()).Should().BeNull();
        var merged = c.Add(NetworkDrop());
        merged!.InterfaceIndexHint.Should().Be(33);
        merged.DestinationPort.Should().Be(9099);
    }

    [Fact]
    public void ExpiredHalf_IsFlushedAsStandaloneEvent()
    {
        var c = NewCorrelator();
        c.Add(NetworkDrop());
        _now = _now.AddSeconds(3);
        var flushed = c.FlushExpired();

        flushed.Should().HaveCount(1);
        flushed[0].Action.Should().Be(TrafficAction.Drop);
        flushed[0].InterfaceIndexHint.Should().Be(33);
        flushed[0].DestinationPort.Should().Be(0, "network drops carry no ports");
    }

    [Fact]
    public void SecondNetworkDropSamePair_DoesNotGrowUnbounded()
    {
        var c = NewCorrelator();
        c.Add(NetworkDrop());
        c.Add(NetworkDrop());   // repeat SYN retry
        c.PendingCount.Should().Be(1, "same-key observations coalesce");
    }
}
```

Note: `InterfaceIndexHint` is a new nullable int on `TrafficEvent` — the correlator does not resolve adapters (that is the ViewModel/service layer's job); it forwards the IfIndex.

**Step 2: Run — verify fail.**

**Step 3: Implement.** Add to `TrafficEvent.cs`:

```csharp
    /// <summary>ETW interface index when the event carried one; resolved to an
    /// adapter during enrichment.</summary>
    public int? InterfaceIndexHint { get; set; }
```

Create `DropCorrelator.cs`:

```csharp
using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

/// <summary>
/// Correlates network-layer packet drops (IfIndex, no ports) with
/// transport-layer drops (ports, no IfIndex) on the (source, destination)
/// address pair within a time window, producing one merged TrafficEvent.
/// Unmatched halves are flushed as standalone drop events after the window.
/// </summary>
public sealed class DropCorrelator
{
    private readonly Func<DateTime> _now;
    private readonly TimeSpan _window;
    private readonly Dictionary<string, DropObservation> _pending = new();
    private readonly object _lock = new();

    public DropCorrelator(Func<DateTime>? clock = null, TimeSpan? window = null)
    {
        _now = clock ?? (() => DateTime.UtcNow);
        _window = window ?? TimeSpan.FromSeconds(2);
    }

    public int PendingCount { get { lock (_lock) return _pending.Count; } }

    /// <summary>Adds an observation; returns a merged TrafficEvent when its
    /// sibling (other layer, same address pair) is already pending.</summary>
    public TrafficEvent? Add(DropObservation obs)
    {
        string key = $"{obs.Source}|{obs.Destination}";
        lock (_lock)
        {
            if (_pending.TryGetValue(key, out var other))
            {
                if (other.HasPorts != obs.HasPorts)
                {
                    _pending.Remove(key);
                    return Merge(obs.HasPorts ? other : obs, obs.HasPorts ? obs : other);
                }
                // Same layer repeated (e.g. SYN retries): keep the newest.
                _pending[key] = obs;
                return null;
            }
            _pending[key] = obs;
            return null;
        }
    }

    /// <summary>Emits pending halves older than the window as standalone drops.
    /// Call periodically (the monitor calls this on a timer).</summary>
    public List<TrafficEvent> FlushExpired()
    {
        var cutoff = _now() - _window;
        var flushed = new List<TrafficEvent>();
        lock (_lock)
        {
            var expired = _pending.Where(kv => kv.Value.Timestamp <= cutoff).ToList();
            foreach (var kv in expired)
            {
                _pending.Remove(kv.Key);
                flushed.Add(ToEvent(kv.Value));
            }
        }
        return flushed;
    }

    private static TrafficEvent Merge(DropObservation network, DropObservation transport)
    {
        var evt = ToEvent(transport);
        evt.InterfaceIndexHint = network.IfIndex;
        return evt;
    }

    private static TrafficEvent ToEvent(DropObservation o) => new()
    {
        Timestamp = o.Timestamp,
        Direction = o.Direction,
        Protocol = o.Protocol,
        Action = TrafficAction.Drop,
        SourceAddress = o.Source,
        DestinationAddress = o.Destination,
        SourcePort = o.RemotePort,
        DestinationPort = o.LocalPort,
        InterfaceIndexHint = o.IfIndex,
        DropReason = o.Reason
    };
}
```

**Step 4: Run — verify pass** (`--filter "DropCorrelatorTests"`), then full suite.

**Step 5: Commit**

```bash
git add src/WinFWManager.Core/Services/DropCorrelator.cs src/WinFWManager.Core/Models/TrafficEvent.cs tests/WinFWManager.Tests/Services/DropCorrelatorTests.cs
git commit -m "feat: add DropCorrelator merging network+transport drop events"
```

---

## Phase 2 — Interface attribution

### Task 6: IfIndex→adapter resolution in NetworkInterfaceService

**Files:**
- Modify: `src/WinFWManager.Core/Services/NetworkInterfaceService.cs`
- Modify: `src/WinFWManager.Core/Services/INetworkInterfaceService.cs`
- Test: `tests/WinFWManager.Tests/Services/NetworkInterfaceServiceTests.cs`

**Step 1: Write failing tests** — append to `NetworkInterfaceServiceTests.cs`:

```csharp
    [Fact]
    public void ResolveByIfIndex_KnownIndex_ReturnsAdapter()
    {
        var adapters = new[]
        {
            new NetworkAdapterInfo { Name = "Ethernet", InterfaceIndex = 12 },
            new NetworkAdapterInfo { Name = "vEthernet (WSL)", InterfaceIndex = 33 },
        };
        NetworkInterfaceService.ResolveByIfIndexFrom(adapters, 33)!.Name
            .Should().Be("vEthernet (WSL)");
    }

    [Fact]
    public void ResolveByIfIndex_UnknownIndex_ReturnsNull()
    {
        var adapters = new[] { new NetworkAdapterInfo { Name = "Ethernet", InterfaceIndex = 12 } };
        NetworkInterfaceService.ResolveByIfIndexFrom(adapters, 99).Should().BeNull();
    }
```

**Step 2: Run — verify fail.**

**Step 3: Implement.** In `INetworkInterfaceService.cs` add:

```csharp
    NetworkAdapterInfo? ResolveByIfIndex(int ifIndex);
```

In `NetworkInterfaceService.cs` add instance + static methods, and subscribe to network changes in the constructor:

```csharp
    public NetworkInterfaceService()
    {
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged +=
            (_, _) => { try { RefreshAsync(); } catch { } };
    }

    public NetworkAdapterInfo? ResolveByIfIndex(int ifIndex)
        => ResolveByIfIndexFrom(_adapters, ifIndex);

    public static NetworkAdapterInfo? ResolveByIfIndexFrom(
        IReadOnlyList<NetworkAdapterInfo> adapters, int ifIndex)
        => ifIndex <= 0 ? null : adapters.FirstOrDefault(a => a.InterfaceIndex == ifIndex);
```

**Step 4: Run — verify pass** (full suite: mock-free tests still pass because `RefreshAsync` in ctor is not called — only subscribed).

**Step 5: Commit**

```bash
git add src/WinFWManager.Core/Services/NetworkInterfaceService.cs src/WinFWManager.Core/Services/INetworkInterfaceService.cs tests/WinFWManager.Tests/Services/NetworkInterfaceServiceTests.cs
git commit -m "feat: add IfIndex-based adapter resolution with network-change refresh"
```

---

## Phase 3 — WSL mode detection

### Task 7: WslNetworkModeDetector

**Files:**
- Create: `src/WinFWManager.Core/Services/WslNetworkModeDetector.cs`
- Modify: `src/WinFWManager.Core/Models/Enums.cs` (add `WslNetworkingMode`)
- Test: `tests/WinFWManager.Tests/Services/WslNetworkModeDetectorTests.cs`

**Step 1: Write failing tests**

```csharp
using FluentAssertions;
using WinFWManager.Core.Models;
using WinFWManager.Core.Services;

namespace WinFWManager.Tests.Services;

public class WslNetworkModeDetectorTests
{
    [Fact]
    public void Parse_NoConfig_DefaultsToNat()
        => WslNetworkModeDetector.ParseConfig(null).Should().Be(WslNetworkingMode.Nat);

    [Fact]
    public void Parse_EmptyConfig_DefaultsToNat()
        => WslNetworkModeDetector.ParseConfig("").Should().Be(WslNetworkingMode.Nat);

    [Fact]
    public void Parse_MirroredMode_ReturnsMirrored()
    {
        var cfg = "[wsl2]\nnetworkingMode=mirrored\n";
        WslNetworkModeDetector.ParseConfig(cfg).Should().Be(WslNetworkingMode.Mirrored);
    }

    [Fact]
    public void Parse_MirroredCaseInsensitiveWithSpaces_ReturnsMirrored()
    {
        var cfg = "[WSL2]\r\n  NetworkingMode = Mirrored \r\n";
        WslNetworkModeDetector.ParseConfig(cfg).Should().Be(WslNetworkingMode.Mirrored);
    }

    [Fact]
    public void Parse_BridgedViaVmSwitch_ReturnsBridged()
    {
        var cfg = "[wsl2]\nvmSwitch=External Switch\n";
        WslNetworkModeDetector.ParseConfig(cfg).Should().Be(WslNetworkingMode.Bridged);
    }

    [Fact]
    public void Parse_ExplicitBridged_ReturnsBridged()
    {
        var cfg = "[wsl2]\nnetworkingMode=bridged\nvmSwitch=LAN\n";
        WslNetworkModeDetector.ParseConfig(cfg).Should().Be(WslNetworkingMode.Bridged);
    }

    [Fact]
    public void Parse_KeyOutsideWsl2Section_Ignored()
    {
        var cfg = "[experimental]\nnetworkingMode=mirrored\n";
        WslNetworkModeDetector.ParseConfig(cfg).Should().Be(WslNetworkingMode.Nat);
    }
}
```

**Step 2: Run — verify fail.**

**Step 3: Implement.** In `Enums.cs` add:

```csharp
public enum WslNetworkingMode
{
    Nat,
    Mirrored,
    Bridged
}
```

Create `WslNetworkModeDetector.cs`:

```csharp
using System.Diagnostics;
using System.Net;
using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

/// <summary>
/// Detects WSL2's networking mode from %USERPROFILE%\.wslconfig.
/// NAT is the default when no config exists. Bridged is implied by a
/// vmSwitch entry. Guest IP is fetched opportunistically via `wsl hostname -I`
/// (cached) for Bridged-mode traffic tagging; failure degrades silently.
/// </summary>
public class WslNetworkModeDetector
{
    private IPAddress? _cachedGuestIp;
    private DateTime _guestIpFetchedAt;
    private static readonly TimeSpan GuestIpTtl = TimeSpan.FromSeconds(60);

    public WslNetworkingMode DetectMode()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wslconfig");
        string? content = null;
        try { if (File.Exists(path)) content = File.ReadAllText(path); } catch { }
        return ParseConfig(content);
    }

    public static WslNetworkingMode ParseConfig(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return WslNetworkingMode.Nat;

        bool inWsl2 = false;
        string? mode = null;
        bool hasVmSwitch = false;

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inWsl2 = line.Equals("[wsl2]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inWsl2 || line.StartsWith('#')) continue;

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            if (key.Equals("networkingMode", StringComparison.OrdinalIgnoreCase))
                mode = value;
            else if (key.Equals("vmSwitch", StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrEmpty(value))
                hasVmSwitch = true;
        }

        if (mode?.Equals("mirrored", StringComparison.OrdinalIgnoreCase) == true)
            return WslNetworkingMode.Mirrored;
        if (mode?.Equals("bridged", StringComparison.OrdinalIgnoreCase) == true || hasVmSwitch)
            return WslNetworkingMode.Bridged;
        return WslNetworkingMode.Nat;
    }

    /// <summary>Fetches the WSL guest IP via `wsl hostname -I` (cached 60s).
    /// Returns null on any failure.</summary>
    public IPAddress? GetGuestIp()
    {
        if (_cachedGuestIp != null && DateTime.UtcNow - _guestIpFetchedAt < GuestIpTtl)
            return _cachedGuestIp;
        try
        {
            var psi = new ProcessStartInfo("wsl.exe", "hostname -I")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return null; }
            var first = output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (first != null && IPAddress.TryParse(first, out var ip))
            {
                _cachedGuestIp = ip;
                _guestIpFetchedAt = DateTime.UtcNow;
                return ip;
            }
        }
        catch { }
        return null;
    }
}
```

**Step 4: Run — verify pass.**

**Step 5: Commit**

```bash
git add src/WinFWManager.Core/Services/WslNetworkModeDetector.cs src/WinFWManager.Core/Models/Enums.cs tests/WinFWManager.Tests/Services/WslNetworkModeDetectorTests.cs
git commit -m "feat: add WslNetworkModeDetector for NAT/Mirrored/Bridged"
```

---

## Phase 4 — ETW monitor rewrite

### Task 8: Rewrite EtwTrafficMonitor on the TCPIP manifest provider

**Files:**
- Modify: `src/WinFWManager.Core/Services/EtwTrafficMonitor.cs` (full rewrite)

No unit tests (requires a live elevated ETW session); correctness comes from Tasks 2–5's tested components. Verify by build + the manual matrix in Task 13.

**Step 1: Rewrite the file** (replace entire contents):

```csharp
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

/// <summary>
/// Real-time traffic capture via the Microsoft-Windows-TCPIP manifest provider.
///
/// Connection-lifecycle and UDP-message events produce allowed-traffic events
/// (with PID); network+transport packet-drop events are merged by
/// <see cref="DropCorrelator"/> into drop events carrying IfIndex and a
/// readable reason. Interface attribution happens downstream: IfIndex (exact)
/// when present, IP/subnet matching (derived) otherwise — see
/// NetworkInterfaceService.
///
/// WSL note: host&lt;-&gt;guest traffic IS visible here, including firewall
/// drops (verified empirically: TcpipNetworkPacketDrops carries the WSL
/// adapter's IfIndex). WSL2 guest→internet traffic in NAT mode is
/// NAT-forwarded by WinNAT, never becomes a host socket, and is not
/// observable by any host-level ETW provider; capturing it would require
/// adapter-level capture (pktmon/NDIS) — out of scope.
/// </summary>
public class EtwTrafficMonitor : IEtwTrafficMonitor
{
    private TraceEventSession? _session;
    private Thread? _processingThread;
    private Timer? _flushTimer;
    private readonly Subject<TrafficEvent> _subject = new();
    private readonly DropCorrelator _dropCorrelator = new();
    private volatile bool _isRunning;
    private const string SessionName = "WinFWManagerETW";
    private static readonly Guid TcpIpProviderGuid = new("2f07e2ee-15db-40f1-90ef-9d7ba282188a");

    public IObservable<TrafficEvent> TrafficEvents => _subject.AsObservable();
    public bool IsRunning => _isRunning;
    public bool RequiresAdmin => !IsAdmin();

    public void Start()
    {
        if (_isRunning) return;
        if (!IsAdmin())
            throw new UnauthorizedAccessException("ETW requires administrator privileges.");

        try { TraceEventSession.GetActiveSession(SessionName)?.Dispose(); } catch { }

        _session = new TraceEventSession(SessionName);
        _session.EnableProvider(TcpIpProviderGuid, TraceEventLevel.Verbose, ulong.MaxValue);

        _session.Source.Dynamic.All += OnEvent;

        _isRunning = true;
        _processingThread = new Thread(() => { try { _session.Source.Process(); } catch { } })
        {
            IsBackground = true,
            Name = "ETW-TCPIP-Processor"
        };
        _processingThread.Start();

        // Periodically flush uncorrelated drop halves as standalone events.
        _flushTimer = new Timer(_ =>
        {
            try
            {
                foreach (var evt in _dropCorrelator.FlushExpired())
                    _subject.OnNext(evt);
            }
            catch { }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void OnEvent(TraceEvent data)
    {
        // Hard filter on event name BEFORE touching payloads — the provider
        // emits hundreds of event types per second.
        string name = data.EventName;
        bool isFlow = TcpIpEventParser.FlowEventNames.Contains(name);
        bool isDrop = !isFlow && TcpIpEventParser.DropEventNames.Contains(name);
        if (!isFlow && !isDrop) return;

        try
        {
            var fields = ExtractFields(data);
            if (isFlow)
            {
                var evt = TcpIpEventParser.Parse(name, fields, data.TimeStamp);
                if (evt != null) _subject.OnNext(evt);
                return;
            }

            var drop = TcpIpEventParser.TryParseDrop(name, fields, data.TimeStamp);
            if (drop != null)
            {
                var merged = _dropCorrelator.Add(drop);
                if (merged != null) _subject.OnNext(merged);
            }
        }
        catch { /* skip malformed events */ }
    }

    private static Dictionary<string, object?> ExtractFields(TraceEvent data)
    {
        var fields = new Dictionary<string, object?>();
        var names = data.PayloadNames;
        for (int i = 0; i < names.Length; i++)
        {
            try { fields[names[i]] = data.PayloadValue(i); } catch { }
        }
        return fields;
    }

    public void Stop()
    {
        _isRunning = false;
        _flushTimer?.Dispose();
        _flushTimer = null;
        _session?.Stop();
        _session?.Dispose();
        _session = null;
    }

    public void Dispose()
    {
        Stop();
        _subject.Dispose();
        GC.SuppressFinalize(this);
    }

    private static bool IsAdmin()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}
```

**Step 2: Build**

```
cd C:\Claude\WinFWManager-clone && dotnet build src/WinFWManager.Core -clp:ErrorsOnly
```
Expected: Build succeeded. (If `PayloadValue(i)` does not exist on this TraceEvent version, use `data.PayloadByName(names[i])` instead.)

**Step 3: Run full test suite** — all green (no test touches the monitor).

**Step 4: Commit**

```bash
git add src/WinFWManager.Core/Services/EtwTrafficMonitor.cs
git commit -m "feat: migrate EtwTrafficMonitor to Microsoft-Windows-TCPIP manifest provider"
```

---

## Phase 5 — Enrichment & UI

### Task 9: ViewModel enrichment — IfIndex first, subnet fallback

**Files:**
- Modify: `src/WinFWManager/ViewModels/TrafficMonitorViewModel.cs` (the enrichment block in `OnEventBatch`, currently ~lines 79–90)
- Modify: `src/WinFWManager/ServiceCollectionExtensions.cs` (register `WslNetworkModeDetector`)

**Step 1: Replace the NIC-resolution block** in `OnEventBatch` with:

```csharp
            // Resolve NIC: IfIndex from ETW is authoritative; otherwise match
            // the local endpoint (and remote peer for host<->VM traffic) by IP.
            NetworkAdapterInfo? adapter = null;
            if (evt.InterfaceIndexHint is int ifIndex)
                adapter = _nicService.ResolveByIfIndex(ifIndex);
            if (adapter != null)
            {
                evt.IsInterfaceExact = true;
            }
            else
            {
                var local = evt.Direction == TrafficDirection.Outbound
                    ? evt.SourceAddress : evt.DestinationAddress;
                var remote = evt.Direction == TrafficDirection.Outbound
                    ? evt.DestinationAddress : evt.SourceAddress;
                adapter = _nicService.ResolveAdapter(local, remote);
            }
            if (adapter != null)
            {
                evt.InterfaceName = adapter.Name;
                evt.AdapterType = adapter.AdapterType;
            }
```

**Step 2: Add mirrored-mode banner support.** Constructor takes `WslNetworkModeDetector detector`; add:

```csharp
    [ObservableProperty] private bool _showMirroredBanner;
```

and in the constructor:

```csharp
        ShowMirroredBanner = detector.DetectMode() == WslNetworkingMode.Mirrored;
```

(add `using WinFWManager.Core.Models;` is already present).

**Step 3: Register in DI.** In `ServiceCollectionExtensions.cs` add:

```csharp
        services.AddSingleton<WslNetworkModeDetector>();
```

**Step 4: Build the WPF project**

```
dotnet build src/WinFWManager/WinFWManager.csproj -clp:ErrorsOnly
```
Expected: Build succeeded. (App must not be running — the exe gets locked.)

**Step 5: Commit**

```bash
git add src/WinFWManager/ViewModels/TrafficMonitorViewModel.cs src/WinFWManager/ServiceCollectionExtensions.cs
git commit -m "feat: enrich traffic with IfIndex-first adapter resolution and mirrored banner flag"
```

### Task 10: Traffic Monitor XAML — Action tooltip, Flow column, derived-NIC style, banner

**Files:**
- Modify: `src/WinFWManager/Views/TrafficMonitorView.xaml`

**Step 1: Update the columns block.** Replace the `NIC` and `Action` columns and add `Flow`:

```xml
                <DataGridTextColumn Header="NIC" Binding="{Binding InterfaceName}" Width="220">
                    <DataGridTextColumn.ElementStyle>
                        <Style TargetType="TextBlock">
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding IsInterfaceExact}" Value="False">
                                    <Setter Property="FontStyle" Value="Italic"/>
                                    <Setter Property="Opacity" Value="0.65"/>
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </DataGridTextColumn.ElementStyle>
                </DataGridTextColumn>
```

```xml
                <DataGridTextColumn Header="Action" Binding="{Binding Action}" Width="60">
                    <DataGridTextColumn.CellStyle>
                        <Style TargetType="DataGridCell">
                            <Setter Property="ToolTip" Value="{Binding DropReason}"/>
                        </Style>
                    </DataGridTextColumn.CellStyle>
                </DataGridTextColumn>
```

After the Action column, add:

```xml
                <DataGridTextColumn Header="Flow" Binding="{Binding FlowDescription}" Width="230"/>
```

**Step 2: Add the mirrored banner** directly after the filter `<Border>` (as a sibling, inside the outer Grid, still Grid.Row="0" — wrap both in a StackPanel or give the banner its own row; simplest: put it inside the filter Border's StackPanel, after the Tip TextBlock):

```xml
                <Border Background="#33427EB5" CornerRadius="3" Padding="6,3" Margin="0,4,0,0"
                        Visibility="{Binding ShowMirroredBanner, Converter={StaticResource BoolToVisibilityConverter}}">
                    <TextBlock Text="WSL runs in mirrored networking mode — WSL traffic shares the host's IP and appears as host traffic."
                               Foreground="{DynamicResource PrimaryTextBrush}" FontSize="11"/>
                </Border>
```

Check `App.xaml`/`DarkTheme.xaml` for an existing `BoolToVisibilityConverter` resource; if none exists, use the built-in `<BooleanToVisibilityConverter x:Key="BoolToVisibilityConverter"/>` declared in the UserControl's resources.

**Step 3: Build** (`dotnet build src/WinFWManager/WinFWManager.csproj -clp:ErrorsOnly`). Expected: success.

**Step 4: Commit**

```bash
git add src/WinFWManager/Views/TrafficMonitorView.xaml
git commit -m "feat: add Flow column, drop-reason tooltip, derived-NIC style and mirrored banner"
```

### Task 11: Dashboard graph — WSL guest highlighting & blocked edges

**Files:**
- Modify: `src/WinFWManager/ViewModels/GraphModels.cs`
- Modify: `src/WinFWManager/ViewModels/DashboardViewModel.cs`
- Modify: `src/WinFWManager/Views/DashboardView.xaml.cs`

**Step 1: Extend models.** In `GraphModels.cs`:
- `GraphNode`: add `public bool IsWslGuest { get; set; }`
- `GraphEdge`: add `public List<string> DropReasons { get; set; } = new();`

**Step 2: Classify remote nodes + collect drop reasons in `DashboardViewModel.BuildGraphData`.**
- Where remote nodes are created (after the `remoteInfo` loop), set `IsWslGuest = true` when `_nicService.ResolveAdapter(null, IPAddress.Parse(remoteIp))?.AdapterType == AdapterType.WSL` (guard TryParse).
- In the per-event loop, when `evt.Action is Block or Drop` and `evt.DropReason != null`, add the reason to the edge's reason set (dedupe; store alongside `edgePorts` in a `Dictionary<(string,string), HashSet<string>>` and copy into `GraphEdge.DropReasons` when edges are built).

**Step 3: Render.** In `DashboardView.xaml.cs`:
- Node fill switch (~line 148): before the `AdapterType` switch, if `node.IsWslGuest` use the same yellow brush as `AdapterType.WSL` (`WslBrush` / `#FFC107`-family — reuse whatever the WSL case uses).
- Edge drawing: where the edge `Line`/`Path` stroke is set, if `edge.AllowedCount == 0 && edge.BlockedCount > 0`, set the stroke to the danger brush and `StrokeDashArray = new DoubleCollection { 4, 3 }`.
- Edge tooltip builder: append a line per entry in `edge.DropReasons` (e.g. `⛔ Firewall (WFP filter)`).

The executor should read `DashboardView.xaml.cs` fully first — the exact insertion points are in the node-drawing and edge-drawing methods; follow existing brush lookup patterns (`TryFindResource`).

**Step 4: Build.** Expected: success.

**Step 5: Commit**

```bash
git add src/WinFWManager/ViewModels/GraphModels.cs src/WinFWManager/ViewModels/DashboardViewModel.cs src/WinFWManager/Views/DashboardView.xaml.cs
git commit -m "feat: highlight WSL guest and blocked flows in dashboard graph"
```

### Task 12: Network Interfaces tab — WSL mode badge

**Files:**
- Modify: `src/WinFWManager/ViewModels/NetworkInterfacesViewModel.cs`
- Modify: `src/WinFWManager/Views/NetworkInterfacesView.xaml`

**Step 1: ViewModel.** Inject `WslNetworkModeDetector`; add:

```csharp
    [ObservableProperty] private string _wslModeText = "";
```

Set it in `RefreshAsync` (so it updates with the adapter list):

```csharp
        var mode = _wslDetector.DetectMode();
        var guestIp = mode == WslNetworkingMode.Nat || mode == WslNetworkingMode.Bridged
            ? _wslDetector.GetGuestIp() : null;
        WslModeText = guestIp != null
            ? $"WSL networking: {mode}  •  guest IP {guestIp}"
            : $"WSL networking: {mode}";
```

**Step 2: XAML.** Read `NetworkInterfacesView.xaml`, find the header/toolbar area (where the Refresh button lives) and add:

```xml
        <TextBlock Text="{Binding WslModeText}" Foreground="{DynamicResource SecondaryTextBrush}"
                   VerticalAlignment="Center" Margin="12,0,0,0" FontSize="11"/>
```

**Step 3: Build.** Expected: success.

**Step 4: Commit**

```bash
git add src/WinFWManager/ViewModels/NetworkInterfacesViewModel.cs src/WinFWManager/Views/NetworkInterfacesView.xaml
git commit -m "feat: show WSL networking mode badge in Network Interfaces tab"
```

---

## Phase 6 — Docs & verification

### Task 13: README update, full test run, manual verification

**Files:**
- Modify: `README.md`

**Step 1: Update README.** In the Traffic Monitor section, replace the known-limitation blockquote's first sentence framing: WSL→host traffic (both allowed and firewall-dropped) is now captured via the `Microsoft-Windows-TCPIP` provider with exact adapter attribution and drop reasons; the remaining limitation is WSL2 guest→internet NAT-forwarded traffic (unchanged, pktmon/NDIS required). Also mention the new Flow column and real Allow/Drop actions. Update the "Key technologies" ETW bullet to name the TCPIP manifest provider.

**Step 2: Full test suite**

```
cd C:\Claude\WinFWManager-clone && dotnet test
```
Expected: all tests pass (49 pre-existing + ~25 new).

**Step 3: Manual verification matrix** (requires the user to run the app elevated; coordinate with them):
1. **Host→WSL allowed:** start `python3 -m http.server 9099` in WSL, `Invoke-WebRequest http://<guest-ip>:9099/` from host → yellow rows, NIC = `vEthernet (WSL (Hyper-V firewall))` (non-italic = exact when drops occur; connection events are subnet-derived italic), Action = Allow, Flow = `vEthernet (WSL (Hyper-V firewall)) → WSL guest ✓`.
2. **WSL→host dropped:** `curl http://172.26.112.1:9099/` from WSL (no firewall rule) → **red row**, Action = Drop, tooltip `Firewall (WFP filter)`, Flow = `WSL guest → vEthernet (WSL (Hyper-V firewall)) ⛔`, NIC non-italic (exact, from IfIndex 33).
3. **WSL→host allowed:** add Hyper-V firewall rule (`New-NetFirewallHyperVRule ... -LocalPorts 9099 -Action Allow -VMCreatorId '{40E0AC32-46A5-438A-A0B2-2B479E8F2E90}'`), repeat → green Inbound rows. Remove rule after.
4. **Host→internet:** browse something → Outbound Allow rows attributed to `Ethernet`.
5. **Dashboard:** WSL guest node yellow; a fully-blocked edge dashed red with reason in tooltip.
6. **Network Interfaces:** badge shows `WSL networking: Nat • guest IP 172.26.x.x`.

**Step 4: Commit**

```bash
git add README.md
git commit -m "docs: update README for TCPIP provider migration and WSL traffic visibility"
```

**Step 5: Push and update PR #1**

```bash
git push lyn fix/wsl-hyperv-identification
```

---

## Notes for the executor

- **Never run `dotnet build`/`dotnet test` while WinFWManager.exe is running** — the exe gets file-locked (MSB3027). Ask the user to close the app first.
- Restore uses the internal Nexus feeds in `NuGet.config` (V3 proxy + hosted); commands may need network access outside the sandbox.
- `NuGet.config` is intentionally untracked — do not `git add` it.
- Direction semantics for `TcpConnectionRundown` are approximated as Outbound (the rundown doesn't distinguish); acceptable for the initial snapshot.
- If `TcpAcceptListenerComplete` payloads lack `LocalAddress`/`RemoteAddress` at runtime, the parser returns null — inbound TCP then surfaces via data-path events only; refine empirically later.
- UDP message events fire per batch and can be chatty; the existing 100 ms UI batching + ring buffer absorb this. If UI pressure is observed, add endpoint-level dedupe as a follow-up (YAGNI now).
- DropCorrelator keys expiry on its own arrival clock (not obs.Timestamp), so TraceEvent's local-time timestamps are safe to pass through.
