using System.Net;

namespace WinFWManager.Core.Models;

/// <summary>
/// An IP network expressed as a base address plus prefix length, with
/// containment testing. Works for both IPv4 and IPv6. Used to attribute a
/// traffic endpoint to the adapter whose subnet it falls within — the primary
/// mechanism for identifying WSL/Hyper-V traffic, whose guest IPs live in the
/// virtual adapter's subnet rather than matching a host adapter address exactly.
/// </summary>
public readonly struct IpSubnet
{
    /// <summary>The masked network address (host bits cleared).</summary>
    public IPAddress Network { get; }

    /// <summary>The prefix length in bits (e.g. 24 for a /24).</summary>
    public int PrefixLength { get; }

    public IpSubnet(IPAddress address, int prefixLength)
    {
        PrefixLength = prefixLength;
        Network = Mask(address, prefixLength);
    }

    /// <summary>True if <paramref name="address"/> falls within this subnet.</summary>
    public bool Contains(IPAddress address)
    {
        if (address.AddressFamily != Network.AddressFamily)
            return false;
        return Mask(address, PrefixLength).Equals(Network);
    }

    private static IPAddress Mask(IPAddress address, int prefixLength)
    {
        byte[] bytes = address.GetAddressBytes();
        int totalBits = bytes.Length * 8;
        prefixLength = Math.Clamp(prefixLength, 0, totalBits);

        for (int i = 0; i < bytes.Length; i++)
        {
            int bitsForThisByte = prefixLength - i * 8;
            byte mask = bitsForThisByte >= 8 ? (byte)0xFF
                      : bitsForThisByte <= 0 ? (byte)0x00
                      : (byte)(0xFF << (8 - bitsForThisByte));
            bytes[i] &= mask;
        }

        return new IPAddress(bytes);
    }
}
