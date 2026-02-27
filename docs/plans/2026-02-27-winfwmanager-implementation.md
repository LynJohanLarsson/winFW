# WinFWManager Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Build a WPF desktop firewall manager with real-time ETW traffic monitoring, log analysis, full rule CRUD, and WSL2/Hyper-V support.

**Architecture:** Hybrid backend — C# ETW for real-time traffic, PowerShell runspace pool for firewall rule CRUD, C# file I/O for log parsing. WPF MVVM frontend with CommunityToolkit.Mvvm and DI.

**Tech Stack:** .NET 8, WPF, C#, xUnit, CommunityToolkit.Mvvm, Microsoft.Diagnostics.Tracing.TraceEvent, MaxMind.GeoIP2, System.Management.Automation

---

## Phase 1: Solution Scaffolding

### Task 1: Create solution and projects

**Files:**
- Create: `src/WinFWManager/WinFWManager.csproj`
- Create: `src/WinFWManager.Core/WinFWManager.Core.csproj`
- Create: `tests/WinFWManager.Tests/WinFWManager.Tests.csproj`
- Create: `WinFWManager.sln`

**Step 1: Create solution and projects**

```bash
cd C:/Claude/winFW
dotnet new sln -n WinFWManager
mkdir -p src tests
dotnet new wpf -n WinFWManager -o src/WinFWManager --framework net8.0-windows
dotnet new classlib -n WinFWManager.Core -o src/WinFWManager.Core --framework net8.0-windows
dotnet new xunit -n WinFWManager.Tests -o tests/WinFWManager.Tests --framework net8.0-windows
```

**Step 2: Add projects to solution and set references**

```bash
cd C:/Claude/winFW
dotnet sln add src/WinFWManager/WinFWManager.csproj
dotnet sln add src/WinFWManager.Core/WinFWManager.Core.csproj
dotnet sln add tests/WinFWManager.Tests/WinFWManager.Tests.csproj
dotnet add src/WinFWManager/WinFWManager.csproj reference src/WinFWManager.Core/WinFWManager.Core.csproj
dotnet add tests/WinFWManager.Tests/WinFWManager.Tests.csproj reference src/WinFWManager.Core/WinFWManager.Core.csproj
```

**Step 3: Add NuGet packages to WinFWManager (WPF app)**

```bash
cd C:/Claude/winFW
dotnet add src/WinFWManager/WinFWManager.csproj package CommunityToolkit.Mvvm
dotnet add src/WinFWManager/WinFWManager.csproj package Microsoft.Extensions.DependencyInjection
dotnet add src/WinFWManager/WinFWManager.csproj package Microsoft.Extensions.Hosting
```

**Step 4: Add NuGet packages to WinFWManager.Core**

```bash
cd C:/Claude/winFW
dotnet add src/WinFWManager.Core/WinFWManager.Core.csproj package Microsoft.Diagnostics.Tracing.TraceEvent
dotnet add src/WinFWManager.Core/WinFWManager.Core.csproj package MaxMind.GeoIP2
dotnet add src/WinFWManager.Core/WinFWManager.Core.csproj package System.Management.Automation
dotnet add src/WinFWManager.Core/WinFWManager.Core.csproj package Microsoft.Extensions.DependencyInjection.Abstractions
```

**Step 5: Add test packages**

```bash
cd C:/Claude/winFW
dotnet add tests/WinFWManager.Tests/WinFWManager.Tests.csproj package Moq
dotnet add tests/WinFWManager.Tests/WinFWManager.Tests.csproj package FluentAssertions
```

**Step 6: Add admin manifest to WPF project**

Create `src/WinFWManager/app.manifest`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
    <security>
      <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v2">
        <requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
      </requestedPrivileges>
    </security>
  </trustInfo>
</assembly>
```

Add to `WinFWManager.csproj` inside `<PropertyGroup>`:

```xml
<ApplicationManifest>app.manifest</ApplicationManifest>
```

**Step 7: Add .gitignore and verify build**

Create `.gitignore` for .NET projects, then:

```bash
cd C:/Claude/winFW
dotnet build
```

Expected: Build succeeded with 0 errors.

**Step 8: Commit**

```bash
cd C:/Claude/winFW
git add -A
git commit -m "feat: scaffold solution with WPF, Core, and Tests projects"
```

---

## Phase 2: Core Models & Enums

### Task 2: Define shared enums

**Files:**
- Create: `src/WinFWManager.Core/Models/Enums.cs`

**Step 1: Write enum definitions**

```csharp
namespace WinFWManager.Core.Models;

public enum TrafficDirection
{
    Inbound,
    Outbound
}

public enum TrafficAction
{
    Allow,
    Block,
    Drop
}

public enum FirewallProfile
{
    Domain,
    Private,
    Public,
    Any
}

public enum FirewallStore
{
    ActiveStore,
    PersistentStore,
    ConfigurableServiceStore,
    GPO
}

public enum AdapterType
{
    Physical,
    Virtual,
    VSwitch,
    WSL,
    HyperV,
    Loopback,
    Unknown
}

public enum TransportProtocol
{
    TCP,
    UDP,
    ICMP,
    ICMPv6,
    Other
}
```

**Step 2: Build**

```bash
cd C:/Claude/winFW && dotnet build src/WinFWManager.Core
```

Expected: Build succeeded.

**Step 3: Commit**

```bash
git add src/WinFWManager.Core/Models/Enums.cs
git commit -m "feat: add core enum definitions for traffic, firewall, and adapter types"
```

### Task 3: Define TrafficEvent model

**Files:**
- Create: `src/WinFWManager.Core/Models/TrafficEvent.cs`
- Create: `tests/WinFWManager.Tests/Models/TrafficEventTests.cs`

**Step 1: Write the test**

```csharp
using FluentAssertions;
using WinFWManager.Core.Models;
using System.Net;

namespace WinFWManager.Tests.Models;

public class TrafficEventTests
{
    [Fact]
    public void TrafficEvent_ShouldStoreAllProperties()
    {
        var evt = new TrafficEvent
        {
            Timestamp = new DateTime(2026, 2, 27, 10, 0, 0),
            Direction = TrafficDirection.Inbound,
            Protocol = TransportProtocol.TCP,
            SourceAddress = IPAddress.Parse("192.168.1.100"),
            SourcePort = 54321,
            DestinationAddress = IPAddress.Parse("10.0.0.1"),
            DestinationPort = 443,
            Action = TrafficAction.Allow,
            ProcessId = 1234,
            ProcessName = "chrome.exe",
            InterfaceName = "Ethernet",
            Profile = FirewallProfile.Private,
            Country = "US",
            Hostname = "example.com"
        };

        evt.Timestamp.Should().Be(new DateTime(2026, 2, 27, 10, 0, 0));
        evt.Direction.Should().Be(TrafficDirection.Inbound);
        evt.Protocol.Should().Be(TransportProtocol.TCP);
        evt.SourceAddress.Should().Be(IPAddress.Parse("192.168.1.100"));
        evt.SourcePort.Should().Be(54321);
        evt.DestinationAddress.Should().Be(IPAddress.Parse("10.0.0.1"));
        evt.DestinationPort.Should().Be(443);
        evt.Action.Should().Be(TrafficAction.Allow);
        evt.ProcessId.Should().Be(1234);
        evt.ProcessName.Should().Be("chrome.exe");
        evt.InterfaceName.Should().Be("Ethernet");
        evt.Profile.Should().Be(FirewallProfile.Private);
        evt.Country.Should().Be("US");
        evt.Hostname.Should().Be("example.com");
    }

    [Fact]
    public void IsWslTraffic_WhenInterfaceContainsWSL_ReturnsTrue()
    {
        var evt = new TrafficEvent { InterfaceName = "vEthernet (WSL)" };
        evt.IsWslTraffic.Should().BeTrue();
    }

    [Fact]
    public void IsWslTraffic_WhenPhysicalAdapter_ReturnsFalse()
    {
        var evt = new TrafficEvent { InterfaceName = "Ethernet" };
        evt.IsWslTraffic.Should().BeFalse();
    }

    [Fact]
    public void IsHyperVTraffic_WhenInterfaceContainsHyperV_ReturnsTrue()
    {
        var evt = new TrafficEvent { InterfaceName = "vEthernet (Default Switch)" };
        evt.IsHyperVTraffic.Should().BeTrue();
    }

    [Fact]
    public void IsPrivateAddress_WhenRfc1918_ReturnsTrue()
    {
        var evt = new TrafficEvent { DestinationAddress = IPAddress.Parse("192.168.1.1") };
        evt.IsDestinationPrivate.Should().BeTrue();
    }
}
```

**Step 2: Run test to verify it fails**

```bash
cd C:/Claude/winFW && dotnet test tests/WinFWManager.Tests --filter "TrafficEventTests" -v n
```

Expected: FAIL — `TrafficEvent` type not found.

**Step 3: Implement TrafficEvent**

```csharp
using System.Net;

namespace WinFWManager.Core.Models;

