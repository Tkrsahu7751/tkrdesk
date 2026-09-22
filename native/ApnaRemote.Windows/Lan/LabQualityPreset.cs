namespace ApnaRemote.Windows.Lan;

/// <summary>Share quality presets — delays/bitrate tuned for LAN feel (not full ABR yet).</summary>
internal enum LabQualityPreset
{
    Performance,
    Balanced,
    Quality,
}

internal readonly record struct LabQualityProfile(
    double JpegQuality,
    int MaxWidth,
    int FrameDelayMs,
    int H264Bitrate,
    int H264Fps);

internal static class LabQualityPresetExtensions
{
    public static LabQualityProfile Profile(this LabQualityPreset preset) => preset switch
    {
        // Faster cadence, lighter pixels — for weak Wi‑Fi / busy host.
        LabQualityPreset.Performance => new(0.55, 1280, 50, 2_000_000, 18),
        // Sharper / higher bitrate — strong LAN.
        LabQualityPreset.Quality => new(0.85, 1920, 33, 8_000_000, 30),
        // Default balanced.
        _ => new(0.72, 1600, 40, 4_000_000, 24),
    };

    /// <summary>If recent send FPS is weak, temporarily drop max width one step (metrics-driven nudge).</summary>
    public static LabQualityProfile WithFpsNudge(this LabQualityProfile profile, int recentSendFps)
    {
        if (recentSendFps <= 0 || recentSendFps >= 10)
            return profile;

        int nudgedWidth = recentSendFps < 6
            ? Math.Min(profile.MaxWidth, 1280)
            : Math.Min(profile.MaxWidth, 1600);
        if (nudgedWidth >= profile.MaxWidth)
            return profile;

        int bitrate = Math.Max(1_000_000, profile.H264Bitrate * nudgedWidth / Math.Max(1, profile.MaxWidth));
        return profile with { MaxWidth = nudgedWidth, H264Bitrate = bitrate };
    }
}
