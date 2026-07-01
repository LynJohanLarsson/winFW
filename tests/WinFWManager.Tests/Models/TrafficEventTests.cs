using FluentAssertions;
using WinFWManager.Core.Models;
using System.Net;

namespace WinFWManager.Tests.Models;

public class TrafficEventTests
{
    [Fact]
    public void TrafficEvent_ShouldStoreAllProperties()
    {
        var evt = new TrafficEvent
        {
            Timestamp = new DateTime(2026, 2, 27, 10, 0, 0),
            Direction = TrafficDirection.Inbound,
            Protocol = TransportProtocol.TCP,
            SourceAddress = IPAddress.Parse("192.168.1.100"),
            SourcePort = 54321,
            DestinationAddress = IPAddress.Parse("10.0.0.1"),
            DestinationPort = 443,
            Action = TrafficAction.Allow,
            ProcessId = 1234,
            ProcessName = "chrome.exe",
            InterfaceName = "Ethernet",
            Profile = FirewallProfile.Private,
            Country = "US",
            Hostname = "example.com"
        };

        evt.Timestamp.Should().Be(new DateTime(2026, 2, 27, 10, 0, 0));
        evt.Direction.Should().Be(TrafficDirection.Inbound);
        evt.Protocol.Should().Be(TransportProtocol.TCP);
        evt.SourceAddress.Should().Be(IPAddress.Parse("192.168.1.100"));
        evt.SourcePort.Should().Be(54321);
        evt.DestinationAddress.Should().Be(IPAddress.Parse("10.0.0.1"));
        evt.DestinationPort.Should().Be(443);
        evt.Action.Should().Be(TrafficAction.Allow);
        evt.ProcessId.Should().Be(1234);
        evt.ProcessName.Should().Be("chrome.exe");
        evt.InterfaceName.Should().Be("Ethernet");
        evt.Profile.Should().Be(FirewallProfile.Private);
        evt.Country.Should().Be("US");
        evt.Hostname.Should().Be("example.com");
    }

    [Fact]
    public void IsWslTraffic_WhenInterfaceContainsWSL_ReturnsTrue()
    {
        var evt = new TrafficEvent { InterfaceName = "vEthernet (WSL)" };
        evt.IsWslTraffic.Should().BeTrue();
    }

    [Fact]
    public void IsWslTraffic_WhenPhysicalAdapter_ReturnsFalse()
    {
        var evt = new TrafficEvent { InterfaceName = "Ethernet" };
        evt.IsWslTraffic.Should().BeFalse();
    }

    [Fact]
    public void IsHyperVTraffic_WhenInterfaceContainsHyperV_ReturnsTrue()
    {
        var evt = new TrafficEvent { InterfaceName = "vEthernet (Default Switch)" };
        evt.IsHyperVTraffic.Should().BeTrue();
    }

    [Fact]
    public void IsWslTraffic_WhenAdapterTypeWsl_ReturnsTrue()
    {
        var evt = new TrafficEvent { AdapterType = AdapterType.WSL };
        evt.IsWslTraffic.Should().BeTrue();
    }

    [Fact]
    public void IsHyperVTraffic_WhenAdapterTypeVSwitch_ReturnsTrue()
    {
        var evt = new TrafficEvent { AdapterType = AdapterType.VSwitch };
        evt.IsHyperVTraffic.Should().BeTrue();
        evt.IsWslTraffic.Should().BeFalse();
    }

    [Fact]
    public void IsHyperVTraffic_WslAdapterIsNotClassifiedHyperV()
    {
        var evt = new TrafficEvent { AdapterType = AdapterType.WSL, InterfaceName = "vEthernet (WSL)" };
        evt.IsWslTraffic.Should().BeTrue();
        evt.IsHyperVTraffic.Should().BeFalse();
    }

    [Fact]
    public void IsPrivateAddress_WhenRfc1918_ReturnsTrue()
    {
        var evt = new TrafficEvent { DestinationAddress = IPAddress.Parse("192.168.1.1") };
        evt.IsDestinationPrivate.Should().BeTrue();
    }
}