public class TrafficEvent
{
    public DateTime Timestamp { get; set; }
    public TrafficDirection Direction { get; set; }
    public TransportProtocol Protocol { get; set; }
    public IPAddress? SourceAddress { get; set; }
    public int SourcePort { get; set; }
    public IPAddress? DestinationAddress { get; set; }
    public int DestinationPort { get; set; }
    public TrafficAction Action { get; set; }
    public int ProcessId { get; set; }
    public string? ProcessName { get; set; }
    public string? InterfaceName { get; set; }
    public long InterfaceLuid { get; set; }
    public FirewallProfile Profile { get; set; }
    public string? Country { get; set; }
    public string? City { get; set; }
    public string? Asn { get; set; }
    public string? Hostname { get; set; }
    public long FilterId { get; set; }

    public bool IsWslTraffic =>
        InterfaceName?.Contains("WSL", StringComparison.OrdinalIgnoreCase) == true;

    public bool IsHyperVTraffic =>
        InterfaceName?.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase) == true
        && !IsWslTraffic;

    public bool IsDestinationPrivate =>
        DestinationAddress != null && IsPrivateAddress(DestinationAddress);

    public bool IsSourcePrivate =>
        SourceAddress != null && IsPrivateAddress(SourceAddress);

    private static bool IsPrivateAddress(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return false;
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 127);
    }
}
```

**Step 4: Run tests**

```bash
cd C:/Claude/winFW && dotnet test tests/WinFWManager.Tests --filter "TrafficEventTests" -v n
```

Expected: All 5 tests PASS.

**Step 5: Commit**

```bash
git add src/WinFWManager.Core/Models/TrafficEvent.cs tests/WinFWManager.Tests/Models/TrafficEventTests.cs
git commit -m "feat: add TrafficEvent model with WSL/HyperV/RFC1918 detection"
```

### Task 4: Define FirewallRuleInfo model

**Files:**
- Create: `src/WinFWManager.Core/Models/FirewallRuleInfo.cs`

**Step 1: Write the model**

```csharp
namespace WinFWManager.Core.Models;

public class FirewallRuleInfo
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Enabled { get; set; }
    public TrafficDirection Direction { get; set; }
    public TrafficAction Action { get; set; }
    public TransportProtocol Protocol { get; set; }
    public string? LocalPort { get; set; }
    public string? RemotePort { get; set; }
    public string? LocalAddress { get; set; }
    public string? RemoteAddress { get; set; }
    public FirewallProfile Profile { get; set; }
    public FirewallStore Store { get; set; }
    public string? InterfaceAlias { get; set; }
    public string? Program { get; set; }
    public string? Group { get; set; }
    public bool IsHyperVRule { get; set; }
}
```

**Step 2: Build and commit**

```bash
cd C:/Claude/winFW && dotnet build src/WinFWManager.Core
git add src/WinFWManager.Core/Models/FirewallRuleInfo.cs
git commit -m "feat: add FirewallRuleInfo model"
```

### Task 5: Define NetworkAdapterInfo model

**Files:**
- Create: `src/WinFWManager.Core/Models/NetworkAdapterInfo.cs`

**Step 1: Write the model**

```csharp
using System.Net;

namespace WinFWManager.Core.Models;

public class NetworkAdapterInfo
{
    public string Name { get; set; } = string.Empty;
    public string InterfaceAlias { get; set; } = string.Empty;
    public Guid InterfaceGuid { get; set; }
    public long InterfaceLuid { get; set; }
    public AdapterType AdapterType { get; set; }
    public string Status { get; set; } = "Unknown";
    public List<IPAddress> IpAddresses { get; set; } = new();
    public string? MacAddress { get; set; }
    public FirewallProfile AssignedProfile { get; set; }
    public string? VSwitchName { get; set; }
    public int InterfaceIndex { get; set; }

    public bool IsVirtual => AdapterType is AdapterType.Virtual
        or AdapterType.VSwitch or AdapterType.WSL or AdapterType.HyperV;
}
```

**Step 2: Build and commit**

```bash
cd C:/Claude/winFW && dotnet build src/WinFWManager.Core
git add src/WinFWManager.Core/Models/NetworkAdapterInfo.cs
git commit -m "feat: add NetworkAdapterInfo model"
```

### Task 6: Define ProcessInfo and GeoInfo models

**Files:**
- Create: `src/WinFWManager.Core/Models/ProcessInfo.cs`
- Create: `src/WinFWManager.Core/Models/GeoInfo.cs`

**Step 1: Write ProcessInfo**

```csharp
namespace WinFWManager.Core.Models;

public class ProcessInfo
{
    public int ProcessId { get; set; }
    public string Name { get; set; } = "Unknown";
    public string? Path { get; set; }
    public DateTime ResolvedAt { get; set; }
    public bool IsExited { get; set; }

    public string DisplayName => IsExited ? $"PID {ProcessId} (exited)" : Name;
}
```

**Step 2: Write GeoInfo**

```csharp
namespace WinFWManager.Core.Models;

public class GeoInfo
{
    public string? Country { get; set; }
    public string? CountryCode { get; set; }
    public string? City { get; set; }
    public string? Asn { get; set; }
    public string? Organization { get; set; }
    public bool IsPrivate { get; set; }

    public string DisplayCountry => IsPrivate ? "Private" : Country ?? "Unknown";
}
```

**Step 3: Build and commit**

```bash
cd C:/Claude/winFW && dotnet build src/WinFWManager.Core
git add src/WinFWManager.Core/Models/ProcessInfo.cs src/WinFWManager.Core/Models/GeoInfo.cs
git commit -m "feat: add ProcessInfo and GeoInfo models"
```

---

## Phase 3: Core Services — Service Interfaces

### Task 7: Define all service interfaces

**Files:**
- Create: `src/WinFWManager.Core/Services/INetworkInterfaceService.cs`
- Create: `src/WinFWManager.Core/Services/IProcessResolver.cs`
- Create: `src/WinFWManager.Core/Services/IGeoIpResolver.cs`
- Create: `src/WinFWManager.Core/Services/IFirewallLogParser.cs`
- Create: `src/WinFWManager.Core/Services/IFirewallRuleService.cs`
- Create: `src/WinFWManager.Core/Services/IEtwTrafficMonitor.cs`

**Step 1: Write all interfaces**

`INetworkInterfaceService.cs`:
```csharp
using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

public interface INetworkInterfaceService
{
    Task<IReadOnlyList<NetworkAdapterInfo>> GetAllAdaptersAsync();
    Task RefreshAsync();
    string? ResolveInterfaceName(long interfaceLuid);
    AdapterType ClassifyAdapter(string interfaceName);
}
```

`IProcessResolver.cs`:
```csharp
using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

public interface IProcessResolver
{
    ProcessInfo Resolve(int processId);
    void ClearCache();
}
```

`IGeoIpResolver.cs`:
```csharp
using System.Net;
using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

public interface IGeoIpResolver : IDisposable
{
    GeoInfo Resolve(IPAddress address);
    Task<string?> ReverseDnsAsync(IPAddress address);
}
```

`IFirewallLogParser.cs`:
```csharp
using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

public interface IFirewallLogParser
{
    Task<IReadOnlyList<TrafficEvent>> ParseFileAsync(
        string filePath,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);
}
```

`IFirewallRuleService.cs`:
```csharp
using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

public interface IFirewallRuleService : IDisposable
{
    Task<IReadOnlyList<FirewallRuleInfo>> GetRulesAsync(FirewallStore store);
    Task<IReadOnlyList<FirewallRuleInfo>> GetHyperVRulesAsync();
    Task CreateRuleAsync(FirewallRuleInfo rule);
    Task UpdateRuleAsync(FirewallRuleInfo rule);
    Task DeleteRuleAsync(string ruleName);
    Task SetRuleEnabledAsync(string ruleName, bool enabled);
    Task<IReadOnlyList<FirewallProfile>> GetActiveProfilesAsync();
    bool IsHyperVFirewallAvailable { get; }
}
```

`IEtwTrafficMonitor.cs`:
```csharp
using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

public interface IEtwTrafficMonitor : IDisposable
{
    IObservable<TrafficEvent> TrafficEvents { get; }
    bool IsRunning { get; }
    bool RequiresAdmin { get; }
    void Start();
    void Stop();
}
```

**Step 2: Build and commit**

```bash
cd C:/Claude/winFW && dotnet build src/WinFWManager.Core
git add src/WinFWManager.Core/Services/
git commit -m "feat: add service interfaces for all core services"
```

---

## Phase 4: ProcessResolver Implementation

### Task 8: Implement ProcessResolver with TDD

**Files:**
- Create: `src/WinFWManager.Core/Services/ProcessResolver.cs`
- Create: `tests/WinFWManager.Tests/Services/ProcessResolverTests.cs`

**Step 1: Write tests**

```csharp
using FluentAssertions;
using WinFWManager.Core.Services;

namespace WinFWManager.Tests.Services;

public class ProcessResolverTests
{
    private readonly ProcessResolver _resolver = new(cacheTtlSeconds: 60);

    [Fact]
    public void Resolve_CurrentProcess_ReturnsProcessInfo()
    {
        var pid = Environment.ProcessId;
        var info = _resolver.Resolve(pid);

        info.ProcessId.Should().Be(pid);
        info.Name.Should().NotBeNullOrEmpty();
        info.IsExited.Should().BeFalse();
    }

