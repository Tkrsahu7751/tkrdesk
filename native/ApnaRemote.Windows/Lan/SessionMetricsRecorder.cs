using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace ApnaRemote.Windows.Lan;

/// <summary>
/// Attended lab metrics: 1 Hz CSV samples while sharing or viewing.
/// Writes under LocalAppData and next to the EXE so Cursor can read paths later.
/// Never stores PIN, fingerprint, screenshots, or keystrokes.
/// </summary>
internal sealed class SessionMetricsRecorder : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly DispatcherTimer _timer;
    private readonly string _version;
    private readonly object _gate = new();
    private StreamWriter? _csv;
    private string? _csvPath;
    private string? _summaryPath;
    private string _role = "";
    private DateTimeOffset _startedUtc;
    private bool _everHadFps;
    private int _zeroFpsStreak;
    private int _staleFrameStreak;
    private long _prevFramesTotal = -1;
    private int _prevFps = -1;
    private int _samples;
    private int _hangSamples;
    private int _lagSamples;
    private long _sumFps;
    private long _sumKbps;
    private int _minFps = int.MaxValue;
    private int _maxFps;
    private int _minKbps = int.MaxValue;
    private int _maxKbps;
    private double _startWsMb = -1;
    private double _endWsMb;
    private double _maxWsMb;
    private long _lastTickMs = -1;
    private Func<SampleSnapshot>? _snapshot;
    private bool _disposed;

    public SessionMetricsRecorder(string versionLabel)
    {
        _version = versionLabel;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (_, _) => Tick();
    }

    public string MetricsFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ApnaRemote",
        "metrics");

    public string StatusText
    {
        get
        {
            lock (_gate)
            {
                if (_csv is null) return "Session metrics idle — start Share or Connect; samples write automatically.";
                return $"Logging {_role} · {_samples}s · {_csvPath}";
            }
        }
    }

    public string? LatestCsvPath
    {
        get { lock (_gate) return _csvPath; }
    }

    /// <summary>Start/stop based on live host share or viewer connected.</summary>
    public void Sync(bool hostSharing, bool viewerConnected, Func<SampleSnapshot> snapshot)
    {
        if (_disposed) return;
        lock (_gate)
        {
            _snapshot = snapshot;
            string want = hostSharing ? "host" : viewerConnected ? "viewer" : "";
            if (want.Length == 0)
            {
                StopLocked(writeSummary: true);
                return;
            }

            if (_csv is not null && _role == want)
                return; // snapshot already refreshed above
            if (_csv is not null) StopLocked(writeSummary: true);
            StartLocked(want);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) StopLocked(writeSummary: true);
        _timer.Stop();
    }

    private void StartLocked(string role)
    {
        Directory.CreateDirectory(MetricsFolder);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string machine = SanitizeFilePart(Environment.MachineName);
        string baseName = $"session-{role}-{machine}-{stamp}";
        _csvPath = Path.Combine(MetricsFolder, baseName + ".csv");
        _summaryPath = Path.Combine(MetricsFolder, baseName + ".summary.json");
        _role = role;
        _startedUtc = DateTimeOffset.UtcNow;
        _everHadFps = false;
        _zeroFpsStreak = 0;
        _staleFrameStreak = 0;
        _prevFramesTotal = -1;
        _prevFps = -1;
        _samples = 0;
        _hangSamples = 0;
        _lagSamples = 0;
        _sumFps = 0;
        _sumKbps = 0;
        _minFps = int.MaxValue;
        _maxFps = 0;
        _minKbps = int.MaxValue;
        _maxKbps = 0;
        _startWsMb = -1;
        _endWsMb = 0;
        _maxWsMb = 0;
        _lastTickMs = -1;

        _csv = new StreamWriter(new FileStream(_csvPath, FileMode.Create, FileAccess.Write, FileShare.Read), Encoding.UTF8)
        {
            AutoFlush = true
        };
        _csv.WriteLine("Utc,Role,Machine,Fps,Kbps,FramesTotal,FramesDropped,BytesTotal,WorkingSetMB,PrivateMB,DeltaFps,HangSuspect,LagSuspect,ZeroFpsStreak,SessionSeconds,TimerSkewMs");
        WritePointerLocked(active: true);
        TryMirrorPointer();
        _timer.Start();
    }

    private void StopLocked(bool writeSummary)
    {
        _timer.Stop();
        if (_csv is null) return;
        try { _csv.Flush(); _csv.Dispose(); }
        catch { /* ignore */ }
        _csv = null;
        if (writeSummary && _summaryPath is not null && _csvPath is not null)
            WriteSummaryLocked();
        WritePointerLocked(active: false);
        TryMirrorPointer();
        _role = "";
        _snapshot = null;
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (_csv is null || _snapshot is null) return;
            SampleSnapshot snap;
            try { snap = _snapshot(); }
            catch { return; }

            long nowMs = Environment.TickCount64;
            int timerSkewMs = 0;
            if (_lastTickMs >= 0)
                timerSkewMs = (int)Math.Clamp(nowMs - _lastTickMs - 1000, 0, 60_000);
            _lastTickMs = nowMs;
            if (timerSkewMs >= 1500) // UI/dispatcher stalled ~1.5s+ past the 1 Hz cadence
            {
                // Counted as lag-ish stall for summary totals below.
            }

            (double ws, double priv) = ReadMemoryMb();
            if (_startWsMb < 0) _startWsMb = ws;
            _endWsMb = ws;
            if (ws > _maxWsMb) _maxWsMb = ws;

            int fps = Math.Max(0, snap.Fps);
            int kbps = Math.Max(0, snap.Kbps);
            if (fps > 0) _everHadFps = true;

            // Frames stopped but UI still reports old FPS → treat as hang (stale meter).
            if (_prevFramesTotal >= 0 && snap.FramesTotal == _prevFramesTotal && _everHadFps)
            {
                _staleFrameStreak++;
                if (_staleFrameStreak >= 2 && fps > 0)
                {
                    fps = 0;
                    kbps = 0;
                }
            }
            else
            {
                _staleFrameStreak = 0;
            }

            _prevFramesTotal = snap.FramesTotal;

            if (fps == 0 && _everHadFps) _zeroFpsStreak++;
            else _zeroFpsStreak = 0;

            int hang = (_zeroFpsStreak >= 3 || _staleFrameStreak >= 2) ? 1 : 0;
            int deltaFps = _prevFps < 0 ? 0 : fps - _prevFps;
            int lag = 0;
            if (_prevFps >= 12 && fps > 0 && fps <= _prevFps / 2) lag = 1;
            if (_prevFps >= 15 && fps is > 0 and < 8) lag = 1;
            if (timerSkewMs >= 1500) lag = 1;
            _prevFps = fps;

            _samples++;
            _sumFps += fps;
            _sumKbps += kbps;
            if (fps < _minFps) _minFps = fps;
            if (fps > _maxFps) _maxFps = fps;
            if (kbps < _minKbps) _minKbps = kbps;
            if (kbps > _maxKbps) _maxKbps = kbps;
            if (hang == 1) _hangSamples++;
            if (lag == 1) _lagSamples++;

            int sessionSeconds = (int)Math.Clamp((DateTimeOffset.UtcNow - _startedUtc).TotalSeconds, 0, 86_400);
            string line = string.Format(CultureInfo.InvariantCulture,
                "{0},{1},{2},{3},{4},{5},{6},{7},{8:0.0},{9:0.0},{10},{11},{12},{13},{14},{15}",
                DateTimeOffset.UtcNow.ToString("o"),
                _role,
                Escape(Environment.MachineName),
                fps,
                kbps,
                snap.FramesTotal,
                snap.FramesDropped,
                snap.BytesTotal,
                ws,
                priv,
                deltaFps,
                hang,
                lag,
                _zeroFpsStreak,
                sessionSeconds,
                timerSkewMs);
            try { _csv.WriteLine(line); }
            catch { /* disk full / locked — keep trying next tick */ }

            if (_samples == 1 || _samples % 15 == 0)
                WritePointerLocked(active: true);
        }
    }

    private void WriteSummaryLocked()
    {
        if (_summaryPath is null || _csvPath is null) return;
        double avgFps = _samples > 0 ? _sumFps / (double)_samples : 0;
        double avgKbps = _samples > 0 ? _sumKbps / (double)_samples : 0;
        var summary = new Dictionary<string, object?>
        {
            ["role"] = _role,
            ["machine"] = Environment.MachineName,
            ["version"] = _version,
            ["startedUtc"] = _startedUtc.ToString("o"),
            ["endedUtc"] = DateTimeOffset.UtcNow.ToString("o"),
            ["durationSeconds"] = _samples,
            ["samples"] = _samples,
            ["fps"] = new Dictionary<string, double>
            {
                ["min"] = _minFps == int.MaxValue ? 0 : _minFps,
                ["max"] = _maxFps,
                ["avg"] = Math.Round(avgFps, 2)
            },
            ["kbps"] = new Dictionary<string, double>
            {
                ["min"] = _minKbps == int.MaxValue ? 0 : _minKbps,
                ["max"] = _maxKbps,
                ["avg"] = Math.Round(avgKbps, 2)
            },
            ["hangSuspectSamples"] = _hangSamples,
            ["lagSuspectSamples"] = _lagSamples,
            ["workingSetMB"] = new Dictionary<string, double>
            {
                ["start"] = Math.Round(_startWsMb < 0 ? 0 : _startWsMb, 1),
                ["end"] = Math.Round(_endWsMb, 1),
                ["max"] = Math.Round(_maxWsMb, 1)
            },
            ["csvPath"] = _csvPath,
            ["note"] = "HangSuspect = FPS at 0 for 3+ seconds after frames once flowed, or framesTotal stuck 2+ seconds while FPS still looked live. LagSuspect = sharp FPS drop. No secrets in this file."
        };
        try
        {
            File.WriteAllText(_summaryPath, JsonSerializer.Serialize(summary, JsonOptions));
            string latestSummary = Path.Combine(MetricsFolder, "latest.summary.json");
            File.Copy(_summaryPath, latestSummary, overwrite: true);
            File.Copy(_csvPath, Path.Combine(MetricsFolder, "latest.csv"), overwrite: true);
        }
        catch { /* ignore */ }
    }

    private void WritePointerLocked(bool active)
    {
        try
        {
            Directory.CreateDirectory(MetricsFolder);
            var pointer = new Dictionary<string, object?>
            {
                ["active"] = active,
                ["role"] = _role,
                ["machine"] = Environment.MachineName,
                ["version"] = _version,
                ["csvPath"] = _csvPath,
                ["summaryPath"] = _summaryPath,
                ["samples"] = _samples,
                ["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o"),
                ["folder"] = MetricsFolder
            };
            File.WriteAllText(
                Path.Combine(MetricsFolder, "latest.pointer.json"),
                JsonSerializer.Serialize(pointer, JsonOptions));
        }
        catch { /* ignore */ }
    }

    private void TryMirrorPointer()
    {
        try
        {
            string? exeDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (string.IsNullOrEmpty(exeDir)) return;
            string mirror = Path.Combine(exeDir, "metrics");
            Directory.CreateDirectory(mirror);
            string src = Path.Combine(MetricsFolder, "latest.pointer.json");
            if (File.Exists(src))
                File.Copy(src, Path.Combine(mirror, "latest.pointer.json"), overwrite: true);
            string latestCsv = Path.Combine(MetricsFolder, "latest.csv");
            if (File.Exists(latestCsv))
                File.Copy(latestCsv, Path.Combine(mirror, "latest.csv"), overwrite: true);
            string latestSummary = Path.Combine(MetricsFolder, "latest.summary.json");
            if (File.Exists(latestSummary))
                File.Copy(latestSummary, Path.Combine(mirror, "latest.summary.json"), overwrite: true);
        }
        catch { /* portable mirror is best-effort */ }
    }

    private static (double WorkingSetMb, double PrivateMb) ReadMemoryMb()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            p.Refresh();
            return (p.WorkingSet64 / (1024.0 * 1024.0), p.PrivateMemorySize64 / (1024.0 * 1024.0));
        }
        catch
        {
            return (0, 0);
        }
    }

    private static string SanitizeFilePart(string value)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');
        return string.IsNullOrWhiteSpace(value) ? "pc" : value.Trim();
    }

    private static string Escape(string value)
    {
        if (value.Contains(',') || value.Contains('"'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    internal readonly record struct SampleSnapshot(
        int Fps,
        int Kbps,
        long FramesTotal,
        long FramesDropped,
        long BytesTotal);
}
