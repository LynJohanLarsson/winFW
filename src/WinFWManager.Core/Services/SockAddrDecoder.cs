using System.Net;

namespace WinFWManager.Core.Services;

/// <summary>
/// Decodes SOCKADDR byte payloads from Microsoft-Windows-TCPIP ETW events.
/// Layout (verified empirically): family = b[0]|b[1]&lt;&lt;8 (2=AF_INET, 23=AF_INET6),
/// port = big-endian at b[2..3], IPv4 addr at b[4..7], IPv6 addr at b[8..23].
/// </summary>
public static class SockAddrDecoder
{
    public static (IPAddress? Ip, int Port) Decode(byte[] bytes)
    {
        if (bytes.Length == 4)
            return (new IPAddress(bytes), 0);
        if (bytes.Length < 8)
            return (null, 0);

        int family = bytes[0] | (bytes[1] << 8);
        int port = (bytes[2] << 8) | bytes[3];

        if (family == 2)
            return (new IPAddress(bytes[4..8]), port);

        if (family == 23 && bytes.Length >= 24)
        {
            var ip = new IPAddress(bytes[8..24]);
            if (ip.IsIPv4MappedToIPv6)
                ip = ip.MapToIPv4();
            return (ip, port);
        }

        return (null, 0);
    }
}