    [Fact]
    public void Resolve_InvalidPid_ReturnsExitedProcess()
    {
        var info = _resolver.Resolve(999999);

        info.ProcessId.Should().Be(999999);
        info.IsExited.Should().BeTrue();
        info.DisplayName.Should().Contain("exited");
    }

    [Fact]
    public void Resolve_SamePidTwice_ReturnsCached()
    {
        var pid = Environment.ProcessId;
        var first = _resolver.Resolve(pid);
        var second = _resolver.Resolve(pid);

        first.Should().BeSameAs(second);
    }

    [Fact]
    public void ClearCache_RemovesCachedEntries()
    {
        var pid = Environment.ProcessId;
        var first = _resolver.Resolve(pid);
        _resolver.ClearCache();
        var second = _resolver.Resolve(pid);

        first.Should().NotBeSameAs(second);
    }
}
```

**Step 2: Run tests — verify fail**

```bash
cd C:/Claude/winFW && dotnet test tests/WinFWManager.Tests --filter "ProcessResolverTests" -v n
```

Expected: FAIL — `ProcessResolver` class not found.

**Step 3: Implement ProcessResolver**

```csharp
using System.Collections.Concurrent;
using System.Diagnostics;
using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

public class ProcessResolver : IProcessResolver
{
    private readonly ConcurrentDictionary<int, (ProcessInfo Info, DateTime CachedAt)> _cache = new();
    private readonly int _cacheTtlSeconds;

    public ProcessResolver(int cacheTtlSeconds = 300)
    {
        _cacheTtlSeconds = cacheTtlSeconds;
    }

    public ProcessInfo Resolve(int processId)
    {
        if (_cache.TryGetValue(processId, out var cached))
        {
            if ((DateTime.UtcNow - cached.CachedAt).TotalSeconds < _cacheTtlSeconds)
                return cached.Info;
            _cache.TryRemove(processId, out _);
        }

        var info = ResolveInternal(processId);
        _cache[processId] = (info, DateTime.UtcNow);
        return info;
    }

    public void ClearCache() => _cache.Clear();

    private static ProcessInfo ResolveInternal(int processId)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            return new ProcessInfo
            {
                ProcessId = processId,
                Name = process.ProcessName,
                Path = TryGetProcessPath(process),
                ResolvedAt = DateTime.UtcNow,
                IsExited = false
            };
        }
        catch (ArgumentException)
        {
            return new ProcessInfo
            {
                ProcessId = processId,
                ResolvedAt = DateTime.UtcNow,
                IsExited = true
            };
        }
    }

    private static string? TryGetProcessPath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch { return null; }
    }
}
```

**Step 4: Run tests — verify pass**

```bash
cd C:/Claude/winFW && dotnet test tests/WinFWManager.Tests --filter "ProcessResolverTests" -v n
```

Expected: All 4 tests PASS.

**Step 5: Commit**

```bash
git add src/WinFWManager.Core/Services/ProcessResolver.cs tests/WinFWManager.Tests/Services/ProcessResolverTests.cs
git commit -m "feat: implement ProcessResolver with TTL cache"
```

---

## Phase 5: FirewallLogParser Implementation

### Task 9: Implement FirewallLogParser with TDD

**Files:**
- Create: `src/WinFWManager.Core/Services/FirewallLogParser.cs`
- Create: `tests/WinFWManager.Tests/Services/FirewallLogParserTests.cs`
- Create: `tests/WinFWManager.Tests/TestData/sample-firewall.log`

**Step 1: Create sample test log file**

```
#Version: 1.5
#Software: Microsoft Windows Firewall
#Time Format: Local
#Fields: date time action protocol src-ip dst-ip src-port dst-port size tcpflags tcpsyn tcpack tcpwin icmptype icmpcode info path SEND
2026-02-27 10:00:01 ALLOW TCP 192.168.1.100 10.0.0.1 54321 443 0 - 0 0 0 - - - SEND
2026-02-27 10:00:02 DROP UDP 172.28.0.5 8.8.8.8 12345 53 64 - - - - - - - RECEIVE
2026-02-27 10:00:03 ALLOW TCP 10.0.0.1 192.168.1.100 443 54321 0 - 0 0 0 - - - RECEIVE
```

**Step 2: Write tests**

```csharp
using FluentAssertions;
using WinFWManager.Core.Models;
using WinFWManager.Core.Services;
using System.Net;

namespace WinFWManager.Tests.Services;

public class FirewallLogParserTests
{
    private readonly FirewallLogParser _parser = new();

    [Fact]
    public async Task ParseFileAsync_ValidLog_ParsesAllEntries()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "sample-firewall.log");
        var events = await _parser.ParseFileAsync(path);

        events.Should().HaveCount(3);
    }

    [Fact]
    public async Task ParseFileAsync_SkipsCommentLines()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "sample-firewall.log");
        var events = await _parser.ParseFileAsync(path);

        events.Should().NotContain(e => e.SourcePort == 0 && e.DestinationPort == 0);
    }

    [Fact]
    public async Task ParseFileAsync_ParsesTcpAllow()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "sample-firewall.log");
        var events = await _parser.ParseFileAsync(path);

        var first = events[0];
        first.Action.Should().Be(TrafficAction.Allow);
        first.Protocol.Should().Be(TransportProtocol.TCP);
        first.SourceAddress.Should().Be(IPAddress.Parse("192.168.1.100"));
        first.SourcePort.Should().Be(54321);
        first.DestinationAddress.Should().Be(IPAddress.Parse("10.0.0.1"));
        first.DestinationPort.Should().Be(443);
        first.Direction.Should().Be(TrafficDirection.Outbound);
    }

    [Fact]
    public async Task ParseFileAsync_ParsesUdpDrop()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "sample-firewall.log");
        var events = await _parser.ParseFileAsync(path);

        var second = events[1];
        second.Action.Should().Be(TrafficAction.Drop);
        second.Protocol.Should().Be(TransportProtocol.UDP);
        second.SourceAddress.Should().Be(IPAddress.Parse("172.28.0.5"));
        second.Direction.Should().Be(TrafficDirection.Inbound);
    }

    [Fact]
    public async Task ParseFileAsync_ReportsProgress()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "sample-firewall.log");
        var progressValues = new List<int>();
        var progress = new Progress<int>(v => progressValues.Add(v));

        await _parser.ParseFileAsync(path, progress);

        // Give Progress<T> callback time to fire (it posts to SynchronizationContext)
        await Task.Delay(100);
        progressValues.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ParseFileAsync_CancellationToken_Cancels()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "sample-firewall.log");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => _parser.ParseFileAsync(path, cancellationToken: cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
```

**Step 3: Run tests — verify fail**

```bash
cd C:/Claude/winFW && dotnet test tests/WinFWManager.Tests --filter "FirewallLogParserTests" -v n
```

Expected: FAIL — `FirewallLogParser` not found.

**Step 4: Implement FirewallLogParser**

```csharp
using System.Net;
using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

public class FirewallLogParser : IFirewallLogParser
{
    public async Task<IReadOnlyList<TrafficEvent>> ParseFileAsync(
        string filePath,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var lines = await File.ReadAllLinesAsync(filePath, cancellationToken);
        var events = new List<TrafficEvent>();
        var dataLines = lines.Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('#')).ToArray();
        int total = dataLines.Length;

        for (int i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var evt = ParseLine(dataLines[i]);
            if (evt != null)
                events.Add(evt);

            if (progress != null && (i % 100 == 0 || i == total - 1))
                progress.Report((int)((i + 1) * 100.0 / total));
        }

        return events.AsReadOnly();
    }

    private static TrafficEvent? ParseLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 17) return null;

        try
        {
            var direction = parts[16].Equals("SEND", StringComparison.OrdinalIgnoreCase)
                ? TrafficDirection.Outbound
                : TrafficDirection.Inbound;

            return new TrafficEvent
            {
                Timestamp = DateTime.Parse($"{parts[0]} {parts[1]}"),
                Action = ParseAction(parts[2]),
                Protocol = ParseProtocol(parts[3]),
                SourceAddress = IPAddress.TryParse(parts[4], out var src) ? src : null,
                DestinationAddress = IPAddress.TryParse(parts[5], out var dst) ? dst : null,
                SourcePort = int.TryParse(parts[6], out var sp) ? sp : 0,
                DestinationPort = int.TryParse(parts[7], out var dp) ? dp : 0,
                Direction = direction
            };
        }
        catch
        {
            return null;
        }
    }

    private static TrafficAction ParseAction(string action) => action.ToUpperInvariant() switch
    {
        "ALLOW" => TrafficAction.Allow,
        "DROP" => TrafficAction.Drop,
        "BLOCK" => TrafficAction.Block,
        _ => TrafficAction.Block
    };

    private static TransportProtocol ParseProtocol(string protocol) => protocol.ToUpperInvariant() switch
    {
        "TCP" => TransportProtocol.TCP,
        "UDP" => TransportProtocol.UDP,
        "ICMP" => TransportProtocol.ICMP,
        "ICMPV6" => TransportProtocol.ICMPv6,
        _ => TransportProtocol.Other
    };
}
```

Note: The sample log file must be set to `CopyToOutputDirectory` in the test `.csproj`. Add to `tests/WinFWManager.Tests/WinFWManager.Tests.csproj`:

```xml
<ItemGroup>
  <Content Include="TestData\**\*" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

