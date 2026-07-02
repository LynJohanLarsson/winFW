using System.Net;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

/// <summary>
/// Real-time traffic capture via the kernel NetworkTCPIP ETW provider.
///
/// Interface attribution is done downstream by IP (see
/// <see cref="NetworkInterfaceService.ResolveAdapter"/>) because these events
/// carry no interface identifier.
///
/// WSL/Hyper-V note: host&lt;-&gt;VM traffic is captured here (it is a real host
/// socket) and tagged to the virtual adapter by subnet. WSL2 guest→internet
/// traffic in NAT mode is NAT-forwarded by WinNAT and never becomes a host
/// socket, so it is not observable via any host-level ETW provider — confirmed
/// against Microsoft-Windows-TCPIP and Microsoft-Windows-WFP (the latter yields
/// no usable per-packet 5-tuple/LUID). Capturing pre-NAT guest packets would
/// require adapter-level capture (pktmon/NDIS) on vEthernet (WSL), which is out
/// of scope for this ETW-based monitor.
/// </summary>
public class EtwTrafficMonitor : IEtwTrafficMonitor
{
    private TraceEventSession? _session;
    private Thread? _processingThread;
    private readonly Subject<TrafficEvent> _subject = new();
    private volatile bool _isRunning;
    private const string SessionName = "WinFWManagerETW";

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
        _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

        var kernel = _session.Source.Kernel;

        // TCP IPv4
        kernel.TcpIpSend += d => Emit(d.saddr, d.daddr, d.sport, d.dport, d.ProcessID, d.TimeStamp, TransportProtocol.TCP, TrafficDirection.Outbound);
        kernel.TcpIpRecv += d => Emit(d.saddr, d.daddr, d.sport, d.dport, d.ProcessID, d.TimeStamp, TransportProtocol.TCP, TrafficDirection.Inbound);
        kernel.TcpIpConnect += d => Emit(d.saddr, d.daddr, d.sport, d.dport, d.ProcessID, d.TimeStamp, TransportProtocol.TCP, TrafficDirection.Outbound);
        kernel.TcpIpAccept += d => Emit(d.saddr, d.daddr, d.sport, d.dport, d.ProcessID, d.TimeStamp, TransportProtocol.TCP, TrafficDirection.Inbound);

        // TCP IPv6
        kernel.TcpIpSendIPV6 += d => Emit(d.saddr, d.daddr, d.sport, d.dport, d.ProcessID, d.TimeStamp, TransportProtocol.TCP, TrafficDirection.Outbound);
        kernel.TcpIpRecvIPV6 += d => Emit(d.saddr, d.daddr, d.sport, d.dport, d.ProcessID, d.TimeStamp, TransportProtocol.TCP, TrafficDirection.Inbound);
        kernel.TcpIpConnectIPV6 += d => Emit(d.saddr, d.daddr, d.sport, d.dport, d.ProcessID, d.TimeStamp, TransportProtocol.TCP, TrafficDirection.Outbound);
        kernel.TcpIpAcceptIPV6 += d => Emit(d.saddr, d.daddr, d.sport, d.dport, d.ProcessID, d.TimeStamp, TransportProtocol.TCP, TrafficDirection.Inbound);

        // UDP IPv4
        kernel.UdpIpSend += d => Emit(d.saddr, d.daddr, d.sport, d.dport, d.ProcessID, d.TimeStamp, TransportProtocol.UDP, TrafficDirection.Outbound);
        kernel.UdpIpRecv += d => Emit(d.saddr, d.daddr, d.sport, d.dport, d.ProcessID, d.TimeStamp, TransportProtocol.UDP, TrafficDirection.Inbound);

        // UDP IPv6
        kernel.UdpIpSendIPV6 += d => Emit(d.saddr, d.daddr, d.sport, d.dport, d.ProcessID, d.TimeStamp, TransportProtocol.UDP, TrafficDirection.Outbound);
        kernel.UdpIpRecvIPV6 += d => Emit(d.saddr, d.daddr, d.sport, d.dport, d.ProcessID, d.TimeStamp, TransportProtocol.UDP, TrafficDirection.Inbound);

        _isRunning = true;
        _processingThread = new Thread(() =>
        {
            try { _session.Source.Process(); } catch { }
        })
        {
            IsBackground = true,
            Name = "ETW-Network-Processor"
        };
        _processingThread.Start();
    }

    private void Emit(IPAddress src, IPAddress dst, int srcPort, int dstPort,
                      int pid, DateTime timestamp, TransportProtocol protocol, TrafficDirection direction)
    {
        try
        {
            _subject.OnNext(new TrafficEvent
            {
                Timestamp = timestamp,
                ProcessId = pid,
                SourceAddress = src,
                DestinationAddress = dst,
                SourcePort = srcPort,
                DestinationPort = dstPort,
                Protocol = protocol,
                Direction = direction,
                Action = TrafficAction.Allow,
            });
        }
        catch { }
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

    private static bool IsAdmin()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}
