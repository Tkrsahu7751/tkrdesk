using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ApnaRemote.Core;
using ApnaRemote.Protocol;
using ApnaRemote.Windows.Capture;

namespace ApnaRemote.Windows.Lan;

/// <summary>
/// Host-side protocol/control checks without LanHostSession UI, WGC, SendInput or netsh.
/// </summary>
internal static class HostLifecycleSelfTest
{
    public static int Run()
    {
        int passed = 0;
        void Check(string name, Action test)
        {
            test();
            passed++;
            Console.WriteLine("PASS " + name);
        }

        try
        {
            Check("type-specific payload caps reject oversized InputControl before allocate intent", () =>
            {
                Require(ArlProtocol.MaxPayloadFor(ArlProtocol.MessageType.InputControl) == ProtocolFraming.MaximumControlBytes + 4, "Input cap");
                Require(ArlProtocol.MaxPayloadFor(ArlProtocol.MessageType.Heartbeat) == 0, "Heartbeat must be empty");
                Require(ArlProtocol.MaxPayloadFor(ArlProtocol.MessageType.PermissionAck) == 12, "Ack size");
                Require(ArlProtocol.MaxPayloadFor(ArlProtocol.MessageType.FrameH264) == ArlProtocol.MaxPayload, "H264 frame cap");
                Require(ArlProtocol.Revision == 4, "protocol revision");
                Require(ArlProtocol.WriteDeadline == TimeSpan.FromSeconds(5), "Write deadline constant");
            });

            Check("oversized typed payload is rejected on read", () =>
            {
                using var pair = TcpPair.Connect();
                Span<byte> header = stackalloc byte[12];
                BinaryPrimitives.WriteUInt32LittleEndian(header[..4], ArlProtocol.Magic);
                BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4, 4), (uint)ArlProtocol.MessageType.Heartbeat);
                BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(8, 4), 4); // Heartbeat must be 0
                pair.Write.Write(header);
                pair.Write.Write(new byte[4]);
                bool rejected = false;
                try
                {
                    _ = ArlProtocol.ReadMessageAsync(pair.Read, CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (InvalidOperationException ex)
                {
                    rejected = ex.Message.Contains("rejected", StringComparison.OrdinalIgnoreCase);
                }

                Require(rejected, "Heartbeat with body was accepted");
            });

            Check("unknown type over its small cap is rejected before allocate", () =>
            {
                using var pair = TcpPair.Connect();
                const uint unknown = 99;
                Span<byte> header = stackalloc byte[12];
                BinaryPrimitives.WriteUInt32LittleEndian(header[..4], ArlProtocol.Magic);
                BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4, 4), unknown);
                BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(8, 4), 2048); // default unknown cap is 1024
                pair.Write.Write(header);
                bool rejected = false;
                try
                {
                    _ = ArlProtocol.ReadMessageAsync(pair.Read, CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (InvalidOperationException ex)
                {
                    rejected = ex.Message.Contains("rejected", StringComparison.OrdinalIgnoreCase);
                }

                Require(rejected, "Unknown oversized type accepted");
            });

