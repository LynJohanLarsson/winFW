using FluentAssertions;
using System.Net;
using WinFWManager.Core.Models;
using WinFWManager.Core.Services;

namespace WinFWManager.Tests.Services;

public class TrafficGraphBuilderTests
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
        IpAddresses = { IPAddress.Parse("172.26.112.1") },
        Subnets = { new IpSubnet(IPAddress.Parse("172.26.112.1"), 20) },
    };

    private static readonly IReadOnlyList<NetworkAdapterInfo> Adapters = new[] { Physical, Wsl };

    private static readonly ISet<RemoteGroupKind> NoneExpanded = new HashSet<RemoteGroupKind>();

    private static TrafficEvent Evt(
        string? process = "chrome.exe",
        string? nic = "Ethernet",
        string? remote = "8.8.8.8",
        TrafficAction action = TrafficAction.Allow,
        TrafficDirection direction = TrafficDirection.Outbound,
        int dstPort = 443,
        TransportProtocol protocol = TransportProtocol.TCP,
        string? dropReason = null,
        string? country = null)
    {
        var remoteIp = remote is null ? null : IPAddress.Parse(remote);
        return new TrafficEvent
        {
            ProcessName = process,
            InterfaceName = nic,
            Direction = direction,
            SourceAddress = direction == TrafficDirection.Outbound
                ? IPAddress.Parse("192.168.1.10") : remoteIp,
            DestinationAddress = direction == TrafficDirection.Outbound
                ? remoteIp : IPAddress.Parse("192.168.1.10"),
            DestinationPort = dstPort,
            Protocol = protocol,
            Action = action,
            DropReason = dropReason,
            Country = country,
            AdapterType = nic == "vEthernet (WSL)" ? AdapterType.WSL : AdapterType.Physical,
        };
    }

    // ---------- (1) Basic three-layer aggregation ----------

    [Fact]
    public void Build_BasicThreeLayers_AggregatesAllowedAndBlockedCounts()
    {
        var events = new[]
        {
            Evt(action: TrafficAction.Allow),
            Evt(action: TrafficAction.Drop),
            Evt(action: TrafficAction.Block),
        };

        var data = TrafficGraphBuilder.Build(events, Adapters, NoneExpanded);

        var proc = data.Nodes.Single(n => n.Kind == GraphNodeKind.Process);
        proc.Id.Should().Be("proc:chrome.exe");
        proc.Label.Should().Be("chrome.exe");
        proc.ConnectionCount.Should().Be(3);

        var nic = data.Nodes.Single(n => n.Kind == GraphNodeKind.Adapter);
        nic.Id.Should().Be("nic:Ethernet");
        nic.AdapterType.Should().Be(AdapterType.Physical);

        var group = data.Nodes.Single(n => n.Kind == GraphNodeKind.RemoteGroup);
        group.Id.Should().Be("group:Internet");

        data.Edges.Should().HaveCount(2);
        var procEdge = data.Edges.Single(e => e.SourceId == "proc:chrome.exe");
        procEdge.TargetId.Should().Be("nic:Ethernet");
        procEdge.AllowedCount.Should().Be(1);
        procEdge.BlockedCount.Should().Be(2);

        var remoteEdge = data.Edges.Single(e => e.SourceId == "nic:Ethernet");
        remoteEdge.TargetId.Should().Be("group:Internet");
        remoteEdge.AllowedCount.Should().Be(1);
        remoteEdge.BlockedCount.Should().Be(2);

        data.MaxEdgeCount.Should().Be(3);
    }

    [Fact]
    public void Build_EmptyEvents_ReturnsEmptyGraphWithMaxEdgeCountOne()
    {
        var data = TrafficGraphBuilder.Build(
            Array.Empty<TrafficEvent>(), Adapters, NoneExpanded);

        data.Nodes.Should().BeEmpty();
        data.Edges.Should().BeEmpty();
        data.MaxEdgeCount.Should().Be(1);
    }

    [Fact]
    public void Build_SkipsEventsWithNullRemoteOrMissingInterface()
    {
        var events = new[]
        {
            Evt(remote: null),
            Evt(nic: null),
            Evt(nic: ""),
            Evt(), // only this one counts
        };

        var data = TrafficGraphBuilder.Build(events, Adapters, NoneExpanded);

        data.Nodes.Single(n => n.Kind == GraphNodeKind.Process)
            .ConnectionCount.Should().Be(1);
        data.Edges.Should().OnlyContain(e => e.TotalCount == 1);
    }

    [Fact]
    public void Build_InboundEvent_UsesSourceAddressAsRemote()
    {
        var events = new[]
        {
            Evt(remote: "8.8.4.4", direction: TrafficDirection.Inbound),
        };

        var data = TrafficGraphBuilder.Build(
            events, Adapters, new HashSet<RemoteGroupKind> { RemoteGroupKind.Internet });

        data.Nodes.Should().Contain(n => n.Id == "ip:8.8.4.4");
    }

    // ---------- (2) (system) bucket ----------

    [Fact]
    public void Build_NullOrEmptyProcessName_BucketsIntoSystem()
    {
        var events = new[]
        {
            Evt(process: null),
            Evt(process: ""),
        };

        var data = TrafficGraphBuilder.Build(events, Adapters, NoneExpanded);

        var proc = data.Nodes.Single(n => n.Kind == GraphNodeKind.Process);
        proc.Id.Should().Be("proc:(system)");
        proc.Label.Should().Be(TrafficGraphBuilder.SystemProcessLabel);
        proc.ConnectionCount.Should().Be(2);
    }

    // ---------- (3) (others) aggregation ----------

    [Fact]
    public void Build_ProcessesBeyondMax_AggregateIntoOthers()
    {
        var events = new[]
        {
            Evt(process: "a.exe"), Evt(process: "a.exe"), Evt(process: "a.exe"),
            Evt(process: "b.exe"), Evt(process: "b.exe"),
            Evt(process: "c.exe"),
            Evt(process: "d.exe"),
        };

        var data = TrafficGraphBuilder.Build(events, Adapters, NoneExpanded, maxProcesses: 2);

        var procNodes = data.Nodes.Where(n => n.Kind == GraphNodeKind.Process).ToList();
        procNodes.Should().HaveCount(3);
        procNodes.Should().Contain(n => n.Id == "proc:a.exe" && n.ConnectionCount == 3);
        procNodes.Should().Contain(n => n.Id == "proc:b.exe" && n.ConnectionCount == 2);
        var others = procNodes.Single(n => n.Id == "proc:(others)");
        others.Label.Should().Be(TrafficGraphBuilder.OthersProcessLabel);
        others.ConnectionCount.Should().Be(2); // c.exe + d.exe

        var othersEdge = data.Edges.Single(e => e.SourceId == "proc:(others)");
        othersEdge.TotalCount.Should().Be(2);
    }

    // ---------- (4) Collapsed group node ----------

    [Fact]
    public void Build_CollapsedGroups_SingleNodeWithEventCountLabel()
    {
        var events = new[]
        {
            Evt(remote: "8.8.8.8"),
            Evt(remote: "1.1.1.1"),
            Evt(remote: "1.1.1.1"),
            Evt(remote: "192.168.1.55"),
            Evt(remote: "172.26.115.7", nic: "vEthernet (WSL)"),
            Evt(remote: "172.26.115.7", nic: "vEthernet (WSL)"),
        };

        var data = TrafficGraphBuilder.Build(events, Adapters, NoneExpanded);

        var internet = data.Nodes.Single(n => n.Id == "group:Internet");
        internet.Kind.Should().Be(GraphNodeKind.RemoteGroup);
        internet.Group.Should().Be(RemoteGroupKind.Internet);
        internet.Label.Should().Be("Internet (3)");
        internet.IsExpanded.Should().BeFalse();

        var lan = data.Nodes.Single(n => n.Id == "group:Lan");
        lan.Group.Should().Be(RemoteGroupKind.Lan);
        lan.Label.Should().Be("LAN (1)");

        var wsl = data.Nodes.Single(n => n.Id == "group:WslGuest");
        wsl.Group.Should().Be(RemoteGroupKind.WslGuest);
        wsl.Label.Should().Be("WSL guest (2)");

        // Two internet remotes collapse onto ONE aggregated edge.
        var internetEdge = data.Edges.Single(e => e.TargetId == "group:Internet");
        internetEdge.SourceId.Should().Be("nic:Ethernet");
        internetEdge.AllowedCount.Should().Be(3);
    }

    // ---------- (5) Expanded group: top remotes + "+N more" ----------

    [Fact]
    public void Build_ExpandedGroup_YieldsTopRemotesPlusMoreNode()
    {
        var events = new[]
        {
            Evt(remote: "1.1.1.1"), Evt(remote: "1.1.1.1"), Evt(remote: "1.1.1.1"),
            Evt(remote: "8.8.8.8"), Evt(remote: "8.8.8.8"),
            Evt(remote: "9.9.9.9"),
            Evt(remote: "4.4.4.4"),
        };

        var data = TrafficGraphBuilder.Build(
            events, Adapters,
            new HashSet<RemoteGroupKind> { RemoteGroupKind.Internet },
            maxRemotesPerGroup: 2);

        // Expanded groups keep a compact header node so they can be collapsed again.
        var header = data.Nodes.Single(n => n.Id == "group:Internet");
        header.Kind.Should().Be(GraphNodeKind.RemoteGroup);
        header.Group.Should().Be(RemoteGroupKind.Internet);
        header.IsExpanded.Should().BeTrue();
        header.Label.Should().Be("Internet ▾");

        var remotes = data.Nodes.Where(n => n.Kind == GraphNodeKind.Remote).ToList();
        remotes.Should().HaveCount(2);
        remotes.Should().Contain(n =>
            n.Id == "ip:1.1.1.1" && n.ConnectionCount == 3 &&
            n.Group == RemoteGroupKind.Internet && n.IsExpanded);
        remotes.Should().Contain(n => n.Id == "ip:8.8.8.8" && n.ConnectionCount == 2);

        var more = data.Nodes.Single(n => n.Id == "more:Internet");
        more.Kind.Should().Be(GraphNodeKind.RemoteGroup);
        more.Label.Should().Be("+2 more"); // 9.9.9.9 and 4.4.4.4
        more.Group.Should().Be(RemoteGroupKind.Internet);

        // Edges route to individual nodes; overflow remotes aggregate on more:.
        // The expanded header carries no traffic — no edge may target it.
        data.Edges.Should().Contain(e => e.TargetId == "ip:1.1.1.1" && e.TotalCount == 3);
        data.Edges.Should().Contain(e => e.TargetId == "ip:8.8.8.8" && e.TotalCount == 2);
        data.Edges.Single(e => e.TargetId == "more:Internet").TotalCount.Should().Be(2);
        data.Edges.Should().NotContain(e =>
            e.TargetId == "group:Internet" || e.SourceId == "group:Internet");
    }

    [Fact]
    public void Build_ExpandedGroup_HeaderPresentEvenWhenRemotesFitWithinMax()
    {
        // A group with fewer remotes than the cutoff gets no "+N more" node, so
        // the header is the only collapse affordance — it must always be there.
        var events = new[]
        {
            Evt(remote: "172.26.115.7", nic: "vEthernet (WSL)"),
            Evt(remote: "172.26.115.7", nic: "vEthernet (WSL)"),
        };

        var data = TrafficGraphBuilder.Build(
            events, Adapters,
            new HashSet<RemoteGroupKind> { RemoteGroupKind.WslGuest });

        var header = data.Nodes.Single(n => n.Id == "group:WslGuest");
        header.Kind.Should().Be(GraphNodeKind.RemoteGroup);
        header.IsExpanded.Should().BeTrue();
        header.Label.Should().Be("WSL guest ▾");

        data.Nodes.Should().NotContain(n => n.Id.StartsWith("more:"));
        data.Nodes.Should().Contain(n => n.Id == "ip:172.26.115.7");

        // Traffic routes to the member node, never to the header.
        data.Edges.Should().NotContain(e =>
            e.TargetId == "group:WslGuest" || e.SourceId == "group:WslGuest");
        data.Edges.Single(e => e.TargetId == "ip:172.26.115.7").TotalCount.Should().Be(2);
    }

    [Fact]
    public void Build_ExpandedLanGroup_HeaderUsesLanDisplayName()
    {
        var events = new[] { Evt(remote: "192.168.1.55") };

        var data = TrafficGraphBuilder.Build(
            events, Adapters, new HashSet<RemoteGroupKind> { RemoteGroupKind.Lan });

        data.Nodes.Single(n => n.Id == "group:Lan").Label.Should().Be("LAN ▾");
    }

    [Fact]
    public void Build_ExpandedGroup_NoMoreNodeWhenRemotesFitWithinMax()
    {
        var events = new[] { Evt(remote: "1.1.1.1"), Evt(remote: "8.8.8.8") };

        var data = TrafficGraphBuilder.Build(
            events, Adapters,
            new HashSet<RemoteGroupKind> { RemoteGroupKind.Internet },
            maxRemotesPerGroup: 2);

        data.Nodes.Should().NotContain(n => n.Id.StartsWith("more:"));
        data.Nodes.Where(n => n.Kind == GraphNodeKind.Remote).Should().HaveCount(2);
    }

    [Fact]
    public void Build_ExpandedWslGroup_MembersFlaggedAsWslGuestWithCountry()
    {
        var events = new[]
        {
            Evt(remote: "172.26.115.7", nic: "vEthernet (WSL)", country: null),
            Evt(remote: "172.26.115.7", nic: "vEthernet (WSL)", country: "SE"),
        };

        var data = TrafficGraphBuilder.Build(
            events, Adapters,
            new HashSet<RemoteGroupKind> { RemoteGroupKind.WslGuest });

        var node = data.Nodes.Single(n => n.Id == "ip:172.26.115.7");
        node.Kind.Should().Be(GraphNodeKind.Remote);
        node.Group.Should().Be(RemoteGroupKind.WslGuest);
        node.IsWslGuest.Should().BeTrue();
        node.Country.Should().Be("SE");
    }

    // ---------- (6) Classification ----------

    [Fact]
    public void Build_ClassifiesWslLanAndInternetRemotes()
    {
        var events = new[]
        {
            Evt(remote: "172.26.115.7", nic: "vEthernet (WSL)"), // in WSL subnet
            Evt(remote: "192.168.1.99"),                          // RFC1918
            Evt(remote: "10.0.0.5"),                              // RFC1918
            Evt(remote: "127.0.0.1"),                             // loopback
            Evt(remote: "8.8.8.8"),                               // public
            Evt(remote: "172.15.0.1"),                            // NOT RFC1918 (below 172.16)
        };

        var data = TrafficGraphBuilder.Build(events, Adapters, NoneExpanded);

        data.Nodes.Single(n => n.Id == "group:WslGuest").ConnectionCount.Should().Be(1);
        data.Nodes.Single(n => n.Id == "group:Lan").ConnectionCount.Should().Be(3);
        data.Nodes.Single(n => n.Id == "group:Internet").ConnectionCount.Should().Be(2);
    }

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.0.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::1", true)]        // IPv6 loopback
    [InlineData("fe80::1", true)]    // link-local
    [InlineData("fc00::1", true)]    // unique-local
    [InlineData("fd12::34", true)]   // unique-local (fd00::/8 within fc00::/7)
    [InlineData("2001:4860:4860::8888", false)]
    public void IsPrivate_ClassifiesAddresses(string ip, bool expected)
    {
        TrafficGraphBuilder.IsPrivate(IPAddress.Parse(ip)).Should().Be(expected);
    }

    // ---------- (7) Drop reasons and ports on adapter→remote edges ----------

    [Fact]
    public void Build_DropReasons_SortedOrdinalOnAdapterRemoteEdge()
    {
        var events = new[]
        {
            Evt(action: TrafficAction.Drop, dropReason: "Zebra rule"),
            Evt(action: TrafficAction.Drop, dropReason: "Alpha rule"),
            Evt(action: TrafficAction.Drop, dropReason: "Alpha rule"),
        };

        var data = TrafficGraphBuilder.Build(events, Adapters, NoneExpanded);

        var edge = data.Edges.Single(e => e.TargetId == "group:Internet");
        edge.DropReasons.Should().Equal("Alpha rule", "Zebra rule");

        // Process→adapter edges carry counts only.
        var procEdge = data.Edges.Single(e => e.SourceId == "proc:chrome.exe");
        procEdge.DropReasons.Should().BeEmpty();
        procEdge.TopPorts.Should().BeEmpty();
    }

    [Fact]
    public void Build_TopPorts_TopThreeByCountOnAdapterRemoteEdge()
    {
        var events = new[]
        {
            Evt(dstPort: 443), Evt(dstPort: 443), Evt(dstPort: 443), Evt(dstPort: 443),
            Evt(dstPort: 80), Evt(dstPort: 80), Evt(dstPort: 80),
            Evt(dstPort: 53, protocol: TransportProtocol.UDP), Evt(dstPort: 53, protocol: TransportProtocol.UDP),
            Evt(dstPort: 22),
            Evt(dstPort: 0), // ignored
        };

        var data = TrafficGraphBuilder.Build(events, Adapters, NoneExpanded);

        var edge = data.Edges.Single(e => e.TargetId == "group:Internet");
        edge.TopPorts.Should().HaveCount(3);
        edge.TopPorts[0].Port.Should().Be(443);
        edge.TopPorts[0].Count.Should().Be(4);
        edge.TopPorts[1].Port.Should().Be(80);
        edge.TopPorts[2].Port.Should().Be(53);
        edge.TopPorts[2].Protocol.Should().Be("UDP");
    }

    // ---------- (8) MatchesDrill ----------

    [Fact]
    public void MatchesDrill_NullDrill_MatchesEverything()
    {
        TrafficGraphBuilder.MatchesDrill(Evt(), null, Adapters).Should().BeTrue();
    }

    [Fact]
    public void MatchesDrill_Process_MatchesByLabel()
    {
        var drill = new DrillSelection(GraphNodeKind.Process, "chrome.exe");
        TrafficGraphBuilder.MatchesDrill(Evt(process: "chrome.exe"), drill, Adapters).Should().BeTrue();
        TrafficGraphBuilder.MatchesDrill(Evt(process: "svchost.exe"), drill, Adapters).Should().BeFalse();
        TrafficGraphBuilder.MatchesDrill(Evt(process: null), drill, Adapters).Should().BeFalse();
    }

    [Fact]
    public void MatchesDrill_SystemLabel_MatchesNullOrEmptyProcess()
    {
        var drill = new DrillSelection(GraphNodeKind.Process, TrafficGraphBuilder.SystemProcessLabel);
        TrafficGraphBuilder.MatchesDrill(Evt(process: null), drill, Adapters).Should().BeTrue();
        TrafficGraphBuilder.MatchesDrill(Evt(process: ""), drill, Adapters).Should().BeTrue();
        TrafficGraphBuilder.MatchesDrill(Evt(process: "chrome.exe"), drill, Adapters).Should().BeFalse();
    }

    [Fact]
    public void MatchesDrill_OthersLabel_MatchesEverything()
    {
        var drill = new DrillSelection(GraphNodeKind.Process, TrafficGraphBuilder.OthersProcessLabel);
        TrafficGraphBuilder.MatchesDrill(Evt(process: "anything.exe"), drill, Adapters).Should().BeTrue();
        TrafficGraphBuilder.MatchesDrill(Evt(process: null), drill, Adapters).Should().BeTrue();
    }

    [Fact]
    public void MatchesDrill_Adapter_MatchesInterfaceNameOrdinal()
    {
        var drill = new DrillSelection(GraphNodeKind.Adapter, "Ethernet");
        TrafficGraphBuilder.MatchesDrill(Evt(nic: "Ethernet"), drill, Adapters).Should().BeTrue();
        TrafficGraphBuilder.MatchesDrill(Evt(nic: "vEthernet (WSL)"), drill, Adapters).Should().BeFalse();
        TrafficGraphBuilder.MatchesDrill(Evt(nic: "ethernet"), drill, Adapters).Should().BeFalse();
    }

    [Fact]
    public void MatchesDrill_Remote_MatchesRemoteEndpointOfDirection()
    {
        var drill = new DrillSelection(GraphNodeKind.Remote, "8.8.8.8");
        TrafficGraphBuilder.MatchesDrill(
            Evt(remote: "8.8.8.8", direction: TrafficDirection.Outbound), drill, Adapters).Should().BeTrue();
        TrafficGraphBuilder.MatchesDrill(
            Evt(remote: "8.8.8.8", direction: TrafficDirection.Inbound), drill, Adapters).Should().BeTrue();
        TrafficGraphBuilder.MatchesDrill(
            Evt(remote: "1.1.1.1"), drill, Adapters).Should().BeFalse();
        TrafficGraphBuilder.MatchesDrill(
            Evt(remote: null), drill, Adapters).Should().BeFalse();
    }

    [Fact]
    public void MatchesDrill_RemoteGroup_MatchesClassificationOfRemote()
    {
        var wslDrill = new DrillSelection(GraphNodeKind.RemoteGroup, "WslGuest");
        var lanDrill = new DrillSelection(GraphNodeKind.RemoteGroup, "Lan");
        var netDrill = new DrillSelection(GraphNodeKind.RemoteGroup, "Internet");

        var wslEvt = Evt(remote: "172.26.115.7", nic: "vEthernet (WSL)");
        var lanEvt = Evt(remote: "192.168.1.50");
        var netEvt = Evt(remote: "8.8.8.8");

        TrafficGraphBuilder.MatchesDrill(wslEvt, wslDrill, Adapters).Should().BeTrue();
        TrafficGraphBuilder.MatchesDrill(lanEvt, wslDrill, Adapters).Should().BeFalse();

        TrafficGraphBuilder.MatchesDrill(lanEvt, lanDrill, Adapters).Should().BeTrue();
        TrafficGraphBuilder.MatchesDrill(wslEvt, lanDrill, Adapters).Should().BeFalse();

        TrafficGraphBuilder.MatchesDrill(netEvt, netDrill, Adapters).Should().BeTrue();
        TrafficGraphBuilder.MatchesDrill(lanEvt, netDrill, Adapters).Should().BeFalse();

        TrafficGraphBuilder.MatchesDrill(Evt(remote: null), netDrill, Adapters).Should().BeFalse();
    }
}
