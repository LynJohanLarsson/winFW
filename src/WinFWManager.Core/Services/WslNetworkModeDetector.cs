using System.Diagnostics;
using System.Net;
using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

/// <summary>
/// Detects WSL2's networking mode from %USERPROFILE%\.wslconfig.
/// NAT is the default when no config exists. Bridged is implied by a
/// vmSwitch entry. Guest IP is fetched opportunistically via `wsl hostname -I`
/// (cached) for Bridged-mode traffic tagging; failure degrades silently.
/// </summary>
public class WslNetworkModeDetector
{
    private readonly object _guestIpLock = new();
    private (IPAddress? Ip, DateTime FetchedAt)? _guestIpCache;
    private static readonly TimeSpan GuestIpTtl = TimeSpan.FromSeconds(60);

    public WslNetworkingMode DetectMode()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wslconfig");
        string? content = null;
        try { if (File.Exists(path)) content = File.ReadAllText(path); } catch { }
        return ParseConfig(content);
    }

    public static WslNetworkingMode ParseConfig(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return WslNetworkingMode.Nat;

        bool inWsl2 = false;
        string? mode = null;
        bool hasVmSwitch = false;

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inWsl2 = line.Equals("[wsl2]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inWsl2 || line.StartsWith('#')) continue;

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            if (key.Equals("networkingMode", StringComparison.OrdinalIgnoreCase))
                mode = value;
            else if (key.Equals("vmSwitch", StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrEmpty(value))
                hasVmSwitch = true;
        }

        if (mode?.Equals("mirrored", StringComparison.OrdinalIgnoreCase) == true)
            return WslNetworkingMode.Mirrored;
        if (mode?.Equals("bridged", StringComparison.OrdinalIgnoreCase) == true || hasVmSwitch)
            return WslNetworkingMode.Bridged;
        return WslNetworkingMode.Nat;
    }

    /// <summary>Fetches the WSL guest IP via `wsl hostname -I`.
    /// Returns null on any failure. Both success and failure are cached for
    /// 60s, so a dead/absent wsl.exe costs at most one spawn per minute.</summary>
    public IPAddress? GetGuestIp()
    {
        lock (_guestIpLock)
        {
            if (_guestIpCache is { } cached && DateTime.UtcNow - cached.FetchedAt < GuestIpTtl)
                return cached.Ip;
        }

        IPAddress? ip = FetchGuestIp();

        lock (_guestIpLock)
        {
            _guestIpCache = (ip, DateTime.UtcNow);
        }
        return ip;
    }

    private static IPAddress? FetchGuestIp()
    {
        try
        {
            var psi = new ProcessStartInfo("wsl.exe", "hostname -I")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            // ReadToEndAsync raced against WaitForExit: a plain ReadToEnd()
            // blocks until the child closes stdout, defeating the timeout.
            var readTask = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return null; }
            string output = readTask.GetAwaiter().GetResult();
            var first = output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (first != null && IPAddress.TryParse(first, out var ip))
                return ip;
        }
        catch { }
        return null;
    }
}
