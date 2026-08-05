using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

/// <summary>
/// Shared filter for <see cref="TrafficEvent"/>s, used by the Traffic Monitor
/// and the Dashboard. Each field holds a comma-separated list of terms;
/// prefix a term with ! to exclude. Text fields use case-insensitive
/// substring matching; port fields use exact integer matching.
/// </summary>
public class TrafficEventFilter
{
    public string SourceIp { get; set; } = "";
    public string SrcPort { get; set; } = "";
    public string DestIp { get; set; } = "";
    public string DstPort { get; set; } = "";
    public string Protocol { get; set; } = "";
    public string Process { get; set; } = "";
    public string Nic { get; set; } = "";
    public string Action { get; set; } = "";

    /// <summary>True when every filter field is blank (matches everything).</summary>
    public bool IsEmpty =>
        string.IsNullOrEmpty(SourceIp)
        && string.IsNullOrEmpty(SrcPort)
        && string.IsNullOrEmpty(DestIp)
        && string.IsNullOrEmpty(DstPort)
        && string.IsNullOrEmpty(Protocol)
        && string.IsNullOrEmpty(Process)
        && string.IsNullOrEmpty(Nic)
        && string.IsNullOrEmpty(Action);

    /// <summary>
    /// Appends "!value" to an existing filter string, keeping the
    /// comma-separated convention. Returns <paramref name="current"/> unchanged
    /// when the negation is already present, so repeated exclusions of the same
    /// value do not pile up.
    /// </summary>
    public static string AppendNegation(string current, string value)
    {
        var negTerm = $"!{value}";
        if (string.IsNullOrEmpty(current))
            return negTerm;
        if (current.Contains(negTerm, StringComparison.OrdinalIgnoreCase))
            return current;
        return $"{current},{negTerm}";
    }

    /// <summary>Resets all filter fields to blank.</summary>
    public void Clear()
    {
        SourceIp = "";
        SrcPort = "";
        DestIp = "";
        DstPort = "";
        Protocol = "";
        Process = "";
        Nic = "";
        Action = "";
    }

    /// <summary>
    /// Returns true when the event passes every filter field (AND logic
    /// across fields).
    /// </summary>
    public bool Matches(TrafficEvent evt)
    {
        if (!MatchesFilter(SourceIp, evt.SourceAddress?.ToString()))
            return false;
        if (!MatchesPortFilter(SrcPort, evt.SourcePort))
            return false;
        if (!MatchesFilter(DestIp, evt.DestinationAddress?.ToString()))
            return false;
        if (!MatchesPortFilter(DstPort, evt.DestinationPort))
            return false;
        if (!MatchesFilter(Protocol, evt.Protocol.ToString()))
            return false;
        if (!MatchesFilter(Process, evt.ProcessName))
            return false;
        if (!MatchesFilter(Nic, evt.InterfaceName))
            return false;
        if (!MatchesFilter(Action, evt.Action.ToString()))
            return false;

        return true;
    }

    /// <summary>
    /// Supports multiple comma-separated terms. Prefix a term with ! to exclude.
    /// e.g. "!192.168.1.1,!10.0.0.1" excludes both IPs.
    /// e.g. "chrome,firefox" includes either.
    /// Mixed: "!svchost" excludes svchost.
    /// </summary>
    private static bool MatchesFilter(string filter, string? fieldValue)
    {
        if (string.IsNullOrEmpty(filter)) return true;

        var terms = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return true;

        var negTerms = new List<string>();
        var posTerms = new List<string>();
        foreach (var term in terms)
        {
            if (term.StartsWith('!') && term.Length > 1)
                negTerms.Add(term[1..]);
            else
                posTerms.Add(term);
        }

        // Negative filters: if field matches ANY negation, exclude
        foreach (var neg in negTerms)
        {
            if (fieldValue?.Contains(neg, StringComparison.OrdinalIgnoreCase) == true)
                return false;
        }

        // Positive filters: field must match at least one (OR logic)
        if (posTerms.Count > 0)
        {
            bool anyMatch = false;
            foreach (var pos in posTerms)
            {
                if (fieldValue?.Contains(pos, StringComparison.OrdinalIgnoreCase) == true)
                {
                    anyMatch = true;
                    break;
                }
            }
            if (!anyMatch) return false;
        }

        return true;
    }

    /// <summary>
    /// Port filter with exact-match terms (comma-separated, ! to exclude).
    /// e.g. "443,80" matches either; "!53" excludes DNS. Exact match avoids
    /// "443" spuriously matching 4431 as a substring would.
    /// </summary>
    private static bool MatchesPortFilter(string filter, int port)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;

        var terms = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return true;

        var posTerms = new List<int>();
        foreach (var term in terms)
        {
            if (term.StartsWith('!') && term.Length > 1)
            {
                if (int.TryParse(term[1..], out var neg) && neg == port)
                    return false;
            }
            else if (int.TryParse(term, out var pos))
            {
                posTerms.Add(pos);
            }
        }

        if (posTerms.Count > 0 && !posTerms.Contains(port))
            return false;

        return true;
    }
}
