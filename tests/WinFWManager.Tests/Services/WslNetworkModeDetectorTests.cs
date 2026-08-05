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
