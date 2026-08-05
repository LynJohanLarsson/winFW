using System.IO;
using Microsoft.Extensions.DependencyInjection;
using WinFWManager.Core.Services;
using WinFWManager.ViewModels;

namespace WinFWManager;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWinFWManagerServices(this IServiceCollection services)
    {
        // Core services
        services.AddSingleton<IProcessResolver>(new ProcessResolver());
        services.AddSingleton<IGeoIpResolver>(sp =>
        {
            try
            {
                var mmdbPath = Path.Combine(AppContext.BaseDirectory, "GeoLite2-City.mmdb");
                return new GeoIpResolver(File.Exists(mmdbPath) ? mmdbPath : null);
            }
            catch
            {
                // MaxMind DLL may be blocked by Application Control policy
                return new NullGeoIpResolver();
            }
        });
        services.AddSingleton<INetworkInterfaceService, NetworkInterfaceService>();
        services.AddSingleton<IFirewallLogParser, FirewallLogParser>();
        services.AddSingleton<IFirewallRuleService, FirewallRuleService>();
        services.AddSingleton<IEtwTrafficMonitor, EtwTrafficMonitor>();
        services.AddSingleton<WslNetworkModeDetector>();

        // ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<TrafficMonitorViewModel>();
        services.AddSingleton<LogViewerViewModel>();
        services.AddSingleton<RulesManagerViewModel>();
        services.AddSingleton<NetworkInterfacesViewModel>();
        services.AddSingleton<DashboardViewModel>();

        return services;
    }
}
