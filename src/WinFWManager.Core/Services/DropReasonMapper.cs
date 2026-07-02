namespace WinFWManager.Core.Services;

/// <summary>
/// Maps TCPIP packet-drop Reason codes to readable text. Only empirically
/// verified codes are named (observed on live captures); everything else
/// falls back to a numeric label. Extend as new codes are confirmed.
/// </summary>
public static class DropReasonMapper
{
    public static string Network(int reason) => reason switch
    {
        256 => "Firewall (WFP filter)",   // verified: WSL->host SYN dropped by Hyper-V firewall
        _ => $"Drop (reason {reason})"
    };

    public static string Transport(int reason) => reason switch
    {
        4 => "Firewall (WFP filter)",     // verified: same drop at transport layer
        _ => $"Drop (reason {reason})"
    };
}
