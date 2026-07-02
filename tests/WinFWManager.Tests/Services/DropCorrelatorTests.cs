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
