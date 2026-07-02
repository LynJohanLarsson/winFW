# WinFW Manager

A modern Windows Firewall management application built with WPF and .NET 8. Monitor real-time network traffic, manage firewall rules, inspect network interfaces, and visualize traffic flow — all from a single dark-themed UI.

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)
![Windows](https://img.shields.io/badge/Platform-Windows-0078D6?logo=windows)
![License](https://img.shields.io/badge/License-MIT-green)

## Features

### Traffic Monitor
- **Real-time packet capture** via ETW (Event Tracing for Windows) — no packet sniffing drivers required
- Live table view with source/destination IPs, ports, protocol, process name, and NIC
- **Positive and negative filtering** — filter by any column, or prefix with `!` to exclude unwanted traffic
- **Right-click context menu** to instantly filter by Source IP, Destination IP, Protocol, Process, or NIC
- **WSL2 / Hyper-V awareness** — traffic is attributed to the owning adapter by IP (exact match, then subnet match on either endpoint), so host↔VM flows are tagged to `vEthernet (WSL …)` / Hyper-V switches and colour-coded (WSL = yellow, Hyper-V = blue)

> **Known limitation — WSL2 guest→internet traffic.** In WSL2's default NAT networking mode, the guest's outbound internet traffic is NAT-forwarded by the Windows host (WinNAT), so it never becomes a host TCP/IP socket. It is therefore **not visible to any host-level ETW provider** (verified empirically: the `Microsoft-Windows-TCPIP` and `Microsoft-Windows-WFP` providers surface no WSL-subnet events for such traffic). WinFW Manager reliably identifies all WSL/Hyper-V traffic that *is* observable at the host socket layer (host↔VM). Capturing the guest's pre-NAT internet packets would require an adapter-level packet capture (e.g. `pktmon`/NDIS) on the `vEthernet (WSL)` interface, which is out of scope for the current ETW-based design.

### Dashboard
- At-a-glance stats: active connections, bandwidth, top talkers
- **Interactive network traffic graph** showing traffic flow between local NICs and remote endpoints
- Hover tooltips with detailed connection info (byte counts, allowed/blocked, top ports, country)
- Filter the graph to focus on specific endpoints

### Rules Manager
- View all Windows Firewall rules with search and filtering
- Create, edit, and delete inbound/outbound rules
- Toggle rules on/off

### Log Viewer
- Parse and display Windows Firewall log files
- Filter and search through historical firewall events

### Network Interfaces
- View all network adapters with status, IP addresses, MAC, speed, and type
- Auto-refreshes on first visit

## Quick Install

### Option 1: Download (Recommended)

1. Go to the [**Latest Release**](https://github.com/jola0909/WinFWManager/releases/latest)
2. Download **`WinFWManager-standalone.exe`** (~150 MB, no dependencies needed)
3. Right-click → **Run as Administrator**

That's it — single exe, nothing to install.

> 💡 **Smaller download?** Grab `WinFWManager-portable.exe` (~15 MB) instead, but you'll need [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) installed.

### Option 2: Build from Source

```bash
git clone https://github.com/jola0909/WinFWManager.git
cd WinFWManager
dotnet publish src/WinFWManager/WinFWManager.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

The exe will be in `src/WinFWManager/bin/Release/net8.0-windows/win-x64/publish/`.

## Requirements

- **Windows 10/11** (or Windows Server 2016+)
- **Run as Administrator** — required for ETW traffic capture and firewall rule management

## Architecture

```
WinFWManager/
├── src/
│   ├── WinFWManager/              # WPF UI (Views, ViewModels, Themes)
│   │   ├── Views/                 # XAML views for each tab
│   │   ├── ViewModels/            # MVVM ViewModels (CommunityToolkit.Mvvm)
│   │   ├── Themes/                # Dark theme resources
│   │   └── Assets/                # App icon
│   └── WinFWManager.Core/         # Core logic (services, models)
│       ├── Models/                 # Data models (FirewallRule, TrafficEvent, etc.)
│       ├── Services/               # ETW monitor, CIM queries, process resolver, GeoIP
│       └── Collections/            # RingBuffer for high-throughput event storage
```

**Key technologies:**
- **WPF** with MVVM pattern (CommunityToolkit.Mvvm)
- **CIM/WMI** via Microsoft.Management.Infrastructure for native firewall queries
- **ETW** via Microsoft.Diagnostics.Tracing.TraceEvent for real-time traffic capture
- **System.Reactive** for event batching and throttling
- **MaxMind GeoIP2** for IP geolocation (graceful fallback if DLL is blocked by WDAC)

## License

This project is licensed under the MIT License — see the [LICENSE](LICENSE) file for details.
