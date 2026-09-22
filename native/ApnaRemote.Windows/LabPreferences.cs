using System.IO;
using System.Text.Json;

namespace ApnaRemote.Windows;

/// <summary>
/// Local-only lab preferences + bounded session history.
/// Never stores PIN, fingerprint, screens, or keystrokes.
/// </summary>
internal sealed class LabPreferences
{
    private const int MaxRecentHosts = 8;
    private const int MaxHistory = 40;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public bool DarkTheme { get; set; }
    /// <summary>Light, Dark, or SciFi. Empty means migrate from <see cref="DarkTheme"/>.</summary>
    public string ColorMode { get; set; } = "";
    public string ThemePack { get; set; } = nameof(UiThemePack.SciFi);
    public string QualityPreset { get; set; } = nameof(Lan.LabQualityPreset.Balanced);
    /// <summary>JPEG is the supported production LAN codec. H264 is retained only for protocol migration.</summary>
    public string StreamCodec { get; set; } = nameof(Lan.LabStreamCodec.Jpeg);
    public List<string> RecentHosts { get; set; } = [];
    public List<LabHistoryEntry> History { get; set; } = [];

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ApnaRemote",
        "lab-preferences.json");

    public static LabPreferences Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new LabPreferences { ColorMode = nameof(UiColorMode.SciFi) };
            string json = File.ReadAllText(path);
            LabPreferences? loaded = JsonSerializer.Deserialize<LabPreferences>(json, JsonOptions);
            if (loaded is null) return new LabPreferences();
            loaded.Sanitize();
            return loaded;
        }
        catch
        {
            return new LabPreferences();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Sanitize();
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    public void RememberHost(string host)
    {
        host = NormalizeHost(host);
        if (host.Length == 0) return;
        RecentHosts.RemoveAll(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));
        RecentHosts.Insert(0, host);
        if (RecentHosts.Count > MaxRecentHosts) RecentHosts.RemoveRange(MaxRecentHosts, RecentHosts.Count - MaxRecentHosts);
    }

    public void ClearRecentHosts() => RecentHosts.Clear();

    public void AddHistory(string role, string peer, string result, int durationSec, long frames)
    {
        History.Insert(0, new LabHistoryEntry
        {
            Role = role is "Host" or "Viewer" ? role : "Lab",
            Peer = SanitizePeer(peer),
            Result = SanitizeResult(result),
            EndedUtc = DateTimeOffset.UtcNow.ToString("u"),
            DurationSec = Math.Clamp(durationSec, 0, 86_400),
            Frames = Math.Max(0, frames),
        });
        if (History.Count > MaxHistory) History.RemoveRange(MaxHistory, History.Count - MaxHistory);
    }

    public void ClearHistory() => History.Clear();

    public void Sanitize()
    {
        ThemePack = NormalizeThemePack(ThemePack);
        ColorMode = NormalizeColorMode(ColorMode, DarkTheme);
        DarkTheme = !string.Equals(ColorMode, nameof(UiColorMode.Light), StringComparison.OrdinalIgnoreCase)
            && !string.Equals(ColorMode, nameof(UiColorMode.SciFi), StringComparison.OrdinalIgnoreCase);
        QualityPreset = Enum.TryParse(QualityPreset, true, out Lan.LabQualityPreset _)
            ? QualityPreset
            : nameof(Lan.LabQualityPreset.Balanced);
        StreamCodec = Enum.TryParse(StreamCodec, true, out Lan.LabStreamCodec _)
            ? StreamCodec
            : nameof(Lan.LabStreamCodec.Jpeg);
        RecentHosts = RecentHosts
            .Select(NormalizeHost)
            .Where(h => h.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxRecentHosts)
            .ToList();
        History = History
            .Where(h => h is not null)
            .Select(h => new LabHistoryEntry
            {
                Role = h.Role is "Host" or "Viewer" ? h.Role : "Lab",
                Peer = SanitizePeer(h.Peer),
                Result = SanitizeResult(h.Result),
                EndedUtc = string.IsNullOrWhiteSpace(h.EndedUtc) ? DateTimeOffset.UtcNow.ToString("u") : h.EndedUtc.Trim(),
                DurationSec = Math.Clamp(h.DurationSec, 0, 86_400),
                Frames = Math.Max(0, h.Frames),
            })
            .Take(MaxHistory)
            .ToList();
    }

    private static string NormalizeHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        string host = value.Trim();
        if (host.Contains(' ') || host.Length > 253) return "";
        if (host.All(char.IsDigit) && host.Length is >= 4 and <= 8) return "";
        if (host.Count(c => c == '-') == 3 && host.Length is >= 15 and <= 19) return "";
        return host;
    }

    private static string SanitizePeer(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "(unknown)";
        string peer = value.Trim();
        if (peer.Length > 80) peer = peer[..80];
        if (peer.All(char.IsDigit) && peer.Length is >= 4 and <= 8) return "(redacted)";
        if (peer.Count(c => c == '-') == 3 && peer.Length is >= 15 and <= 19) return "(redacted)";
        return peer;
    }

    private static string SanitizeResult(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Ended";
        string result = value.Trim();
        if (result.Length > 120) result = result[..120];
        // Strip accidental secrets if status text echoed them.
        if (result.All(char.IsDigit) && result.Length is >= 4 and <= 8) return "Ended";
        return result;
    }

    private static string NormalizeThemePack(string? value)
    {
        // Unified glass UI: Classic/Studio prefs migrate to SciFi pack id (layout is always glass).
        if (string.Equals(value, "Studio", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "Classic", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "SciFi", StringComparison.OrdinalIgnoreCase))
            return nameof(UiThemePack.SciFi);
        return nameof(UiThemePack.SciFi);
    }

    private static string NormalizeColorMode(string? value, bool darkThemeFallback)
    {
        if (!string.IsNullOrWhiteSpace(value) && Enum.TryParse(value, true, out UiColorMode mode))
            return mode.ToString();
        return darkThemeFallback ? nameof(UiColorMode.Dark) : nameof(UiColorMode.Light);
    }
}

public sealed class LabHistoryEntry
{
    public string Role { get; set; } = "Lab";
    public string Peer { get; set; } = "(unknown)";
    public string Result { get; set; } = "Ended";
    public string EndedUtc { get; set; } = "";
    public int DurationSec { get; set; }
    public long Frames { get; set; }

    public string Summary =>
        $"{EndedUtc} · {Role} · {Peer} · {DurationSec}s · {Frames} frames · {Result}";
}
