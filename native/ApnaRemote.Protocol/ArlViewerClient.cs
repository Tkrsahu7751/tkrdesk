using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using ApnaRemote.Core;

namespace ApnaRemote.Protocol;

public enum ArlViewerPhase
{
    Idle,
    Connecting,
    WaitingApproval,
    WaitingFrame,
    Viewing,
    Controlling,
    Ended,
}

public sealed record ArlViewerTimeouts(TimeSpan Connect, TimeSpan Approval, TimeSpan Read, TimeSpan Write, TimeSpan Heartbeat)
{
    public static ArlViewerTimeouts Default { get; } = new(
        TimeSpan.FromSeconds(8), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2));
}

/// <summary>
/// UI-agnostic ARL1 TLS viewer. Delivers JPEG frames via <see cref="FrameReceived"/>; callers marshal to their UI thread.
/// </summary>
public sealed class ArlViewerClient : IDisposable
{
    private readonly ArlViewerTimeouts _timeouts;
    private readonly string _peerDisplayName;
    private Attempt? _attempt;
    private bool _disposed;
    private readonly ArlViewerHeldInput _held = new();
    private SynchronizationContext? _sync;

    public event Action? Changed;
    public event Action<byte[]>? FrameReceived;

    public ArlViewerPhase Phase { get; private set; }
    public bool IsBusy => _attempt is not null;
    public bool IsConnected => Phase is ArlViewerPhase.Viewing or ArlViewerPhase.Controlling;
    public bool ControlEnabled => Phase == ArlViewerPhase.Controlling;
    public bool ControlPaused { get; private set; }
    public string Status { get; private set; } = "Not connected.";
    public long FramesReceived { get; private set; }
    public long BytesReceived { get; private set; }
    public int ReceiveFps { get; private set; }
    public int ReceiveKbps { get; private set; }
    public int RemoteWidth { get; private set; }
    public int RemoteHeight { get; private set; }
    public Task Completion { get; private set; } = Task.CompletedTask;

    public ArlViewerClient(ArlViewerTimeouts? timeouts = null, string? peerDisplayName = null)
    {
        _timeouts = timeouts ?? ArlViewerTimeouts.Default;
        _peerDisplayName = string.IsNullOrWhiteSpace(peerDisplayName)
            ? Environment.MachineName
            : peerDisplayName.Trim();
    }

    /// <summary>Optional: marshal Changed/FrameReceived onto this context (e.g. UI thread).</summary>
    public void SetSynchronizationContext(SynchronizationContext? context) => _sync = context;

    public void Connect(string host, int port, string pin, string fingerprint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsBusy) return;
        host = host.Trim();
        pin = pin.Trim();
        fingerprint = fingerprint.Trim();
        if (string.IsNullOrWhiteSpace(host)) { SetStatus("Enter the host PC address."); return; }
        if (port is < 1 or > 65535) { SetStatus("Port must be between 1 and 65535 (normally 5720)."); return; }
        if (pin.Length != 6 || pin.Any(c => c is < '0' or > '9'))
        {
            SetStatus("Enter the 6-digit PIN shown on the host.");
            return;
        }
        if (!LanTls.IsValidFingerprintInput(fingerprint))
        {
            SetStatus("Enter the host fingerprint (XXXX-XXXX-XXXX-XXXX) shown on the host.");
            return;
        }

