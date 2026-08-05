using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

/// <summary>
/// Correlates network-layer packet drops (IfIndex, no ports) with
/// transport-layer drops (ports, no IfIndex) on the (source, destination)
/// address pair within a time window, producing one merged TrafficEvent.
/// Unmatched halves are flushed as standalone drop events after the window.
/// </summary>
public sealed class DropCorrelator
{
    private readonly Func<DateTime> _now;
    private readonly TimeSpan _window;
    private readonly Dictionary<string, (DropObservation Obs, DateTime ArrivedAt)> _pending = new();
    private readonly object _lock = new();

    public DropCorrelator(Func<DateTime>? clock = null, TimeSpan? window = null)
    {
        _now = clock ?? (() => DateTime.UtcNow);
        _window = window ?? TimeSpan.FromSeconds(2);
    }

    public int PendingCount { get { lock (_lock) return _pending.Count; } }

    /// <summary>Adds an observation; returns a merged TrafficEvent when its
    /// sibling (other layer, same address pair) is already pending.
    /// Arrival is stamped with the correlator's own clock; expiry never keys
    /// on <see cref="DropObservation.Timestamp"/> (which may be on a different
    /// clock basis, e.g. ETW local time) — that stays the display timestamp.</summary>
    public TrafficEvent? Add(DropObservation obs)
    {
        string key = $"{obs.Source}|{obs.Destination}";
        lock (_lock)
        {
            if (_pending.TryGetValue(key, out var entry))
            {
                var other = entry.Obs;
                if (other.HasPorts != obs.HasPorts)
                {
                    _pending.Remove(key);
                    return Merge(obs.HasPorts ? other : obs, obs.HasPorts ? obs : other);
                }
                // Same layer repeated (e.g. SYN retries): keep the newest.
                _pending[key] = (obs, _now());
                return null;
            }
            _pending[key] = (obs, _now());
            return null;
        }
    }

    /// <summary>Emits pending halves older than the window as standalone drops.
    /// Call periodically (the monitor calls this on a timer).</summary>
    public List<TrafficEvent> FlushExpired()
    {
        var cutoff = _now() - _window;
        var flushed = new List<TrafficEvent>();
        lock (_lock)
        {
            var expired = _pending.Where(kv => kv.Value.ArrivedAt <= cutoff).ToList();
            foreach (var kv in expired)
            {
                _pending.Remove(kv.Key);
                flushed.Add(ToEvent(kv.Value.Obs));
            }
        }
        return flushed;
    }

    /// <summary>Discards all pending halves. Call when a capture session ends
    /// so stale observations don't leak into the next session's flushes.</summary>
    public void Clear()
    {
        lock (_lock) _pending.Clear();
    }

    private static TrafficEvent Merge(DropObservation network, DropObservation transport)
    {
        var evt = ToEvent(transport);
        evt.InterfaceIndexHint = network.IfIndex;
        // A firewall verdict is the most actionable label — prefer it over the
        // transport half's (often secondary) reason, e.g. "Endpoint not found".
        if (network.Reason == DropReasonMapper.FirewallLabel)
            evt.DropReason = network.Reason;
        return evt;
    }

    private static TrafficEvent ToEvent(DropObservation o) => new()
    {
        Timestamp = o.Timestamp,
        Direction = o.Direction,
        Protocol = o.Protocol,
        Action = TrafficAction.Drop,
        SourceAddress = o.Source,
        DestinationAddress = o.Destination,
        SourcePort = o.RemotePort,
        DestinationPort = o.LocalPort,
        InterfaceIndexHint = o.IfIndex,
        DropReason = o.Reason
    };
}
