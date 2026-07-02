using System.Net;
using System.Net.Sockets;
using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

/// <summary>Drill selection: dashboard is filtered to events touching this node.</summary>
public sealed record DrillSelection(GraphNodeKind Kind, string Value);

/// <summary>
/// Pure aggregation of traffic events into the three-layer dashboard graph:
/// Process → Adapter → Remote. Remotes collapse into WSL guest / LAN /
/// Internet group nodes unless their group is expanded, in which case a
/// compact collapsible header precedes the top remotes plus an aggregated
/// "+N more" node when the cutoff is exceeded.
/// </summary>
public static class TrafficGraphBuilder
{
    public const string SystemProcessLabel = "(system)";
    public const string OthersProcessLabel = "(others)";

    /// <summary>True when the event involves the drilled node. Null drill matches everything.</summary>
    public static bool MatchesDrill(TrafficEvent evt, DrillSelection? drill,
        IReadOnlyList<NetworkAdapterInfo> adapters)
    {
        if (drill is null) return true;

        switch (drill.Kind)
        {
            case GraphNodeKind.Process:
                // "(others)" is an aggregate bucket, not drillable — match all.
                if (drill.Value == OthersProcessLabel) return true;
                return string.Equals(ProcessLabel(evt), drill.Value, StringComparison.Ordinal);

            case GraphNodeKind.Adapter:
                return string.Equals(evt.InterfaceName, drill.Value, StringComparison.Ordinal);

            case GraphNodeKind.Remote:
                return RemoteEndpoint(evt)?.ToString() == drill.Value;

            case GraphNodeKind.RemoteGroup:
                var remote = RemoteEndpoint(evt);
                return remote != null
                    && Classify(remote, adapters) == Enum.Parse<RemoteGroupKind>(drill.Value);

            default:
                return false;
        }
    }

