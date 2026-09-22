using System.Runtime.InteropServices.WindowsRuntime;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Threading;
using ApnaRemote.Core;
using ApnaRemote.Protocol;
using ApnaRemote.Windows.Capture;
using ApnaRemote.Windows.Input;
using Microsoft.Win32;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ApnaRemote.Windows.Lan;

/// <summary>Attended revision-3 LAN lab. PIN proof is cert-bound; fingerprint compare is still out-of-band.</summary>
internal sealed class LanHostSession : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private HostRun? _run;
    private bool _disposed;
    public event Action? Changed;
    public bool IsListening { get; private set; }
    public bool IsSharing { get; private set; }
    public string Pin { get; private set; } = "";
    public string FingerprintText { get; private set; } = "";
    public int Port => ArlProtocol.DefaultPort;
    public string Status { get; private set; } = "LAN receiving off.";
    public string LocalAddressesText { get; private set; } = "";
    public long FramesSent { get; private set; }
    public long FramesDropped { get; private set; }
    public long BytesSent { get; private set; }
    public int SendFps { get; private set; }
    public int SendKbps { get; private set; }
    public bool CanRevoke => IsSharing && _run?.Control?.Snapshot.Phase is SessionPhase.Controlling or SessionPhase.Paused;
    public bool CanPause => IsSharing && _run?.Control?.Snapshot.Phase == SessionPhase.Controlling;
    public bool CanGrant => IsSharing && _run?.Monitor is not null && _run.Control?.Snapshot.Phase is SessionPhase.Viewing or SessionPhase.Paused;
    public string ControlStatus => _run?.Control?.Snapshot.Phase switch
    {
        SessionPhase.Controlling => "Control approved",
        SessionPhase.Paused => "Control paused · screen still visible",
        _ => "View-only"
    };
    public LabQualityPreset QualityPreset { get; set; } = LabQualityPreset.Balanced;
    /// <summary>Preferred outbound codec. H.264 falls back to JPEG if natives fail.</summary>
    public LabStreamCodec StreamCodec { get; set; } = LabStreamCodec.Jpeg;
    /// <summary>Codec actually announced in the last Approved message.</summary>
    public LabStreamCodec ActiveStreamCodec { get; private set; } = LabStreamCodec.Jpeg;

    public LanHostSession(Dispatcher dispatcher) { _dispatcher = dispatcher; RefreshLocalAddresses(); }
    public void RefreshLocalAddresses() { LocalAddressesText = string.Join(", ", GetLikelyLanIPv4()); RaiseChanged(); }

    public void StartListening(IntPtr ownerHwnd)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_run is not null) return;
        if (!GraphicsCaptureSession.IsSupported()) { Status = "Windows capture is unavailable."; RaiseChanged(); return; }
        var run = new HostRun();
        try
        {
            run.Certificate = LanTls.CreateEphemeral(out string fingerprint);
            run.Fingerprint = fingerprint;
            run.Listener.Start();
        }
        catch
        {
            run.Dispose();
            throw;
        }

        _run = run;
        run.Gate.EnableReceiving();
        Pin = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6");
        FingerprintText = run.Fingerprint;
        string pin = Pin;
        IsListening = true;
        RefreshLocalAddresses();
        Status = $"Listening on port {Port} (TLS). Share PIN and fingerprint with the other PC.";
        RaiseChanged();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _ = Task.Run(() => ListenAsync(run, ownerHwnd, pin));
    }

    public void RevokeControl() => ChangeControl(false);
    public void GrantControl() => ChangeControl(true);
    private void ChangeControl(bool allow)
    {
        _dispatcher.VerifyAccess();
        if (_run is not { } run || !IsSharing || run.Control is not { } state) return;
        CaptureMonitor? selected = run.Monitor;
        try
        {
            if (allow && (selected is null || !CaptureDisplayBounds.IsCurrent(selected)))
            {
                Status = "Cannot grant control — selected monitor is missing or changed.";
                RaiseChanged();
                return;
            }
        }
        catch
        {
            Status = "Cannot change control — monitor check failed.";
            RaiseChanged();
            return;
        }

        // Never call Stop() here: a disconnected peer / inactive gate must not tear down listening.
        if (!state.Change(allow))
        {
            Status = "Control change ignored — session already ended.";
            RaiseChanged();
            return;
        }

        Status = allow
            ? "Control approved locally; waiting for viewer acknowledgement."
            : "Control revoked. View-only continues.";
        RaiseChanged();
    }
    public void PauseControl()
    {
        _dispatcher.VerifyAccess();
        if (!CanPause) return;
        _run?.Control?.Pause();
        Status = "Control paused. The screen is still visible.";
        RaiseChanged();
    }
    public void Stop() => Stop("LAN receiving off.");

    private void Stop(string reason)
    {
        _dispatcher.VerifyAccess();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        HostRun? run = _run;
        _run = null;
        run?.Cancel(); // Stops receiving and releases held input synchronously.
        IsListening = IsSharing = false;
        Pin = ""; FingerprintText = ""; FramesSent = 0; FramesDropped = 0; BytesSent = 0; SendFps = 0; SendKbps = 0;
        Status = reason;
        RaiseChanged();
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is not (SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff
            or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect))
        {
            return;
        }

        // Fail-closed: lock / logoff / console disconnect must cancel listen AND live share
        // (including pending Accept) so a locked PC cannot approve or keep streaming.
        HostRun? observedRun = _run;
        if (observedRun is null) return;
        _ = _dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_run, observedRun)) return;
            if (IsSharing || IsListening)
            {
                Stop("Session locked or disconnected — sharing stopped for safety.");
            }
        });
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop(); // Never block the UI on an async dialog/capture worker.
    }

    private async Task ListenAsync(HostRun run, IntPtr owner, string pin)
    {
        string? stopReason = null;
        try
        {
            while (!run.Token.IsCancellationRequested)
            {
                using TcpClient client = await run.Listener.AcceptTcpClientAsync(run.Token).ConfigureAwait(false);
                lock (run.Sync)
                {
                    run.Token.ThrowIfCancellationRequested();
                    run.Client = client;
                }
                bool stopAfterViewerEnd = false;
                try { await HandleAsync(run, client, owner, pin).ConfigureAwait(false); }
                catch (OperationCanceledException) when (run.Token.IsCancellationRequested) { break; }
                catch (Exception ex) { Post(run, () => Status = "Session ended: " + ex.Message); }
                finally
                {
                    run.Gate.Disconnect();
                    if (run.Gate.Snapshot.CleanupFault)
                    {
                        stopReason = "Input cleanup failed. Receiving disabled; restart only after investigating.";
                        run.Cancel();
                    }
                    bool viewerEndedShare = run.LiveShareStarted;
                    run.LiveShareStarted = false;
                    run.Control = null; run.Monitor = null;
                    lock (run.Sync) run.Client = null;
                    Post(run, () =>
                    {
                        IsSharing = false;
                        FramesSent = 0; FramesDropped = 0; BytesSent = 0; SendFps = 0; SendKbps = 0;
                        if (viewerEndedShare && stopReason is null && !run.Token.IsCancellationRequested)
                            Status = "Viewer ended — sharing stopped. Press Start sharing to allow a new connection.";
                        else if (!viewerEndedShare && IsListening && stopReason is null && !run.Token.IsCancellationRequested)
                            Status = $"Listening on port {Port} (TLS). Share PIN and fingerprint with the other PC.";
                    });
                    // After a live share, do not keep listening: End/Disconnect clears host Share.
                    if (viewerEndedShare && stopReason is null)
                    {
                        stopReason = "Viewer ended — sharing stopped.";
                        stopAfterViewerEnd = true;
                        run.Cancel();
                    }
                }
                if (stopAfterViewerEnd) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { stopReason = "Host stopped: " + ex.Message; }
        finally
        {
            run.Cancel();
            if (!_dispatcher.HasShutdownStarted)
            {
                try
                {
                    await _dispatcher.InvokeAsync(() =>
                    {
                        if (!ReferenceEquals(_run, run)) return;
                        SystemEvents.SessionSwitch -= OnSessionSwitch;
                        _run = null; IsSharing = IsListening = false; Pin = ""; FingerprintText = "";
                        if (stopReason is not null) Status = stopReason;
                        RaiseChanged();
                    }).Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_dispatcher.HasShutdownStarted) { }
            }
            run.Dispose();
        }
    }

    private async Task HandleAsync(HostRun run, TcpClient client, IntPtr owner, string pin)
    {
        client.NoDelay = true;
        await using SslStream stream = await LanTls.AuthenticateAsServerAsync(
            client.GetStream(), run.Certificate!, run.Token).ConfigureAwait(false);
        using var helloDeadline = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
        helloDeadline.CancelAfter(TimeSpan.FromSeconds(8));
        var hello = await ArlProtocol.ReadMessageAsync(stream, helloDeadline.Token).ConfigureAwait(false);
        string peerId, displayName;
        byte[] pinProof;
        try
        {
            if (hello.Type != ArlProtocol.MessageType.HelloViewer) throw new InvalidOperationException("Expected viewer hello.");
            (peerId, displayName, pinProof) = ArlProtocol.DecodeHelloViewer(hello.Payload);
        }
        catch (Exception ex)
        {
            await WriteAsync(stream, ArlProtocol.MessageType.HelloReject, ArlProtocol.EncodeString(ex.Message), run.Token);
            return;
        }
        if (!LanTls.VerifyPinProof(pin, run.Certificate!, pinProof))
        {
            run.PinFailures++;
            await WriteAsync(stream, ArlProtocol.MessageType.HelloReject,
                ArlProtocol.EncodeString(run.PinFailures >= LanTls.MaxPinFailuresPerListen
                    ? "Too many wrong PIN attempts. Restart sharing for a new PIN."
                    : "Wrong PIN."), run.Token);
            if (run.PinFailures >= LanTls.MaxPinFailuresPerListen)
            {
                Post(run, () =>
                {
                    Stop("Too many wrong PIN attempts — receiving stopped.");
                });
            }

            return;
        }

        run.PinFailures = 0;
        Guid id = run.Gate.RequestFromAuthenticatedPeer(new(peerId, displayName));
        await WriteAsync(stream, ArlProtocol.MessageType.HelloOk, ArlProtocol.EncodeGuid(id), run.Token);
        await WriteAsync(stream, ArlProtocol.MessageType.WaitingApproval, [], run.Token);
        Post(run, () => Status = "Waiting for your local sharing decision.");

        using var approval = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
        approval.CancelAfter(HostSessionGate.ApprovalTimeout);
        Task disconnectWatch = WatchPendingDisconnectAsync(client, approval);
        GraphicsCaptureItem? item = null;
        CaptureMonitor? monitor = null;
        bool control = false;
        try
        {
            HostConsent? consent = await _dispatcher.InvokeAsync(() =>
            {
                approval.Token.ThrowIfCancellationRequested();
                var dialog = new HostConsentWindow(displayName, client.Client.RemoteEndPoint?.ToString() ?? "unknown");
                // Do not set Owner: modal owner would disable Stop receiving on MainWindow.
                using var closeOnCancel = approval.Token.Register(() =>
                    _ = _dispatcher.BeginInvoke(() => { if (dialog.IsVisible) dialog.Close(); }));
                approval.Token.ThrowIfCancellationRequested();
                bool? ok = dialog.ShowDialog();
                if (dialog.StopReceivingRequested) return HostConsent.StopReceiving;
                return ok == true ? dialog.Consent : null;
            }, DispatcherPriority.Normal, approval.Token).Task.ConfigureAwait(false);
            approval.Token.ThrowIfCancellationRequested();
            if (consent is HostConsent { EndsListening: true })
            {
                run.Gate.RejectLocally(id);
                await WriteAsync(stream, ArlProtocol.MessageType.Rejected,
                    ArlProtocol.EncodeString("Sharing stopped."), run.Token);
                Post(run, () => Stop());
                return;
            }
            if (consent is null)
            {
                run.Gate.RejectLocally(id);
                string reason = !IsSocketAlive(client)
                    ? "Viewer disconnected during approval."
                    : approval.IsCancellationRequested && !run.Token.IsCancellationRequested
                        ? "Approval timed out."
                        : "Connection declined.";
                await WriteAsync(stream, ArlProtocol.MessageType.Rejected, ArlProtocol.EncodeString(reason), run.Token);
                return;
            }
            monitor = consent.Monitor;
            control = consent.Control && monitor is not null;
            if (monitor is not null)
                item = await _dispatcher.InvokeAsync(() => MonitorCaptureSource.Create(monitor),
                    DispatcherPriority.Normal, approval.Token).Task.ConfigureAwait(false);
            else
                item = await _dispatcher.InvokeAsync(async () =>
                {
                    approval.Token.ThrowIfCancellationRequested();
                    var picker = new GraphicsCapturePicker();
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, owner);
                    return await picker.PickSingleItemAsync().AsTask(approval.Token);
                }, DispatcherPriority.Normal, approval.Token).Task.Unwrap().WaitAsync(approval.Token).ConfigureAwait(false);
            approval.Token.ThrowIfCancellationRequested();
        }
        finally
        {
            approval.Cancel();
            await disconnectWatch.ConfigureAwait(false);
        }
        run.Token.ThrowIfCancellationRequested();
        if (item is null)
        {
            run.Gate.RejectLocally(id);
            await WriteAsync(stream, ArlProtocol.MessageType.Rejected, ArlProtocol.EncodeString("Capture selection cancelled."), run.Token);
            return;
        }
        var bounds = monitor?.Bounds ?? new DisplayBounds(0, 0, item.Size.Width, item.Size.Height);
        if (monitor is not null && (!CaptureDisplayBounds.IsCurrent(monitor) || item.Size.Width != bounds.Width || item.Size.Height != bounds.Height))
            throw new InvalidOperationException("Monitor changed; select it again.");
        if (!run.Gate.ApproveLocally(id, item.DisplayName, bounds, control))
            throw new InvalidOperationException("Approval expired.");
        run.Monitor = monitor;
        run.Control = new HostControlState(run.Gate);

        LabStreamCodec activeCodec = LabStreamCodec.Jpeg;
        NetworkH264Encoder? h264 = null;
        if (StreamCodec == LabStreamCodec.H264)
        {
            if (NetworkH264Bootstrap.TryEnsureLoaded(out string? h264Error))
            {
                h264 = new NetworkH264Encoder();
                activeCodec = LabStreamCodec.H264;
            }
            else
            {
                Post(run, () => Status = "H.264 unavailable (" + (h264Error ?? "native load failed") + ") — using JPEG.");
            }
        }

        int streamCodecWire = activeCodec == LabStreamCodec.H264
            ? ArlProtocol.StreamCodecH264
            : ArlProtocol.StreamCodecJpeg;
        await WriteAsync(stream, ArlProtocol.MessageType.Approved,
            ArlProtocol.EncodeApproved(bounds.Width, bounds.Height, control, run.Gate.Snapshot.PermissionEpoch, streamCodecWire), run.Token);
        Post(run, () =>
        {
            run.LiveShareStarted = true;
            IsSharing = true;
            ActiveStreamCodec = activeCodec;
            Status = activeCodec == LabStreamCodec.H264
                ? $"Sharing {item.DisplayName} with {displayName} (H.264)."
                : $"Sharing {item.DisplayName} with {displayName} (JPEG).";
        });
        try
        {
            await ShareFramesAsync(run, stream, item, monitor, id, peerId, h264, activeCodec).ConfigureAwait(false);
        }
        finally
        {
            h264?.Dispose();
        }
    }

    private static async Task WatchPendingDisconnectAsync(TcpClient client, CancellationTokenSource approval)
    {
        try
        {
            while (!approval.IsCancellationRequested)
            {
                await Task.Delay(100, approval.Token).ConfigureAwait(false);
                if (client.Client.Poll(0, SelectMode.SelectRead) && client.Client.Available == 0) { approval.Cancel(); return; }
            }
        }
        catch (OperationCanceledException) { }
        catch { approval.Cancel(); }
    }

    private async Task ShareFramesAsync(HostRun run, Stream stream, GraphicsCaptureItem item,
        CaptureMonitor? monitor, Guid id, string peer, NetworkH264Encoder? h264, LabStreamCodec codec)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
        using var closeSocket = linked.Token.Register(() => { run.Gate.Disconnect(); stream.Close(); });
        void RequestEnd()
        {
            try { linked.Cancel(); } catch (ObjectDisposedException) { }
        }
        using IDirect3DDevice device = Direct3DDeviceFactory.Create();
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        using var capture = pool.CreateCaptureSession(item);
        SoftwareBitmap? pending = null;
        object bitmapLock = new();
        int closing = 0;
        int copyBusy = 0;
        long dropped = 0;
        var initialSize = item.Size;
        void Closed(GraphicsCaptureItem sender, object args) => RequestEnd();
        void Frame(Direct3D11CaptureFramePool sender, object args)
        {
            if (Volatile.Read(ref closing) != 0 || Interlocked.CompareExchange(ref copyBusy, 1, 0) != 0)
            {
                using var skipped = sender.TryGetNextFrame();
                Interlocked.Increment(ref dropped);
                return;
            }

            Direct3D11CaptureFrame? frame = null;
            try
            {
                frame = sender.TryGetNextFrame();
                if (frame is null)
                {
                    Interlocked.Exchange(ref copyBusy, 0);
                    return;
                }

                if (frame.ContentSize.Width != initialSize.Width || frame.ContentSize.Height != initialSize.Height)
                {
                    frame.Dispose();
                    Interlocked.Exchange(ref copyBusy, 0);
                    RequestEnd();
                    return;
                }

                Direct3D11CaptureFrame keep = frame;
                frame = null;
                _ = SoftwareBitmap.CreateCopyFromSurfaceAsync(keep.Surface).AsTask().ContinueWith(task =>
                {
                    try
                    {
                        keep.Dispose();
                        if (!task.IsCompletedSuccessfully)
                        {
                            RequestEnd();
                            return;
                        }

                        lock (bitmapLock)
                        {
                            if (Volatile.Read(ref closing) != 0)
                            {
                                task.Result.Dispose();
                                return;
                            }

                            if (pending is not null)
                            {
                                Interlocked.Increment(ref dropped);
                                pending.Dispose();
                            }

                            pending = task.Result;
                        }
                    }
                    finally
                    {
                        Interlocked.Exchange(ref copyBusy, 0);
                    }
                }, TaskScheduler.Default);
            }
            catch
            {
                frame?.Dispose();
                Interlocked.Exchange(ref copyBusy, 0);
                RequestEnd();
            }
        }
        item.Closed += Closed;
        pool.FrameArrived += Frame;
        Task reader = Task.CompletedTask, watchdog = Task.CompletedTask;
        using var hostClipboard = new ApnaRemote.Windows.Input.ClipboardSyncBridge(async text =>
        {
            if (!linked.IsCancellationRequested)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                await WriteAsync(stream, ArlProtocol.MessageType.ClipboardText, bytes, linked.Token).ConfigureAwait(false);
            }
        });
        hostClipboard.Start();
        try
        {
            reader = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var message = await ArlProtocol.ReadMessageAsync(stream, linked.Token).ConfigureAwait(false);
                        if (message.Type == ArlProtocol.MessageType.Heartbeat) run.Gate.Heartbeat(id, peer);
                        else if (message.Type == ArlProtocol.MessageType.PermissionAck)
                        {
                            var update = ArlProtocol.DecodePermission(message.Payload);
                            run.Control!.Acknowledge(update.Epoch, update.Control, update.Paused);
                        }
                        else if (message.Type == ArlProtocol.MessageType.InputControl)
                        {
                            // Window picker shares stay view-only: ignore input, do not tear down the session.
                            if (monitor is null) continue;
                            if (!CaptureDisplayBounds.IsCurrent(monitor)) { linked.Cancel(); return; }
                            var command = await ProtocolFraming.ReadAsync(new MemoryStream(message.Payload), linked.Token);
                            _ = run.Control!.Apply(peer, command);
                        }
                        else if (message.Type == ArlProtocol.MessageType.ClipboardText)
                        {
                            if (message.Payload.Length > 0 && message.Payload.Length <= 256 * 1024)
                            {
                                string text = Encoding.UTF8.GetString(message.Payload);
                                hostClipboard.ApplyRemoteText(text);
                            }
                        }
                        else { linked.Cancel(); return; }
                    }
                }
                catch { linked.Cancel(); }
            });
            watchdog = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        await Task.Delay(250, linked.Token);
                        run.Gate.Tick();
                        if (!run.Gate.CanShareFrames(id, peer) || (monitor is not null && !CaptureDisplayBounds.IsCurrent(monitor)))
                        { linked.Cancel(); return; }
                    }
                }
                catch { linked.Cancel(); }
            });
            capture.StartCapture();
            long frames = 0, bytes = 0, fpsTick = Environment.TickCount64, heartbeatTick = 0, windowBytes = 0;
            int fpsCount = 0, sentEpoch = 1;
            while (!linked.IsCancellationRequested)
            {
                var state = run.Gate.Snapshot;
                if (state.PermissionEpoch != sentEpoch)
                {
                    await WriteAsync(stream, ArlProtocol.MessageType.PermissionChanged,
                        ArlProtocol.EncodePermission(state.PermissionEpoch, state.Phase == SessionPhase.Controlling, state.Phase == SessionPhase.Paused), linked.Token);
                    sentEpoch = state.PermissionEpoch;
                }
                long now = Environment.TickCount64;
                if (now - heartbeatTick >= 2000)
                {
                    await WriteAsync(stream, ArlProtocol.MessageType.Heartbeat, [], linked.Token);
                    heartbeatTick = Environment.TickCount64;
                }
                SoftwareBitmap? bitmap;
                lock (bitmapLock) { bitmap = pending; pending = null; }
                if (bitmap is not null)
                {
                    var profile = QualityPreset.Profile().WithFpsNudge(SendFps);
                    byte[] payload;
                    ArlProtocol.MessageType frameType;
                    using (bitmap)
                    {
                        if (codec == LabStreamCodec.H264 && h264 is not null)
                        {
                            var (bgra, encW, encH) = await CopyScaledBgraAsync(bitmap, profile.MaxWidth).ConfigureAwait(false);
                            if (!h264.EnsureSize(encW, encH, profile.H264Bitrate, profile.H264Fps) ||
                                !h264.TryEncodeBgra(bgra, encW, encH, out payload))
                            {
                                Interlocked.Increment(ref dropped);
                                DecaySendFpsIfIdle();
                                await Task.Delay(profile.FrameDelayMs, linked.Token);
                                continue;
                            }

                            frameType = ArlProtocol.MessageType.FrameH264;
                        }
                        else
                        {
                            payload = await EncodeJpegAsync(bitmap, profile.JpegQuality, profile.MaxWidth).ConfigureAwait(false);
                            frameType = ArlProtocol.MessageType.FrameJpeg;
                        }
                    }

                    if (!run.Gate.CanShareFrames(id, peer)) break;
                    await WriteAsync(stream, frameType, payload, linked.Token);
                    long afterWrite = Environment.TickCount64;
                    frames++; fpsCount++;
                    bytes += payload.Length;
                    windowBytes += payload.Length;
                    if (afterWrite - fpsTick >= 1000)
                    {
                        int fps = (int)Math.Round(fpsCount * 1000.0 / (afterWrite - fpsTick));
                        int kbps = (int)Math.Round(windowBytes * 8.0 / (afterWrite - fpsTick));
                        long count = frames, totalBytes = bytes, drops = Interlocked.Read(ref dropped);
                        Post(run, () =>
                        {
                            FramesSent = count;
                            BytesSent = totalBytes;
                            FramesDropped = drops;
                            SendFps = fps;
                            SendKbps = kbps;
                        }, throttleUi: true);
                        fpsCount = 0; windowBytes = 0; fpsTick = afterWrite;
                    }
                }

                DecaySendFpsIfIdle();

                await Task.Delay(QualityPreset.Profile().WithFpsNudge(SendFps).FrameDelayMs, linked.Token);
            }

            void DecaySendFpsIfIdle()
            {
                long idleMs = Environment.TickCount64 - fpsTick;
                if (idleMs < 1500 || frames <= 0) return;
                Post(run, () =>
                {
                    SendFps = 0;
                    SendKbps = 0;
                }, throttleUi: true);
                fpsCount = 0; windowBytes = 0; fpsTick = Environment.TickCount64;
            }
        }
        finally
        {
            linked.Cancel();
            pool.FrameArrived -= Frame;
            item.Closed -= Closed;
            lock (bitmapLock) { Volatile.Write(ref closing, 1); pending?.Dispose(); pending = null; }
            for (int i = 0; i < 50 && Volatile.Read(ref copyBusy) != 0; i++)
                await Task.Delay(10).ConfigureAwait(false);
            await Task.WhenAll(reader, watchdog).ConfigureAwait(false);
        }
    }

    private static Task WriteAsync(Stream stream, ArlProtocol.MessageType type, byte[] payload, CancellationToken ct)
        => ArlProtocol.WriteMessageWithDeadlineAsync(stream, type, payload, ArlProtocol.WriteDeadline, ct);
    private void Post(HostRun run, Action action, bool allowCancelled = false, bool throttleUi = false)
    {
        if (_dispatcher.HasShutdownStarted) return;
        _ = _dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(_run, run) && (allowCancelled || !run.Token.IsCancellationRequested))
            {
                action();
                if (throttleUi) RaiseChangedThrottled();
                else RaiseChanged();
            }
        });
    }

    private long _lastUiRaiseTick;
    private void RaiseChangedThrottled()
    {
        long now = Environment.TickCount64;
        if (now - _lastUiRaiseTick < 1500) return;
        _lastUiRaiseTick = now;
        RaiseChanged();
    }
    private sealed class HostRun : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        public CancellationToken Token { get; }
        public readonly object Sync = new();
        public readonly TcpListener Listener = new(IPAddress.Any, ArlProtocol.DefaultPort);
        public readonly HostSessionGate Gate = new(new WindowsSendInputSink());
        public X509Certificate2? Certificate;
        public string Fingerprint = "";
        public int PinFailures;
        public TcpClient? Client;
        public CaptureMonitor? Monitor;
        public HostControlState? Control;
        /// <summary>True after Accept and live share started; used to auto-stop listening when the viewer Ends.</summary>
        public bool LiveShareStarted;
        public HostRun() => Token = _cts.Token;
        public void Cancel()
        {
            _cts.Cancel(); Gate.DisableReceiving(); Listener.Stop();
            lock (Sync) Client?.Dispose();
        }
        public void Dispose()
        {
            Listener.Stop();
            Certificate?.Dispose();
            _cts.Dispose();
        }
    }

    private static async Task<(byte[] Pixels, int Width, int Height)> CopyScaledBgraAsync(SoftwareBitmap bitmap, int maxWidth)
    {
        SoftwareBitmap? converted = null;
        SoftwareBitmap? scaled = null;
        SoftwareBitmap bgra = bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8
            ? bitmap
            : (converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8));

        try
        {
            maxWidth = Math.Clamp(maxWidth, 640, 1920);
            int width = bgra.PixelWidth;
            int height = bgra.PixelHeight;
            SoftwareBitmap source = bgra;
            if (width > maxWidth)
            {
                int outW = maxWidth & ~1;
                int outH = Math.Max(2, (height * outW / width) & ~1);
                using var stream = new InMemoryRandomAccessStream();
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetSoftwareBitmap(bgra);
                encoder.BitmapTransform.ScaledWidth = (uint)outW;
                encoder.BitmapTransform.ScaledHeight = (uint)outH;
                encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Linear;
                await encoder.FlushAsync();
                stream.Seek(0);
                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
                scaled = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                source = scaled;
                width = source.PixelWidth & ~1;
                height = source.PixelHeight & ~1;
            }
            else
            {
                width &= ~1;
                height &= ~1;
            }

            byte[] pixels = new byte[checked(width * height * 4)];
            // CopyToBuffer wants the full buffer; if odd crop, copy via a temp full-size then trim rows.
            if (source.PixelWidth == width && source.PixelHeight == height)
            {
                source.CopyToBuffer(pixels.AsBuffer());
            }
            else
            {
                byte[] full = new byte[checked(source.PixelWidth * source.PixelHeight * 4)];
                source.CopyToBuffer(full.AsBuffer());
                for (int y = 0; y < height; y++)
                    System.Buffer.BlockCopy(full, y * source.PixelWidth * 4, pixels, y * width * 4, width * 4);
            }

            return (pixels, width, height);
        }
        finally
        {
            scaled?.Dispose();
            converted?.Dispose();
        }
    }

    private static async Task<byte[]> EncodeJpegAsync(SoftwareBitmap bitmap, double quality, int maxWidth)
    {
        // Avoid an extra BGRA copy when capture already produced Bgra8; Convert owns a new bitmap otherwise.
        SoftwareBitmap? converted = null;
        SoftwareBitmap bgra = bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8
            ? bitmap
            : (converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8));

        try
        {
            int width = bgra.PixelWidth;
            int height = bgra.PixelHeight;
            maxWidth = Math.Clamp(maxWidth, 640, 1920);

            using var stream = new InMemoryRandomAccessStream();
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
            encoder.SetSoftwareBitmap(bgra);
            if (width > maxWidth)
            {
                encoder.BitmapTransform.ScaledWidth = (uint)maxWidth;
                encoder.BitmapTransform.ScaledHeight = (uint)Math.Max(2, (height * maxWidth / width) & ~1);
                encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Linear;
            }

            var propertySet = new BitmapPropertySet
            {
                {
                    "ImageQuality",
                    new BitmapTypedValue((float)quality, global::Windows.Foundation.PropertyType.Single)
                }
            };
            await encoder.BitmapProperties.SetPropertiesAsync(propertySet);
            await encoder.FlushAsync();

            stream.Seek(0);
            byte[] bytes = new byte[stream.Size];
            using Stream netStream = stream.AsStreamForRead();
            await netStream.ReadExactlyAsync(bytes);
            return bytes;
        }
        finally
        {
            converted?.Dispose();
        }
    }

    private static bool IsSocketAlive(TcpClient client)
    {
        try
        {
            Socket socket = client.Client;
            return socket.Connected && !(socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> GetLikelyLanIPv4()
    {
        var wifi = new List<string>();
        var other = new List<string>();

        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            bool isWifi = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
                || nic.Description.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase)
                || nic.Description.Contains("Wireless", StringComparison.OrdinalIgnoreCase)
                || nic.Name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase);

            foreach (UnicastIPAddressInformation addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                string ip = addr.Address.ToString();
                if (ip.StartsWith("169.254.", StringComparison.Ordinal))
                {
                    continue;
                }

                if (isWifi)
                {
                    wifi.Add(ip);
                }
                else
                {
                    other.Add(ip);
                }
            }
        }

        // Prefer Wi‑Fi first so the SHARE panel shows the address the other PC needs.
        foreach (string ip in wifi.Concat(other).Distinct(StringComparer.Ordinal))
        {
            yield return ip;
        }
    }

    private void RaiseChanged()
    {
        if (_dispatcher.CheckAccess())
        {
            Changed?.Invoke();
        }
        else
        {
            _ = _dispatcher.BeginInvoke(() => Changed?.Invoke());
        }
    }
}