**Step 5: Run tests — verify pass**

```bash
cd C:/Claude/winFW && dotnet test tests/WinFWManager.Tests --filter "FirewallLogParserTests" -v n
```

Expected: All 6 tests PASS.

**Step 6: Commit**

```bash
git add src/WinFWManager.Core/Services/FirewallLogParser.cs tests/WinFWManager.Tests/Services/FirewallLogParserTests.cs tests/WinFWManager.Tests/TestData/ tests/WinFWManager.Tests/WinFWManager.Tests.csproj
git commit -m "feat: implement FirewallLogParser with async file parsing"
```

---

## Phase 6: NetworkInterfaceService Implementation

### Task 10: Implement NetworkInterfaceService

**Files:**
- Create: `src/WinFWManager.Core/Services/NetworkInterfaceService.cs`
- Create: `tests/WinFWManager.Tests/Services/NetworkInterfaceServiceTests.cs`

**Step 1: Write tests**

```csharp
using FluentAssertions;
using WinFWManager.Core.Models;
using WinFWManager.Core.Services;

namespace WinFWManager.Tests.Services;

public class NetworkInterfaceServiceTests
{
    [Fact]
    public void ClassifyAdapter_WSL_ReturnsWSL()
    {
        var svc = new NetworkInterfaceService();
        svc.ClassifyAdapter("vEthernet (WSL)").Should().Be(AdapterType.WSL);
    }

    [Fact]
    public void ClassifyAdapter_HyperVSwitch_ReturnsVSwitch()
    {
        var svc = new NetworkInterfaceService();
        svc.ClassifyAdapter("vEthernet (Default Switch)").Should().Be(AdapterType.VSwitch);
    }

    [Fact]
    public void ClassifyAdapter_Physical_ReturnsPhysical()
    {
        var svc = new NetworkInterfaceService();
        svc.ClassifyAdapter("Ethernet").Should().Be(AdapterType.Physical);
    }

    [Fact]
    public void ClassifyAdapter_Loopback_ReturnsLoopback()
    {
        var svc = new NetworkInterfaceService();
        svc.ClassifyAdapter("Loopback Pseudo-Interface 1").Should().Be(AdapterType.Loopback);
    }

    [Fact]
    public async Task GetAllAdaptersAsync_ReturnsAtLeastOne()
    {
        var svc = new NetworkInterfaceService();
        var adapters = await svc.GetAllAdaptersAsync();
        adapters.Should().NotBeEmpty();
    }

    [Fact]
    public async Task RefreshAsync_DoesNotThrow()
    {
        var svc = new NetworkInterfaceService();
        var act = () => svc.RefreshAsync();
        await act.Should().NotThrowAsync();
    }
}
```

**Step 2: Run tests — verify fail**

```bash
cd C:/Claude/winFW && dotnet test tests/WinFWManager.Tests --filter "NetworkInterfaceServiceTests" -v n
```

**Step 3: Implement NetworkInterfaceService**

```csharp
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

public class NetworkInterfaceService : INetworkInterfaceService
{
    private List<NetworkAdapterInfo> _adapters = new();
    private readonly ConcurrentDictionary<long, string> _luidToName = new();

    public async Task<IReadOnlyList<NetworkAdapterInfo>> GetAllAdaptersAsync()
    {
        await RefreshAsync();
        return _adapters.AsReadOnly();
    }

    public Task RefreshAsync()
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces();
        var adapters = new List<NetworkAdapterInfo>();

        foreach (var ni in interfaces)
        {
            var props = ni.GetIPProperties();
            var addresses = props.UnicastAddresses
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                         || a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                .ToList();

            var adapter = new NetworkAdapterInfo
            {
                Name = ni.Name,
                InterfaceAlias = ni.Description,
                AdapterType = ClassifyAdapter(ni.Name),
                Status = ni.OperationalStatus.ToString(),
                IpAddresses = addresses,
                MacAddress = FormatMac(ni.GetPhysicalAddress()),
                InterfaceIndex = ni.GetIPProperties().GetIPv4Properties()?.Index ?? 0
            };

            adapters.Add(adapter);
        }

        _adapters = adapters;
        return Task.CompletedTask;
    }

    public string? ResolveInterfaceName(long interfaceLuid)
    {
        if (_luidToName.TryGetValue(interfaceLuid, out var name))
            return name;

        // LUID resolution will be populated when we integrate with ETW
        // which provides LUID-to-name mapping from Get-NetAdapter
        return null;
    }

    public AdapterType ClassifyAdapter(string interfaceName)
    {
        if (string.IsNullOrEmpty(interfaceName))
            return AdapterType.Unknown;

        if (interfaceName.Contains("Loopback", StringComparison.OrdinalIgnoreCase))
            return AdapterType.Loopback;

        if (interfaceName.Contains("WSL", StringComparison.OrdinalIgnoreCase))
            return AdapterType.WSL;

        if (interfaceName.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase))
            return AdapterType.VSwitch;

        if (interfaceName.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase))
            return AdapterType.HyperV;

        if (interfaceName.StartsWith("vSwitch", StringComparison.OrdinalIgnoreCase))
            return AdapterType.VSwitch;

        // Check for common virtual adapter patterns
        if (interfaceName.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
            || interfaceName.Contains("VPN", StringComparison.OrdinalIgnoreCase)
            || interfaceName.Contains("TAP", StringComparison.OrdinalIgnoreCase))
            return AdapterType.Virtual;

        return AdapterType.Physical;
    }

    private static string? FormatMac(PhysicalAddress mac)
    {
        var bytes = mac.GetAddressBytes();
        if (bytes.Length == 0) return null;
        return string.Join(":", bytes.Select(b => b.ToString("X2")));
    }
}
```

**Step 4: Run tests — verify pass**

```bash
cd C:/Claude/winFW && dotnet test tests/WinFWManager.Tests --filter "NetworkInterfaceServiceTests" -v n
```

Expected: All 6 tests PASS.

**Step 5: Commit**

```bash
git add src/WinFWManager.Core/Services/NetworkInterfaceService.cs tests/WinFWManager.Tests/Services/NetworkInterfaceServiceTests.cs
git commit -m "feat: implement NetworkInterfaceService with adapter classification"
```

---

## Phase 7: GeoIpResolver Implementation

### Task 11: Implement GeoIpResolver with TDD

**Files:**
- Create: `src/WinFWManager.Core/Services/GeoIpResolver.cs`
- Create: `tests/WinFWManager.Tests/Services/GeoIpResolverTests.cs`

**Step 1: Write tests**

```csharp
using FluentAssertions;
using System.Net;
using WinFWManager.Core.Services;

namespace WinFWManager.Tests.Services;

public class GeoIpResolverTests
{
    [Fact]
    public void Resolve_PrivateAddress_ReturnsPrivate()
    {
        var resolver = new GeoIpResolver(mmdbPath: null);
        var info = resolver.Resolve(IPAddress.Parse("192.168.1.1"));

        info.IsPrivate.Should().BeTrue();
        info.DisplayCountry.Should().Be("Private");
    }

    [Fact]
    public void Resolve_LoopbackAddress_ReturnsPrivate()
    {
        var resolver = new GeoIpResolver(mmdbPath: null);
        var info = resolver.Resolve(IPAddress.Loopback);

        info.IsPrivate.Should().BeTrue();
    }

    [Fact]
    public void Resolve_Rfc1918_10Network_ReturnsPrivate()
    {
        var resolver = new GeoIpResolver(mmdbPath: null);
        var info = resolver.Resolve(IPAddress.Parse("10.0.0.1"));

        info.IsPrivate.Should().BeTrue();
    }

    [Fact]
    public void Resolve_Rfc1918_172Network_ReturnsPrivate()
    {
        var resolver = new GeoIpResolver(mmdbPath: null);
        var info = resolver.Resolve(IPAddress.Parse("172.16.0.1"));

        info.IsPrivate.Should().BeTrue();
    }

    [Fact]
    public void Resolve_PublicAddress_WithoutMmdb_ReturnsUnknown()
    {
        var resolver = new GeoIpResolver(mmdbPath: null);
        var info = resolver.Resolve(IPAddress.Parse("8.8.8.8"));

        info.IsPrivate.Should().BeFalse();
        info.DisplayCountry.Should().Be("Unknown");
    }

    [Fact]
    public void Resolve_SameAddressTwice_ReturnsCached()
    {
        var resolver = new GeoIpResolver(mmdbPath: null);
        var first = resolver.Resolve(IPAddress.Parse("192.168.1.1"));
        var second = resolver.Resolve(IPAddress.Parse("192.168.1.1"));

        first.Should().BeSameAs(second);
    }

    [Fact]
    public async Task ReverseDnsAsync_Localhost_ReturnsHostname()
    {
        var resolver = new GeoIpResolver(mmdbPath: null);
        var hostname = await resolver.ReverseDnsAsync(IPAddress.Loopback);

        hostname.Should().NotBeNullOrEmpty();
    }
}
```