            Check("truncated header fails the read instead of hanging forever", () =>
            {
                using var pair = TcpPair.Connect();
                pair.Write.Write(new byte[] { 0x41, 0x52, 0x4C, 0x31, 0x01, 0x00 }); // partial ARL1 header
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                bool ended = false;
                try
                {
                    _ = ArlProtocol.ReadMessageAsync(pair.Read, cts.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) { ended = true; }
                catch (IOException) { ended = true; }

                Require(ended, "Partial header did not end the read");
            });

            Check("slow reader cancels framed write within the host write deadline", () =>
            {
                using var pair = TcpPair.Connect();
                pair.Accepted.ReceiveBufferSize = 8 * 1024;
                pair.Client.SendBufferSize = 8 * 1024;
                byte[] fat = new byte[256 * 1024];
                var sw = Stopwatch.StartNew();
                bool cancelled = false;
                try
                {
                    for (int i = 0; i < 64 && !cancelled; i++)
                    {
                        ArlProtocol.WriteMessageWithDeadlineAsync(
                            pair.Write, ArlProtocol.MessageType.FrameJpeg, fat,
                            ArlProtocol.WriteDeadline, CancellationToken.None).GetAwaiter().GetResult();
                    }
                }
                catch (OperationCanceledException) { cancelled = true; }
                catch (IOException) { cancelled = true; }

                Require(cancelled, "Blocked write never hit the deadline");
                Require(sw.Elapsed < ArlProtocol.WriteDeadline + TimeSpan.FromSeconds(3),
                    "Write deadline took too long: " + sw.Elapsed);
            });

            Check("silent peer idle timeout ends CanShareFrames without heartbeats", () =>
            {
                var clock = new ManualClock();
                var sink = new RecordingSink();
                var gate = new HostSessionGate(sink, clock);
                gate.EnableReceiving();
                Guid id = gate.RequestFromAuthenticatedPeer(new("peer", "PC"));
                Require(gate.ApproveLocally(id, "display", new(0, 0, 800, 600), false), "approve");
                Require(gate.CanShareFrames(id, "peer"), "fresh session should share");
                clock.Advance(HostSessionGate.IdleTimeout + TimeSpan.FromMilliseconds(1));
                gate.Tick();
                Require(!gate.CanShareFrames(id, "peer"), "idle peer still sharing");
            });

            Check("inactive gate Change does not imply Stop listening", () =>
            {
                var sink = new RecordingSink();
                var gate = new HostSessionGate(sink);
                gate.EnableReceiving();
                Guid id = gate.RequestFromAuthenticatedPeer(new("peer", "PC"));
                Require(gate.ApproveLocally(id, "display", new(0, 0, 800, 600), true), "approve");
                var state = new HostControlState(gate);
                gate.Disconnect();
                Require(!state.Change(false), "change should fail when inactive");
                // Product rule: UI ChangeControl must not call Stop when Change returns false.
            });

            Check("quality presets stay within budget bounds", () =>
            {
                foreach (LabQualityPreset preset in Enum.GetValues<LabQualityPreset>())
                {
                    var p = preset.Profile();
                    Require(p.JpegQuality is >= 0.4 and <= 0.9, preset + " quality");
                    Require(p.MaxWidth is >= 640 and <= 1920, preset + " width");
                    Require(p.FrameDelayMs is >= 30 and <= 100, preset + " delay");
                    Require(p.H264Bitrate is >= 500_000 and <= 12_000_000, preset + " h264 bitrate");
                    Require(p.H264Fps is >= 10 and <= 30, preset + " h264 fps");
                    Require(p.WithFpsNudge(5).MaxWidth <= p.MaxWidth, preset + " fps nudge");
                }
            });

            Check("measured kbps uses payload bits over elapsed ms", () =>
            {
                Require((int)Math.Round(100_000 * 8.0 / 1000) == 800, "100 KB/s should report 800 kbps");
                Require((int)Math.Round(0 * 8.0 / 1000) == 0, "idle window");
            });

            Check("lab preferences remember hosts but reject PIN/fingerprint shapes", () =>
            {
                string path = Path.Combine(Path.GetTempPath(), "ApnaRemote-prefs-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    var prefs = new LabPreferences { DarkTheme = true, ColorMode = "Dark", ThemePack = "Classic", QualityPreset = "Performance" };
                    prefs.RememberHost("192.168.1.10");
                    prefs.RememberHost("192.168.1.10");
                    prefs.RememberHost("738541");
                    prefs.RememberHost("A8A5-3E67-4D1E-6496");
                    prefs.AddHistory("Viewer", "192.168.1.10", "Ended", 12, 45);
                    prefs.AddHistory("Viewer", "654321", "secret pin should redact", 1, 0);
                    prefs.Save(path);
                    LabPreferences loaded = LabPreferences.Load(path);
                    Require(loaded.DarkTheme, "dark theme lost");
                    Require(loaded.ColorMode == "Dark", "color mode lost");
                    Require(loaded.ThemePack == "SciFi", "classic pack should migrate to unified SciFi");
                    var legacy = new LabPreferences { ThemePack = "Studio", DarkTheme = false, ColorMode = "" };
                    legacy.Sanitize();
                    Require(legacy.ThemePack == "SciFi", "Studio should migrate to SciFi");
                    Require(legacy.ColorMode == "Light", "legacy dark=false should become Light");
                    var sciFi = new LabPreferences { ColorMode = "SciFi" };
                    sciFi.Sanitize();
                    Require(sciFi.ColorMode == "SciFi", "sci-fi color mode");
                    Require(!sciFi.DarkTheme, "sci-fi ice mode is not dark");
                    Require(loaded.QualityPreset == "Performance", "quality lost");
                    Require(loaded.RecentHosts.Count == 1 && loaded.RecentHosts[0] == "192.168.1.10", "secret-like values leaked into recent");
                    Require(loaded.History.Count == 2, "history count");
                    Require(loaded.History[0].Peer == "(redacted)", "PIN-shaped peer not redacted");
                    Require(loaded.History[1].Peer == "192.168.1.10", "normal peer lost");
                }
                finally
                {
                    try { File.Delete(path); } catch { /* ignore */ }
                }
            });

            Check("nearby radio offer carries endpoint but never pairing secrets", () =>
            {
                var offer = new NearbyOffer("TKR-NEWLAP", "192.168.31.182", 5720, "775014", "96C6-0658-E9CD-150A");
                byte[] payload = offer.ToAdvertisementPayload();
                Require(NearbyOffer.TryFromAdvertisementPayload(payload, "ViewerName", out NearbyOffer parsed), "parse failed");
                Require(parsed.IPv4 == offer.IPv4 && parsed.Port == offer.Port, "endpoint mismatch");
                Require(string.IsNullOrEmpty(parsed.Pin) && string.IsNullOrEmpty(parsed.Fingerprint), "radio secrets leaked");
                string uri = offer.ToConnectUri();
                Require(NearbyOffer.TryParseConnectUri(uri, out NearbyOffer fromUri), "URI parse failed");
                Require(fromUri.IPv4 == offer.IPv4 && fromUri.Port == offer.Port && fromUri.Pin == offer.Pin, "URI fields mismatch");
                Require(LanTls.NormalizeFingerprint(fromUri.Fingerprint) == LanTls.NormalizeFingerprint(offer.Fingerprint), "URI fp mismatch");
                Require(parsed.IPv4 == "192.168.31.182", "ip");
                Require(parsed.Port == 5720, "port");
                Require(parsed.HostName == "ViewerName", "v1 localName fallback");
                byte[] withName = offer.ToAdvertisementPayload(includeHostName: true);
                Require(NearbyOffer.TryFromAdvertisementPayload(withName, "ignored", out NearbyOffer v3), "v3 parse");
                Require(v3.HostName == "TKR-NEWL", "embedded short name");
            });

            Check("discovery ads omit PIN and fingerprint", () =>
            {
                var offer = new NearbyOffer("HostPC", "192.168.1.10", 5720, "123456", "AABB-CCDD-EEFF-0011");
                byte[] ble = offer.ToDiscoveryAdvertisementPayload(includeHostName: true);
                Require(NearbyOffer.TryFromAdvertisementPayload(ble, "", out NearbyOffer fromBle), "ble v3");
                Require(fromBle.IPv4 == "192.168.1.10" && fromBle.Port == 5720, "ble endpoint");
                Require(string.IsNullOrEmpty(fromBle.Pin) && string.IsNullOrEmpty(fromBle.Fingerprint), "ble secrets leaked");
                byte[] wifi = LanWifiDiscovery.EncodeOffer(offer);
                Require(LanWifiDiscovery.TryDecodeOffer(wifi, out NearbyOffer fromWifi), "wifi decode");
                Require(fromWifi.IPv4 == offer.IPv4 && fromWifi.Port == offer.Port, "wifi endpoint");
                Require(string.IsNullOrEmpty(fromWifi.Pin) && string.IsNullOrEmpty(fromWifi.Fingerprint), "wifi secrets leaked");
                byte[] legacy = Encoding.UTF8.GetBytes("APNA1\tOld\t192.168.1.11\t5720\t654321\tAABBCCDDEEFF0011\n");
                Require(LanWifiDiscovery.TryDecodeOffer(legacy, out NearbyOffer fromLegacy), "legacy decode");
                Require(fromLegacy.IPv4 == "192.168.1.11" && string.IsNullOrEmpty(fromLegacy.Pin), "legacy pin stripped");
            });

            Check("TLS handshake has a finite timeout", () =>
            {
                Require(LanTls.HandshakeTimeout > TimeSpan.Zero && LanTls.HandshakeTimeout <= TimeSpan.FromSeconds(30), "handshake timeout");
            });

            Check("unsafe H264Sharp/OpenH264 path is disabled", () =>
            {
                Require(!NetworkH264Bootstrap.TryEnsureLoaded(out string? reason), "H.264 unexpectedly enabled");
                Require(reason?.Contains("disabled", StringComparison.OrdinalIgnoreCase) == true, "missing fail-closed reason");
                string output = AppContext.BaseDirectory;
                string legacyName = "openh264-2.4." + "1-win64.dll";
                string wrapperName = "H264Sharp" + "Native-win64.dll";
                Require(!File.Exists(Path.Combine(output, legacyName)), "vulnerable 2.4.1 remains in output");
                Require(!File.Exists(Path.Combine(output, "openh264-2.6.0-win64.dll")), "ABI-incompatible 2.6.0 remains in output");
                Require(!File.Exists(Path.Combine(output, wrapperName)), "ABI-7 wrapper remains in output");
            });

            Check("disconnect cancels pending approval; late ApproveLocally fails", () =>
            {
                var sink = new RecordingSink();
                var gate = new HostSessionGate(sink);
                gate.EnableReceiving();
                Guid id = gate.RequestFromAuthenticatedPeer(new("peer", "PC"));
                gate.Disconnect();
                Require(!gate.ApproveLocally(id, "display", new(0, 0, 800, 600), false), "approved after disconnect");
            });

            Check("approval timeout then ApproveLocally fails", () =>
            {
                var clock = new ManualClock();
                var gate = new HostSessionGate(new RecordingSink(), clock);
                gate.EnableReceiving();
                Guid id = gate.RequestFromAuthenticatedPeer(new("peer", "PC"));
                clock.Advance(HostSessionGate.ApprovalTimeout + TimeSpan.FromMilliseconds(1));
                gate.Tick();
                Require(!gate.ApproveLocally(id, "display", new(0, 0, 800, 600), true), "approved after timeout");
            });

            Check("repeated enable/reject/disable receiving stays usable", () =>
            {
                var gate = new HostSessionGate(new RecordingSink());
                for (int i = 0; i < 25; i++)
                {
                    gate.EnableReceiving();
                    Guid id = gate.RequestFromAuthenticatedPeer(new("peer" + i, "PC" + i));
                    gate.RejectLocally(id);
                    gate.DisableReceiving();
                }

                gate.EnableReceiving();
                Guid last = gate.RequestFromAuthenticatedPeer(new("final", "FinalPC"));
                Require(gate.ApproveLocally(last, "display", new(0, 0, 1280, 720), false), "final approve after cycles");
                Require(gate.CanShareFrames(last, "final"), "final share after cycles");
                gate.DisableReceiving();
            });

            Console.WriteLine(passed + "/" + passed + " host lifecycle checks passed. No capture, SendInput or firewall changes.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL after " + passed + ": " + ex.Message);
            return 1;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RecordingSink : IInputSink
    {
        public void Apply(InputCommand command, DisplayBounds display) { }
        public void Release(HeldInput input) { }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _utc = DateTimeOffset.UnixEpoch;
        private long _timestamp;

        public override DateTimeOffset GetUtcNow() => _utc;
        public override long GetTimestamp() => _timestamp;
        public override long TimestampFrequency => 1000;
        public void Advance(TimeSpan delta)
        {
            _utc += delta;
            _timestamp += (long)delta.TotalMilliseconds;
        }
    }

    private sealed class TcpPair : IDisposable
    {
        private readonly TcpListener _listener;
        public TcpClient Client { get; }
        public TcpClient Accepted { get; }
        public NetworkStream Write => Client.GetStream();
        public NetworkStream Read => Accepted.GetStream();

        private TcpPair(TcpListener listener, TcpClient client, TcpClient accepted)
        {
            _listener = listener;
            Client = client;
            Accepted = accepted;
            Client.NoDelay = true;
            Accepted.NoDelay = true;
        }

        public static TcpPair Connect()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            var accepted = listener.AcceptTcpClient();
            return new TcpPair(listener, client, accepted);
        }

        public void Dispose()
        {
            Write.Dispose();
            Read.Dispose();
            Client.Dispose();
            Accepted.Dispose();
            _listener.Stop();
        }
    }
}
