using System.Net;
using WinFWManager.Core.Models;

namespace WinFWManager.Tests.Models;

public class IpSubnetTests
{
    [Theory]
    [InlineData("192.168.1.0", 24, "192.168.1.50", true)]
    [InlineData("192.168.1.0", 24, "192.168.2.50", false)]
    [InlineData("172.20.0.1", 20, "172.20.15.2", true)]   // WSL-style /20 guest IP
    [InlineData("172.20.0.1", 20, "172.21.0.2", false)]
    [InlineData("10.0.0.0", 8, "10.255.255.255", true)]
    [InlineData("127.0.0.0", 8, "127.0.0.1", true)]
    public void Contains_Ipv4(string network, int prefix, string test, bool expected)
    {
        var subnet = new IpSubnet(IPAddress.Parse(network), prefix);
        subnet.Contains(IPAddress.Parse(test)).Should().Be(expected);
    }

    [Fact]
    public void Contains_DerivesNetworkFromHostAddress()
    {
        // Constructed from a host address, not the network address.
        var subnet = new IpSubnet(IPAddress.Parse("192.168.1.77"), 24);
        subnet.Contains(IPAddress.Parse("192.168.1.1")).Should().BeTrue();
        subnet.Network.Should().Be(IPAddress.Parse("192.168.1.0"));
    }

    [Fact]
    public void Contains_Ipv6()
    {
        var subnet = new IpSubnet(IPAddress.Parse("fe80::1"), 64);
        subnet.Contains(IPAddress.Parse("fe80::abcd")).Should().BeTrue();
        subnet.Contains(IPAddress.Parse("fe81::1")).Should().BeFalse();
    }

    [Fact]
    public void Contains_MismatchedFamily_ReturnsFalse()
    {
        var subnet = new IpSubnet(IPAddress.Parse("192.168.1.0"), 24);
        subnet.Contains(IPAddress.Parse("fe80::1")).Should().BeFalse();
    }
}
