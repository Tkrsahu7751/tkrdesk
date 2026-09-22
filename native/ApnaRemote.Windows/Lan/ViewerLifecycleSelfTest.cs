using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ApnaRemote.Core;
using ApnaRemote.Protocol;
using ApnaRemote.Windows.Input;

namespace ApnaRemote.Windows.Lan;

/// <summary>Actual viewer + WPF dispatcher + synthetic loopback peer. No host adapter, capture or OS input.</summary>
internal static class ViewerLifecycleSelfTest
{
    private static readonly ViewerTimeouts Normal = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(80));

    public static async Task<int> RunAsync(Dispatcher dispatcher)
    {
        int passed = 0;
        async Task Check(string name, Func<Task> test)
        {
            await test();
            passed++;
            Console.WriteLine("PASS " + name);
        }

        try
        {
            await Check("TLS handshake timeout is configured for viewer path", () =>
            {
                Require(LanTls.HandshakeTimeout > TimeSpan.Zero && LanTls.HandshakeTimeout <= TimeSpan.FromSeconds(30),
                    "handshake timeout missing");
                return Task.CompletedTask;
            });

            await Check("same-size monitors do not substitute for the selected identity", () =>
            {
                var selected = new CaptureMonitor(new IntPtr(1), "DISPLAY1", new(-1920, 0, 1920, 1080), false);
                var other = new CaptureMonitor(new IntPtr(2), "DISPLAY2", new(0, 0, 1920, 1080), true);
                Require(!CaptureDisplayBounds.Matches(selected, [other]), "Matched by size instead of identity");
                Require(!CaptureDisplayBounds.Matches(selected, [selected with { Bounds = new(0, 0, 1920, 1080) }]), "Moved monitor accepted");
                Require(CaptureDisplayBounds.Matches(selected, [other, selected]), "Exact monitor missing");
                return Task.CompletedTask;
            });

            await Check("legacy viewer handshake is rejected before consent", () =>
            {
                using var cert = LanTls.CreateEphemeral(out _);
                byte[] proof = LanTls.ComputePinProof("123456", cert);
                byte[] hello = ArlProtocol.EncodeHelloViewer("peer", "PC", proof);
                bool rejected = false;
                try { ArlProtocol.DecodeHelloViewer(hello.AsSpan(0, hello.Length - 4)); }
                catch (InvalidOperationException ex) { rejected = ex.Message.Contains("Incompatible lab version", StringComparison.Ordinal); }
                Require(rejected, "Old viewer could bypass revision requirement");
                return Task.CompletedTask;
            });

            await Check("PIN proof binds to certificate and rejects wrong PIN", () =>
            {
                using var cert = LanTls.CreateEphemeral(out _);
                using var other = LanTls.CreateEphemeral(out _);
                byte[] good = LanTls.ComputePinProof("654321", cert);
                Require(LanTls.VerifyPinProof("654321", cert, good), "Matching PIN/cert rejected");
                Require(!LanTls.VerifyPinProof("000000", cert, good), "Wrong PIN accepted");
                Require(!LanTls.VerifyPinProof("654321", other, good), "Proof accepted on different cert");
                Require(good.Length == LanTls.PinProofBytes, "Proof size wrong");
                return Task.CompletedTask;
            });

            await Check("local revoke releases held input; regrant requires matching ack", () =>
            {
                var sink = new RecordingInput();
                var gate = new HostSessionGate(sink); gate.EnableReceiving();
                Guid id = gate.RequestFromAuthenticatedPeer(new("peer", "PC"));
                Require(gate.ApproveLocally(id, "display", new(-1920, 0, 1920, 1080), true), "Approval failed");
                var state = new HostControlState(gate);
                InputCommand Key(int epoch, long sequence) => new(1, id, epoch, sequence, InputKind.KeyDown, Code: 0x41);
                Require(state.Apply("peer", Key(1, 1)).Accepted, "Initial input rejected");
                Require(state.Change(false) && sink.Released == 1, "Revoke did not release the held key");
                Require(gate.CanShareFrames(id, "peer") && !state.Apply("peer", Key(1, 2)).Accepted, "Revoke failed to block old input");
                Require(state.Change(true), "Regrant failed");
                Require(!state.Apply("peer", Key(3, 1)).Accepted, "Input allowed before ack");
                state.Acknowledge(2, true, false);
                state.Acknowledge(3, false, false);
                Require(!state.Apply("peer", Key(3, 1)).Accepted, "Stale/mismatched ack granted input");
                state.Acknowledge(3, true, false);
                Require(state.Apply("peer", Key(3, 1)).Accepted, "Current ack not accepted");
                state.Pause();
                Require(sink.Released == 2 && gate.CanShareFrames(id, "peer") && !state.Apply("peer", Key(3, 2)).Accepted,
                    "Pause failed to release input while preserving viewing");
                return Task.CompletedTask;
            });

            await Check("legacy host cannot silently accept a new viewer", async () =>
            {
                using var peer = new Peer(); using var viewer = new LanViewerSession(dispatcher, Normal);
                viewer.Connect("127.0.0.1", peer.Port, "123456", peer.Fingerprint); await peer.AcceptAsync();
                await peer.SendAsync(ArlProtocol.MessageType.HelloOk, peer.SessionId.ToByteArray());
                await viewer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Require(!viewer.IsConnected && viewer.Status.Contains("Incompatible lab version", StringComparison.Ordinal), "Legacy host silently accepted");
            });

            await Check("focus release queues key and button ups once, in order", async () =>
            {
                using var peer = new Peer(); using var viewer = new LanViewerSession(dispatcher, Normal);
                viewer.Connect("127.0.0.1", peer.Port, "123456", peer.Fingerprint); await peer.AcceptAsync(); await peer.HelloAsync();
                await peer.ApproveAsync(true); await peer.SendAsync(ArlProtocol.MessageType.FrameJpeg, Jpeg());
                await Until(() => viewer.ControlEnabled);
                Require(viewer.TrySendKey(0x11, true) && viewer.TrySendButton(PointerButton.Left, true), "Down enqueue failed");
                viewer.ReleaseHeldInput(); viewer.ReleaseHeldInput();
                var commands = new List<InputCommand>();
                while (commands.Count < 4)
                {
                    var message = await peer.ReadAsync();
                    if (message.Type == ArlProtocol.MessageType.InputControl)
                        commands.Add(await ProtocolFraming.ReadAsync(new MemoryStream(message.Payload), CancellationToken.None));
                }
                Require(commands[0].Kind == InputKind.KeyDown && commands[1].Kind == InputKind.ButtonDown &&
                    commands.Skip(2).Select(c => c.Kind).ToHashSet().SetEquals([InputKind.KeyUp, InputKind.ButtonUp]) &&
                    commands.Select(c => c.Sequence).SequenceEqual(new long[] { 1, 2, 3, 4 }), "Release order/sequence incorrect");
                var ledger = new ViewerHeldInput(); ledger.Sent(InputKind.KeyDown, 0x11); ledger.Sent(InputKind.KeyDown, 0x11);
                Require(ledger.TakeAll().Length == 1 && ledger.TakeAll().Length == 0, "Repeated releases or repeat-down duplicated held state");
                viewer.Disconnect(); await viewer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            });

            await Check("viewer acknowledges revoke/pause and resumes only in the new epoch", async () =>
            {
                using var peer = new Peer(); using var viewer = new LanViewerSession(dispatcher, Normal);
                viewer.Connect("127.0.0.1", peer.Port, "123456", peer.Fingerprint); await peer.AcceptAsync(); await peer.HelloAsync();
                await peer.ApproveAsync(true); await peer.SendAsync(ArlProtocol.MessageType.FrameJpeg, Jpeg());
                await Until(() => viewer.ControlEnabled);
                await peer.SendAsync(ArlProtocol.MessageType.PermissionChanged, ArlProtocol.EncodePermission(2, false));
                await Until(() => !viewer.ControlEnabled);
                (ArlProtocol.MessageType Type, byte[] Payload) ack;
                do { ack = await peer.ReadAsync(); } while (ack.Type != ArlProtocol.MessageType.PermissionAck);
                Require(ArlProtocol.DecodePermission(ack.Payload).Epoch == 2 && !viewer.TrySendKey(0x41, true) && viewer.RemoteImage is not null, "Revoke was not acknowledged/view-only");
                await peer.SendAsync(ArlProtocol.MessageType.PermissionChanged, ArlProtocol.EncodePermission(3, true));
                await Until(() => viewer.ControlEnabled);
                Require(viewer.TrySendKey(0x41, true), "Regrant input enqueue failed");
                do { ack = await peer.ReadAsync(); } while (ack.Type != ArlProtocol.MessageType.InputControl);
                var command = await ProtocolFraming.ReadAsync(new MemoryStream(ack.Payload), CancellationToken.None);
                Require(command.PermissionEpoch == 3 && command.Sequence == 1, "Queued old epoch or stale sequence survived");
                await peer.SendAsync(ArlProtocol.MessageType.PermissionChanged, ArlProtocol.EncodePermission(4, false, true));
                await Until(() => viewer.ControlPaused && !viewer.ControlEnabled);
                Require(viewer.IsConnected && viewer.RemoteImage is not null, "Pause unexpectedly hid approved viewing");
                await peer.SendAsync(ArlProtocol.MessageType.PermissionChanged, ArlProtocol.EncodePermission(4, true));
                await viewer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Require(!viewer.IsBusy && viewer.Status.Contains("Stale permission"), "Repeated epoch was accepted");
            });

            await Check("invalid PIN/port never starts a connection", () =>
            {
                using var viewer = new LanViewerSession(dispatcher, Normal);
                viewer.Connect("127.0.0.1", 5720, "123a56", "AAAA-BBBB-CCCC-DDDD");
                Require(!viewer.IsBusy && !viewer.IsConnected, "Invalid PIN started connection");
                viewer.Connect("127.0.0.1", 0, "123456", "AAAA-BBBB-CCCC-DDDD");
                Require(!viewer.IsBusy && viewer.Status.Contains("Port"), "Invalid port accepted");
                viewer.Connect("127.0.0.1", 5720, "123456", "not-a-fingerprint");
                Require(!viewer.IsBusy && viewer.Status.Contains("fingerprint", StringComparison.OrdinalIgnoreCase), "Bad fingerprint accepted");
                return Task.CompletedTask;
            });

            await Check("wrong TLS fingerprint is rejected", async () =>
            {
                using var peer = new Peer();
                using var viewer = new LanViewerSession(dispatcher, Normal);
                viewer.Connect("127.0.0.1", peer.Port, "123456", "DEAD-BEEF-CAFE-F00D");
                try { await peer.AcceptAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch { /* expected */ }
                await viewer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Require(!viewer.IsConnected && !viewer.IsBusy, "Wrong fingerprint connected");
                Require(viewer.Status.Contains("fingerprint", StringComparison.OrdinalIgnoreCase),
                    "Wrong fingerprint message unclear: " + viewer.Status);
            });

            await Check("approval is not LIVE; first decoded frame enables ordered control; End clears media", async () =>
            {
                using var peer = new Peer();
                using var viewer = new LanViewerSession(dispatcher, Normal);
                viewer.Connect("127.0.0.1", peer.Port, "123456", peer.Fingerprint);
                Require(viewer.IsBusy && !viewer.IsConnected && !viewer.ControlEnabled, "Premature LIVE");
                await peer.AcceptAsync();
                await peer.HelloAsync();
                await Until(() => viewer.Phase == ViewerPhase.WaitingApproval);
                Require(!viewer.TrySendKey(0x41, true), "Input before approval");
                await peer.ApproveAsync(control: true);
                await Until(() => viewer.Phase == ViewerPhase.WaitingFrame);
                Require(!viewer.IsConnected && !viewer.ControlEnabled, "Approval claimed first frame");
                // Independent heartbeat must arrive without any JPEG from the host.
                var hb = await peer.ReadAsync();
                Require(hb.Type == ArlProtocol.MessageType.Heartbeat, "Heartbeat depended on frames");
                await peer.SendAsync(ArlProtocol.MessageType.FrameJpeg, Jpeg());
                await Until(() => viewer.IsConnected);
                Require(viewer.ControlEnabled && viewer.RemoteImage?.IsFrozen == true, "Missing decoded/frozen frame");
                Require(viewer.TrySendKey(0x41, true) && viewer.TrySendKey(0x41, false), "Input enqueue failed");
                var commands = new List<InputCommand>();
                while (commands.Count < 2)
                {
                    var message = await peer.ReadAsync();
                    if (message.Type != ArlProtocol.MessageType.InputControl) continue;
                    commands.Add(await ProtocolFraming.ReadAsync(new MemoryStream(message.Payload), CancellationToken.None));
                }
                Require(commands[0].Kind == InputKind.KeyDown && commands[1].Kind == InputKind.KeyUp &&
                    commands[0].Sequence < commands[1].Sequence && commands.All(c => c.SessionId == peer.SessionId),
                    "Input order or session identity corrupted");
                await peer.SendAsync(ArlProtocol.MessageType.End, []);
                await viewer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Require(!viewer.IsBusy && !viewer.ControlEnabled && viewer.RemoteImage is null && viewer.RemoteWidth == 0,
                    "End left media/permission behind");
            });

            await Check("view-only session refuses input", async () =>
            {
                using var peer = new Peer();
                using var viewer = new LanViewerSession(dispatcher, Normal);
                viewer.Connect("127.0.0.1", peer.Port, "123456", peer.Fingerprint);
                await peer.AcceptAsync(); await peer.HelloAsync(); await peer.ApproveAsync(false);
                await peer.SendAsync(ArlProtocol.MessageType.FrameJpeg, Jpeg());
                await Until(() => viewer.IsConnected);
                Require(!viewer.ControlEnabled && !viewer.TrySendKey(0x41, true), "View-only accepted input");
                viewer.Disconnect(); await viewer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            });

            await Check("host rejection preserves reason and permits retry", async () =>
            {
                using var peer = new Peer();
                using var viewer = new LanViewerSession(dispatcher, Normal);
                viewer.Connect("127.0.0.1", peer.Port, "123456", peer.Fingerprint); await peer.AcceptAsync();
                await peer.SendAsync(ArlProtocol.MessageType.HelloReject, ArlProtocol.EncodeString("Wrong PIN."));
                await viewer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Require(!viewer.IsBusy && !viewer.IsConnected && viewer.Status.Contains("Wrong PIN"), "Rejection lost");
            });

            await Check("approval timeout closes the connection", async () =>
            {
                using var peer = new Peer();
                using var viewer = new LanViewerSession(dispatcher, Normal with { Approval = TimeSpan.FromMilliseconds(250) });
                viewer.Connect("127.0.0.1", peer.Port, "123456", peer.Fingerprint); await peer.AcceptAsync(); await peer.HelloAsync();
                await viewer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Require(!viewer.IsBusy && viewer.Status.Contains("approval timed out"), "Approval waited forever");
            });

            await Check("frames before approval cannot become LIVE", async () =>
            {
                using var peer = new Peer();
                using var viewer = new LanViewerSession(dispatcher, Normal);
                viewer.Connect("127.0.0.1", peer.Port, "123456", peer.Fingerprint); await peer.AcceptAsync();
                await peer.SendAsync(ArlProtocol.MessageType.FrameJpeg, Jpeg());
                await viewer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Require(!viewer.IsConnected && viewer.RemoteImage is null && viewer.Status.Contains("before host approval"), "Preapproval frame accepted");
            });

            await Check("heartbeats cannot postpone the first-frame deadline", async () =>
            {
                using var peer = new Peer();
                using var viewer = new LanViewerSession(dispatcher, Normal with { Read = TimeSpan.FromMilliseconds(300) });
                viewer.Connect("127.0.0.1", peer.Port, "123456", peer.Fingerprint); await peer.AcceptAsync();
                await peer.HelloAsync(); await peer.ApproveAsync(false);
                for (int i = 0; i < 4; i++)
                {
                    await peer.SendAsync(ArlProtocol.MessageType.Heartbeat, []);
                    await Task.Delay(45);
                }
                await viewer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Require(!viewer.IsConnected && viewer.Status.Contains("No screen frame"), "False live after empty approval");
            });

            await Check("corrupt JPEG ends with no live image", async () =>
            {
                using var peer = new Peer();
                using var viewer = new LanViewerSession(dispatcher, Normal);
                viewer.Connect("127.0.0.1", peer.Port, "123456", peer.Fingerprint); await peer.AcceptAsync();
                await peer.HelloAsync(); await peer.ApproveAsync(false);
                await peer.SendAsync(ArlProtocol.MessageType.FrameJpeg, [1, 2, 3]);
                await viewer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Require(viewer.RemoteImage is null && viewer.Status.Contains("Invalid JPEG"), "Corrupt JPEG accepted");
            });

            await Check("invalid display dimensions never become LIVE", async () =>
            {
                using var peer = new Peer();
                using var viewer = new LanViewerSession(dispatcher, Normal);
                viewer.Connect("127.0.0.1", peer.Port, "123456", peer.Fingerprint); await peer.AcceptAsync(); await peer.HelloAsync();
                await peer.SendAsync(ArlProtocol.MessageType.Approved, ArlProtocol.EncodeApproved(int.MaxValue, 2, true, 1));
                await viewer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Require(!viewer.IsConnected && !viewer.ControlEnabled && viewer.Status.Contains("dimensions"), "Bad dimensions accepted");
            });

            await Check("silent live host times out and clears the image", async () =>
            {
                using var peer = new Peer();
                using var viewer = new LanViewerSession(dispatcher, Normal with { Read = TimeSpan.FromMilliseconds(350) });
                viewer.Connect("127.0.0.1", peer.Port, "123456", peer.Fingerprint); await peer.AcceptAsync();
                await peer.HelloAsync(); await peer.ApproveAsync(false);
                await peer.SendAsync(ArlProtocol.MessageType.FrameJpeg, Jpeg());
                await Until(() => viewer.IsConnected);
                await viewer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                // Peer stops reading/writing: either read deadline or outbound heartbeat write may win the race.
                bool endedHonestly = viewer.Status.Contains("stopped responding", StringComparison.OrdinalIgnoreCase)
                    || viewer.Status.Contains("Heartbeat could not reach", StringComparison.OrdinalIgnoreCase);
                Require(!viewer.IsBusy && viewer.RemoteImage is null && endedHonestly, "Silent host stayed live");
            });

            await Check("input burst is bounded and ends instead of dropping transitions", async () =>
            {
                using var peer = new Peer();
                using var viewer = new LanViewerSession(dispatcher, Normal);
                viewer.Connect("127.0.0.1", peer.Port, "123456", peer.Fingerprint); await peer.AcceptAsync();
                await peer.HelloAsync(); await peer.ApproveAsync(true);
                await peer.SendAsync(ArlProtocol.MessageType.FrameJpeg, Jpeg());
                await Until(() => viewer.IsConnected);
                string text = new('a', InputValidation.MaxTextCharacters);
                // Peer deliberately stops reading. No data goes to an OS input sink.
                for (int i = 0; i < 10000 && viewer.IsBusy; i++) viewer.TrySendText(text);
                Require(!viewer.IsBusy && viewer.Status.Contains("queue filled"), "Input producer had no bounded overflow path");
                await viewer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            });

            await Check("old completion cannot clobber immediate reconnect", async () =>
            {
                using var first = new Peer(); using var second = new Peer();
                using var viewer = new LanViewerSession(dispatcher, Normal);
                viewer.Connect("127.0.0.1", first.Port, "123456", first.Fingerprint); await first.AcceptAsync(); await first.HelloAsync();
                await Until(() => viewer.Phase == ViewerPhase.WaitingApproval);
                Task old = viewer.Completion;
                viewer.Disconnect();
                viewer.Connect("127.0.0.1", second.Port, "123456", second.Fingerprint);
                await second.AcceptAsync(); await second.HelloAsync();
                await old.WaitAsync(TimeSpan.FromSeconds(5));
                await Until(() => viewer.Phase == ViewerPhase.WaitingApproval);
                Require(viewer.IsBusy && !viewer.IsConnected, "Old attempt reset the new connection");
                viewer.Disconnect(); await viewer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            });

            await Check("cancel queued presentation prevents a stale image", async () =>
            {
                using var peer = new Peer();
                using var viewer = new LanViewerSession(dispatcher, Normal);
                viewer.Connect("127.0.0.1", peer.Port, "123456", peer.Fingerprint); await peer.AcceptAsync();
                await peer.HelloAsync(); await peer.ApproveAsync(false);
                await Until(() => viewer.Phase == ViewerPhase.WaitingFrame);
                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Posted(object? sender, DispatcherHookEventArgs e)
                {
                    if (e.Operation.Priority != DispatcherPriority.Background) return;
                    _ = dispatcher.BeginInvoke(DispatcherPriority.Send, () => { viewer.Disconnect(); cancelled.TrySetResult(); });
                }
                dispatcher.Hooks.OperationPosted += Posted;
                try
                {
                    await peer.SendAsync(ArlProtocol.MessageType.FrameJpeg, Jpeg());
                    await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await viewer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                    Require(!viewer.IsConnected && viewer.RemoteImage is null && viewer.FramesReceived == 0, "Stale callback restored image");
                }
                finally { dispatcher.Hooks.OperationPosted -= Posted; }
            });

            Console.WriteLine($"{passed}/{passed} viewer lifecycle checks passed. Loopback only; no capture, SendInput or firewall changes.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL after {passed} viewer checks: {ex}");
            return 1;
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static async Task Until(Func<bool> predicate)
    {
        long end = Environment.TickCount64 + 5000;
        while (!predicate())
        {
            if (Environment.TickCount64 >= end) throw new TimeoutException("Expected viewer state not reached.");
            await Task.Delay(10);
        }
    }

    private static byte[] Jpeg()
    {
        var source = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgr24, null,
            new byte[] { 30, 90, 180, 50, 100, 190, 70, 110, 200, 90, 120, 210 }, 6);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }

    private sealed class Peer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(10));
        private readonly X509Certificate2 _certificate;
        private TcpClient? _client;
        private SslStream? _stream;
        public Guid SessionId { get; } = Guid.NewGuid();
        public string Fingerprint { get; }
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public Peer()
        {
            _certificate = LanTls.CreateEphemeral(out string fingerprint);
            Fingerprint = fingerprint;
            _listener.Start();
        }
        public async Task AcceptAsync()
        {
            _client = await _listener.AcceptTcpClientAsync(_deadline.Token);
            _client.NoDelay = true;
            _stream = await LanTls.AuthenticateAsServerAsync(_client.GetStream(), _certificate, _deadline.Token);
            Require((await ReadAsync()).Type == ArlProtocol.MessageType.HelloViewer, "Expected viewer hello");
        }
        public async Task HelloAsync()
        {
            await SendAsync(ArlProtocol.MessageType.HelloOk, ArlProtocol.EncodeGuid(SessionId));
            await SendAsync(ArlProtocol.MessageType.WaitingApproval, []);
        }
        public Task ApproveAsync(bool control)
            => SendAsync(ArlProtocol.MessageType.Approved, ArlProtocol.EncodeApproved(2, 2, control, 1));
        public Task SendAsync(ArlProtocol.MessageType type, byte[] payload)
            => ArlProtocol.WriteMessageAsync(_stream!, type, payload, _deadline.Token);
        public Task<(ArlProtocol.MessageType Type, byte[] Payload)> ReadAsync()
            => ArlProtocol.ReadMessageAsync(_stream!, _deadline.Token);
        public void Dispose()
        {
            _deadline.Cancel();
            _stream?.Dispose();
            _client?.Dispose();
            _certificate.Dispose();
            _listener.Stop();
            _deadline.Dispose();
        }
    }

    private sealed class RecordingInput : IInputSink
    {
        public int Released;
        public void Apply(InputCommand command, DisplayBounds bounds) { }
        public void Release(HeldInput input) => Released++;
    }
}
