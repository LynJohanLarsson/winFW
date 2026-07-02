using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinFWManager.Core.Services;

namespace WinFWManager.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IEtwTrafficMonitor _etwMonitor;

    [ObservableProperty] private bool _isMonitoring;
    [ObservableProperty] private bool _isAdmin;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private int _selectedTabIndex;

    public MainViewModel(IEtwTrafficMonitor etwMonitor)
    {
        _etwMonitor = etwMonitor;
        _isAdmin = !etwMonitor.RequiresAdmin;
        _statusText = _isAdmin ? "Running as Administrator" : "Limited mode — run as Administrator for full access";
    }

    [RelayCommand]
    private void ToggleMonitoring()
    {
        if (IsMonitoring)
        {
            _etwMonitor.Stop();
            IsMonitoring = false;
            StatusText = "Monitoring stopped";
        }
        else
        {
            try
            {
                _etwMonitor.Start();
                IsMonitoring = true;
                StatusText = "Monitoring active";
            }
            catch (UnauthorizedAccessException)
            {
                StatusText = "Cannot start monitoring — administrator privileges required";
            }
            catch (Exception ex)
            {
                StatusText = $"Cannot start monitoring — {ex.Message}";
            }
        }
    }
}
