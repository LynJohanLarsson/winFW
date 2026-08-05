using System.Net;
using WinFWManager.Core.Models;
using WinFWManager.Core.Services;

namespace WinFWManager.Tests.Services;

public class NetworkInterfaceServiceTests
{
    private static readonly NetworkAdapterInfo Physical = new()
    {
        Name = "Ethernet",
        AdapterType = AdapterType.Physical,
        IpAddresses = { IPAddress.Parse("192.168.1.10") },
        Subnets = { new IpSubnet(IPAddress.Parse("192.168.1.10"), 24) },
    };

    private static readonly NetworkAdapterInfo Wsl = new()
    {
        Name = "vEthernet (WSL)",
        AdapterType = AdapterType.WSL,
        IpAddresses = { IPAddress.Parse("172.20.0.1") },
        Subnets = { new IpSubnet(IPAddress.Parse("172.20.0.1"), 20) },
    };

    private static readonly NetworkAdapterInfo HyperV = new()
    {
        Name = "vEthernet (Default Switch)",
        AdapterType = AdapterType.VSwitch,
        IpAddresses = { IPAddress.Parse("172.30.0.1") },
        Subnets = { new IpSubnet(IPAddress.Parse("172.30.0.1"), 20) },
    };

    private static readonly IReadOnlyList<NetworkAdapterInfo> Adapters = new[] { Physical, Wsl, HyperV };

    [Fact]
    public void ResolveAdapter_LocalExactMatch_ReturnsOwningAdapter()
    {
        var a = NetworkInterfaceService.ResolveAdapterFrom(
            Adapters, IPAddress.Parse("192.168.1.10"), IPAddress.Parse("8.8.8.8"));
        a.Should().BeSameAs(Physical);
    }

    [Fact]
    public void ResolveAdapter_LocalInPhysicalSubnet_ReturnsPhysical()
    {
        var a = NetworkInterfaceService.ResolveAdapterFrom(
            Adapters, IPAddress.Parse("192.168.1.55"), IPAddress.Parse("8.8.8.8"));
        a.Should().BeSameAs(Physical);
    }

    [Fact]
    public void ResolveAdapter_RemotePeerInWslSubnet_ReturnsWsl()
    {
        // Host<->WSL: the local endpoint isn't a known adapter address, but the
        // peer (WSL guest) IP falls inside the WSL adapter's subnet.
        var a = NetworkInterfaceService.ResolveAdapterFrom(
            Adapters, IPAddress.Parse("100.100.100.100"), IPAddress.Parse("172.20.15.9"));
        a.Should().BeSameAs(Wsl);
    }

    [Fact]
    public void ResolveAdapter_RemotePeerInHyperVSubnet_ReturnsHyperV()
    {
        var a = NetworkInterfaceService.ResolveAdapterFrom(
            Adapters, null, IPAddress.Parse("172.30.4.5"));
        a.Should().BeSameAs(HyperV);
    }

    [Fact]
    public void ResolveAdapter_NoMatch_ReturnsNull()
    {
        var a = NetworkInterfaceService.ResolveAdapterFrom(
            Adapters, IPAddress.Parse("8.8.8.8"), IPAddress.Parse("1.1.1.1"));
        a.Should().BeNull();
    }

    [Fact]
    public void ClassifyAdapter_WSL_ReturnsWSL()
    {
        var svc = new NetworkInterfaceService();
        svc.ClassifyAdapter("vEthernet (WSL)").Should().Be(AdapterType.WSL);
    }

    [Fact]
    public void ClassifyAdapter_HyperVSwitch_ReturnsVSwitch()
    {
        var svc = new NetworkInterfaceService();
        svc.ClassifyAdapter("vEthernet (Default Switch)").Should().Be(AdapterType.VSwitch);
    }

    [Fact]
    public void ClassifyAdapter_Physical_ReturnsPhysical()
    {
        var svc = new NetworkInterfaceService();
        svc.ClassifyAdapter("Ethernet").Should().Be(AdapterType.Physical);
    }

    [Fact]
    public void ClassifyAdapter_Loopback_ReturnsLoopback()
    {
        var svc = new NetworkInterfaceService();
        svc.ClassifyAdapter("Loopback Pseudo-Interface 1").Should().Be(AdapterType.Loopback);
    }

    [Fact]
    public async Task GetAllAdaptersAsync_ReturnsAtLeastOne()
    {
        var svc = new NetworkInterfaceService();
        var adapters = await svc.GetAllAdaptersAsync();
        adapters.Should().NotBeEmpty();
    }

    [Fact]
    public async Task RefreshAsync_DoesNotThrow()
    {
        var svc = new NetworkInterfaceService();
        var act = () => svc.RefreshAsync();
        await act.Should().NotThrowAsync();
    }

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
}
