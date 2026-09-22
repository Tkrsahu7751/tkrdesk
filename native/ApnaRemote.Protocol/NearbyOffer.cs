using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ApnaRemote.Protocol;

/// <summary>
/// Compact pairing offer for BLE / QR / Wi‑Fi find.
/// Discovery ads must not carry PIN or fingerprint (same-LAN MITM candy).
/// QR / typed entry still carry secrets; TLS + host Accept remain mandatory.
/// </summary>
public readonly record struct NearbyOffer(string HostName, string IPv4, int Port, string Pin, string Fingerprint)
{
    public const ushort ManufacturerCompanyId = 0xFFFF;
    /// <summary>Legacy radio format identifier. Legacy secrets are ignored when decoding.</summary>
    public const byte FormatVersion = 1;
    /// <summary>Discovery-safe BLE: same layout, PIN+FP bytes zeroed — secrets not trusted from air.</summary>
    public const byte DiscoveryFormatVersion = 3;
    private static readonly byte[] Magic = [(byte)'A', (byte)'R'];

    public bool HasSecrets =>
        Pin.Length == 6 && Pin.All(char.IsAsciiDigit) &&
        LanTls.NormalizeFingerprint(Fingerprint).Length == 16;

    public static bool TryParseFingerprintBytes(string fingerprint, out byte[] bytes)
    {
        bytes = new byte[8];
        string hex = fingerprint.Replace("-", "", StringComparison.Ordinal).Trim();
        if (hex.Length != 16) return false;
        for (int i = 0; i < 8; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                return false;
        }

        return true;
    }

    public static string FormatFingerprint(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 8) return "";
        return string.Create(19, bytes.ToArray(), static (span, b) =>
        {
            WriteHex(span, 0, b[0]); WriteHex(span, 2, b[1]); span[4] = '-';
            WriteHex(span, 5, b[2]); WriteHex(span, 7, b[3]); span[9] = '-';
            WriteHex(span, 10, b[4]); WriteHex(span, 12, b[5]); span[14] = '-';
            WriteHex(span, 15, b[6]); WriteHex(span, 17, b[7]);
        });

        static void WriteHex(Span<char> dest, int offset, byte value)
        {
            dest[offset] = ToHex((value >> 4) & 0xF);
            dest[offset + 1] = ToHex(value & 0xF);
        }

        static char ToHex(int n) => (char)(n < 10 ? '0' + n : 'A' + (n - 10));
    }

    /// <summary>BLE advertisement without secrets (v3). Prefer this for Find nearby.</summary>
    public byte[] ToDiscoveryAdvertisementPayload(bool includeHostName = false)
    {
        if (!IPAddress.TryParse(IPv4, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("IPv4 required for nearby offer.");
        if (Port is < 1 or > 65535)
            throw new ArgumentException("Port out of range.");

        byte[] addr = ip.GetAddressBytes();
        string shortName = includeHostName ? TruncateAscii(HostName, 8) : "";
        bool withName = shortName.Length > 0;
        var payload = new byte[23 + (withName ? 1 + shortName.Length : 0)];
        payload[0] = Magic[0];
        payload[1] = Magic[1];
        payload[2] = DiscoveryFormatVersion;
        Buffer.BlockCopy(addr, 0, payload, 3, 4);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(7, 2), (ushort)Port);
        // Bytes 9..22 intentionally zero — no PIN / fingerprint on the air.
        if (withName)
        {
            payload[23] = (byte)shortName.Length;
            Encoding.ASCII.GetBytes(shortName, payload.AsSpan(24, shortName.Length));
        }

        return payload;
    }

    /// <summary>Compatibility alias for radio discovery. Pairing secrets belong only in the explicit QR URI.</summary>
    public byte[] ToAdvertisementPayload(bool includeHostName = false)
        => ToDiscoveryAdvertisementPayload(includeHostName);

    public static bool TryFromAdvertisementPayload(ReadOnlySpan<byte> payload, string localName, out NearbyOffer offer)
    {
        offer = default;
        if (payload.Length < 23) return false;
        if (payload[0] != Magic[0] || payload[1] != Magic[1]) return false;
        byte ver = payload[2];
        if (ver is not (1 or 2 or 3)) return false;
        string ip = $"{payload[3]}.{payload[4]}.{payload[5]}.{payload[6]}";
        ushort port = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(7, 2));
        if (port == 0 || !IPAddress.TryParse(ip, out IPAddress? parsedIp) || parsedIp.AddressFamily != AddressFamily.InterNetwork)
            return false;
        // Radio discovery is an untrusted hint. Ignore PIN/fingerprint bytes from every version.
        string name = string.IsNullOrWhiteSpace(localName) ? ip : localName.Trim();
        if ((ver == 2 || ver == 3) && payload.Length >= 24)
        {
            int n = payload[23];
            if (n is > 0 and <= 16 && payload.Length >= 24 + n)
            {
                string embedded = Encoding.ASCII.GetString(payload.Slice(24, n)).Trim();
                if (embedded.Length > 0) name = embedded;
            }
        }

        offer = new NearbyOffer(name, ip, port, "", "");
        return true;
    }

    private static string TruncateAscii(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var sb = new StringBuilder(max);
        foreach (char c in value.Trim())
        {
            if (c is >= (char)0x20 and <= (char)0x7E && c is not ('|' or ','))
                sb.Append(c);
            if (sb.Length >= max) break;
        }

        return sb.ToString();
    }

    /// <summary>URI for QR / deep link. Example: apnaremote://v1?ip=…&amp;port=5720&amp;pin=…&amp;fp=…</summary>
    public string ToConnectUri()
    {
        string fp = LanTls.NormalizeFingerprint(Fingerprint);
        if (fp.Length != 16) throw new ArgumentException("Fingerprint format invalid.");
        return string.Create(CultureInfo.InvariantCulture,
            $"apnaremote://v1?ip={Uri.EscapeDataString(IPv4)}&port={Port}&pin={Uri.EscapeDataString(Pin)}&fp={fp}&name={Uri.EscapeDataString(HostName ?? "")}");
    }

    public static bool TryParseConnectUri(string? text, out NearbyOffer offer)
    {
        offer = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)) return false;
        if (!string.Equals(uri.Scheme, "apnaremote", StringComparison.OrdinalIgnoreCase)) return false;

        var query = ParseQuery(uri.Query);
        if (!query.TryGetValue("ip", out string? ip) || string.IsNullOrWhiteSpace(ip)) return false;
        if (!IPAddress.TryParse(ip, out var addr) || addr.AddressFamily != AddressFamily.InterNetwork) return false;
        int port = ArlProtocol.DefaultPort;
        if (query.TryGetValue("port", out string? portText) &&
            (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
            return false;
        if (!query.TryGetValue("pin", out string? pin) || pin.Length != 6 || !pin.All(char.IsAsciiDigit)) return false;
        if (!query.TryGetValue("fp", out string? fpRaw)) return false;
        string fpNorm = LanTls.NormalizeFingerprint(fpRaw);
        if (fpNorm.Length != 16) return false;
        string fpDisplay = FormatFingerprint(Convert.FromHexString(fpNorm));
        query.TryGetValue("name", out string? name);
        offer = new NearbyOffer(string.IsNullOrWhiteSpace(name) ? ip : name.Trim(), ip, port, pin, fpDisplay);
        return true;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return map;
        if (query[0] == '?') query = query[1..];
        foreach (string part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string key = Uri.UnescapeDataString(part[..eq]);
            string value = Uri.UnescapeDataString(part[(eq + 1)..]);
            map[key] = value;
        }

        return map;
    }
}
