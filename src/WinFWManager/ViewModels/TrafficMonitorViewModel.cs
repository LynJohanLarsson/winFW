using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reactive.Linq;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinFWManager.Core.Collections;
using WinFWManager.Core.Models;
using WinFWManager.Core.Services;

namespace WinFWManager.ViewModels;

public partial class TrafficMonitorViewModel : ObservableObject, IDisposable
{
    private readonly IEtwTrafficMonitor _etwMonitor;
    private readonly IProcessResolver _processResolver;
    private readonly IGeoIpResolver _geoIpResolver;
    private readonly INetworkInterfaceService _nicService;
    private readonly RingBuffer<TrafficEvent> _eventBuffer = new(50_000);
    private IDisposable? _subscription;
    private readonly Dispatcher _dispatcher;
    private bool _nicCacheLoaded;

    public ObservableCollection<TrafficEvent> Events { get; } = new();
    public ICollectionView EventsView { get; }

    [ObservableProperty] private string _filterSourceIp = string.Empty;
    [ObservableProperty] private string _filterSrcPort = string.Empty;
    [ObservableProperty] private string _filterDestIp = string.Empty;
    [ObservableProperty] private string _filterDstPort = string.Empty;
    [ObservableProperty] private string _filterProtocol = string.Empty;
    [ObservableProperty] private string _filterProcess = string.Empty;
    [ObservableProperty] private string _filterNic = string.Empty;
    [ObservableProperty] private bool _isAutoScroll = true;
    [ObservableProperty] private int _eventCount;

    public TrafficMonitorViewModel(
        IEtwTrafficMonitor etwMonitor,
        IProcessResolver processResolver,
        IGeoIpResolver geoIpResolver,
        INetworkInterfaceService nicService)
    {
        _etwMonitor = etwMonitor;
        _processResolver = processResolver;
        _geoIpResolver = geoIpResolver;
        _nicService = nicService;
        _dispatcher = Dispatcher.CurrentDispatcher;

        EventsView = CollectionViewSource.GetDefaultView(Events);
        EventsView.Filter = FilterPredicate;

        _subscription = _etwMonitor.TrafficEvents
            .Buffer(TimeSpan.FromMilliseconds(100))
            .Where(batch => batch.Count > 0)
            .ObserveOn(System.Threading.SynchronizationContext.Current!)
            .Subscribe(OnEventBatch);
    }

    private void OnEventBatch(IList<TrafficEvent> batch)
    {
        // Lazy-load NIC cache on first batch
        if (!_nicCacheLoaded)
        {
            _nicCacheLoaded = true;
            _ = _nicService.RefreshAsync();
        }

        foreach (var evt in batch)
        {
            // Enrich with process and geo info
            var processInfo = _processResolver.Resolve(evt.ProcessId);
            evt.ProcessName = processInfo.DisplayName;

            if (evt.DestinationAddress != null)
            {
                var geoInfo = _geoIpResolver.Resolve(evt.DestinationAddress);
                evt.Country = geoInfo.DisplayCountry;
            }

            // Resolve NIC by matching the local endpoint (source for outbound,
            // destination for inbound) and the remote peer for host<->VM traffic.
            var local = evt.Direction == TrafficDirection.Outbound
                ? evt.SourceAddress : evt.DestinationAddress;
            var remote = evt.Direction == TrafficDirection.Outbound
                ? evt.DestinationAddress : evt.SourceAddress;
            var adapter = _nicService.ResolveAdapter(local, remote);
            if (adapter != null)
            {
                evt.InterfaceName = adapter.Name;
                evt.AdapterType = adapter.AdapterType;
            }

            _eventBuffer.Add(evt);
            Events.Add(evt);

            // Keep display collection manageable
            while (Events.Count > 5000)
                Events.RemoveAt(0);
        }

        EventCount = _eventBuffer.Count;
    }

