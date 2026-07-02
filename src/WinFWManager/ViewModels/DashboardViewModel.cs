using System.Collections.ObjectModel;
using System.Reactive.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinFWManager.Core.Collections;
using WinFWManager.Core.Models;
using WinFWManager.Core.Services;

namespace WinFWManager.ViewModels;

public partial class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly IEtwTrafficMonitor _etwMonitor;
    private readonly INetworkInterfaceService _nicService;
    private readonly RingBuffer<TrafficEvent> _recentEvents = new(10_000);
    private readonly TrafficEventFilter _filter = new();
    private readonly HashSet<RemoteGroupKind> _expandedGroups = new();
    private IDisposable? _subscription;
    private readonly DispatcherTimer _refreshTimer;

    private List<NetworkAdapterInfo> _adapters = new();

    [ObservableProperty] private int _totalConnections;
    [ObservableProperty] private int _blockedConnections;
    [ObservableProperty] private double _blockedPercent;
    [ObservableProperty] private int _allowedConnections;
    [ObservableProperty] private int _inboundCount;
    [ObservableProperty] private int _outboundCount;
    [ObservableProperty] private TrafficGraphData? _graphData;

    [ObservableProperty] private string _filterSourceIp = string.Empty;
    [ObservableProperty] private string _filterSrcPort = string.Empty;
    [ObservableProperty] private string _filterDestIp = string.Empty;
    [ObservableProperty] private string _filterDstPort = string.Empty;
    [ObservableProperty] private string _filterProtocol = string.Empty;
    [ObservableProperty] private string _filterProcess = string.Empty;
    [ObservableProperty] private string _filterNic = string.Empty;
    [ObservableProperty] private string _filterAction = string.Empty;

    [ObservableProperty] private DrillSelection? _drill;
    [ObservableProperty] private string _drillLabel = string.Empty;
    [ObservableProperty] private bool _hasDrill;

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
    }

    private void OnEventBatch(IList<TrafficEvent> batch)
    {
        foreach (var evt in batch)
            _recentEvents.Add(evt);
    }

    partial void OnFilterSourceIpChanged(string value) { _filter.SourceIp = value; RefreshStats(); }
    partial void OnFilterSrcPortChanged(string value) { _filter.SrcPort = value; RefreshStats(); }
    partial void OnFilterDestIpChanged(string value) { _filter.DestIp = value; RefreshStats(); }
    partial void OnFilterDstPortChanged(string value) { _filter.DstPort = value; RefreshStats(); }
    partial void OnFilterProtocolChanged(string value) { _filter.Protocol = value; RefreshStats(); }
    partial void OnFilterProcessChanged(string value) { _filter.Process = value; RefreshStats(); }
    partial void OnFilterNicChanged(string value) { _filter.Nic = value; RefreshStats(); }
    partial void OnFilterActionChanged(string value) { _filter.Action = value; RefreshStats(); }

    partial void OnDrillChanged(DrillSelection? value)
    {
        DrillLabel = value == null ? string.Empty : DescribeDrill(value);
        HasDrill = value != null;
        RefreshStats();
    }

    private static string DescribeDrill(DrillSelection drill) => drill.Kind switch
    {
        GraphNodeKind.RemoteGroup => drill.Value switch
        {
            nameof(RemoteGroupKind.WslGuest) => "WSL guest",
            nameof(RemoteGroupKind.Lan) => "LAN",
            nameof(RemoteGroupKind.Internet) => "Internet",
            _ => drill.Value
        },
        _ => drill.Value
    };

    [RelayCommand]
    private void ClearFilters()
    {
        FilterSourceIp = string.Empty;
        FilterSrcPort = string.Empty;
        FilterDestIp = string.Empty;
        FilterDstPort = string.Empty;
        FilterProtocol = string.Empty;
        FilterProcess = string.Empty;
        FilterNic = string.Empty;
        FilterAction = string.Empty;
    }

    [RelayCommand]
    private void ClearDrill() => Drill = null;

    /// <summary>
    /// Node click from the graph view: group nodes expand/collapse, other
    /// nodes set (or replace) the drill selection.
    /// </summary>
    public void ToggleNode(GraphNode node)
    {
        switch (node.Kind)
        {
            case GraphNodeKind.RemoteGroup:
                if (node.Group is not RemoteGroupKind kind) return;
                if (node.Id.StartsWith("more:", StringComparison.Ordinal)
                    || _expandedGroups.Contains(kind))
                {
                    _expandedGroups.Remove(kind);
                }
                else
                {
                    _expandedGroups.Add(kind);
                }
                RefreshStats();
                break;

            case GraphNodeKind.Process:
                if (node.Label == TrafficGraphBuilder.OthersProcessLabel) return;
                Drill = new DrillSelection(GraphNodeKind.Process, node.Label);
                break;

            case GraphNodeKind.Adapter:
                Drill = new DrillSelection(GraphNodeKind.Adapter, node.Label);
                break;

            case GraphNodeKind.Remote:
                var ip = node.Id.StartsWith("ip:", StringComparison.Ordinal)
                    ? node.Id[3..] : node.Label;
                Drill = new DrillSelection(GraphNodeKind.Remote, ip);
                break;
        }
    }

    /// <summary>True when a shared filter or drill selection is narrowing the
    /// events feeding the graph (used by the view for the empty-state text).</summary>
    public bool IsGraphFiltered => !_filter.IsEmpty || HasDrill;

    private void RefreshStats()
    {
        // COUPLING NOTE: _recentEvents holds the SAME TrafficEvent instances
        // that TrafficMonitorViewModel enriches in place (ProcessName,
        // InterfaceName, AdapterType) on the UI thread. Graph attribution
        // therefore depends on TrafficMonitorViewModel being an eagerly
        // created, never-disposed singleton. If that wiring ever changes,
        // enrichment must move into the monitor pipeline itself.
        IEnumerable<TrafficEvent> query = _recentEvents.ToList();
        if (!_filter.IsEmpty)
            query = query.Where(_filter.Matches);
        if (Drill != null)
            query = query.Where(e => TrafficGraphBuilder.MatchesDrill(e, Drill, _adapters));
        var events = query.ToList();

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

        GraphData = TrafficGraphBuilder.Build(events, _adapters, _expandedGroups);
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
