using System.Collections.ObjectModel;
using System.Reactive.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using WinFWManager.Core.Collections;
using WinFWManager.Core.Models;
using WinFWManager.Core.Services;

namespace WinFWManager.ViewModels;

public partial class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly IEtwTrafficMonitor _etwMonitor;
    private readonly INetworkInterfaceService _nicService;
    private readonly RingBuffer<TrafficEvent> _recentEvents = new(10_000);
    private IDisposable? _subscription;
    private readonly DispatcherTimer _refreshTimer;

    private HashSet<string> _localIps = new(StringComparer.Ordinal);
    private List<NetworkAdapterInfo> _adapters = new();

    [ObservableProperty] private int _totalConnections;
    [ObservableProperty] private int _blockedConnections;
    [ObservableProperty] private double _blockedPercent;
    [ObservableProperty] private int _allowedConnections;
    [ObservableProperty] private int _inboundCount;
    [ObservableProperty] private int _outboundCount;
    [ObservableProperty] private TrafficGraphData? _graphData;
    [ObservableProperty] private string _graphFilter = string.Empty;

    public ObservableCollection<TopTalkerEntry> TopTalkers { get; } = new();
    public ObservableCollection<TopTalkerEntry> TopBlocked { get; } = new();

    public DashboardViewModel(IEtwTrafficMonitor etwMonitor, INetworkInterfaceService nicService)
    {
        _etwMonitor = etwMonitor;
        _nicService = nicService;

        _ = InitAdaptersAsync();

        _subscription = _etwMonitor.TrafficEvents
            .Buffer(TimeSpan.FromMilliseconds(500))
            .Where(batch => batch.Count > 0)
            .ObserveOn(System.Threading.SynchronizationContext.Current!)
            .Subscribe(OnEventBatch);

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => RefreshStats();
        _refreshTimer.Start();
    }

    private async Task InitAdaptersAsync()
    {
        var adapters = await _nicService.GetAllAdaptersAsync();
        _adapters = adapters.ToList();
        _localIps = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in _adapters)
            foreach (var ip in a.IpAddresses)
                _localIps.Add(ip.ToString());
    }

    private void OnEventBatch(IList<TrafficEvent> batch)
    {
        foreach (var evt in batch)
            _recentEvents.Add(evt);
    }

    partial void OnGraphFilterChanged(string value) => RefreshStats();

    private void RefreshStats()
    {
        var events = _recentEvents.ToList();
        TotalConnections = events.Count;
        BlockedConnections = events.Count(e => e.Action is TrafficAction.Block or TrafficAction.Drop);
        AllowedConnections = events.Count(e => e.Action == TrafficAction.Allow);
        BlockedPercent = TotalConnections > 0 ? (double)BlockedConnections / TotalConnections * 100 : 0;
        InboundCount = events.Count(e => e.Direction == TrafficDirection.Inbound);
        OutboundCount = events.Count(e => e.Direction == TrafficDirection.Outbound);

        // Top talkers by destination IP
        var topTalkers = events
            .Where(e => e.DestinationAddress != null)
            .GroupBy(e => e.DestinationAddress!.ToString())
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => new TopTalkerEntry
            {
                Address = g.Key,
                Count = g.Count(),
                Country = g.First().Country ?? "Unknown"
            })
            .ToList();

        TopTalkers.Clear();
        foreach (var t in topTalkers)
            TopTalkers.Add(t);

        // Top blocked destinations
        var topBlocked = events
            .Where(e => e.Action is TrafficAction.Block or TrafficAction.Drop && e.DestinationAddress != null)
            .GroupBy(e => e.DestinationAddress!.ToString())
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => new TopTalkerEntry
            {
                Address = g.Key,
                Count = g.Count(),
                Country = g.First().Country ?? "Unknown"
            })
            .ToList();

        TopBlocked.Clear();
        foreach (var t in topBlocked)
            TopBlocked.Add(t);

        // Build traffic graph
        BuildGraphData(events);
    }

    private void BuildGraphData(List<TrafficEvent> events)
    {
        var filter = GraphFilter?.Trim() ?? "";

        // Apply filter
        if (!string.IsNullOrEmpty(filter))
        {
            events = events.Where(e =>
                (e.SourceAddress?.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase) == true) ||
                (e.DestinationAddress?.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase) == true) ||
                (e.InterfaceName?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true) ||
                (e.ProcessName?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true)
            ).ToList();
        }

        var edgeCounts = new Dictionary<(string nic, string remote), (int allowed, int blocked)>();
        var edgePorts = new Dictionary<(string nic, string remote), Dictionary<(int port, string proto), int>>();
        var edgeDropReasons = new Dictionary<(string nic, string remote), HashSet<string>>();
        var nicTrafficCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var remoteInfo = new Dictionary<string, (string? country, int count, int blocked)>(StringComparer.Ordinal);

        foreach (var evt in events)
        {
            string? nicName = null;
            string? remoteIp = null;

            var local = evt.Direction == TrafficDirection.Outbound
                ? evt.SourceAddress : evt.DestinationAddress;
            remoteIp = (evt.Direction == TrafficDirection.Outbound
                ? evt.DestinationAddress : evt.SourceAddress)?.ToString();

            nicName = evt.InterfaceName;
            if (string.IsNullOrEmpty(nicName))
            {
                var remote = evt.Direction == TrafficDirection.Outbound
                    ? evt.DestinationAddress : evt.SourceAddress;
                nicName = _nicService.ResolveAdapter(local, remote)?.Name;
            }

            if (string.IsNullOrEmpty(nicName) || string.IsNullOrEmpty(remoteIp))
                continue;

            var key = (nicName, remoteIp);
            if (!edgeCounts.TryGetValue(key, out var counts))
                counts = (0, 0);

            bool isBlocked = evt.Action is TrafficAction.Block or TrafficAction.Drop;
            edgeCounts[key] = isBlocked
                ? (counts.allowed, counts.blocked + 1)
                : (counts.allowed + 1, counts.blocked);

            if (isBlocked && evt.DropReason != null)
            {
                if (!edgeDropReasons.TryGetValue(key, out var reasons))
                {
                    reasons = new HashSet<string>(StringComparer.Ordinal);
                    edgeDropReasons[key] = reasons;
                }
                reasons.Add(evt.DropReason);
            }

            nicTrafficCount[nicName] = nicTrafficCount.GetValueOrDefault(nicName) + 1;

            // Track destination port per edge
            if (evt.DestinationPort > 0)
            {
                if (!edgePorts.TryGetValue(key, out var portDict))
                {
                    portDict = new Dictionary<(int port, string proto), int>();
                    edgePorts[key] = portDict;
                }
                var portKey = (evt.DestinationPort, evt.Protocol.ToString());
                portDict[portKey] = portDict.GetValueOrDefault(portKey) + 1;
            }

            if (!remoteInfo.TryGetValue(remoteIp, out var ri))
                ri = (evt.Country, 0, 0);
            remoteInfo[remoteIp] = (ri.country ?? evt.Country, ri.count + 1,
                ri.blocked + (isBlocked ? 1 : 0));
        }

        // Local nodes (NICs with traffic)
        var localNodes = new List<GraphNode>();
        foreach (var adapter in _adapters)
        {
            if (!nicTrafficCount.ContainsKey(adapter.Name)) continue;
            localNodes.Add(new GraphNode
            {
                Id = adapter.Name,
                Label = adapter.Name,
                IsLocal = true,
                ConnectionCount = nicTrafficCount[adapter.Name],
                AdapterType = adapter.AdapterType
            });
        }

        // Include NICs found in events but not in adapter list
        foreach (var (nic, count) in nicTrafficCount)
        {
            if (localNodes.Any(n => n.Id.Equals(nic, StringComparison.OrdinalIgnoreCase)))
                continue;
            localNodes.Add(new GraphNode
            {
                Id = nic,
                Label = nic,
                IsLocal = true,
                ConnectionCount = count
            });
        }

        localNodes = localNodes.OrderByDescending(n => n.ConnectionCount).ToList();

        // Remote nodes (top 15)
        var remoteNodes = remoteInfo
            .OrderByDescending(kv => kv.Value.count)
            .Take(15)
            .Select(kv => new GraphNode
            {
                Id = kv.Key,
                Label = kv.Key,
                IsLocal = false,
                ConnectionCount = kv.Value.count,
                Country = kv.Value.country,
                IsWslGuest = System.Net.IPAddress.TryParse(kv.Key, out var parsedIp) &&
                             _nicService.ResolveAdapter(null, parsedIp)?.AdapterType == AdapterType.WSL
            })
            .ToList();

        // Edges (only for nodes that made the cut)
        var remoteIds = new HashSet<string>(remoteNodes.Select(n => n.Id), StringComparer.Ordinal);
        var edges = edgeCounts
            .Where(kv => remoteIds.Contains(kv.Key.remote))
            .Select(kv =>
            {
                var edge = new GraphEdge
                {
                    SourceId = kv.Key.nic,
                    TargetId = kv.Key.remote,
                    AllowedCount = kv.Value.allowed,
                    BlockedCount = kv.Value.blocked,
                    DropReasons = edgeDropReasons.TryGetValue(kv.Key, out var reasons)
                        ? reasons.OrderBy(r => r, StringComparer.Ordinal).ToList() : new List<string>()
                };
                if (edgePorts.TryGetValue(kv.Key, out var portDict))
                {
                    edge.TopPorts = portDict
                        .OrderByDescending(p => p.Value)
                        .Take(5)
                        .Select(p => new PortCount
                        {
                            Port = p.Key.port,
                            Protocol = p.Key.proto,
                            Count = p.Value
                        })
                        .ToList();
                }
                return edge;
            })
            .Where(e => e.TotalCount > 0)
            .ToList();

        var maxEdge = edges.Count > 0 ? edges.Max(e => e.TotalCount) : 1;

        GraphData = new TrafficGraphData
        {
            Nodes = localNodes.Concat(remoteNodes).ToList(),
            Edges = edges,
            MaxEdgeCount = Math.Max(maxEdge, 1)
        };
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        _subscription?.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class TopTalkerEntry
{
    public string Address { get; set; } = string.Empty;
    public int Count { get; set; }
    public string Country { get; set; } = "Unknown";
}