    partial void OnFilterSourceIpChanged(string value) => EventsView.Refresh();
    partial void OnFilterSrcPortChanged(string value) => EventsView.Refresh();
    partial void OnFilterDestIpChanged(string value) => EventsView.Refresh();
    partial void OnFilterDstPortChanged(string value) => EventsView.Refresh();
    partial void OnFilterProtocolChanged(string value) => EventsView.Refresh();
    partial void OnFilterProcessChanged(string value) => EventsView.Refresh();
    partial void OnFilterNicChanged(string value) => EventsView.Refresh();

    private bool FilterPredicate(object obj)
    {
        if (obj is not TrafficEvent evt) return false;

        if (!MatchesFilter(FilterSourceIp, evt.SourceAddress?.ToString()))
            return false;
        if (!MatchesPortFilter(FilterSrcPort, evt.SourcePort))
            return false;
        if (!MatchesFilter(FilterDestIp, evt.DestinationAddress?.ToString()))
            return false;
        if (!MatchesPortFilter(FilterDstPort, evt.DestinationPort))
            return false;
        if (!MatchesFilter(FilterProtocol, evt.Protocol.ToString()))
            return false;
        if (!MatchesFilter(FilterProcess, evt.ProcessName))
            return false;
        if (!MatchesFilter(FilterNic, evt.InterfaceName))
            return false;

        return true;
    }

    /// <summary>
    /// Supports multiple comma-separated terms. Prefix a term with ! to exclude.
    /// e.g. "!192.168.1.1,!10.0.0.1" excludes both IPs.
    /// e.g. "chrome,firefox" includes either.
    /// Mixed: "!svchost" excludes svchost.
    /// </summary>
    private static bool MatchesFilter(string filter, string? fieldValue)
    {
        if (string.IsNullOrEmpty(filter)) return true;

        var terms = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return true;

        var negTerms = new List<string>();
        var posTerms = new List<string>();
        foreach (var term in terms)
        {
            if (term.StartsWith('!') && term.Length > 1)
                negTerms.Add(term[1..]);
            else
                posTerms.Add(term);
        }

        // Negative filters: if field matches ANY negation, exclude
        foreach (var neg in negTerms)
        {
            if (fieldValue?.Contains(neg, StringComparison.OrdinalIgnoreCase) == true)
                return false;
        }

        // Positive filters: field must match at least one (OR logic)
        if (posTerms.Count > 0)
        {
            bool anyMatch = false;
            foreach (var pos in posTerms)
            {
                if (fieldValue?.Contains(pos, StringComparison.OrdinalIgnoreCase) == true)
                {
                    anyMatch = true;
                    break;
                }
            }
            if (!anyMatch) return false;
        }

        return true;
    }

    /// <summary>
    /// Port filter with exact-match terms (comma-separated, ! to exclude).
    /// e.g. "443,80" matches either; "!53" excludes DNS. Exact match avoids
    /// "443" spuriously matching 4431 as a substring would.
    /// </summary>
    private static bool MatchesPortFilter(string filter, int port)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;

        var terms = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return true;

        var posTerms = new List<int>();
        foreach (var term in terms)
        {
            if (term.StartsWith('!') && term.Length > 1)
            {
                if (int.TryParse(term[1..], out var neg) && neg == port)
                    return false;
            }
            else if (int.TryParse(term, out var pos))
            {
                posTerms.Add(pos);
            }
        }

        if (posTerms.Count > 0 && !posTerms.Contains(port))
            return false;

