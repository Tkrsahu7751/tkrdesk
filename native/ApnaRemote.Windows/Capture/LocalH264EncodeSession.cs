using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using static ApnaRemote.Windows.Capture.MediaFoundationInterop;

namespace ApnaRemote.Windows.Capture;

/// <summary>
/// Local-only H.264 encode via Media Foundation Sink Writer (raw COM vtable calls).
/// Converts BGRA frames to NV12, encodes to a temp MP4 for metrics, then deletes it by default.
/// </summary>
internal sealed class LocalH264EncodeSession : IDisposable
{
    private const int TargetFps = 30;
    private const int TargetBitrate = 4_000_000;
    private const long SampleDuration = 10_000_000L / TargetFps;

    private readonly object _gate = new();
    private readonly string _outputPath;
    private readonly bool _keepFile;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly TimeSpan _cpuStart;
    private readonly long _wallStartTick;
    private readonly int _width;
    private readonly int _height;

    private IntPtr _writer;
    private IntPtr _attributes;
    private int _streamIndex;
    private long _sampleTime;
    private long _framesEncoded;
    private long _bytesProcessed;
    private int _encodeFpsCounter;
    private int _displayedEncodeFps;
    private long _lastFpsTick;
    private bool _started;
    private bool _disposed;
    private string? _lastError;

    public bool IsActive => _started && !_disposed;
    public int Width => _width;
    public int Height => _height;
    public long FramesEncoded => Interlocked.Read(ref _framesEncoded);
    public int EncodeFramesPerSecond => _displayedEncodeFps;
    public long BytesProcessed => Interlocked.Read(ref _bytesProcessed);
    public string? OutputPath => _keepFile ? _outputPath : null;
    public string? LastError => _lastError;

    public LocalH264EncodeSession(int width, int height, bool keepFile = false)
    {
        if (width < 2 || height < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Encode size must be at least 2x2.");
        }

        _width = width & ~1;
        _height = height & ~1;
        _keepFile = keepFile;
        _cpuStart = _process.TotalProcessorTime;
        _wallStartTick = Environment.TickCount64;
        _lastFpsTick = _wallStartTick;

        string directory = Path.Combine(Path.GetTempPath(), "ApnaRemote", "capture-lab");
        Directory.CreateDirectory(directory);
        _outputPath = Path.Combine(directory, "local-encode-" + Guid.NewGuid().ToString("N") + ".mp4");

        ThrowIfFailed(MFStartup(MF_VERSION, MFSTARTUP_FULL), "MFStartup");
        try
        {
            ThrowIfFailed(MFCreateAttributes(out _attributes, 2), "MFCreateAttributes");
            // Software encoder path is more accepting for lab NV12/RGB input types.
            AttributesSetUint32(_attributes, MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 0);
            AttributesSetUint32(_attributes, MF_SINK_WRITER_DISABLE_THROTTLING, 1);

            _writer = CreateSinkWriter(_outputPath, _attributes);
            ConfigureTypes();
            SinkBeginWriting(_writer);
            _started = true;
        }
        catch
        {
            CleanupNative();
            MFShutdown();
            TryDeleteOutput();
            throw;
        }
    }

