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
    private readonly TrafficEventFilter _filter = new();
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
    [ObservableProperty] private string _filterAction = string.Empty;
    [ObservableProperty] private bool _isAutoScroll = true;
    [ObservableProperty] private int _eventCount;
    [ObservableProperty] private bool _showMirroredBanner;

    public TrafficMonitorViewModel(
        IEtwTrafficMonitor etwMonitor,
        IProcessResolver processResolver,
        IGeoIpResolver geoIpResolver,
        INetworkInterfaceService nicService,
        WslNetworkModeDetector wslDetector)
    {
        _etwMonitor = etwMonitor;
        _processResolver = processResolver;
        _geoIpResolver = geoIpResolver;
        _nicService = nicService;
        _dispatcher = Dispatcher.CurrentDispatcher;
        ShowMirroredBanner = wslDetector.DetectMode() == WslNetworkingMode.Mirrored;

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
            // Enrich with process and geo info. PID 0 means "unknown" (e.g.
            // packet drops carry no PID) — resolving it would misleadingly
            // show the kernel Idle process, so leave the name empty instead.
            if (evt.ProcessId > 0)
            {
                var processInfo = _processResolver.Resolve(evt.ProcessId);
                evt.ProcessName = processInfo.DisplayName;
            }

            if (evt.DestinationAddress != null)
            {
                var geoInfo = _geoIpResolver.Resolve(evt.DestinationAddress);
                evt.Country = geoInfo.DisplayCountry;
            }

            // Resolve NIC: IfIndex from ETW is authoritative; otherwise match
            // the local endpoint (and remote peer for host<->VM traffic) by IP.
            NetworkAdapterInfo? adapter = null;
            if (evt.InterfaceIndexHint is int ifIndex)
                adapter = _nicService.ResolveByIfIndex(ifIndex);
            if (adapter != null)
            {
                evt.IsInterfaceExact = true;
            }
            else
            {
                var local = evt.Direction == TrafficDirection.Outbound
                    ? evt.SourceAddress : evt.DestinationAddress;
                var remote = evt.Direction == TrafficDirection.Outbound
                    ? evt.DestinationAddress : evt.SourceAddress;
                adapter = _nicService.ResolveAdapter(local, remote);
            }
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

    partial void OnFilterSourceIpChanged(string value) { _filter.SourceIp = value; EventsView.Refresh(); }
    partial void OnFilterSrcPortChanged(string value) { _filter.SrcPort = value; EventsView.Refresh(); }
    partial void OnFilterDestIpChanged(string value) { _filter.DestIp = value; EventsView.Refresh(); }
    partial void OnFilterDstPortChanged(string value) { _filter.DstPort = value; EventsView.Refresh(); }
    partial void OnFilterProtocolChanged(string value) { _filter.Protocol = value; EventsView.Refresh(); }
    partial void OnFilterProcessChanged(string value) { _filter.Process = value; EventsView.Refresh(); }
    partial void OnFilterNicChanged(string value) { _filter.Nic = value; EventsView.Refresh(); }
    partial void OnFilterActionChanged(string value) { _filter.Action = value; EventsView.Refresh(); }

    private bool FilterPredicate(object obj)
        => obj is TrafficEvent evt && _filter.Matches(evt);

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
    private void FilterByAction()
    {
        if (SelectedEvent != null)
            FilterAction = SelectedEvent.Action.ToString();
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

    [RelayCommand]
    private void ExcludeAction()
    {
        if (SelectedEvent != null)
            FilterAction = AppendNegation(FilterAction, SelectedEvent.Action.ToString());
    }

    private static string AppendNegation(string current, string value)
        => TrafficEventFilter.AppendNegation(current, value);

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
