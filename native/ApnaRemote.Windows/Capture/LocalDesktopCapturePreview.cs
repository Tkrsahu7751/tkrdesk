using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace ApnaRemote.Windows.Capture;

/// <summary>
/// Opt-in local Windows.Graphics.Capture preview, with optional local Media Foundation H.264 encode metrics.
/// Preview is never blocked by encode. Does not open a network port or share frames remotely.
/// </summary>
internal sealed class LocalDesktopCapturePreview : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly object _gate = new();
    private readonly ConcurrentQueue<EncodeWorkItem> _encodeQueue = new();
    private readonly AutoResetEvent _encodeSignal = new(false);

    private IDirect3DDevice? _device;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private GraphicsCaptureItem? _item;
    private LocalH264EncodeSession? _encoder;
    private Thread? _encodeThread;
    private WriteableBitmap? _bitmap;
    private bool _disposed;
    private bool _frameInFlight;
    private bool _encodeEnabled;
    private bool _encodeThreadRunning;
    private long _framesPresented;
    private long _lastFpsTick;
    private int _fpsCounter;
    private int _displayedFps;
    private EncodeMetrics? _finalEncodeMetrics;
    private string? _encodeError;

    public event Action? Changed;

    public bool IsRunning { get; private set; }
    public bool IsSupported => GraphicsCaptureSession.IsSupported();
    public bool IsEncoding => _encodeEnabled && _encoder?.IsActive == true && _encodeError is null;
    public bool WantsEncoding => _encodeEnabled;
    public string Status { get; private set; } = "Local preview idle. No network sharing.";
    public ImageSource? PreviewImage { get; private set; }
    public int FramesPerSecond => _displayedFps;
    public long FramesPresented => _framesPresented;
    public EncodeMetrics? LastEncodeMetrics => _finalEncodeMetrics ?? _encoder?.SnapshotMetrics();

    public LocalDesktopCapturePreview(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _lastFpsTick = Environment.TickCount64;
    }

    public Task StartAsync(IntPtr ownerHwnd) => StartAsync(ownerHwnd, encodeEnabled: false);

    public async Task StartAsync(IntPtr ownerHwnd, bool encodeEnabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsSupported)
        {
            Status = "Windows.Graphics.Capture is not supported on this device.";
            RaiseChanged();
            return;
        }

        if (ownerHwnd == IntPtr.Zero)
        {
            Status = "Owner window handle is missing; cannot show the system capture picker.";
            RaiseChanged();
            return;
        }

        if (IsRunning)
        {
            return;
        }

        _encodeEnabled = encodeEnabled;
        _encodeError = null;
        _finalEncodeMetrics = null;
        Status = encodeEnabled
            ? "Waiting for capture target… preview first, then local H.264 encode on a background thread."
            : "Waiting for you to pick a window or display in the system UI…";
        RaiseChanged();

        var picker = new GraphicsCapturePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerHwnd);
        GraphicsCaptureItem? item = await picker.PickSingleItemAsync();
        if (item is null)
        {
            Status = "Local preview cancelled. Nothing is being captured.";
            RaiseChanged();
            return;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopCore_NoLock(finalizeEncoder: false);

            _device = Direct3DDeviceFactory.Create();
            _item = item;
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                item.Size);
            _framePool.FrameArrived += OnFrameArrived;
            _session = _framePool.CreateCaptureSession(item);
            _session.StartCapture();

            _framesPresented = 0;
            _fpsCounter = 0;
            _displayedFps = 0;
            _lastFpsTick = Environment.TickCount64;
            IsRunning = true;
            Status = encodeEnabled
                ? $"Capturing “{item.DisplayName}” · preview live · H.264 encode starting in background · no LAN"
                : $"Capturing “{item.DisplayName}” locally only. No LAN transport.";

            if (encodeEnabled)
            {
                StartEncodeThread_NoLock();
            }
        }

        item.Closed += OnItemClosed;
        RaiseChanged();
    }

    public void Stop()
    {
        EncodeMetrics? metrics;
        lock (_gate)
        {
            metrics = StopCore_NoLock(finalizeEncoder: true);
        }

        _finalEncodeMetrics = metrics;
        PreviewImage = null;
        _bitmap = null;
        if (metrics is { } m)
        {
            Status = FormatStoppedStatus(m);
        }
        else if (!string.IsNullOrEmpty(_encodeError))
        {
            Status = "Local preview stopped. Encode error was: " + _encodeError;
        }
        else
        {
            Status = "Local preview stopped. No network or capture is active.";
        }

        RaiseChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            _ = StopCore_NoLock(finalizeEncoder: true);
        }

        PreviewImage = null;
        _bitmap = null;
        _encodeSignal.Dispose();
    }

    private void StartEncodeThread_NoLock()
    {
        if (_encodeThreadRunning)
        {
            return;
        }

        _encodeThreadRunning = true;
        _encodeThread = new Thread(EncodeThreadMain)
        {
            IsBackground = true,
            Name = "ApnaRemote-LocalH264",
        };
        _encodeThread.SetApartmentState(ApartmentState.MTA);
        _encodeThread.Start();
    }

    private void EncodeThreadMain()
    {
        try
        {
            while (_encodeThreadRunning && !_disposed)
            {
                if (!_encodeQueue.TryDequeue(out EncodeWorkItem? work))
                {
                    _ = _encodeSignal.WaitOne(100);
                    continue;
                }

                // Keep only the latest frame if the encoder is behind.
                while (_encodeQueue.TryDequeue(out EncodeWorkItem? newer))
                {
                    work = newer;
                }

                if (!_encodeEnabled || !IsRunning)
                {
                    continue;
                }

                try
                {
                    if (_encoder is null)
                    {
                        _encoder = new LocalH264EncodeSession(work.Width, work.Height, keepFile: false);
                        _ = _dispatcher.BeginInvoke(() =>
                        {
                            Status = $"H.264 encode session ready · {_encoder.Width}x{_encoder.Height} · no network";
                            RaiseChanged();
                        });
                    }

                    if (!_encoder.TryEncodeBgraFrame(work.Pixels, work.Width, work.Height) &&
                        !string.IsNullOrEmpty(_encoder.LastError))
                    {
                        _encodeError = _encoder.LastError;
                        _encodeEnabled = false;
                        _ = _dispatcher.BeginInvoke(() =>
                        {
                            Status = "H.264 encode error: " + _encodeError + " · preview continues.";
                            RaiseChanged();
                        });
                    }
                }
                catch (Exception ex)
                {
                    _encodeError = ex.GetType().Name + ": " + ex.Message;
                    _encodeEnabled = false;
                    try { _encoder?.Dispose(); } catch { /* ignore */ }
                    _encoder = null;
                    _ = _dispatcher.BeginInvoke(() =>
                    {
                        Status = "H.264 encode failed: " + _encodeError + " · preview continues.";
                        RaiseChanged();
                    });
                }
            }
        }
        finally
        {
            _encodeThreadRunning = false;
        }
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        _ = _dispatcher.BeginInvoke(() =>
        {
            Stop();
            if (_finalEncodeMetrics is null && string.IsNullOrEmpty(_encodeError))
            {
                Status = "Capture target closed. Local preview ended.";
                RaiseChanged();
            }
        });
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_frameInFlight)
        {
            using Direct3D11CaptureFrame? discarded = sender.TryGetNextFrame();
            return;
        }

        Direct3D11CaptureFrame? frame = sender.TryGetNextFrame();
        if (frame is null)
        {
            return;
        }

        _frameInFlight = true;
        _ = PresentAsync(frame);
    }

    private async Task PresentAsync(Direct3D11CaptureFrame frame)
    {
        try
        {
            using (frame)
            {
                using SoftwareBitmap softwareBitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
                using SoftwareBitmap bgra = softwareBitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8
                    && softwareBitmap.BitmapAlphaMode == BitmapAlphaMode.Premultiplied
                    ? SoftwareBitmap.Copy(softwareBitmap)
                    : SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

                int width = bgra.PixelWidth;
                int height = bgra.PixelHeight;
                byte[] pixels = new byte[width * height * 4];
                bgra.CopyToBuffer(pixels.AsBuffer());

                // Preview first — encode must not stall the visible lab.
                await _dispatcher.InvokeAsync(() => ApplyFrame(width, height, pixels));
                QueueEncodeCopy(pixels, width, height);
            }
        }
        catch (Exception ex)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                Stop();
                Status = "Local capture failed: " + ex.GetType().Name + ". Preview stopped.";
                RaiseChanged();
            });
        }
        finally
        {
            _frameInFlight = false;
        }
    }

    private void QueueEncodeCopy(byte[] pixels, int width, int height)
    {
        if (!_encodeEnabled || _encodeError is not null)
        {
            return;
        }

        byte[] copy = new byte[pixels.Length];
        Buffer.BlockCopy(pixels, 0, copy, 0, pixels.Length);
        _encodeQueue.Enqueue(new EncodeWorkItem(copy, width, height));
        _ = _encodeSignal.Set();
    }

    private void ApplyFrame(int width, int height, byte[] pixels)
    {
        if (!IsRunning || _disposed)
        {
            return;
        }

        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            PreviewImage = _bitmap;
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);

        _framesPresented++;
        _fpsCounter++;
        long now = Environment.TickCount64;
        if (now - _lastFpsTick >= 1000)
        {
            _displayedFps = _fpsCounter;
            _fpsCounter = 0;
            _lastFpsTick = now;
            Status = FormatLiveStatus();
        }

        RaiseChanged();
    }

    private string FormatLiveStatus()
    {
        if (!string.IsNullOrEmpty(_encodeError))
        {
            return $"Preview {_displayedFps} FPS · encode failed ({_encodeError}) · no network";
        }

        if (_encodeEnabled && _encoder?.IsActive == true)
        {
            EncodeMetrics m = _encoder.SnapshotMetrics();
            return $"Preview {_displayedFps} FPS · Encode {m.EncodeFramesPerSecond} FPS · {m.BitrateKbps:0} kbps · {m.WorkingSetMb:0} MB · CPU ~{m.CpuPercentApprox:0}% · no network";
        }

        if (_encodeEnabled)
        {
            return $"Preview {_displayedFps} FPS · H.264 encode warming up · {_framesPresented} frames · no network";
        }

        return $"Local preview {_displayedFps} FPS · {_framesPresented} frames · no network";
    }

    private static string FormatStoppedStatus(EncodeMetrics m)
    {
        string error = string.IsNullOrEmpty(m.Error) ? "" : " · error: " + m.Error;
        return $"Stopped · encoded {m.FramesEncoded} frames · avg {m.BitrateKbps:0} kbps · {m.BytesProcessed / 1024.0:0} KB · {m.ElapsedSeconds:0.0}s · {m.WorkingSetMb:0} MB · CPU ~{m.CpuPercentApprox:0}% · temp encode deleted · no network{error}";
    }

    private EncodeMetrics? StopCore_NoLock(bool finalizeEncoder)
    {
        if (_item is not null)
        {
            _item.Closed -= OnItemClosed;
        }

        if (_framePool is not null)
        {
            _framePool.FrameArrived -= OnFrameArrived;
        }

        _session?.Dispose();
        _session = null;
        _framePool?.Dispose();
        _framePool = null;
        if (_device is IDisposable disposableDevice)
        {
            disposableDevice.Dispose();
        }

        _device = null;
        _item = null;
        IsRunning = false;
        _displayedFps = 0;

        _encodeThreadRunning = false;
        _ = _encodeSignal.Set();
        Thread? encodeThread = _encodeThread;
        _encodeThread = null;

        // Release lock wait outside by joining after draining — join without holding if possible.
        // Caller holds _gate; join briefly with timeout to avoid UI deadlock.
        if (encodeThread is not null && encodeThread.IsAlive)
        {
            _ = encodeThread.Join(2000);
        }

        while (_encodeQueue.TryDequeue(out _))
        {
            // Drop queued frames on stop.
        }

        EncodeMetrics? metrics = null;
        if (_encoder is not null)
        {
            if (finalizeEncoder)
            {
                try
                {
                    metrics = _encoder.Complete();
                }
                catch (Exception ex)
                {
                    _encodeError ??= ex.Message;
                    try { _encoder.Dispose(); } catch { /* ignore */ }
                }
            }
            else
            {
                try { _encoder.Dispose(); } catch { /* ignore */ }
            }

            _encoder = null;
        }

        _encodeEnabled = false;
        return metrics;
    }

    private void RaiseChanged() => Changed?.Invoke();

    private sealed record EncodeWorkItem(byte[] Pixels, int Width, int Height);
}