**Step 2: Run tests — verify fail**

```bash
cd C:/Claude/winFW && dotnet test tests/WinFWManager.Tests --filter "GeoIpResolverTests" -v n
```

**Step 3: Implement GeoIpResolver**

```csharp
using System.Collections.Concurrent;
using System.Net;
using MaxMind.GeoIP2;
using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

public class GeoIpResolver : IGeoIpResolver
{
    private readonly DatabaseReader? _reader;
    private readonly ConcurrentDictionary<IPAddress, GeoInfo> _geoCache = new();
    private readonly ConcurrentDictionary<IPAddress, (string? Hostname, DateTime CachedAt)> _dnsCache = new();
    private static readonly TimeSpan DnsCacheTtl = TimeSpan.FromMinutes(5);

    public GeoIpResolver(string? mmdbPath)
    {
        if (mmdbPath != null && File.Exists(mmdbPath))
        {
            _reader = new DatabaseReader(mmdbPath);
        }
    }

    public GeoInfo Resolve(IPAddress address)
    {
        if (_geoCache.TryGetValue(address, out var cached))
            return cached;

        var info = ResolveInternal(address);
        _geoCache[address] = info;
        return info;
    }

    public async Task<string?> ReverseDnsAsync(IPAddress address)
    {
        if (_dnsCache.TryGetValue(address, out var cached))
        {
            if (DateTime.UtcNow - cached.CachedAt < DnsCacheTtl)
                return cached.Hostname;
            _dnsCache.TryRemove(address, out _);
        }

        try
        {
            var entry = await Dns.GetHostEntryAsync(address);
            var hostname = entry.HostName;
            _dnsCache[address] = (hostname, DateTime.UtcNow);
            return hostname;
        }
        catch
        {
            _dnsCache[address] = (null, DateTime.UtcNow);
            return null;
        }
    }

    public void Dispose()
    {
        _reader?.Dispose();
        GC.SuppressFinalize(this);
    }

    private GeoInfo ResolveInternal(IPAddress address)
    {
        if (IsPrivateAddress(address))
        {
            return new GeoInfo { IsPrivate = true };
        }

        if (_reader == null)
        {
            return new GeoInfo { IsPrivate = false };
        }

        try
        {
            var response = _reader.City(address);
            return new GeoInfo
            {
                Country = response.Country.Name,
                CountryCode = response.Country.IsoCode,
                City = response.City.Name,
                IsPrivate = false
            };
        }
        catch
        {
            return new GeoInfo { IsPrivate = false };
        }
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return address.Equals(IPAddress.IPv6Loopback);
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || bytes[0] == 127;
    }
}
```

**Step 4: Run tests — verify pass**

```bash
cd C:/Claude/winFW && dotnet test tests/WinFWManager.Tests --filter "GeoIpResolverTests" -v n
```

Expected: All 7 tests PASS.

**Step 5: Commit**

```bash
git add src/WinFWManager.Core/Services/GeoIpResolver.cs tests/WinFWManager.Tests/Services/GeoIpResolverTests.cs
git commit -m "feat: implement GeoIpResolver with MaxMind + DNS cache"
```

---

## Phase 8: FirewallRuleService (PowerShell Runspace)

### Task 12: Implement FirewallRuleService

**Files:**
- Create: `src/WinFWManager.Core/Services/FirewallRuleService.cs`
- Create: `src/WinFWManager.Core/Services/PowerShellRunspacePool.cs`

**Step 1: Implement PowerShellRunspacePool helper**

```csharp
using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace WinFWManager.Core.Services;

public class PowerShellRunspacePool : IDisposable
{
    private readonly RunspacePool _pool;

    public PowerShellRunspacePool(int minRunspaces = 1, int maxRunspaces = 5)
    {
        var iss = InitialSessionState.CreateDefault();
        iss.ImportPSModule(new[] { "NetSecurity" });
        _pool = RunspaceFactory.CreateRunspacePool(minRunspaces, maxRunspaces, iss);
        _pool.Open();
    }

    public async Task<IReadOnlyList<T>> InvokeAsync<T>(string script, Dictionary<string, object>? parameters = null)
    {
        using var ps = PowerShell.Create();
        ps.RunspacePool = _pool;
        ps.AddScript(script);

        if (parameters != null)
        {
            foreach (var p in parameters)
                ps.AddParameter(p.Key, p.Value);
        }

        var results = await Task.Run(() => ps.Invoke<T>());

        if (ps.HadErrors)
        {
            var errors = string.Join("; ", ps.Streams.Error.Select(e => e.ToString()));
            throw new InvalidOperationException($"PowerShell error: {errors}");
        }

        return results.ToList().AsReadOnly();
    }

    public async Task InvokeAsync(string script, Dictionary<string, object>? parameters = null)
    {
        using var ps = PowerShell.Create();
        ps.RunspacePool = _pool;
        ps.AddScript(script);

        if (parameters != null)
        {
            foreach (var p in parameters)
                ps.AddParameter(p.Key, p.Value);
        }

        await Task.Run(() => ps.Invoke());

        if (ps.HadErrors)
        {
            var errors = string.Join("; ", ps.Streams.Error.Select(e => e.ToString()));
            throw new InvalidOperationException($"PowerShell error: {errors}");
        }
    }

    public void Dispose()
    {
        _pool.Close();
        _pool.Dispose();
        GC.SuppressFinalize(this);
    }
}
```

**Step 2: Implement FirewallRuleService**

