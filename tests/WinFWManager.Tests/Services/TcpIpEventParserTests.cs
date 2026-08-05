using System.Net;
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
        => TcpIpEventParser.Parse("TcpTcbStartTimer", new Dictionary<string, object?>(), DateTime.UtcNow).Should().BeNull();

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
        drop.Reason.Should().Be("Endpoint not found (no listener)");
    }

    [Fact]
    public void TryParseDrop_TransportDrop_MapsIpTransportProtocol()
    {
        var fields = new Dictionary<string, object?>
        {
            ["LocalSockAddr"] = V4("172.26.112.1", 9099),
            ["RemoteSockAddr"] = V4("172.26.127.184", 44216),
            ["Reason"] = 4,
            ["IPTransportProtocol"] = 6
        };
        var drop = TcpIpEventParser.TryParseDrop("TcpipTransportPacketDrops", fields, DateTime.UtcNow);

        drop!.Protocol.Should().Be(TransportProtocol.TCP);
    }
}