    public bool TryEncodeBgraFrame(byte[] bgraPixels, int sourceWidth, int sourceHeight)
    {
        if (!_started || _disposed || _writer == IntPtr.Zero)
        {
            return false;
        }

        int width = sourceWidth & ~1;
        int height = sourceHeight & ~1;
        if (width != _width || height != _height)
        {
            return false;
        }

        int required = checked((_width * _height * 3) / 2);
        byte[] nv12 = new byte[required];
        BgraToNv12(bgraPixels, sourceWidth, sourceHeight, _width, _height, nv12);

        lock (_gate)
        {
            if (!_started || _disposed || _writer == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                ThrowIfFailed(MFCreateMemoryBuffer(required, out IntPtr buffer), "MFCreateMemoryBuffer");
                try
                {
                    BufferLock(buffer, out IntPtr scan0, out _, out _);
                    try
                    {
                        Marshal.Copy(nv12, 0, scan0, required);
                    }
                    finally
                    {
                        BufferUnlock(buffer);
                    }

                    BufferSetCurrentLength(buffer, required);
                    ThrowIfFailed(MFCreateSample(out IntPtr sample), "MFCreateSample");
                    try
                    {
                        SampleAddBuffer(sample, buffer);
                        SampleSetSampleTime(sample, _sampleTime);
                        SampleSetSampleDuration(sample, SampleDuration);
                        SinkWriteSample(_writer, _streamIndex, sample);
                        _sampleTime += SampleDuration;
                        Interlocked.Increment(ref _framesEncoded);
                        _encodeFpsCounter++;

                        long now = Environment.TickCount64;
                        if (now - _lastFpsTick >= 1000)
                        {
                            _displayedEncodeFps = _encodeFpsCounter;
                            _encodeFpsCounter = 0;
                            _lastFpsTick = now;
                            RefreshByteCount_NoLock();
                        }
                    }
                    finally
                    {
                        Marshal.Release(sample);
                    }
                }
                finally
                {
                    Marshal.Release(buffer);
                }

                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }
    }

    public EncodeMetrics Complete()
    {
        lock (_gate)
        {
            if (_started && _writer != IntPtr.Zero)
            {
                try
                {
                    SinkFinalize(_writer);
                }
                catch (Exception ex)
                {
                    _lastError ??= ex.GetType().Name + ": " + ex.Message;
                }
            }

            RefreshByteCount_NoLock();
            CleanupNative();
            _started = false;
        }

        MFShutdown();
        EncodeMetrics metrics = CaptureMetrics();
        if (!_keepFile)
        {
            TryDeleteOutput();
        }

        return metrics;
    }

    public EncodeMetrics SnapshotMetrics()
    {
        lock (_gate)
        {
            RefreshByteCount_NoLock();
        }

        return CaptureMetrics();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _ = Complete();
        }
        catch
        {
            CleanupNative();
            try { MFShutdown(); } catch { /* best-effort */ }
            if (!_keepFile)
            {
                TryDeleteOutput();
            }
        }
    }