```csharp
using System.Management.Automation;
using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

public class FirewallRuleService : IFirewallRuleService
{
    private readonly PowerShellRunspacePool _pool;
    private bool _hyperVAvailable;

    public bool IsHyperVFirewallAvailable => _hyperVAvailable;

    public FirewallRuleService()
    {
        _pool = new PowerShellRunspacePool();
        CheckHyperVAvailability();
    }

    public async Task<IReadOnlyList<FirewallRuleInfo>> GetRulesAsync(FirewallStore store)
    {
        var storeName = store switch
        {
            FirewallStore.ActiveStore => "ActiveStore",
            FirewallStore.PersistentStore => "PersistentStore",
            FirewallStore.ConfigurableServiceStore => "ConfigurableServiceStore",
            FirewallStore.GPO => "RSOP",
            _ => "ActiveStore"
        };

        var script = $@"
            Get-NetFirewallRule -PolicyStore {storeName} -ErrorAction SilentlyContinue | ForEach-Object {{
                $rule = $_
                $port = $_ | Get-NetFirewallPortFilter -ErrorAction SilentlyContinue
                $addr = $_ | Get-NetFirewallAddressFilter -ErrorAction SilentlyContinue
                $app  = $_ | Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue
                [PSCustomObject]@{{
                    Name          = $rule.Name
                    DisplayName   = $rule.DisplayName
                    Description   = $rule.Description
                    Enabled       = $rule.Enabled.ToString()
                    Direction     = $rule.Direction.ToString()
                    Action        = $rule.Action.ToString()
                    Profile       = $rule.Profile.ToString()
                    Protocol      = $port.Protocol
                    LocalPort     = $port.LocalPort -join ','
                    RemotePort    = $port.RemotePort -join ','
                    LocalAddress  = $addr.LocalAddress -join ','
                    RemoteAddress = $addr.RemoteAddress -join ','
                    Program       = $app.Program
                    Group         = $rule.Group
                }}
            }}
        ";

        var results = await _pool.InvokeAsync<PSObject>(script);
        return results.Select(r => MapToRuleInfo(r, store)).ToList().AsReadOnly();
    }

    public async Task<IReadOnlyList<FirewallRuleInfo>> GetHyperVRulesAsync()
    {
        if (!_hyperVAvailable)
            return Array.Empty<FirewallRuleInfo>().AsReadOnly();

        var script = @"
            Get-NetFirewallHyperVRule -ErrorAction SilentlyContinue | ForEach-Object {
                [PSCustomObject]@{
                    Name        = $_.Name
                    DisplayName = $_.DisplayName
                    Description = $_.Description
                    Enabled     = $_.Enabled.ToString()
                    Direction   = $_.Direction.ToString()
                    Action      = $_.Action.ToString()
                    Profile     = $_.Profile.ToString()
                    Protocol    = $_.Protocol
                    LocalPort   = $_.LocalPorts -join ','
                    RemotePort  = $_.RemotePorts -join ','
                    LocalAddress  = $_.LocalAddresses -join ','
                    RemoteAddress = $_.RemoteAddresses -join ','
                    Program     = ''
                    Group       = ''
                }
            }
        ";

        var results = await _pool.InvokeAsync<PSObject>(script);
        return results.Select(r =>
        {
            var rule = MapToRuleInfo(r, FirewallStore.PersistentStore);
            rule.IsHyperVRule = true;
            return rule;
        }).ToList().AsReadOnly();
    }

    public async Task CreateRuleAsync(FirewallRuleInfo rule)
    {
        var script = rule.IsHyperVRule
            ? BuildHyperVCreateScript(rule)
            : BuildCreateScript(rule);
        await _pool.InvokeAsync(script);
    }

    public async Task UpdateRuleAsync(FirewallRuleInfo rule)
    {
        var parameters = new Dictionary<string, object> { ["Name"] = rule.Name };
        var setProps = new List<string>();

        if (!string.IsNullOrEmpty(rule.DisplayName)) setProps.Add($"-NewDisplayName '{rule.DisplayName}'");
        if (!string.IsNullOrEmpty(rule.Description)) setProps.Add($"-Description '{rule.Description}'");

        var script = $"Set-NetFirewallRule -Name $Name {string.Join(' ', setProps)}";
        await _pool.InvokeAsync(script, parameters);
    }

    public async Task DeleteRuleAsync(string ruleName)
    {
        var parameters = new Dictionary<string, object> { ["Name"] = ruleName };
        await _pool.InvokeAsync("Remove-NetFirewallRule -Name $Name", parameters);
    }

    public async Task SetRuleEnabledAsync(string ruleName, bool enabled)
    {
        var cmd = enabled ? "Enable-NetFirewallRule" : "Disable-NetFirewallRule";
        var parameters = new Dictionary<string, object> { ["Name"] = ruleName };
        await _pool.InvokeAsync($"{cmd} -Name $Name", parameters);
    }

    public async Task<IReadOnlyList<FirewallProfile>> GetActiveProfilesAsync()
    {
        var script = @"
            Get-NetFirewallProfile | Where-Object { $_.Enabled -eq 'True' } |
            Select-Object -ExpandProperty Name
        ";
        var results = await _pool.InvokeAsync<string>(script);
        return results.Select(ParseProfile).ToList().AsReadOnly();
    }

    public void Dispose()
    {
        _pool.Dispose();
        GC.SuppressFinalize(this);
    }

    private void CheckHyperVAvailability()
    {
        try
        {
            using var ps = System.Management.Automation.PowerShell.Create();
            ps.AddScript("Get-Command Get-NetFirewallHyperVRule -ErrorAction SilentlyContinue");
            var result = ps.Invoke();
            _hyperVAvailable = result.Count > 0;
        }
        catch { _hyperVAvailable = false; }
    }

    private static string BuildCreateScript(FirewallRuleInfo rule) =>
        $@"New-NetFirewallRule -DisplayName '{Escape(rule.DisplayName)}' " +
        $"-Direction {rule.Direction} -Action {rule.Action} " +
        $"-Protocol {rule.Protocol} " +
        (!string.IsNullOrEmpty(rule.LocalPort) ? $"-LocalPort {rule.LocalPort} " : "") +
        (!string.IsNullOrEmpty(rule.RemotePort) ? $"-RemotePort {rule.RemotePort} " : "") +
        (!string.IsNullOrEmpty(rule.LocalAddress) ? $"-LocalAddress {rule.LocalAddress} " : "") +
        (!string.IsNullOrEmpty(rule.RemoteAddress) ? $"-RemoteAddress {rule.RemoteAddress} " : "") +
        (!string.IsNullOrEmpty(rule.Program) ? $"-Program '{Escape(rule.Program)}' " : "") +
        $"-Profile {rule.Profile} -Enabled {(rule.Enabled ? "True" : "False")}";

    private static string BuildHyperVCreateScript(FirewallRuleInfo rule) =>
        $@"New-NetFirewallHyperVRule -DisplayName '{Escape(rule.DisplayName)}' " +
        $"-Direction {rule.Direction} -Action {rule.Action}";

    private static string Escape(string s) => s.Replace("'", "''");

    private static FirewallRuleInfo MapToRuleInfo(PSObject obj, FirewallStore store) => new()
    {
        Name = GetProp(obj, "Name"),
        DisplayName = GetProp(obj, "DisplayName"),
        Description = GetProp(obj, "Description"),
        Enabled = GetProp(obj, "Enabled").Equals("True", StringComparison.OrdinalIgnoreCase),
        Direction = GetProp(obj, "Direction").Contains("Inbound", StringComparison.OrdinalIgnoreCase)
            ? TrafficDirection.Inbound : TrafficDirection.Outbound,
        Action = GetProp(obj, "Action").Contains("Allow", StringComparison.OrdinalIgnoreCase)
            ? TrafficAction.Allow : TrafficAction.Block,
        Protocol = ParseProtocol(GetProp(obj, "Protocol")),
        LocalPort = GetProp(obj, "LocalPort"),
        RemotePort = GetProp(obj, "RemotePort"),
        LocalAddress = GetProp(obj, "LocalAddress"),
        RemoteAddress = GetProp(obj, "RemoteAddress"),
        Program = GetProp(obj, "Program"),
        Group = GetProp(obj, "Group"),
        Profile = ParseProfile(GetProp(obj, "Profile")),
        Store = store
    };

    private static string GetProp(PSObject obj, string name) =>
        obj.Properties[name]?.Value?.ToString() ?? string.Empty;

    private static TransportProtocol ParseProtocol(string p) => p.ToUpperInvariant() switch
    {
        "TCP" => TransportProtocol.TCP,
        "UDP" => TransportProtocol.UDP,
        "ICMPV4" or "ICMP" => TransportProtocol.ICMP,
        "ICMPV6" => TransportProtocol.ICMPv6,
        _ => TransportProtocol.Other
    };

    private static FirewallProfile ParseProfile(string p) => p.ToLowerInvariant() switch
    {
        "domain" => FirewallProfile.Domain,
        "private" => FirewallProfile.Private,
        "public" => FirewallProfile.Public,
        _ => FirewallProfile.Any
    };
}
```

**Step 3: Build and commit**

```bash
cd C:/Claude/winFW && dotnet build src/WinFWManager.Core
git add src/WinFWManager.Core/Services/PowerShellRunspacePool.cs src/WinFWManager.Core/Services/FirewallRuleService.cs
git commit -m "feat: implement FirewallRuleService with PowerShell runspace pool"
```

---

## Phase 9: EtwTrafficMonitor Implementation

### Task 13: Implement EtwTrafficMonitor

**Files:**
- Create: `src/WinFWManager.Core/Services/EtwTrafficMonitor.cs`
- Create: `src/WinFWManager.Core/Collections/RingBuffer.cs`
- Create: `tests/WinFWManager.Tests/Collections/RingBufferTests.cs`

**Step 1: Write RingBuffer tests**

```csharp
using FluentAssertions;
using WinFWManager.Core.Collections;

namespace WinFWManager.Tests.Collections;

public class RingBufferTests
{
    [Fact]
    public void Add_UnderCapacity_ContainsAllItems()
    {
        var buffer = new RingBuffer<int>(5);
        buffer.Add(1); buffer.Add(2); buffer.Add(3);
        buffer.ToList().Should().Equal(1, 2, 3);
        buffer.Count.Should().Be(3);
    }

    [Fact]
    public void Add_OverCapacity_DropsOldest()
    {
        var buffer = new RingBuffer<int>(3);
        buffer.Add(1); buffer.Add(2); buffer.Add(3); buffer.Add(4);
        buffer.ToList().Should().Equal(2, 3, 4);
        buffer.Count.Should().Be(3);
    }

    [Fact]
    public void Clear_EmptiesBuffer()
    {
        var buffer = new RingBuffer<int>(5);
        buffer.Add(1); buffer.Add(2);
        buffer.Clear();
        buffer.Count.Should().Be(0);
        buffer.ToList().Should().BeEmpty();
    }

    [Fact]
    public void ThreadSafety_ConcurrentAdds_NoExceptions()
    {
        var buffer = new RingBuffer<int>(100);
        var tasks = Enumerable.Range(0, 10).Select(i =>
            Task.Run(() =>
            {
                for (int j = 0; j < 50; j++)
                    buffer.Add(i * 50 + j);
            }));

        Task.WaitAll(tasks.ToArray());
        buffer.Count.Should().Be(100);
    }
}
```

**Step 2: Run tests — verify fail**

```bash
cd C:/Claude/winFW && dotnet test tests/WinFWManager.Tests --filter "RingBufferTests" -v n
```

**Step 3: Implement RingBuffer**

```csharp
using System.Collections;

namespace WinFWManager.Core.Collections;

public class RingBuffer<T> : IEnumerable<T>
{
    private readonly T[] _buffer;
    private readonly int _capacity;
    private int _head;
    private int _count;
    private readonly object _lock = new();

    public RingBuffer(int capacity)
    {
        _capacity = capacity;
        _buffer = new T[capacity];
        _head = 0;
        _count = 0;
    }

    public int Count { get { lock (_lock) return _count; } }
    public int Capacity => _capacity;

    public void Add(T item)
    {
        lock (_lock)
        {
            _buffer[_head] = item;
            _head = (_head + 1) % _capacity;
            if (_count < _capacity) _count++;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_buffer, 0, _capacity);
            _head = 0;
            _count = 0;
        }
    }

    public List<T> ToList()
    {
        lock (_lock)
        {
            var list = new List<T>(_count);
            if (_count == 0) return list;

            int start = _count < _capacity ? 0 : _head;
            for (int i = 0; i < _count; i++)
                list.Add(_buffer[(start + i) % _capacity]);
            return list;
        }
    }

    public IEnumerator<T> GetEnumerator() => ToList().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
```