    /// <summary>Builds the three-layer graph (Process → Adapter → Remote) from
    /// pre-filtered events. Events with no remote endpoint or no interface name
    /// are skipped.</summary>
    public static TrafficGraphData Build(
        IReadOnlyList<TrafficEvent> events,
        IReadOnlyList<NetworkAdapterInfo> adapters,
        ISet<RemoteGroupKind> expandedGroups,
        int maxProcesses = 8,
        int maxRemotesPerGroup = 10)
    {
        // Project usable events onto graph coordinates, classifying each remote once.
        var rows = new List<Row>();
        var groupByIp = new Dictionary<string, RemoteGroupKind>(StringComparer.Ordinal);
        foreach (var evt in events)
        {
            var remote = RemoteEndpoint(evt);
            if (remote is null || string.IsNullOrEmpty(evt.InterfaceName)) continue;

            string ip = remote.ToString();
            if (!groupByIp.TryGetValue(ip, out var group))
            {
                group = Classify(remote, adapters);
                groupByIp[ip] = group;
            }
            rows.Add(new Row(evt, ProcessLabel(evt), evt.InterfaceName!, ip, group));
        }

        var nodes = new List<GraphNode>();

        // ---- Process layer: top-N labels keep their node, the rest fold into (others).
        var topProcs = rows
            .GroupBy(r => r.Proc, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(maxProcesses)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        string ProcLabelOf(Row r) => topProcs.Contains(r.Proc) ? r.Proc : OthersProcessLabel;

        foreach (var g in rows.GroupBy(ProcLabelOf, StringComparer.Ordinal)
                     .OrderByDescending(g => g.Count())
                     .ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            nodes.Add(new GraphNode
            {
                Id = $"proc:{g.Key}",
                Label = g.Key,
                Kind = GraphNodeKind.Process,
                IsLocal = true,
                ConnectionCount = g.Count(),
            });
        }

        // ---- Adapter layer: one node per interface name seen.
        foreach (var g in rows.GroupBy(r => r.Nic, StringComparer.Ordinal)
                     .OrderByDescending(g => g.Count())
                     .ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            nodes.Add(new GraphNode
            {
                Id = $"nic:{g.Key}",
                Label = g.Key,
                Kind = GraphNodeKind.Adapter,
                IsLocal = true,
                AdapterType = g.First().Evt.AdapterType,
                ConnectionCount = g.Count(),
            });
        }

        // ---- Remote layer: collapsed group nodes, or expanded top remotes + "+N more".
        var remoteNodeIdByIp = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var groupRows in rows.GroupBy(r => r.Group).OrderBy(g => g.Key))
        {
            var kind = groupRows.Key;

            if (!expandedGroups.Contains(kind))
            {
                int eventCount = groupRows.Count();
                nodes.Add(new GraphNode
                {
                    Id = $"group:{kind}",
                    Label = $"{GroupLabel(kind)} ({eventCount})",
                    Kind = GraphNodeKind.RemoteGroup,
                    Group = kind,
                    ConnectionCount = eventCount,
                });
                foreach (var r in groupRows)
                    remoteNodeIdByIp[r.Ip] = $"group:{kind}";
                continue;
            }

            // Expanded: emit a compact header first so the group can always be
            // collapsed again (the "+N more" node only exists past the cutoff).
            // No edges route to the header — traffic goes to the member nodes.
            nodes.Add(new GraphNode
            {
                Id = $"group:{kind}",
                Label = $"{GroupLabel(kind)} ▾",
                Kind = GraphNodeKind.RemoteGroup,
                Group = kind,
                IsExpanded = true,
                ConnectionCount = groupRows.Count(),
            });

            var byIp = groupRows
                .GroupBy(r => r.Ip, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .ToList();

            foreach (var ipGroup in byIp.Take(maxRemotesPerGroup))
            {
                nodes.Add(new GraphNode
                {
                    Id = $"ip:{ipGroup.Key}",
                    Label = ipGroup.Key,
                    Kind = GraphNodeKind.Remote,
                    Group = kind,
                    IsExpanded = true,
                    ConnectionCount = ipGroup.Count(),
                    Country = ipGroup.Select(r => r.Evt.Country).FirstOrDefault(c => c != null),
                    IsWslGuest = kind == RemoteGroupKind.WslGuest,
                });
                remoteNodeIdByIp[ipGroup.Key] = $"ip:{ipGroup.Key}";
            }

            var overflow = byIp.Skip(maxRemotesPerGroup).ToList();
            if (overflow.Count > 0)
            {
                nodes.Add(new GraphNode
                {
                    Id = $"more:{kind}",
                    Label = $"+{overflow.Count} more",
                    Kind = GraphNodeKind.RemoteGroup,
                    Group = kind,
                    IsExpanded = true,
                    ConnectionCount = overflow.Sum(g => g.Count()),
                });
                foreach (var ipGroup in overflow)
                    remoteNodeIdByIp[ipGroup.Key] = $"more:{kind}";
            }
        }

        // ---- Edges: process→adapter and adapter→remote, both carrying
        // counts, top ports and drop reasons.
        var edges = new Dictionary<(string source, string target), GraphEdge>();
        var edgePorts = new Dictionary<(string source, string target),
            Dictionary<(int port, string proto), PortTally>>();

        void Tally(string source, string target, Row r, bool isBlocked)
        {
            var key = (source, target);
            if (!edges.TryGetValue(key, out var edge))
                edges[key] = edge = new GraphEdge { SourceId = source, TargetId = target };

            if (isBlocked) edge.BlockedCount++; else edge.AllowedCount++;

            if (isBlocked && r.Evt.DropReason != null
                && !edge.DropReasons.Contains(r.Evt.DropReason))
            {
                edge.DropReasons.Add(r.Evt.DropReason);
            }

            if (r.Evt.DestinationPort > 0)
            {
                if (!edgePorts.TryGetValue(key, out var portDict))
                    edgePorts[key] = portDict = new Dictionary<(int, string), PortTally>();
                var portKey = (r.Evt.DestinationPort, r.Evt.Protocol.ToString());
                if (!portDict.TryGetValue(portKey, out var tally))
                    portDict[portKey] = tally = new PortTally();
                tally.Total++;
                if (isBlocked)
                {
                    tally.Blocked++;
                    if (r.Evt.DropReason != null)
                        tally.Reasons.Add(r.Evt.DropReason);
                }
            }
        }

        foreach (var r in rows)
        {
            bool isBlocked = r.Evt.Action is TrafficAction.Block or TrafficAction.Drop;
            Tally($"proc:{ProcLabelOf(r)}", $"nic:{r.Nic}", r, isBlocked);
            Tally($"nic:{r.Nic}", remoteNodeIdByIp[r.Ip], r, isBlocked);
        }

        foreach (var (key, edge) in edges)
        {
            edge.DropReasons.Sort(StringComparer.Ordinal);
            if (edgePorts.TryGetValue(key, out var portDict))
            {
                edge.TopPorts = portDict
                    .OrderByDescending(p => p.Value.Total)
                    .ThenBy(p => p.Key.port)
                    .Take(3)
                    .Select(p => new PortCount
                    {
                        Port = p.Key.port,
                        Protocol = p.Key.proto,
                        Count = p.Value.Total,
                        BlockedCount = p.Value.Blocked,
                        DropReasons = p.Value.Reasons.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                    })
                    .ToList();
            }
        }

        var edgeList = edges.Values.ToList();
        return new TrafficGraphData
        {
            Nodes = nodes,
            Edges = edgeList,
            MaxEdgeCount = Math.Max(edgeList.Count > 0 ? edgeList.Max(e => e.TotalCount) : 1, 1),
        };
    }

    /// <summary>Classifies a remote endpoint into its dashboard group.</summary>
    public static RemoteGroupKind Classify(IPAddress remote, IReadOnlyList<NetworkAdapterInfo> adapters)
    {
        if (NetworkInterfaceService.ResolveAdapterFrom(adapters, null, remote)?.AdapterType
            == AdapterType.WSL)
        {
            return RemoteGroupKind.WslGuest;
        }
        return IsPrivate(remote) ? RemoteGroupKind.Lan : RemoteGroupKind.Internet;
    }

    /// <summary>RFC1918/loopback for IPv4; loopback, link-local (fe80::/10) and
    /// unique-local (fc00::/7) for IPv6.</summary>
    public static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            return b[0] == 10
                || b[0] == 127
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168);
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || IPAddress.IPv6Loopback.Equals(address))
                return true;
            return (address.GetAddressBytes()[0] & 0xFE) == 0xFC; // fc00::/7
        }
        return false;
    }

    private static IPAddress? RemoteEndpoint(TrafficEvent evt)
        => evt.Direction == TrafficDirection.Outbound
            ? evt.DestinationAddress : evt.SourceAddress;

    private static string ProcessLabel(TrafficEvent evt)
        => string.IsNullOrEmpty(evt.ProcessName) ? SystemProcessLabel : evt.ProcessName;

    private static string GroupLabel(RemoteGroupKind kind) => kind switch
    {
        RemoteGroupKind.WslGuest => "WSL guest",
        RemoteGroupKind.Lan => "LAN",
        _ => "Internet",
    };

    private readonly record struct Row(
        TrafficEvent Evt, string Proc, string Nic, string Ip, RemoteGroupKind Group);

    private sealed class PortTally
    {
        public int Total;
        public int Blocked;
        public readonly HashSet<string> Reasons = new(StringComparer.Ordinal);
    }
}
