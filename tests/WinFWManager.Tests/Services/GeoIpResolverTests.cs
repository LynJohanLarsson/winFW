using System.Net;
using WinFWManager.Core.Services;

namespace WinFWManager.Tests.Services;

public class GeoIpResolverTests
{
    [Fact]
    public void Resolve_PrivateAddress_ReturnsPrivate()
    {
        var resolver = new GeoIpResolver(mmdbPath: null);
        var info = resolver.Resolve(IPAddress.Parse("192.168.1.1"));

        info.IsPrivate.Should().BeTrue();
        info.DisplayCountry.Should().Be("Private");
    }

    [Fact]
    public void Resolve_LoopbackAddress_ReturnsPrivate()
    {
        var resolver = new GeoIpResolver(mmdbPath: null);
        var info = resolver.Resolve(IPAddress.Loopback);

        info.IsPrivate.Should().BeTrue();
    }

    [Fact]
    public void Resolve_Rfc1918_10Network_ReturnsPrivate()
    {
        var resolver = new GeoIpResolver(mmdbPath: null);
        var info = resolver.Resolve(IPAddress.Parse("10.0.0.1"));

        info.IsPrivate.Should().BeTrue();
    }

    [Fact]
    public void Resolve_Rfc1918_172Network_ReturnsPrivate()
    {
        var resolver = new GeoIpResolver(mmdbPath: null);
        var info = resolver.Resolve(IPAddress.Parse("172.16.0.1"));

        info.IsPrivate.Should().BeTrue();
    }

    [Fact]
    public void Resolve_PublicAddress_WithoutMmdb_ReturnsUnknown()
    {
        var resolver = new GeoIpResolver(mmdbPath: null);
        var info = resolver.Resolve(IPAddress.Parse("8.8.8.8"));

        info.IsPrivate.Should().BeFalse();
        info.DisplayCountry.Should().Be("Unknown");
    }

    [Fact]
    public void Resolve_SameAddressTwice_ReturnsCached()
    {
        var resolver = new GeoIpResolver(mmdbPath: null);
        var first = resolver.Resolve(IPAddress.Parse("192.168.1.1"));
        var second = resolver.Resolve(IPAddress.Parse("192.168.1.1"));

        first.Should().BeSameAs(second);
    }

    [Fact]
    public async Task ReverseDnsAsync_Localhost_ReturnsHostname()
    {
        var resolver = new GeoIpResolver(mmdbPath: null);
        var hostname = await resolver.ReverseDnsAsync(IPAddress.Loopback);

        hostname.Should().NotBeNullOrEmpty();
    }
}