        return true;
    }

    [ObservableProperty] private TrafficEvent? _selectedEvent;

    [RelayCommand]
    private void ClearEvents()
    {
        Events.Clear();
        _eventBuffer.Clear();
        EventCount = 0;
    }

    [RelayCommand]
    private void FilterBySourceIp()
    {
        if (SelectedEvent?.SourceAddress != null)
            FilterSourceIp = SelectedEvent.SourceAddress.ToString();
    }

    [RelayCommand]
    private void FilterByDestIp()
    {
        if (SelectedEvent?.DestinationAddress != null)
            FilterDestIp = SelectedEvent.DestinationAddress.ToString();
    }

    [RelayCommand]
    private void FilterBySrcPort()
    {
        if (SelectedEvent != null)
            FilterSrcPort = SelectedEvent.SourcePort.ToString();
    }

    [RelayCommand]
    private void FilterByDstPort()
    {
        if (SelectedEvent != null)
            FilterDstPort = SelectedEvent.DestinationPort.ToString();
    }

    [RelayCommand]
    private void FilterByProtocol()
    {
        if (SelectedEvent != null)
            FilterProtocol = SelectedEvent.Protocol.ToString();
    }

    [RelayCommand]
    private void FilterByProcess()
    {
        if (SelectedEvent?.ProcessName != null)
            FilterProcess = SelectedEvent.ProcessName;
    }

    [RelayCommand]
    private void FilterByNic()
    {
        if (SelectedEvent?.InterfaceName != null)
            FilterNic = SelectedEvent.InterfaceName;
    }

    [RelayCommand]
    private void ExcludeSourceIp()
    {
        if (SelectedEvent?.SourceAddress != null)
            FilterSourceIp = AppendNegation(FilterSourceIp, SelectedEvent.SourceAddress.ToString());
    }

    [RelayCommand]
    private void ExcludeDestIp()
    {
        if (SelectedEvent?.DestinationAddress != null)
            FilterDestIp = AppendNegation(FilterDestIp, SelectedEvent.DestinationAddress.ToString());
    }

    [RelayCommand]
    private void ExcludeSrcPort()
    {
        if (SelectedEvent != null)
            FilterSrcPort = AppendNegation(FilterSrcPort, SelectedEvent.SourcePort.ToString());
    }

    [RelayCommand]
    private void ExcludeDstPort()
    {
        if (SelectedEvent != null)
            FilterDstPort = AppendNegation(FilterDstPort, SelectedEvent.DestinationPort.ToString());
    }

    [RelayCommand]
    private void ExcludeProtocol()
    {
        if (SelectedEvent != null)
            FilterProtocol = AppendNegation(FilterProtocol, SelectedEvent.Protocol.ToString());
    }

    [RelayCommand]
    private void ExcludeProcess()
    {
        if (SelectedEvent?.ProcessName != null)
            FilterProcess = AppendNegation(FilterProcess, SelectedEvent.ProcessName);
    }

    [RelayCommand]
    private void ExcludeNic()
    {
        if (SelectedEvent?.InterfaceName != null)
            FilterNic = AppendNegation(FilterNic, SelectedEvent.InterfaceName);
    }

    private static string AppendNegation(string current, string value)
    {
        var negTerm = $"!{value}";
        if (string.IsNullOrEmpty(current))
            return negTerm;
        if (current.Contains(negTerm, StringComparison.OrdinalIgnoreCase))
            return current;
        return $"{current},{negTerm}";
    }

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
    }

    [RelayCommand]
    private void CreateRuleFromTraffic()
    {
        if (SelectedEvent == null) return;

        var evt = SelectedEvent;
        var rule = new FirewallRuleInfo
        {
            DisplayName = $"Block {evt.DestinationAddress}:{evt.DestinationPort}",
            Name = $"WinFW_Block_{evt.DestinationAddress}_{evt.DestinationPort}_{DateTime.Now.Ticks}",
            Direction = evt.Direction,
            Action = TrafficAction.Block,
            Protocol = evt.Protocol,
            RemoteAddress = evt.DestinationAddress?.ToString(),
            RemotePort = evt.DestinationPort > 0 ? evt.DestinationPort.ToString() : null,
            Profile = evt.Profile,
            Enabled = true,
            IsHyperVRule = evt.IsHyperVTraffic
        };

        var dialog = new Views.RuleEditorDialog(rule);
        dialog.ShowDialog();
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        GC.SuppressFinalize(this);
    }
}
