using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

namespace ApnaRemote.Windows.Lan;

/// <summary>
/// Generates and resolves clean 9-digit TKR IDs (e.g. "842 195 037") for AnyDesk/RustDesk style
/// effortless pairing without ever requiring users to type raw IP addresses.
/// </summary>
public static class TkrIdService
{
    private static string? _cachedLocalId;
    private static readonly ConcurrentDictionary<string, DiscoveredPeerInfo> _idMap = new(StringComparer.OrdinalIgnoreCase);

    public sealed record DiscoveredPeerInfo(string TkrId, string HostName, string IPv4, int Port, DateTime LastSeen);

    /// <summary>
    /// Computes a stable, deterministic 9-digit TKR ID for this PC based on machine name and primary MAC.
    /// Format: "XXX XXX XXX" (e.g. "842 195 037").
    /// </summary>
    public static string GetLocalTkrId()
    {
        if (!string.IsNullOrEmpty(_cachedLocalId)) return _cachedLocalId;

        try
        {
            string seed = Environment.MachineName.Trim().ToUpperInvariant() + "_" + Environment.UserName.Trim();
            var mac = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => n.GetPhysicalAddress().ToString())
                .FirstOrDefault(p => !string.IsNullOrEmpty(p));

            if (!string.IsNullOrEmpty(mac)) seed += "_" + mac;

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
            uint val = BitConverter.ToUInt32(hash, 0);
            uint number = 100_000_000 + (val % 900_000_000);
            string raw = number.ToString("D9");
            _cachedLocalId = $"{raw[..3]} {raw.Substring(3, 3)} {raw[6..]}";
        }
        catch
        {
            _cachedLocalId = "842 195 037";
        }

        return _cachedLocalId;
    }

    /// <summary>
    /// Removes spaces, dashes, dots, and non-digit characters.
    /// </summary>
    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        return new string(input.Where(char.IsDigit).ToArray());
    }

    /// <summary>
    /// Returns true if the input represents a valid 9-digit TKR ID.
    /// </summary>
    public static bool IsTkrId(string? input)
    {
        string norm = Normalize(input);
        return norm.Length == 9;
    }

    /// <summary>
    /// Formats 9 raw digits into "XXX XXX XXX".
    /// </summary>
    public static string FormatTkrId(string? rawDigits)
    {
        string norm = Normalize(rawDigits);
        if (norm.Length != 9) return rawDigits ?? "";
        return $"{norm[..3]} {norm.Substring(3, 3)} {norm[6..]}";
    }

    /// <summary>
    /// Stores or updates a discovered host's TKR ID mapping.
    /// </summary>
    public static void RegisterPeer(string tkrId, string hostName, string ipv4, int port)
    {
        string norm = Normalize(tkrId);
        if (norm.Length != 9) return;
        _idMap[norm] = new DiscoveredPeerInfo(FormatTkrId(norm), hostName, ipv4, port, DateTime.UtcNow);
    }

    /// <summary>
    /// Tries to resolve a 9-digit TKR ID into an active host endpoint.
    /// </summary>
    public static bool TryResolve(string input, out string ip, out int port, out string hostName)
    {
        ip = "";
        port = 5720;
        hostName = "";

        string norm = Normalize(input);
        if (norm.Length != 9) return false;

        if (_idMap.TryGetValue(norm, out var info))
        {
            // Valid for up to 10 minutes from last beacon
            if ((DateTime.UtcNow - info.LastSeen).TotalMinutes <= 10)
            {
                ip = info.IPv4;
                port = info.Port;
                hostName = info.HostName;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves a 9-digit TKR ID to a DiscoveredPeerInfo if present on the LAN.
    /// </summary>
    public static DiscoveredPeerInfo? ResolveTkrId(string input)
    {
        if (TryResolve(input, out string ip, out int port, out string hostName))
        {
            return new DiscoveredPeerInfo(FormatTkrId(input), hostName, ip, port, DateTime.UtcNow);
        }
        return null;
    }

    /// <summary>
    /// Returns all recently seen active workstations for Lab Grid monitoring.
    /// </summary>
    public static IReadOnlyCollection<DiscoveredPeerInfo> GetAllDiscoveredPeers()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-5);
        return _idMap.Values.Where(p => p.LastSeen >= cutoff).ToList();
    }
}