**Step 4: Run tests — verify pass**

```bash
cd C:/Claude/winFW && dotnet test tests/WinFWManager.Tests --filter "RingBufferTests" -v n
```

Expected: All 4 tests PASS.

**Step 5: Commit RingBuffer**

```bash
git add src/WinFWManager.Core/Collections/RingBuffer.cs tests/WinFWManager.Tests/Collections/RingBufferTests.cs
git commit -m "feat: implement thread-safe RingBuffer collection"
```

**Step 6: Implement EtwTrafficMonitor**

```csharp
using System.Net;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

public class EtwTrafficMonitor : IEtwTrafficMonitor
{
    private TraceEventSession? _session;
    private Thread? _processingThread;
    private readonly Subject<TrafficEvent> _subject = new();
    private volatile bool _isRunning;
    private const string SessionName = "WinFWManagerETW";
    private static readonly Guid WfpProviderGuid = new("0c478c5b-0351-41b1-8c58-4a6737da32e3");

    public IObservable<TrafficEvent> TrafficEvents => _subject.AsObservable();
    public bool IsRunning => _isRunning;
    public bool RequiresAdmin => !IsAdmin();

    public void Start()
    {
        if (_isRunning) return;
        if (!IsAdmin())
            throw new UnauthorizedAccessException("ETW requires administrator privileges.");

        // Clean up any previous session
        try { TraceEventSession.GetActiveSession(SessionName)?.Dispose(); } catch { }

        _session = new TraceEventSession(SessionName);
        _session.EnableProvider(WfpProviderGuid);

        _isRunning = true;
        _processingThread = new Thread(ProcessEvents)
        {
            IsBackground = true,
            Name = "ETW-WFP-Processor"
        };
        _processingThread.Start();
    }

    public void Stop()
    {
        _isRunning = false;
        _session?.Stop();
        _session?.Dispose();
        _session = null;
    }

    public void Dispose()
    {
        Stop();
        _subject.Dispose();
        GC.SuppressFinalize(this);
    }

    private void ProcessEvents()
    {
        if (_session == null) return;

        _session.Source.Dynamic.All += (TraceEvent data) =>
        {
            try
            {
                var evt = MapTraceEvent(data);
                if (evt != null)
                    _subject.OnNext(evt);
            }
            catch { /* skip malformed events */ }
        };

        _session.Source.Process();
    }

    private static TrafficEvent? MapTraceEvent(TraceEvent data)
    {
        // WFP events vary by opcode; extract what we can
        try
        {
            return new TrafficEvent
            {
                Timestamp = data.TimeStamp,
                ProcessId = data.ProcessID,
                // Additional fields extracted based on event opcode
                // The exact payload depends on the WFP event type
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool IsAdmin()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}
```

Note: The ETW mapping logic is intentionally minimal here. The exact WFP event structure needs to be refined during development by inspecting actual ETW events on a live system. The `Microsoft-Windows-WFP` provider has dozens of event types — we'll add specific parsers for the most relevant ones (classify, permit, block) in a follow-up refinement task.

Add `System.Reactive` NuGet package to Core:

```bash
dotnet add src/WinFWManager.Core/WinFWManager.Core.csproj package System.Reactive
```

**Step 7: Build and commit**

```bash
cd C:/Claude/winFW && dotnet build src/WinFWManager.Core
git add src/WinFWManager.Core/Services/EtwTrafficMonitor.cs src/WinFWManager.Core/WinFWManager.Core.csproj
git commit -m "feat: implement EtwTrafficMonitor with WFP ETW provider"
```

---

## Phase 10: DI Registration & App Bootstrapping

### Task 14: Wire up DI and App.xaml.cs

**Files:**
- Modify: `src/WinFWManager/App.xaml.cs`
- Create: `src/WinFWManager/ServiceCollectionExtensions.cs`

**Step 1: Create service registration extension**

```csharp
using Microsoft.Extensions.DependencyInjection;
using WinFWManager.Core.Services;

namespace WinFWManager;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWinFWManagerServices(this IServiceCollection services)
    {
        services.AddSingleton<IProcessResolver>(new ProcessResolver());
        services.AddSingleton<IGeoIpResolver>(sp =>
        {
            var mmdbPath = Path.Combine(AppContext.BaseDirectory, "GeoLite2-City.mmdb");
            return new GeoIpResolver(File.Exists(mmdbPath) ? mmdbPath : null);
        });
        services.AddSingleton<INetworkInterfaceService, NetworkInterfaceService>();
        services.AddSingleton<IFirewallLogParser, FirewallLogParser>();
        services.AddSingleton<IFirewallRuleService, FirewallRuleService>();
        services.AddSingleton<IEtwTrafficMonitor, EtwTrafficMonitor>();
        return services;
    }
}
```

**Step 2: Update App.xaml.cs**

```csharp
using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace WinFWManager;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        services.AddWinFWManagerServices();
        // ViewModels registered here as we add them
        Services = services.BuildServiceProvider();

        var mainWindow = new MainWindow();
        mainWindow.Show();
    }
}
```

**Step 3: Build and commit**

```bash
cd C:/Claude/winFW && dotnet build src/WinFWManager
git add src/WinFWManager/App.xaml.cs src/WinFWManager/ServiceCollectionExtensions.cs
git commit -m "feat: wire up DI container and app bootstrapping"
```

---

## Phase 11: UI Shell — MainWindow with Tabs

### Task 15: Build MainWindow with tab layout and top bar

**Files:**
- Modify: `src/WinFWManager/MainWindow.xaml`
- Modify: `src/WinFWManager/MainWindow.xaml.cs`
- Create: `src/WinFWManager/ViewModels/MainViewModel.cs`
- Create: `src/WinFWManager/Themes/DarkTheme.xaml`

**Step 1: Create dark theme resource dictionary**

`src/WinFWManager/Themes/DarkTheme.xaml` — define a Palo Alto-inspired dark theme with colors for backgrounds, grids, status indicators:

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <!-- Background colors -->
    <Color x:Key="PrimaryBg">#1E1E2E</Color>
    <Color x:Key="SecondaryBg">#2D2D3F</Color>
    <Color x:Key="TertiaryBg">#3D3D5C</Color>

    <SolidColorBrush x:Key="PrimaryBgBrush" Color="{StaticResource PrimaryBg}"/>
    <SolidColorBrush x:Key="SecondaryBgBrush" Color="{StaticResource SecondaryBg}"/>
    <SolidColorBrush x:Key="TertiaryBgBrush" Color="{StaticResource TertiaryBg}"/>

    <!-- Text colors -->
    <SolidColorBrush x:Key="PrimaryTextBrush" Color="#E0E0E0"/>
    <SolidColorBrush x:Key="SecondaryTextBrush" Color="#A0A0B0"/>

    <!-- Accent colors -->
    <SolidColorBrush x:Key="AccentBrush" Color="#4A9EFF"/>
    <SolidColorBrush x:Key="SuccessBrush" Color="#4CAF50"/>
    <SolidColorBrush x:Key="DangerBrush" Color="#F44336"/>
    <SolidColorBrush x:Key="WarningBrush" Color="#FFC107"/>
    <SolidColorBrush x:Key="WslBrush" Color="#FFD700"/>
    <SolidColorBrush x:Key="HyperVBrush" Color="#42A5F5"/>

    <!-- Border -->
    <SolidColorBrush x:Key="BorderBrush" Color="#404060"/>
