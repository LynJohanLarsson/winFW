# WinFW Manager

A modern Windows Firewall management application built with WPF and .NET 10. Monitor real-time network traffic, manage firewall rules, inspect network interfaces, and visualize traffic flow — all from a single dark-themed UI.

![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)
![Windows](https://img.shields.io/badge/Platform-Windows-0078D6?logo=windows)
![License](https://img.shields.io/badge/License-MIT-green)

## Features

### Traffic Monitor
- **Real-time packet capture** via ETW (Event Tracing for Windows) — no packet sniffing drivers required
- Live table view with time, direction, source/destination IPs and ports, protocol, NIC, process name, action, drop reason, flow, profile, country, and hostname
- **Positive and negative filtering** — filter by any column, or prefix with `!` to exclude unwanted traffic
- **Right-click context menu** with include *and* exclude filters for Source IP, Src Port, Dest IP, Dst Port, Protocol, Process, NIC, and Action, plus copy-selected-rows and clear-all-filters
- **Create a firewall rule straight from an event** — right-click → *Create Rule from Traffic…* pre-fills the rule editor from the selected row
- **WSL2 / Hyper-V awareness** — traffic is attributed to the owning adapter by IP (exact match, then subnet match on either endpoint), so host↔VM flows are tagged to `vEthernet (WSL …)` / Hyper-V switches and colour-coded (WSL = yellow, Hyper-V = blue)
- **Real Allow/Drop actions** — each event shows whether the firewall allowed or dropped it, with the resolved drop reason in its own **Reason** column (e.g. *Filter Block*, *No matching endpoint*) and also as a tooltip on the Action cell
- **Flow column** — a compact traffic path per event, e.g. `WSL guest → vEthernet (WSL) ⛔`
- **Exact adapter attribution for dropped traffic** — dropped packets carry the interface index, so the NIC is identified exactly; *italic* NIC text means the adapter was derived by subnet matching instead

> **WSL traffic visibility.** WSL→host traffic — both allowed **and** firewall-dropped — is captured via the `Microsoft-Windows-TCPIP` manifest provider, with exact adapter attribution and human-readable drop reasons. The remaining limitation is unchanged: in NAT mode, WSL2 guest→internet traffic is NAT-forwarded by WinNAT and never becomes a host socket, so capturing it would require adapter-level capture (`pktmon`/NDIS), which is out of scope for the ETW-based design. WinFW Manager is aware of the WSL networking mode: **NAT** is fully supported; **Mirrored** is detected and an explanatory banner is shown (WSL traffic is indistinguishable from host traffic by design); **Bridged** is handled best-effort via guest IP tagging.

### Dashboard
- At-a-glance stats: active connections, bandwidth, top talkers
- **Interactive network traffic graph** showing traffic flow between local NICs and remote endpoints
- Hover tooltips with detailed connection info (byte counts, allowed/blocked, top ports, country)
- **WSL-guest nodes highlighted yellow**; fully-blocked flows drawn as dashed red edges, with drop reasons listed in the edge tooltip
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
- **WSL networking-mode badge** showing the detected mode (NAT / Mirrored / Bridged) and the guest IP
- Auto-refreshes on first visit

## Quick Install

### Option 1: Download (Recommended)

1. Go to the [**Latest Release**](https://github.com/jola0909/WinFWManager/releases/latest)
2. Download **`WinFWManager-<version>-standalone.exe`** (~68 MB, no dependencies needed)
3. Right-click → **Run as Administrator**

That's it — single exe, nothing to install.

> 💡 **Smaller download?** Grab `WinFWManager-<version>-portable.exe` (~7 MB) instead, but you'll need the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) installed.

### Option 2: Build from Source

```bash
git clone https://github.com/jola0909/WinFWManager.git
cd WinFWManager
dotnet publish src/WinFWManager/WinFWManager.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

The exe will be in `src/WinFWManager/bin/Release/net10.0-windows/win-x64/publish/`.

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
│   │   ├── Converters/            # XAML value converters
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
- **ETW** via Microsoft.Diagnostics.Tracing.TraceEvent, using the `Microsoft-Windows-TCPIP` manifest provider for real-time traffic capture with allow/drop visibility
- **System.Reactive** for event batching and throttling
- **MaxMind GeoIP2** for IP geolocation (graceful fallback if DLL is blocked by WDAC)

## License

This project is licensed under the MIT License — see the [LICENSE](LICENSE) file for details.
