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
