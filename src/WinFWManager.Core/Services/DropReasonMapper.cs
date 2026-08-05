namespace WinFWManager.Core.Services;

/// <summary>
/// Maps TCPIP packet-drop Reason codes to readable text.
///
/// Sources:
/// - Network layer: IP_DISCARD_REASON (fwpsk.h), sequential per the Windows 11
///   24H2 DDI documentation (learn.microsoft.com, ne-fwpsk-ip_discard_reason).
///   NOTE: the docs show no explicit values and the enum HAS changed between
///   Windows versions — mappings below match this project's verified target
///   (Windows 11 24H2+). Code 256 does not fit the documented sequence; it is
///   kept from live verification: it appears exactly when the Hyper-V/WSL
///   firewall blocks inbound traffic.
/// - Transport layer: INET_DISCARD_REASON (fwpsk.h), sequential per
///   learn.microsoft.com "Transport layer discard reasons" (stable since Vista).
///
/// Unknown codes fall back to a numeric label tagged with the layer so new
/// codes can be identified from live captures and added here.
/// </summary>
public static class DropReasonMapper
{
    public static string Network(int reason) => reason switch
    {
        0 => "Bad source address",
        1 => "Not locally destined",
        2 => "Protocol unreachable",
        3 => "Port unreachable",
        4 => "Bad length",
        5 => "Malformed header",
        6 => "No route",
        7 => "Beyond scope",
        8 => "Inspection drop (WFP)",
        9 => "Too many decapsulations",
        10 => "Administratively prohibited",
        11 => "Bad checksum",
        12 => "First fragment incomplete",
        13 => "Header not contiguous",
        14 => "Header not aligned",
        16 => "Hop limit exceeded (TTL)",
        17 => "Address unreachable",
        18 => "RSC packet",
        19 => "Source violation",
        22 => "Inspection absorb (WFP)",
        23 => "Don't-fragment, MTU exceeded",
        24 => "Buffer length exceeded",
        25 => "Address resolution timeout",
        26 => "Address resolution failure",
        27 => "IPsec failure",
        28 => "Extension headers failure",
        29 => "Allocation failure",
        256 => "Firewall (WFP filter)",   // verified live: Hyper-V/WSL firewall block
        _ => $"Network drop (reason {reason})"
    };

    public static string Transport(int reason) => reason switch
    {
        0 => "Source unspecified",
        1 => "Destination multicast",
        2 => "Invalid transport header",
        3 => "Invalid checksum",
        4 => "Endpoint not found (no listener)",
        5 => "Remote address mismatch",
        6 => "Session state drop",
        7 => "Receive inspection failure (WFP)",
        8 => "Invalid ACK segment",
        9 => "Expected SYN",
        10 => "Invalid RST segment",
        11 => "SYN received in SYN_RCVD",
        12 => "Simultaneous connect",
        13 => "TCP PAWS check failed",
        14 => "Land attack detected",
        15 => "Missed reset",
        16 => "Outside receive window",
        17 => "Duplicate segment",
        18 => "Receive window closed",
        19 => "Connection closed",
        20 => "Connection closing (FIN_WAIT_2)",
        21 => "Reassembly conflict",
        22 => "FIN already received",
        23 => "Invalid flags to listener",
        24 => "Urgent delivery allocation failure",
        25 => "Connection closed (urgent delivery)",
        26 => "RST outside window (TIME_WAIT)",
        27 => "SYN with incompatible flags (TIME_WAIT)",
        28 => "Invalid segment (TIME_WAIT)",
        _ => $"Transport drop (reason {reason})"
    };

    /// <summary>The label that identifies a firewall (WFP) block; the drop
    /// correlator prefers this over other labels when merging layers.</summary>
    public const string FirewallLabel = "Firewall (WFP filter)";
}