</ResourceDictionary>
```

**Step 2: Create MainViewModel**

```csharp
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
        }
    }
}
```

**Step 3: Build MainWindow.xaml**

```xml
<Window x:Class="WinFWManager.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="WinFW Manager" Height="800" Width="1400"
        WindowStartupLocation="CenterScreen"
        Background="{DynamicResource PrimaryBgBrush}">

    <Window.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="Themes/DarkTheme.xaml"/>
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Window.Resources>

    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="48"/>  <!-- Top bar -->
            <RowDefinition Height="*"/>   <!-- Main content -->
            <RowDefinition Height="28"/>  <!-- Status bar -->
        </Grid.RowDefinitions>

        <!-- Top Bar -->
        <Border Grid.Row="0" Background="{DynamicResource SecondaryBgBrush}"
                BorderBrush="{DynamicResource BorderBrush}" BorderThickness="0,0,0,1">
            <DockPanel Margin="12,0">
                <TextBlock Text="WinFW Manager" FontSize="18" FontWeight="Bold"
                           Foreground="{DynamicResource PrimaryTextBrush}"
                           VerticalAlignment="Center" DockPanel.Dock="Left"/>

                <!-- Admin indicator -->
                <Border DockPanel.Dock="Right" CornerRadius="4" Padding="8,4" Margin="8,0"
                        VerticalAlignment="Center"
                        Background="{Binding IsAdmin, Converter={x:Static BooleanToVisibilityConverter.Default}}">
                    <TextBlock Text="{Binding StatusText}"
                               Foreground="{DynamicResource SecondaryTextBrush}" FontSize="11"/>
                </Border>

                <!-- Start/Stop button -->
                <Button DockPanel.Dock="Right" Content="{Binding IsMonitoring,
                        Converter={x:Null}}"
                        Command="{Binding ToggleMonitoringCommand}"
                        Margin="8,0" Padding="16,6" VerticalAlignment="Center"/>

                <StackPanel/>
            </DockPanel>
        </Border>

        <!-- Main Content: Tab Control -->
        <TabControl Grid.Row="1" Background="Transparent"
                    SelectedIndex="{Binding SelectedTabIndex}"
                    Foreground="{DynamicResource PrimaryTextBrush}">
            <TabItem Header="Traffic Monitor">
                <TextBlock Text="Traffic Monitor — coming next" Foreground="{DynamicResource SecondaryTextBrush}"
                           HorizontalAlignment="Center" VerticalAlignment="Center"/>
            </TabItem>
            <TabItem Header="Log Viewer">
                <TextBlock Text="Log Viewer" Foreground="{DynamicResource SecondaryTextBrush}"
                           HorizontalAlignment="Center" VerticalAlignment="Center"/>
            </TabItem>
            <TabItem Header="Rules Manager">
                <TextBlock Text="Rules Manager" Foreground="{DynamicResource SecondaryTextBrush}"
                           HorizontalAlignment="Center" VerticalAlignment="Center"/>
            </TabItem>
            <TabItem Header="Network Interfaces">
                <TextBlock Text="Network Interfaces" Foreground="{DynamicResource SecondaryTextBrush}"
                           HorizontalAlignment="Center" VerticalAlignment="Center"/>
            </TabItem>
            <TabItem Header="Dashboard">
                <TextBlock Text="Dashboard" Foreground="{DynamicResource SecondaryTextBrush}"
                           HorizontalAlignment="Center" VerticalAlignment="Center"/>
            </TabItem>
        </TabControl>

        <!-- Status Bar -->
        <Border Grid.Row="2" Background="{DynamicResource SecondaryBgBrush}"
                BorderBrush="{DynamicResource BorderBrush}" BorderThickness="0,1,0,0">
            <TextBlock Text="{Binding StatusText}" Foreground="{DynamicResource SecondaryTextBrush}"
                       Margin="12,0" VerticalAlignment="Center" FontSize="11"/>
        </Border>
    </Grid>
</Window>
```

**Step 4: Update MainWindow.xaml.cs**

```csharp
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WinFWManager.ViewModels;
using WinFWManager.Core.Services;

namespace WinFWManager;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel(App.Services.GetRequiredService<IEtwTrafficMonitor>());
    }
}
```

**Step 5: Build and verify app launches**

```bash
cd C:/Claude/winFW && dotnet build src/WinFWManager
```

Expected: Build succeeded. App launches and shows dark-themed window with 5 tabs.

**Step 6: Commit**

```bash
git add src/WinFWManager/
git commit -m "feat: build MainWindow shell with dark theme and tab layout"
```

---

## Phase 12-16: UI Tab Implementations

**Note to implementer:** Phases 12-16 each follow the same pattern: create a ViewModel, create a UserControl (View), wire into MainWindow tab. These are detailed below as high-level tasks — expand each following the same TDD pattern used above.

### Task 16: Traffic Monitor tab (UserControl + ViewModel)

**Files:**
- Create: `src/WinFWManager/Views/TrafficMonitorView.xaml` + `.xaml.cs`
- Create: `src/WinFWManager/ViewModels/TrafficMonitorViewModel.cs`

**Key implementation details:**
- ViewModel subscribes to `IEtwTrafficMonitor.TrafficEvents`
- Uses `RingBuffer<TrafficEvent>` (capacity 50,000)
- Batches events via `Observable.Buffer(TimeSpan.FromMilliseconds(100))`
- Exposes `ObservableCollection<TrafficEvent>` for DataGrid binding (populated from batch)
- Filter properties: `FilterSourceIp`, `FilterDestIp`, `FilterProtocol`, `FilterNic`, `FilterProcess`, `FilterDirection`
- `ICollectionView` with composite filter predicate
- View is a DataGrid with columns from design doc + quick filter TextBoxes above each column
- Right-click ContextMenu with commands: CreateRuleFromTraffic, CopyRow, FilterToSource, FilterToDest
- Color-coded rows via DataTrigger: green=Allow, red=Block, yellow=WSL, blue=HyperV
- Auto-scroll toggle via `ScrollIntoView` on collection change

### Task 17: Log Viewer tab (UserControl + ViewModel)

**Files:**
- Create: `src/WinFWManager/Views/LogViewerView.xaml` + `.xaml.cs`
- Create: `src/WinFWManager/ViewModels/LogViewerViewModel.cs`

**Key implementation details:**
- ViewModel uses `IFirewallLogParser` to load log files
- File picker button triggers `OpenFileDialog`
- Same DataGrid layout as Traffic Monitor (reuse column definitions via shared style/template)
- Progress bar during file loading
- Date range filter (DatePicker start/end)
- Same right-click context menu as Traffic Monitor

### Task 18: Rules Manager tab (UserControl + ViewModel)

**Files:**
- Create: `src/WinFWManager/Views/RulesManagerView.xaml` + `.xaml.cs`
- Create: `src/WinFWManager/ViewModels/RulesManagerViewModel.cs`
- Create: `src/WinFWManager/Views/RuleEditorDialog.xaml` + `.xaml.cs`
- Create: `src/WinFWManager/ViewModels/RuleEditorViewModel.cs`

**Key implementation details:**
- ViewModel uses `IFirewallRuleService` to load rules from selected store
- Store selector ComboBox (ActiveStore, PersistentStore, ConfigurableServiceStore, GPO)
- Profile filter ComboBox (Domain, Private, Public, Any)
- Search TextBox filtering by rule name or program
- Toggle for Hyper-V rules (calls `GetHyperVRulesAsync`)
- Toolbar commands: NewRule, EditRule, DeleteRule, ToggleEnabled, Refresh
- RuleEditorDialog: modal dialog for creating/editing rules with all fields from `FirewallRuleInfo`
- Delete confirmation dialog

### Task 19: Network Interfaces tab (UserControl + ViewModel)

**Files:**
- Create: `src/WinFWManager/Views/NetworkInterfacesView.xaml` + `.xaml.cs`
- Create: `src/WinFWManager/ViewModels/NetworkInterfacesViewModel.cs`

**Key implementation details:**
- ViewModel uses `INetworkInterfaceService.GetAllAdaptersAsync()`
- ListView or DataGrid showing all adapters
- Columns: Name, Type (with color-coded badge), Status, IP Addresses, MAC, Profile, vSwitch
- Refresh button
- Double-click adapter: switches to Traffic Monitor tab and sets NIC filter

### Task 20: Dashboard tab (UserControl + ViewModel)

**Files:**
- Create: `src/WinFWManager/Views/DashboardView.xaml` + `.xaml.cs`
- Create: `src/WinFWManager/ViewModels/DashboardViewModel.cs`

**Key implementation details:**
- Summary cards: Total Connections, Blocked %, Active Rules count, Active Profile
- Top 5 talkers (source IPs by connection count)
- Top 5 blocked destinations
- Refresh periodically from Traffic Monitor data
- Lower priority — can be placeholder initially

---

## Phase 17: Integration & Polish

### Task 21: Create Rule from Traffic context menu

**Files:**
- Modify: `src/WinFWManager/ViewModels/TrafficMonitorViewModel.cs`
- Uses: `RuleEditorDialog`

**Key implementation details:**
- Right-click traffic event → "Create Rule" → opens RuleEditorDialog pre-populated with: source/dest IPs, ports, protocol, direction, NIC
- For WSL traffic: checkbox to create as Hyper-V rule instead
- After creation, refresh Rules Manager

### Task 22: Left sidebar filter tree

**Files:**
- Create: `src/WinFWManager/Views/FilterSidebarView.xaml` + `.xaml.cs`
- Create: `src/WinFWManager/ViewModels/FilterSidebarViewModel.cs`
- Modify: `src/WinFWManager/MainWindow.xaml`

**Key implementation details:**
- TreeView with sections: By NIC, By Profile, By Direction
- NIC section populated from `INetworkInterfaceService`
- Clicking a node sets the corresponding filter on the active Traffic Monitor or Log Viewer
- Collapsible sidebar with toggle button

### Task 23: Final build, smoke test, cleanup

**Steps:**
1. `dotnet build` — verify clean build with 0 warnings
2. `dotnet test` — verify all tests pass
3. Run app as admin — verify ETW monitoring starts
4. Run app without admin — verify graceful degradation
5. Test log file loading with a real `pfirewall.log`
6. Test rule listing from ActiveStore
7. Clean up `Class1.cs` and any template files
8. Final commit

```bash
cd C:/Claude/winFW
dotnet build
dotnet test
git add -A
git commit -m "feat: complete WinFWManager v0.1 — traffic monitor, log viewer, rules manager"
```
