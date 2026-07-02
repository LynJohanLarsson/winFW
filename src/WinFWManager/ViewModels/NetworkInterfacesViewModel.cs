using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinFWManager.Core.Models;
using WinFWManager.Core.Services;

namespace WinFWManager.ViewModels;

public partial class NetworkInterfacesViewModel : ObservableObject
{
    private readonly INetworkInterfaceService _nicService;
    private readonly WslNetworkModeDetector _wslDetector;

    public ObservableCollection<NetworkAdapterInfo> Adapters { get; } = new();

    [ObservableProperty] private NetworkAdapterInfo? _selectedAdapter;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _wslModeText = "";

    private bool _hasLoadedOnce;

    public NetworkInterfacesViewModel(INetworkInterfaceService nicService, WslNetworkModeDetector wslDetector)
    {
        _nicService = nicService;
        _wslDetector = wslDetector;
    }

    /// <summary>
    /// Called when the view becomes visible (tab selected).
    /// Triggers auto-refresh on first view, and can be called to refresh on subsequent visits.
    /// </summary>
    public async Task OnActivatedAsync()
    {
        if (!_hasLoadedOnce)
        {
            _hasLoadedOnce = true;
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            await _nicService.RefreshAsync();
            var adapters = await _nicService.GetAllAdaptersAsync();
            Adapters.Clear();
            foreach (var adapter in adapters)
                Adapters.Add(adapter);

            // DetectMode/GetGuestIp may spawn wsl.exe (5s cap) — keep it off the UI thread.
            var wslText = await Task.Run(() =>
            {
                var mode = _wslDetector.DetectMode();
                var guestIp = mode is WslNetworkingMode.Nat or WslNetworkingMode.Bridged
                    ? _wslDetector.GetGuestIp() : null;
                return guestIp != null
                    ? $"WSL networking: {mode}  •  guest IP {guestIp}"
                    : $"WSL networking: {mode}";
            });
            WslModeText = wslText;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
