using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ApnaRemote.Windows.Services;

/// <summary>
/// Automatic update check service for TKR Desk.
/// Verifies new releases on GitHub CDN / tkrdesk.com and notifies users with 1-click update.
/// </summary>
public sealed class UpdateCheckerService : IDisposable
{
    public const string CurrentVersion = "1.0";
    private readonly HttpClient _http;
    private bool _disposed;

    public bool IsUpdateAvailable { get; private set; }
    public string LatestVersion { get; private set; } = "1.0";
    public string DownloadUrl { get; private set; } = "https://tkrdesk.com";
    public string UpdateBannerText => $"Update Available: TKR Desk v{LatestVersion}! Click to upgrade.";

    public event Action? UpdateFound;

    public UpdateCheckerService()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TKRDesk-Windows/" + CurrentVersion);
        _http.Timeout = TimeSpan.FromSeconds(5);
    }

    public async Task CheckForUpdatesAsync()
    {
        if (_disposed) return;
        try
        {
            // Lightweight GitHub release check
            string json = await _http.GetStringAsync("https://api.github.com/repos/Tkrsahu7751/tkrdesk/releases/latest");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("tag_name", out var tagProp))
            {
                string tag = tagProp.GetString() ?? "";
                string version = tag.TrimStart('v', 'V');
                if (Version.TryParse(version, out var parsedRemote) &&
                    Version.TryParse(CurrentVersion, out var parsedCurrent))
                {
                    if (parsedRemote > parsedCurrent)
                    {
                        IsUpdateAvailable = true;
                        LatestVersion = version;
                        if (doc.RootElement.TryGetProperty("html_url", out var urlProp))
                        {
                            DownloadUrl = urlProp.GetString() ?? "https://tkrdesk.com";
                        }
                        UpdateFound?.Invoke();
                    }
                }
            }
        }
        catch
        {
            // Offline or rate-limited; gracefully silent
        }
    }

    public void OpenDownloadPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = DownloadUrl,
                UseShellExecute = true
            });
        }
        catch { }
    }

    public void Dispose()
    {
        _disposed = true;
        _http.Dispose();
    }
}
