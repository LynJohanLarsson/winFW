using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

/// <summary>
/// Real-time traffic capture via the Microsoft-Windows-TCPIP manifest provider.
///
/// Connection-lifecycle and UDP-message events produce allowed-traffic events
/// (with PID); network+transport packet-drop events are merged by
/// <see cref="DropCorrelator"/> into drop events carrying IfIndex and a
/// readable reason. Interface attribution happens downstream: IfIndex (exact)
/// when present, IP/subnet matching (derived) otherwise — see
/// NetworkInterfaceService.
///
/// WSL note: host&lt;-&gt;guest traffic IS visible here, including firewall
/// drops (verified empirically: TcpipNetworkPacketDrops carries the WSL
/// adapter's IfIndex). WSL2 guest→internet traffic in NAT mode is
/// NAT-forwarded by WinNAT, never becomes a host socket, and is not
/// observable by any host-level ETW provider; capturing it would require
/// adapter-level capture (pktmon/NDIS) — out of scope.
/// </summary>
public class EtwTrafficMonitor : IEtwTrafficMonitor
{
    private TraceEventSession? _session;
    private Thread? _processingThread;
    private Timer? _flushTimer;
    private readonly Subject<TrafficEvent> _subject = new();
    private readonly DropCorrelator _dropCorrelator = new();
    private volatile bool _isRunning;
    private const string SessionName = "WinFWManagerETW";
    private static readonly Guid TcpIpProviderGuid = new("2f07e2ee-15db-40f1-90ef-9d7ba282188a");

    public IObservable<TrafficEvent> TrafficEvents => _subject.AsObservable();
    public bool IsRunning => _isRunning;
    public bool RequiresAdmin => !IsAdmin();

    public void Start()
    {
        if (_isRunning) return;
        if (!IsAdmin())
            throw new UnauthorizedAccessException("ETW requires administrator privileges.");

        try { TraceEventSession.GetActiveSession(SessionName)?.Dispose(); } catch { }

        _session = new TraceEventSession(SessionName);
        _session.EnableProvider(TcpIpProviderGuid, TraceEventLevel.Verbose, ulong.MaxValue);

        _session.Source.Dynamic.All += OnEvent;

        _isRunning = true;
        // Capture the session locally: the thread must pump THIS session even if
        // a fast Stop()/Start() swaps the field before or while it runs. When the
        // pump exits (normal return, teardown, or fault), the flag must drop so
        // IsRunning never reports a dead session as active.
        var session = _session;
        _processingThread = new Thread(() =>
        {
            try { session.Source.Process(); }
            catch { }
            finally { _isRunning = false; }
        })
        {
            IsBackground = true,
            Name = "ETW-TCPIP-Processor"
        };
        _processingThread.Start();

        // Periodically flush uncorrelated drop halves as standalone events.
        _flushTimer = new Timer(_ =>
        {
            try
            {
                foreach (var evt in _dropCorrelator.FlushExpired())
                    _subject.OnNext(evt);
            }
            catch { }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void OnEvent(TraceEvent data)
    {
        // Hard filter on event name BEFORE touching payloads — the provider
        // emits hundreds of event types per second.
        string name = data.EventName;
        bool isFlow = TcpIpEventParser.FlowEventNames.Contains(name);
        bool isDrop = !isFlow && TcpIpEventParser.DropEventNames.Contains(name);
        if (!isFlow && !isDrop) return;

        try
        {
            var fields = ExtractFields(data);
            if (isFlow)
            {
                var evt = TcpIpEventParser.Parse(name, fields, data.TimeStamp);
                if (evt != null) _subject.OnNext(evt);
                return;
            }

            var drop = TcpIpEventParser.TryParseDrop(name, fields, data.TimeStamp);
            if (drop != null)
            {
                var merged = _dropCorrelator.Add(drop);
                if (merged != null) _subject.OnNext(merged);
            }
        }
        catch { /* skip malformed events */ }
    }

    private static Dictionary<string, object?> ExtractFields(TraceEvent data)
    {
        var fields = new Dictionary<string, object?>();
        var names = data.PayloadNames;
        for (int i = 0; i < names.Length; i++)
        {
            try { fields[names[i]] = data.PayloadValue(i); } catch { }
        }
        return fields;
    }

    public void Stop()
    {
        _isRunning = false;
        _flushTimer?.Dispose();
        _flushTimer = null;
        _dropCorrelator.Clear();
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