        var attempt = new Attempt();
        _attempt = attempt;
        ClearMedia();
        Phase = ArlViewerPhase.Connecting;
        SetStatus($"Connecting to {host}:{port}… (wrong IP will fail in a few seconds)");
        Completion = Task.Run(() => RunAsync(attempt, host, port, pin, fingerprint));
    }

    public void Disconnect() => Disconnect("Disconnected. Connect again for a new host approval.");

    public void Disconnect(string reason)
    {
        Attempt? attempt = _attempt;
        _attempt = null;
        attempt?.Cancel();
        ClearMedia();
        Phase = ArlViewerPhase.Ended;
        SetStatus(reason);
    }

    public bool TrySendPointerMove(double x, double y, bool force = false)
    {
        if (_attempt is not { } attempt || !ControlEnabled) return false;
        long now = Environment.TickCount64;
        if (!force && now - attempt.LastMoveTick < 33) return false;
        attempt.LastMoveTick = now;
        return TrySend(InputKind.PointerMove, x: x, y: y);
    }

    public bool TrySendButton(PointerButton button, bool down)
        => TrySend(down ? InputKind.ButtonDown : InputKind.ButtonUp, code: (int)button);
    public bool TrySendKey(int virtualKey, bool down)
        => TrySend(down ? InputKind.KeyDown : InputKind.KeyUp, code: virtualKey);
    public bool TrySendScroll(int delta)
        => delta != 0 && TrySend(InputKind.Scroll, delta: Math.Clamp(delta, -1200, 1200));
    public bool TrySendText(string text)
        => !string.IsNullOrEmpty(text) && TrySend(InputKind.Text, text: text);

    public void ReleaseHeldInput()
    {
        foreach (HeldInput held in _held.TakeAll())
        {
            if (!ControlEnabled) break;
            _ = TrySend(held.DownKind == InputKind.KeyDown ? InputKind.KeyUp : InputKind.ButtonUp, code: held.Code);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }

    private bool TrySend(InputKind kind, int code = 0, double x = 0, double y = 0, int delta = 0, string? text = null)
    {
        if (_attempt is not { } attempt || !ControlEnabled || attempt.Token.IsCancellationRequested) return false;
        var command = new InputCommand(InputValidation.ProtocolVersion, attempt.SessionId, attempt.Epoch,
            ++attempt.Sequence, kind, X: x, Y: y, Code: code, Delta: delta, Text: text);
        if (!InputValidation.IsValid(command)) return false;
        if (attempt.Input.Writer.TryWrite(command)) { _held.Sent(kind, code); return true; }
        Disconnect("Input queue filled — session stopped. Reconnect when the network is stable.");
        return false;
    }

    private async Task RunAsync(Attempt attempt, string host, int port, string pin, string fingerprint)
    {
        string reason = "Host ended the share.";
        Task input = Task.CompletedTask, heartbeat = Task.CompletedTask;
        try
        {
            using (var connect = Deadline(attempt.Token, _timeouts.Connect))
            {
                try { await attempt.Client.ConnectAsync(host, port, connect.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!attempt.Token.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        "Wrong or unreachable address. Copy the host IP from the Share panel (same Wi‑Fi). Also check Start sharing and firewall.");
                }
            }
            attempt.Client.NoDelay = true;
            await using SslStream stream = await LanTls.AuthenticateAsClientAsync(
                attempt.Client.GetStream(), fingerprint, attempt.Token).ConfigureAwait(false);
            if (stream.RemoteCertificate is null)
                throw new AuthenticationException("Host did not present a TLS certificate.");
            using var remoteCert = new X509Certificate2(stream.RemoteCertificate);
            byte[] pinProof = LanTls.ComputePinProof(pin, remoteCert);
            string peerId = _peerDisplayName + "-" + Guid.NewGuid().ToString("N")[..8];
            await WriteAsync(attempt, stream, ArlProtocol.MessageType.HelloViewer,
                ArlProtocol.EncodeHelloViewer(peerId, _peerDisplayName, pinProof)).ConfigureAwait(false);
            await OnUiAsync(attempt, () => SetStatus("TLS OK — proving PIN…")).ConfigureAwait(false);

            bool approved = false;
            using (var approval = Deadline(attempt.Token, _timeouts.Approval))
            {
                while (!approved)
                {
                    (ArlProtocol.MessageType Type, byte[] Payload) message;
                    try { message = await ArlProtocol.ReadMessageAsync(stream, approval.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!attempt.Token.IsCancellationRequested)
                    { throw new TimeoutException("Host approval timed out. Connect again and approve on the host."); }
                    switch (message.Type)
                    {
                        case ArlProtocol.MessageType.HelloOk:
                            if (attempt.SessionId != Guid.Empty) throw new InvalidDataException("Repeated handshake.");
                            attempt.SessionId = ArlProtocol.DecodeGuid(message.Payload);
                            if (attempt.SessionId == Guid.Empty) throw new InvalidDataException("Missing session identity.");
                            break;
                        case ArlProtocol.MessageType.WaitingApproval:
                            if (attempt.SessionId == Guid.Empty) throw new InvalidDataException("Approval request before handshake.");
                            await OnUiAsync(attempt, () =>
                            {
                                Phase = ArlViewerPhase.WaitingApproval;
                                SetStatus("Waiting for host approval and display selection…");
                            }).ConfigureAwait(false);
                            break;
                        case ArlProtocol.MessageType.Approved:
                            if (attempt.SessionId == Guid.Empty) throw new InvalidDataException("Approval before handshake.");
                            var info = ArlProtocol.DecodeApproved(message.Payload);
                            if (info.Width is < 1 or > 16384 || info.Height is < 1 or > 16384 ||
                                (long)info.Width * info.Height > 33_554_432)
                                throw new InvalidDataException("Host display dimensions are unsupported.");
                            if (info.StreamCodec == ArlProtocol.StreamCodecH264)
                                throw new InvalidDataException("Host is streaming H.264. This phone build is JPEG-only — set host Settings → Video to JPEG, or use the Windows viewer.");
                            attempt.Epoch = info.PermissionEpoch;
                            attempt.AllowControl = info.AllowControl;
                            approved = true;
                            await OnUiAsync(attempt, () =>
                            {
                                RemoteWidth = info.Width; RemoteHeight = info.Height;
                                Phase = ArlViewerPhase.WaitingFrame;
                                SetStatus("Host approved — waiting for the first screen frame…");
                            }).ConfigureAwait(false);
                            break;
                        case ArlProtocol.MessageType.HelloReject:
                        case ArlProtocol.MessageType.Rejected:
                            reason = FormatHostDecision(ArlProtocol.DecodeString(message.Payload));
                            return;
                        case ArlProtocol.MessageType.End:
                            reason = "Host ended before approval.";
                            return;
                        default:
                            throw new InvalidDataException("Unexpected message before host approval.");
                    }
                }
            }

            input = SendInputAsync(attempt, stream);
            heartbeat = SendHeartbeatsAsync(attempt, stream);
            int frameCounter = 0, fps = 0, kbps = 0;
            long frames = 0, bytes = 0, windowBytes = 0, lastFpsTick = Environment.TickCount64;
            long firstFrameDeadline = Environment.TickCount64 + (long)_timeouts.Read.TotalMilliseconds;
            bool firstFrame = true;
            while (true)
            {
                TimeSpan readBudget = firstFrame
                    ? TimeSpan.FromMilliseconds(Math.Max(1, firstFrameDeadline - Environment.TickCount64))
                    : _timeouts.Read;
                using var read = Deadline(attempt.Token, readBudget);
                (ArlProtocol.MessageType Type, byte[] Payload) message;
                try { message = await ArlProtocol.ReadMessageAsync(stream, read.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!attempt.Token.IsCancellationRequested)
                { throw new TimeoutException(firstFrame ? "No screen frame arrived after approval. Ask the host to share again." : "Host stopped responding — check Wi-Fi and reconnect."); }
                if (firstFrame && Environment.TickCount64 >= firstFrameDeadline)
                    throw new TimeoutException("No screen frame arrived after approval. Ask the host to share again.");

                if (message.Type == ArlProtocol.MessageType.End) return;
                if (message.Type == ArlProtocol.MessageType.Heartbeat) continue;
                if (message.Type == ArlProtocol.MessageType.PermissionChanged)
                {
                    var update = ArlProtocol.DecodePermission(message.Payload);
                    if (update.Epoch <= attempt.Epoch) throw new InvalidDataException("Stale permission update.");
                    await OnUiAsync(attempt, () =>
                    {
                        _held.Clear();
                        attempt.Epoch = update.Epoch;
                        attempt.Sequence = 0;
                        attempt.AllowControl = false;
                        ControlPaused = update.Paused;
                        Phase = frames == 0 ? ArlViewerPhase.WaitingFrame : ArlViewerPhase.Viewing;
                        SetStatus(update.Paused ? "Host paused control. Screen viewing continues." : "Host changed permissions.");
                    }).ConfigureAwait(false);
                    await WriteAsync(attempt, stream, ArlProtocol.MessageType.PermissionAck,
                        ArlProtocol.EncodePermission(update.Epoch, update.Control, update.Paused)).ConfigureAwait(false);
                    await OnUiAsync(attempt, () =>
                    {
                        attempt.AllowControl = update.Control;
                        if (frames > 0) Phase = update.Control ? ArlViewerPhase.Controlling : ArlViewerPhase.Viewing;
                        SetStatus(update.Control ? "Host allowed control." :
                            update.Paused ? "Control paused by host; screen is still visible." : "Control revoked by host. View-only continues.");
                    }).ConfigureAwait(false);
                    continue;
                }
                if (message.Type == ArlProtocol.MessageType.FrameH264)
                    throw new InvalidDataException("Host sent H.264. This phone build is JPEG-only — set host Settings → Video to JPEG.");
                if (message.Type != ArlProtocol.MessageType.FrameJpeg)
                    throw new InvalidDataException("Unexpected message during viewing.");

                ValidateJpegHeader(message.Payload);
                firstFrame = false;
                frames++; frameCounter++;
                bytes += message.Payload.Length;
                windowBytes += message.Payload.Length;
                long now = Environment.TickCount64, elapsed = now - lastFpsTick;
                if (elapsed >= 1000)
                {
                    fps = (int)Math.Round(frameCounter * 1000.0 / elapsed);
                    kbps = (int)Math.Round(windowBytes * 8.0 / elapsed);
                    frameCounter = 0; windowBytes = 0; lastFpsTick = now;
                }

                byte[] jpegCopy = message.Payload;
                await OnUiAsync(attempt, () =>
                {
                    FramesReceived = frames;
                    BytesReceived = bytes;
                    ReceiveFps = fps;
                    ReceiveKbps = kbps;
                    Phase = attempt.AllowControl ? ArlViewerPhase.Controlling : ArlViewerPhase.Viewing;
                    SetStatus(attempt.AllowControl
                        ? "Host approved control. TLS LAN lab."
                        : ControlPaused ? "Control paused by host; screen is still visible." : "Live view-only. TLS LAN lab.");
                    FrameReceived?.Invoke(jpegCopy);
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { reason = attempt.Failure ?? "Disconnected."; }
        catch (TimeoutException ex) { reason = ex.Message; }
        catch (SocketException ex) { reason = $"Wrong or unreachable address ({ex.SocketErrorCode}). Copy the host IP from Share, same Wi‑Fi, Start sharing, firewall."; }
        catch (EndOfStreamException) { reason = "Host connection closed. Connect again for a new approval."; }
        catch (AuthenticationException ex) { reason = ex.Message; }
        catch (Exception ex) { reason = FriendlyStopReason(ex); }
        finally
        {
            attempt.Cancel();
            await Task.WhenAll(input, heartbeat).ConfigureAwait(false);
            string finalReason = attempt.Failure ?? reason;
            await OnUiAsync(attempt, () =>
            {
                _attempt = null;
                ClearMedia();
                Phase = ArlViewerPhase.Ended;
                SetStatus(finalReason);
            }, allowCancelled: true).ConfigureAwait(false);
            attempt.Dispose();
        }
    }

    private static string FormatHostDecision(string raw)
    {
        raw = (raw ?? "").Trim();
        if (raw.Contains("Wrong PIN", StringComparison.OrdinalIgnoreCase))
            return "Wrong PIN. Copy the 6-digit PIN from the host Share panel (or Scan nearby again), then Connect.";
        if (raw.Contains("stopped receiving", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("Sharing stopped", StringComparison.OrdinalIgnoreCase))
            return "Host stopped sharing. Ask them to Start sharing again, then Connect.";
        if (raw.Equals("Host rejected.", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("Host rejected", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("Connection declined.", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("Connection declined", StringComparison.OrdinalIgnoreCase))
            return "Host pressed Reject. Connect again when they are ready to Accept.";
        if (raw.Length == 0) return "Host declined the connection.";
        if (raw.StartsWith("Wrong ", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("Host ", StringComparison.OrdinalIgnoreCase))
            return raw;
        return "Host: " + raw;
    }

    private static string FriendlyStopReason(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            string m = e.Message;
            if (m.Contains("RemoteCertificateValidationCallback", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("certificate was rejected", StringComparison.OrdinalIgnoreCase))
            {
                return "Wrong fingerprint. Open the host Share panel, copy the Fingerprint exactly (or Scan nearby again), then Connect. If the host clicked Stop/Start sharing, PIN and fingerprint both changed.";
            }
        }

        return "Viewer stopped: " + ex.Message;
    }

    private async Task SendInputAsync(Attempt attempt, Stream stream)
    {
        try
        {
            await foreach (InputCommand command in attempt.Input.Reader.ReadAllAsync(attempt.Token).ConfigureAwait(false))
            {
                if (command.PermissionEpoch != Volatile.Read(ref attempt.Epoch)) continue;
                await WriteAsync(attempt, stream, ArlProtocol.MessageType.InputControl, ProtocolFraming.Encode(command)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (attempt.Token.IsCancellationRequested) { }
        catch (Exception) { attempt.Fail("Control channel stopped responding. Session ended to release input."); }
    }

    private async Task SendHeartbeatsAsync(Attempt attempt, Stream stream)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_timeouts.Heartbeat, attempt.Token).ConfigureAwait(false);
                await WriteAsync(attempt, stream, ArlProtocol.MessageType.Heartbeat, ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (attempt.Token.IsCancellationRequested) { }
        catch (Exception) { attempt.Fail("Heartbeat could not reach the host. Check Wi-Fi and reconnect."); }
    }

    private async Task WriteAsync(Attempt attempt, Stream stream, ArlProtocol.MessageType type, ReadOnlyMemory<byte> payload)
    {
        using var deadline = Deadline(attempt.Token, _timeouts.Write);
        await attempt.Writer.WaitAsync(deadline.Token).ConfigureAwait(false);
        try { await ArlProtocol.WriteMessageAsync(stream, type, payload, deadline.Token).ConfigureAwait(false); }
        finally { attempt.Writer.Release(); }
    }

    private Task OnUiAsync(Attempt attempt, Action action, bool allowCancelled = false)
    {
        if (!allowCancelled && attempt.Token.IsCancellationRequested && !ReferenceEquals(_attempt, attempt))
            return Task.CompletedTask;

        void Run()
        {
            if (ReferenceEquals(_attempt, attempt) && (allowCancelled || !attempt.Token.IsCancellationRequested))
                action();
        }

        if (_sync is null)
        {
            Run();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sync.Post(_ =>
        {
            try { Run(); tcs.TrySetResult(); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }, null);
        return tcs.Task;
    }

    private static void ValidateJpegHeader(byte[] jpeg)
    {
        if (jpeg.Length < 3 || jpeg[0] != 0xff || jpeg[1] != 0xd8 || jpeg[2] != 0xff)
            throw new InvalidDataException("Invalid JPEG frame.");
    }

    private static CancellationTokenSource Deadline(CancellationToken token, TimeSpan duration)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(duration);
        return cts;
    }

    private void ClearMedia()
    {
        _held.Clear();
        ControlPaused = false;
        FramesReceived = 0; BytesReceived = 0; ReceiveFps = 0; ReceiveKbps = 0;
        RemoteWidth = 0; RemoteHeight = 0;
    }

    private void SetStatus(string status) { Status = status; Changed?.Invoke(); }

    private sealed class Attempt : IDisposable
    {
        public readonly TcpClient Client = new();
        private readonly CancellationTokenSource _cts = new();
        public CancellationToken Token { get; }
        public readonly SemaphoreSlim Writer = new(1, 1);
        public readonly Channel<InputCommand> Input = Channel.CreateBounded<InputCommand>(new BoundedChannelOptions(128)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        public Guid SessionId;
        public int Epoch;
        public bool AllowControl;
        public long Sequence, LastMoveTick;
        public string? Failure;
        public Attempt() => Token = _cts.Token;
        public void Fail(string reason) { Interlocked.CompareExchange(ref Failure, reason, null); Cancel(); }
        public void Cancel() { _cts.Cancel(); Input.Writer.TryComplete(); Client.Dispose(); }
        public void Dispose() { Client.Dispose(); Writer.Dispose(); _cts.Dispose(); }
    }
}

internal sealed class ArlViewerHeldInput
{
    private readonly HashSet<HeldInput> _held = [];

    public void Sent(InputKind kind, int code)
    {
        if (kind is InputKind.KeyDown or InputKind.ButtonDown) _held.Add(new(kind, code));
        if (kind is InputKind.KeyUp) _held.Remove(new(InputKind.KeyDown, code));
        if (kind is InputKind.ButtonUp) _held.Remove(new(InputKind.ButtonDown, code));
    }

    public HeldInput[] TakeAll()
    {
        var copy = _held.ToArray();
        _held.Clear();
        return copy;
    }

    public void Clear() => _held.Clear();
}
