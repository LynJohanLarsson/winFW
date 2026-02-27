# WinFWManager Design Document

## Overview

WinFWManager is a WPF desktop application (.NET 8, C#) that provides a Palo Alto-style firewall management experience for Windows Firewall. It replaces manual PowerShell log sifting with a unified UI for real-time traffic monitoring, log analysis, rule management, and network interface visibility — with first-class support for WSL2 and Hyper-V firewall.

## Goals

- Real-time traffic monitoring via ETW (Event Tracing for Windows)
- Historical firewall log file import and analysis
- Full CRUD for Windows Firewall rules across all stores
- Hyper-V firewall rule management (Windows 11+)
- NIC-aware filtering: see which adapter (physical, virtual, vSwitch, WSL) traffic traverses
- Process and GeoIP resolution for traffic context
- Right-click "create rule from traffic" workflow

## Architecture

### Hybrid Backend

- **C# ETW** (via `Microsoft.Diagnostics.Tracing.TraceEvent`) for real-time traffic capture — high performance, kernel-level events
- **PowerShell Runspace Pool** (via `System.Management.Automation`) for firewall rule CRUD — leverages `NetSecurity` module cmdlets which are the most complete API
- **C# file I/O** for firewall log parsing — simple space-delimited format

### Solution Structure

```
WinFWManager.sln
├── WinFWManager/                    # WPF app (UI, Views, ViewModels)
├── WinFWManager.Core/               # Business logic, models, services
└── WinFWManager.Tests/              # Unit tests
```

### Key Patterns

- MVVM via `CommunityToolkit.Mvvm`
- Dependency injection via `Microsoft.Extensions.DependencyInjection`
- ETW events arrive on background threads, batched (100ms window), marshaled to UI via Dispatcher
- PowerShell runspace pool (3-5 runspaces) for non-blocking operations

## Core Services (WinFWManager.Core)

### EtwTrafficMonitor

Subscribes to `Microsoft-Windows-WFP` ETW provider. Emits `TrafficEvent` objects via `IObservable<TrafficEvent>`.

**Captures:** connection established/closed (TCP), packet allowed/blocked (UDP + TCP), filter matches (which rule matched).

**Event data:** IP addresses, ports, protocol, interface LUID, direction, PID, filter ID, action (allow/block).

**Performance:** ring buffer of 50,000 events (oldest drop off), UI updates batched per 100ms, column virtualization in DataGrid.

### FirewallLogParser

Parses standard Windows Firewall log format (`pfirewall.log`). Fields: `date time action protocol src-ip dst-ip src-port dst-port size tcpflags tcpsyn tcpack tcpwin icmptype icmpcode info path`.

Async file loading with progress reporting for large files.

### FirewallRuleService

PowerShell runspace pool for all rule operations:

| Operation | Cmdlets |
|-----------|---------|
| List rules | `Get-NetFirewallRule` + `Get-NetFirewallPortFilter` + `Get-NetFirewallAddressFilter` + `Get-NetFirewallApplicationFilter` |
| Create rule | `New-NetFirewallRule` |
| Edit rule | `Set-NetFirewallRule` |
| Delete rule | `Remove-NetFirewallRule` |
| Enable/Disable | `Enable-NetFirewallRule` / `Disable-NetFirewallRule` |
| Hyper-V rules | `Get-NetFirewallHyperVRule`, `New-NetFirewallHyperVRule`, etc. |
| Profile info | `Get-NetFirewallProfile` |

**Stores handled:** ActiveStore (read-only composite), PersistentStore (where new rules go), ConfigurableServiceStore, GPO/RSOP (read-only display).

### NetworkInterfaceService

Enumerates all network adapters (physical + virtual). Maps adapter GUIDs and interface LUIDs to friendly names. Identifies WSL and Hyper-V adapters, tracks vSwitch membership. Refreshed periodically.

### ProcessResolver

Maps PIDs to process name/path via `Process.GetProcessById()`. Cached in `ConcurrentDictionary<int, ProcessInfo>` with TTL expiry. Short-lived processes shown as "PID N (exited)" when unresolvable.

### GeoIpResolver

Offline MaxMind GeoLite2 `.mmdb` database lookup. Resolves remote IPs to country, city, ASN/organization. Private/RFC1918 IPs labeled "Private" without lookup. DNS reverse lookup (`Dns.GetHostEntryAsync`) cached with 5-minute TTL.

## UI Layout

### Main Window

- **Top bar:** App title, global filters (time range, profile selector: Domain/Private/Public/Any), start/stop monitoring button, admin status indicator
- **Left sidebar:** Quick-access filter tree (by NIC, by Profile, by Direction)
- **Center:** Tab control

### Tab 1: Traffic Monitor (default)

Real-time scrolling DataGrid from ETW events.

**Columns:** Time, Direction, Protocol, Source IP, Src Port, Dest IP, Dst Port, NIC/Adapter, Process, Action, Profile, Country, Hostname.

**Features:**
- Auto-scroll with pause button
- Quick filter row above grid (type in any column to filter)
- Right-click context menu: Create rule from connection, Whois, Copy, Filter to source/dest
- Color coding: green=allowed, red=blocked, yellow=WSL traffic, blue=Hyper-V

**Filter chain:** ETW Event -> NIC filter -> Profile filter -> Protocol filter -> Process filter -> IP/Port filter -> Direction filter -> DataGrid

### Tab 2: Firewall Log Viewer

Same grid layout as Traffic Monitor, loaded from log files. File picker for `pfirewall.log` or custom paths. Date range filter. Same right-click context menu.

### Tab 3: Rules Manager

DataGrid of all firewall rules across stores.

**Columns:** Name, Enabled, Direction, Action, Protocol, LocalPort, RemotePort, LocalAddress, RemoteAddress, Profile, Store, Interface, Program.

**Toolbar:** New Rule, Edit, Delete, Enable/Disable, Refresh.

**Filters:** Store dropdown, Profile filter, search box for name/program.

**Sub-section:** Toggle for Hyper-V Firewall Rules.

### Tab 4: Network Interfaces

List of all adapters. Shows: Name, Type (Physical/Virtual/vSwitch/WSL), Status, IP addresses, MAC, Profile assigned, vSwitch membership. Click adapter to filter Traffic Monitor to that NIC.

### Tab 5: Dashboard

Summary stats: total connections, blocked %, top talkers, top blocked destinations. Lower priority — can be deferred.

## WSL / Hyper-V Integration

WSL2 traffic flow: WSL eth0 -> Hyper-V vSwitch -> vEthernet (WSL) -> NAT -> physical NIC.

**Identification methods:**
1. Interface LUID matching `vEthernet (WSL)` adapter
2. Source IP in WSL subnet range (typically `172.x.x.x`)
3. Hyper-V firewall rules via `Get-NetFirewallHyperVRule` (Windows 11+)

**"Create Rule from Traffic" for WSL:** offers choice between standard Windows Firewall rule or Hyper-V firewall rule.

## Error Handling & Permissions

- App manifest requests `requireAdministrator` elevation
- Without admin: limited functionality warning banner, can view some rules and parse logs, no ETW, no rule creation
- PowerShell errors surfaced in status bar (not modal dialogs)
- ETW disconnection: auto-reconnect with exponential backoff
- Graceful degradation: if Hyper-V firewall cmdlets unavailable (older Windows), that UI section is hidden

## Dependencies

- `Microsoft.Diagnostics.Tracing.TraceEvent` — ETW subscription
- `CommunityToolkit.Mvvm` — MVVM framework
- `Microsoft.Extensions.DependencyInjection` — DI container
- `MaxMind.GeoIP2` — GeoIP database reader
- `System.Management.Automation` — PowerShell runspace
- GeoLite2 `.mmdb` database file (bundled or first-run download)