    private void ConfigureTypes()
    {
        ThrowIfFailed(MFCreateMediaType(out IntPtr outputType), "MFCreateMediaType(output)");
        try
        {
            AttributesSetGuid(outputType, MF_MT_MAJOR_TYPE, MFMediaType_Video);
            AttributesSetGuid(outputType, MF_MT_SUBTYPE, MFVideoFormat_H264);
            AttributesSetUint32(outputType, MF_MT_AVG_BITRATE, TargetBitrate);
            AttributesSetUint32(outputType, MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
            AttributesSetUint64(outputType, MF_MT_FRAME_SIZE, PackSize(_width, _height));
            AttributesSetUint64(outputType, MF_MT_FRAME_RATE, PackRatio(TargetFps, 1));
            AttributesSetUint64(outputType, MF_MT_PIXEL_ASPECT_RATIO, PackRatio(1, 1));
            SinkAddStream(_writer, outputType, out _streamIndex);
        }
        finally
        {
            Marshal.Release(outputType);
        }

        ThrowIfFailed(MFCreateMediaType(out IntPtr inputType), "MFCreateMediaType(input)");
        try
        {
            AttributesSetGuid(inputType, MF_MT_MAJOR_TYPE, MFMediaType_Video);
            AttributesSetGuid(inputType, MF_MT_SUBTYPE, MFVideoFormat_NV12);
            AttributesSetUint32(inputType, MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
            AttributesSetUint64(inputType, MF_MT_FRAME_SIZE, PackSize(_width, _height));
            AttributesSetUint64(inputType, MF_MT_FRAME_RATE, PackRatio(TargetFps, 1));
            AttributesSetUint64(inputType, MF_MT_PIXEL_ASPECT_RATIO, PackRatio(1, 1));
            AttributesSetUint32(inputType, MF_MT_DEFAULT_STRIDE, _width);
            AttributesSetUint32(inputType, MF_MT_SAMPLE_SIZE, (_width * _height * 3) / 2);
            AttributesSetUint32(inputType, MF_MT_FIXED_SIZE_SAMPLES, 1);
            AttributesSetUint32(inputType, MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
            SinkSetInputMediaType(_writer, _streamIndex, inputType);
        }
        finally
        {
            Marshal.Release(inputType);
        }
    }

    private static void BgraToNv12(
        byte[] bgra,
        int sourceWidth,
        int sourceHeight,
        int width,
        int height,
        byte[] nv12)
    {
        int ySize = width * height;
        for (int y = 0; y < height; y++)
        {
            int srcY = Math.Min(y, sourceHeight - 1);
            for (int x = 0; x < width; x++)
            {
                int srcX = Math.Min(x, sourceWidth - 1);
                int si = ((srcY * sourceWidth) + srcX) * 4;
                byte b = bgra[si];
                byte g = bgra[si + 1];
                byte r = bgra[si + 2];
                int Y = ((66 * r) + (129 * g) + (25 * b) + 128) >> 8;
                nv12[(y * width) + x] = (byte)Math.Clamp(Y + 16, 0, 255);
            }
        }

        int uvIndex = ySize;
        for (int y = 0; y < height; y += 2)
        {
            int srcY = Math.Min(y, sourceHeight - 1);
            for (int x = 0; x < width; x += 2)
            {
                int srcX = Math.Min(x, sourceWidth - 1);
                int si = ((srcY * sourceWidth) + srcX) * 4;
                byte b = bgra[si];
                byte g = bgra[si + 1];
                byte r = bgra[si + 2];
                int U = ((-38 * r) - (74 * g) + (112 * b) + 128) >> 8;
                int V = ((112 * r) - (94 * g) - (18 * b) + 128) >> 8;
                nv12[uvIndex++] = (byte)Math.Clamp(U + 128, 0, 255);
                nv12[uvIndex++] = (byte)Math.Clamp(V + 128, 0, 255);
            }
        }
    }

    private void RefreshByteCount_NoLock()
    {
        try
        {
            if (_writer != IntPtr.Zero)
            {
                var stats = new MfSinkWriterStatistics { cb = Marshal.SizeOf<MfSinkWriterStatistics>() };
                SinkGetStatistics(_writer, _streamIndex, ref stats);
                if (stats.qwByteCountProcessed > 0)
                {
                    Interlocked.Exchange(ref _bytesProcessed, stats.qwByteCountProcessed);
                    return;
                }
            }
        }
        catch
        {
            // Fall back to file length.
        }

        try
        {
            if (File.Exists(_outputPath))
            {
                Interlocked.Exchange(ref _bytesProcessed, new FileInfo(_outputPath).Length);
            }
        }
        catch
        {
            // Metrics are best-effort.
        }
    }

    private EncodeMetrics CaptureMetrics()
    {
        _process.Refresh();
        double elapsedSec = Math.Max(0.001, (Environment.TickCount64 - _wallStartTick) / 1000.0);
        TimeSpan cpuDelta = _process.TotalProcessorTime - _cpuStart;
        double cpuPercent = 100.0 * cpuDelta.TotalSeconds / (elapsedSec * Math.Max(1, Environment.ProcessorCount));
        long bytes = Interlocked.Read(ref _bytesProcessed);
        double kbps = bytes * 8.0 / elapsedSec / 1000.0;
        return new EncodeMetrics(
            FramesEncoded: Interlocked.Read(ref _framesEncoded),
            EncodeFramesPerSecond: _displayedEncodeFps,
            BytesProcessed: bytes,
            BitrateKbps: kbps,
            WorkingSetMb: _process.WorkingSet64 / (1024.0 * 1024.0),
            CpuPercentApprox: cpuPercent,
            ElapsedSeconds: elapsedSec,
            KeptFilePath: _keepFile && File.Exists(_outputPath) ? _outputPath : null,
            Error: _lastError);
    }

    private void CleanupNative()
    {
        if (_writer != IntPtr.Zero)
        {
            Marshal.Release(_writer);
            _writer = IntPtr.Zero;
        }

        if (_attributes != IntPtr.Zero)
        {
            Marshal.Release(_attributes);
            _attributes = IntPtr.Zero;
        }
    }

    private void TryDeleteOutput()
    {
        try
        {
            if (File.Exists(_outputPath))
            {
                File.Delete(_outputPath);
            }
        }
        catch
        {
            // Temp cleanup is best-effort.
        }
    }
}

internal readonly record struct EncodeMetrics(
    long FramesEncoded,
    int EncodeFramesPerSecond,
    long BytesProcessed,
    double BitrateKbps,
    double WorkingSetMb,
    double CpuPercentApprox,
    double ElapsedSeconds,
    string? KeptFilePath,
    string? Error);
