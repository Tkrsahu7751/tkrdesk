using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using ApnaRemote.Core;
using ApnaRemote.Protocol;

namespace ApnaRemote.Windows.Lan;

/// <summary>
/// Headless same-PC LAN lab check (loopback + optional LAN IP).
/// Run: ApnaRemote.exe --lan-lab-selftest
/// Synthetic JPEG over TLS proves protocol + TCP. No UI picker / SendInput.
/// </summary>
internal static class LanLabSelfTest
{
    public static int Run()
    {
        int code = 1;
        var thread = new Thread(() => code = RunCore())
        {
            IsBackground = false,
            Name = "ApnaRemote-LanLabSelfTest",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        return code;
    }

    private static int RunCore()
    {
        Console.WriteLine("Apna Remote LAN lab self-test");
        Console.WriteLine("Synthetic JPEG over TLS (loopback). No UI, no WebRTC.");

        try
        {
            byte[] jpeg = CreateSolidJpeg().GetAwaiter().GetResult();
            Console.WriteLine($"Synthetic JPEG bytes={jpeg.Length}");

            int loopback = RunPairAsync(IPAddress.Loopback, "127.0.0.1", jpeg).GetAwaiter().GetResult();
            if (loopback != 0) return loopback;

            string? lanIp = GetFirstLanIPv4();
            if (lanIp is not null)
            {
                Console.WriteLine("Also testing bind/connect via LAN IP " + lanIp);
                int lan = RunPairAsync(IPAddress.Parse(lanIp), lanIp, jpeg).GetAwaiter().GetResult();
                if (lan != 0)
                {
                    Console.WriteLine("LAN-IP path failed (firewall may block even same PC). Loopback still passed.");
                    return 2;
                }
            }
            else Console.WriteLine("No LAN IPv4 found; loopback-only OK.");

            Console.WriteLine("PASS LAN lab self-test.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunPairAsync(IPAddress bindAddress, string connectHost, byte[] jpeg)
    {
        const string pin = "424242";
        using var certificate = LanTls.CreateEphemeral(out string fingerprint);
        var listener = new TcpListener(bindAddress, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Console.WriteLine($"Host listening {bindAddress}:{port} TLS fp={fingerprint}");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var gate = new HostSessionGate(new NullInputSink());
        gate.EnableReceiving();

        Task<int> hostTask = Task.Run(() => HostAsync(listener, certificate, gate, pin, jpeg, cts.Token), cts.Token);
        await Task.Delay(200, cts.Token).ConfigureAwait(false);
        Task<int> viewerTask = Task.Run(
            () => ViewerAsync(connectHost, port, pin, fingerprint, "selftest-viewer", "SelfTest", cts.Token), cts.Token);

        int[] results = await Task.WhenAll(hostTask, viewerTask).ConfigureAwait(false);
        try { listener.Stop(); } catch { /* ignore */ }
        if (results[0] != 0) { Console.WriteLine("Host side FAIL code=" + results[0]); return results[0]; }
        if (results[1] != 0) { Console.WriteLine("Viewer side FAIL code=" + results[1]); return results[1]; }
        Console.WriteLine($"OK path {connectHost}:{port}");
        return 0;
    }

    private static async Task<int> HostAsync(
        TcpListener listener, X509Certificate2 certificate, HostSessionGate gate,
        string pin, byte[] jpeg, CancellationToken ct)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
        client.NoDelay = true;
        await using var stream = await LanTls.AuthenticateAsServerAsync(client.GetStream(), certificate, ct).ConfigureAwait(false);

        var (type, payload) = await ArlProtocol.ReadMessageAsync(stream, ct).ConfigureAwait(false);
        if (type != ArlProtocol.MessageType.HelloViewer) { Console.WriteLine("Host expected HelloViewer"); return 10; }
        var (peerId, displayName, pinProof) = ArlProtocol.DecodeHelloViewer(payload);
        if (!LanTls.VerifyPinProof(pin, certificate, pinProof)) { Console.WriteLine("Host PIN proof mismatch"); return 11; }

        Guid requestId = gate.RequestFromAuthenticatedPeer(new PeerIdentity(peerId, displayName));
        await ArlProtocol.WriteMessageAsync(stream, ArlProtocol.MessageType.HelloOk, ArlProtocol.EncodeGuid(requestId), ct).ConfigureAwait(false);
        await ArlProtocol.WriteMessageAsync(stream, ArlProtocol.MessageType.WaitingApproval, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
        if (!gate.ApproveLocally(requestId, "selftest-display", new DisplayBounds(0, 0, 320, 180), allowControl: false))
        { Console.WriteLine("Host ApproveLocally failed"); return 12; }

        await ArlProtocol.WriteMessageAsync(stream, ArlProtocol.MessageType.Approved,
            ArlProtocol.EncodeApproved(320, 180, allowControl: false, permissionEpoch: 1), ct).ConfigureAwait(false);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(async () =>
        {
            try
            {
                while (!linked.IsCancellationRequested)
                {
                    var (t, _) = await ArlProtocol.ReadMessageAsync(stream, linked.Token).ConfigureAwait(false);
                    if (t == ArlProtocol.MessageType.Heartbeat) gate.Heartbeat(requestId, peerId);
                    else if (t == ArlProtocol.MessageType.End) linked.Cancel();
                }
            }
            catch { linked.Cancel(); }
        }, linked.Token);

        const int frameCount = 45;
        for (int i = 0; i < frameCount; i++)
        {
            gate.Tick();
            if (!gate.CanShareFrames(requestId, peerId)) { Console.WriteLine("Host lost CanShareFrames at frame " + i); return 13; }
            await ArlProtocol.WriteMessageAsync(stream, ArlProtocol.MessageType.FrameJpeg, jpeg, linked.Token).ConfigureAwait(false);
            if (i % 15 == 0)
                await ArlProtocol.WriteMessageAsync(stream, ArlProtocol.MessageType.Heartbeat, ReadOnlyMemory<byte>.Empty, linked.Token).ConfigureAwait(false);
            await Task.Delay(40, linked.Token).ConfigureAwait(false);
        }

        await ArlProtocol.WriteMessageAsync(stream, ArlProtocol.MessageType.End, ArlProtocol.EncodeString("selftest done"), CancellationToken.None).ConfigureAwait(false);
        linked.Cancel();
        Console.WriteLine($"Host sent {frameCount} frames");
        return 0;
    }

    private static async Task<int> ViewerAsync(
        string host, int port, string pin, string fingerprint, string peerId, string peerName, CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
        client.NoDelay = true;
        await using var stream = await LanTls.AuthenticateAsClientAsync(client.GetStream(), fingerprint, ct).ConfigureAwait(false);
        if (stream.RemoteCertificate is null) { Console.WriteLine("Viewer missing remote cert"); return 24; }
        using var remoteCert = new X509Certificate2(stream.RemoteCertificate);
        byte[] pinProof = LanTls.ComputePinProof(pin, remoteCert);

        await ArlProtocol.WriteMessageAsync(stream, ArlProtocol.MessageType.HelloViewer,
            ArlProtocol.EncodeHelloViewer(peerId, peerName, pinProof), ct).ConfigureAwait(false);

        bool approved = false;
        int frames = 0;
        long lastHb = Environment.TickCount64;
        while (!ct.IsCancellationRequested)
        {
            var (type, payload) = await ArlProtocol.ReadMessageAsync(stream, ct).ConfigureAwait(false);
            switch (type)
            {
                case ArlProtocol.MessageType.HelloReject:
                    Console.WriteLine("Viewer HelloReject: " + ArlProtocol.DecodeString(payload));
                    return 20;
                case ArlProtocol.MessageType.Rejected:
                    Console.WriteLine("Viewer Rejected: " + ArlProtocol.DecodeString(payload));
                    return 21;
                case ArlProtocol.MessageType.Approved:
                    approved = true;
                    Console.WriteLine("Viewer approved");
                    break;
                case ArlProtocol.MessageType.FrameJpeg:
                    if (!approved) { Console.WriteLine("Viewer got frame before approve"); return 22; }
                    if (payload.Length < 20) { Console.WriteLine("Viewer frame too small"); return 23; }
                    frames++;
                    long now = Environment.TickCount64;
                    if (now - lastHb >= 1500)
                    {
                        await ArlProtocol.WriteMessageAsync(stream, ArlProtocol.MessageType.Heartbeat, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
                        lastHb = now;
                    }
                    break;
                case ArlProtocol.MessageType.End:
                    Console.WriteLine($"Viewer received {frames} frames then End");
                    return frames >= 30 ? 0 : 24;
                case ArlProtocol.MessageType.HelloOk:
                case ArlProtocol.MessageType.WaitingApproval:
                case ArlProtocol.MessageType.Heartbeat:
                    break;
            }
        }

        return frames >= 30 ? 0 : 25;
    }

    private static Task<byte[]> CreateSolidJpeg()
    {
        byte[] jpeg = Convert.FromBase64String(
            "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAn/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/8QAFQEBAQAAAAAAAAAAAAAAAAAAAAX/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIQAxAAAAGfAP/EABQQAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQEAAQUCf//EABQRAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQMBAT8Bf//EABQRAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQIBAT8Bf//Z");
        return Task.FromResult(jpeg);
    }

    private static string? GetFirstLanIPv4()
    {
        foreach (IPAddress addr in Dns.GetHostAddresses(Dns.GetHostName()))
        {
            if (addr.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr))
                return addr.ToString();
        }

        return null;
    }
}
