using System.Net;
using FluentAssertions;
using WinFWManager.Core.Models;
using WinFWManager.Core.Services;
using Xunit;

namespace WinFWManager.Tests.Services;

public class TrafficEventFilterTests
{
    private static TrafficEvent Evt() => new()
    {
        SourceAddress = IPAddress.Parse("172.26.127.184"),
        SourcePort = 44216,
        DestinationAddress = IPAddress.Parse("172.26.112.1"),
        DestinationPort = 9099,
        Protocol = TransportProtocol.TCP,
        ProcessName = "chrome.exe",
        InterfaceName = "vEthernet (WSL (Hyper-V firewall))",
        Action = TrafficAction.Drop
    };

    [Fact]
    public void EmptyFilter_MatchesEverything()
        => new TrafficEventFilter().Matches(Evt()).Should().BeTrue();

    [Fact]
    public void SubstringMatch_CaseInsensitive()
        => new TrafficEventFilter { Nic = "wsl" }.Matches(Evt()).Should().BeTrue();

    [Fact]
    public void NegatedTerm_Excludes()
        => new TrafficEventFilter { Process = "!chrome" }.Matches(Evt()).Should().BeFalse();

    [Fact]
    public void CommaSeparatedPositives_OrLogic()
        => new TrafficEventFilter { Process = "firefox,chrome" }.Matches(Evt()).Should().BeTrue();

    [Fact]
    public void Action_FiltersOnEnumText()
    {
        new TrafficEventFilter { Action = "drop" }.Matches(Evt()).Should().BeTrue();
        new TrafficEventFilter { Action = "!drop" }.Matches(Evt()).Should().BeFalse();
    }

    [Fact]
    public void Port_ExactMatchOnly()
    {
        new TrafficEventFilter { DstPort = "9099" }.Matches(Evt()).Should().BeTrue();
        new TrafficEventFilter { DstPort = "909" }.Matches(Evt()).Should().BeFalse("ports are exact, not substring");
        new TrafficEventFilter { DstPort = "!9099" }.Matches(Evt()).Should().BeFalse();
    }

    [Fact]
    public void MultipleFields_AndLogic()
    {
        new TrafficEventFilter { Nic = "wsl", Action = "drop" }.Matches(Evt()).Should().BeTrue();
        new TrafficEventFilter { Nic = "wsl", Action = "allow" }.Matches(Evt()).Should().BeFalse();
    }

    [Fact]
    public void Clear_ResetsAllFieldsAndIsEmpty()
    {
        var f = new TrafficEventFilter { SourceIp = "1", SrcPort = "2", DestIp = "3", DstPort = "4",
            Protocol = "tcp", Process = "x", Nic = "y", Action = "drop" };
        f.IsEmpty.Should().BeFalse();
        f.Clear();
        f.IsEmpty.Should().BeTrue();
        f.Matches(Evt()).Should().BeTrue();
    }

    [Fact]
    public void NullFieldValues_DoNotMatchPositiveTerms()
    {
        var evt = new TrafficEvent();  // null addresses, null process/interface
        new TrafficEventFilter { Process = "chrome" }.Matches(evt).Should().BeFalse();
        new TrafficEventFilter { Process = "!chrome" }.Matches(evt).Should().BeTrue();
    }
}
