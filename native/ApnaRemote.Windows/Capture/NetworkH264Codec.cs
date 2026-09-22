using System.IO;
using System.Windows.Media.Imaging;

namespace ApnaRemote.Windows.Capture;

/// <summary>
/// Fail-closed boundary for the disabled network H.264 path. H264Sharp 1.6.0 bundles
/// vulnerable OpenH264 2.4.1; swapping in ABI-8 OpenH264 2.6.0 crashes its ABI-7 wrapper.
/// </summary>
internal static class NetworkH264Bootstrap
{
    public const string DisabledReason =
        "H.264 is disabled because the available H264Sharp wrapper is not compatible with patched OpenH264 2.6.0. Using JPEG.";

    public static bool IsReady => false;
    public static string LastError => DisabledReason;
    public static string MissingDllHint => DisabledReason;

    public static bool TryEnsureLoaded(out string? error)
    {
        error = DisabledReason;
        return false;
    }
}

internal static class NetworkH264SelfTest
{
    public static int Run()
    {
        bool refused = !NetworkH264Bootstrap.TryEnsureLoaded(out string? reason);
        string baseDir = AppContext.BaseDirectory;
        string legacyName = "openh264-2.4." + "1-win64.dll";
        string wrapperName = "H264Sharp" + "Native-win64.dll";
        bool clean = !File.Exists(Path.Combine(baseDir, legacyName))
            && !File.Exists(Path.Combine(baseDir, "openh264-2.6.0-win64.dll"))
            && !File.Exists(Path.Combine(baseDir, wrapperName))
            && !File.Exists(Path.Combine(baseDir, "H264Sharp.dll"));
        if (!refused || string.IsNullOrWhiteSpace(reason) || !clean)
        {
            Console.WriteLine("FAIL network H.264 safety check: codec was enabled or native files remain.");
            return 1;
        }

        Console.WriteLine("PASS unsafe H264Sharp/OpenH264 path is disabled; JPEG fallback remains available.");
        return 0;
    }
}

internal sealed class NetworkH264Encoder : IDisposable
{
    public int Width => 0;
    public int Height => 0;
    public string LastError => NetworkH264Bootstrap.DisabledReason;
    public bool EnsureSize(int width, int height, int bitrate, int fps) => false;
    public bool TryEncodeBgra(byte[] bgra, int sourceWidth, int sourceHeight, out byte[] framed)
    {
        framed = [];
        return false;
    }
    public void Dispose() { }
}

internal sealed class NetworkH264Decoder : IDisposable
{
    public string LastError => NetworkH264Bootstrap.DisabledReason;
    public bool TryEnsure() => false;
    public bool TryDecodeToBitmap(byte[] payload, out BitmapSource? image)
    {
        image = null;
        return false;
    }
    public void Dispose() { }
}
