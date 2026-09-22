using System.Diagnostics;

namespace ApnaRemote.Windows.Capture;

/// <summary>
/// Headless local H.264 smoke check — synthetic BGRA frames, no picker, no network.
/// Run: ApnaRemote.exe --encode-lab-check
/// </summary>
internal static class LocalH264EncodeLabCheck
{
    public static int Run()
    {
        // Media Foundation sink writer is more reliable on MTA than the WPF STA thread.
        int exitCode = 1;
        var thread = new Thread(() => exitCode = RunOnMta())
        {
            IsBackground = false,
            Name = "ApnaRemote-EncodeLabCheck",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        return exitCode;
    }

    private static int RunOnMta()
    {
        const int width = 1280;
        const int height = 720;
        const int frameCount = 90;

        Console.WriteLine("Apna Remote encode lab check");
        Console.WriteLine("Synthetic BGRA -> Media Foundation H.264 (local only, no network, no screen picker).");

        try
        {
            using var session = new LocalH264EncodeSession(width, height, keepFile: false);
            byte[] pixels = new byte[width * height * 4];
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < frameCount; i++)
            {
                FillFrame(pixels, width, height, i);
                if (!session.TryEncodeBgraFrame(pixels, width, height))
                {
                    Console.WriteLine("FAIL frame " + i + ": " + (session.LastError ?? "unknown encode error"));
                    return 2;
                }
            }

            EncodeMetrics metrics = session.Complete();
            sw.Stop();

            Console.WriteLine(
                $"RESULT frames={metrics.FramesEncoded} encodeFps~{metrics.EncodeFramesPerSecond} " +
                $"bytes={metrics.BytesProcessed} kbps~{metrics.BitrateKbps:0} " +
                $"workingSetMb={metrics.WorkingSetMb:0.0} cpu%~{metrics.CpuPercentApprox:0.0} " +
                $"elapsedSec={metrics.ElapsedSeconds:0.00} wallMs={sw.ElapsedMilliseconds}");

            if (!string.IsNullOrEmpty(metrics.Error))
            {
                Console.WriteLine("FAIL encode error: " + metrics.Error);
                return 3;
            }

            if (metrics.FramesEncoded < frameCount)
            {
                Console.WriteLine($"FAIL expected at least {frameCount} encoded frames.");
                return 4;
            }

            if (metrics.BytesProcessed <= 0)
            {
                Console.WriteLine("FAIL encoder produced zero bytes (file/stats empty).");
                return 5;
            }

            Console.WriteLine("PASS local H.264 encode lab check.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL " + ex.GetType().Name + ": " + ex.Message);
            if (ex.InnerException is not null)
            {
                Console.WriteLine("INNER " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message);
            }

            return 1;
        }
    }

    private static void FillFrame(byte[] pixels, int width, int height, int frameIndex)
    {
        byte b = (byte)(frameIndex * 3);
        byte g = (byte)(40 + (frameIndex * 5));
        byte r = (byte)(80 + (frameIndex * 7));
        for (int y = 0; y < height; y++)
        {
            int row = y * width * 4;
            byte rowBias = (byte)((y + frameIndex) & 0xFF);
            for (int x = 0; x < width; x++)
            {
                int i = row + (x * 4);
                pixels[i] = (byte)(b + rowBias);
                pixels[i + 1] = (byte)(g + x);
                pixels[i + 2] = r;
                pixels[i + 3] = 255;
            }
        }
    }
}
