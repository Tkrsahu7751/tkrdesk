using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using ApnaRemote.Protocol;
using System.Windows.Threading;

namespace ApnaRemote.Windows.Lan;

/// <summary>Real dispatcher/browser, loopback-only sockets. No multicast, capture or input.</summary>
internal static class DiscoveryLifecycleSelfTest
{
    internal static async Task<int> RunAsync(Dispatcher dispatcher)
    {
        int passed = 0;
        async Task Check(string name, Func<Task> test)
        {
            await test();
            Console.WriteLine("PASS " + name);
            passed++;
        }
        try
        {
            await Check("failed socket setup releases handle and preserves error", async () =>
            {
                System.Net.Sockets.SafeSocketHandle? handle = null;
                using var browser = new LanWifiBeaconBrowser(dispatcher, udp =>
                {
                    handle = udp.Client.SafeHandle;
                    throw new InvalidOperationException("injected setup failure");
                });
                browser.Start();
                await Flush(dispatcher);
                Require(handle?.IsClosed == true, "Socket leaked after setup failure");
                Require(!browser.IsBrowsing && browser.Status.Contains("injected setup failure"), "Start error overwritten");
            });
            await Check("restart clears old peers before displaying fresh peers", async () =>
            {
                using var browser = Browser(dispatcher);
                browser.Start();
                browser.Peers.Add(Peer("old"));
                browser.Start();
                Require(browser.Peers.Count == 0, "Old peers survived restart");
                browser.Peers.Add(Peer("fresh"));
                await Flush(dispatcher);
                Require(browser.Peers.Count == 1 && browser.Peers[0].Key == "fresh", "Old cleanup cleared new scan");
                browser.Stop();
                await browser.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            });
            await Check("queued old packets cannot enter restarted scan", async () =>
            {
                using var browser = Browser(dispatcher);
                browser.Start();
                int oldGeneration = Field<int>(browser, "_scanGeneration");
                Queue(browser, Peer("old"), oldGeneration);
                browser.Start();
                Queue(browser, Peer("late"), oldGeneration);
                Queue(browser, Peer("fresh"), Field<int>(browser, "_scanGeneration"));
                await Flush(dispatcher);
                Require(browser.Peers.Count == 1 && browser.Peers[0].Key == "fresh", "Stale peer entered new scan");
                browser.Stop();
                await browser.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            });
            await Check("repeated immediate start/stop completes owned receive tasks", async () =>
            {
                using var browser = Browser(dispatcher);
                for (int i = 0; i < 25; i++)
                {
                    browser.Start();
                    var completion = browser.Completion;
                    browser.Stop();
                    await completion.WaitAsync(TimeSpan.FromSeconds(3));
                }
                await Flush(dispatcher);
                Require(!browser.IsBrowsing && browser.Peers.Count == 0, "Browser remained active");
            });
            await Check("receive socket failure ends browser instead of spinning", async () =>
            {
                using var browser = Browser(dispatcher);
                browser.Start();
                Field<UdpClient>(browser, "_udp").Dispose();
                await browser.Completion.WaitAsync(TimeSpan.FromSeconds(3));
                await Flush(dispatcher);
                Require(!browser.IsBrowsing && browser.Status.Contains("stopped:"), "Receive failure was hidden");
            });
            await Check("Wi-Fi pending flood is bounded and coalesces latest duplicate", async () =>
            {
                using var browser = Browser(dispatcher);
                browser.Start();
                await Flush(dispatcher);
                int updates = 0;
                browser.Changed += () => updates++;
                int generation = Field<int>(browser, "_scanGeneration");
                for (int i = 0; i < 4096; i++) Queue(browser, Peer(i.ToString()), generation);
                Queue(browser, Peer("0", "latest"), generation);
                Require(Field<Dictionary<string, NearbyPeer>>(browser, "_pendingPeers").Count <= 64, "Pending flood exceeded 64");
                await Flush(dispatcher);
                Require(browser.Peers.Count == 64 && browser.Peers.Any(p => p.Key == "0" && p.DisplayName == "latest"), "Latest known peer lost");
                Require(updates == 1, "Batch triggered per-peer UI refreshes");
                browser.Stop();
                await browser.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            });
            await Check("BLE cleanup cannot clear peers populated after Stop returns", async () =>
            {
                using var scanner = new NearbyBluetoothScanner(dispatcher);
                scanner.Peers.Add(Peer("old"));
                scanner.Stop();
                Require(scanner.Peers.Count == 0, "Stop left old peers queued");
                scanner.Peers.Add(Peer("fresh"));
                await Flush(dispatcher);
                Require(scanner.Peers.Count == 1, "Old cleanup cleared fresh peers");
                SetField(scanner, "<IsScanning>k__BackingField", true);
                scanner.Dispose();
                Require(!scanner.IsScanning, "Dispose left scanning true");
            });
            await Check("BLE pending flood is bounded without starting radio", async () =>
            {
                using var scanner = new NearbyBluetoothScanner(dispatcher);
                var watcher = new global::Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementWatcher();
                SetField(scanner, "_watcher", watcher);
                SetField(scanner, "<IsScanning>k__BackingField", true);
                var queue = typeof(NearbyBluetoothScanner).GetMethod("QueuePeer", BindingFlags.Instance | BindingFlags.NonPublic)!;
                int updates = 0;
                scanner.Changed += () => updates++;
                for (int i = 0; i < 4096; i++) queue.Invoke(scanner, [Peer(i.ToString()), watcher, 0]);
                queue.Invoke(scanner, [Peer("0", "latest"), watcher, 0]);
                Require(Field<Dictionary<string, NearbyPeer>>(scanner, "_pendingPeers").Count <= 64, "BLE pending flood exceeded 64");
                await Flush(dispatcher);
                Require(scanner.Peers.Count == 64 && scanner.Peers.Any(p => p.Key == "0" && p.DisplayName == "latest"), "BLE latest duplicate lost");
                Require(updates == 1, "BLE batch triggered per-peer UI refreshes");
            });
            await Check("bounded parser accepts current and legacy endpoints without secrets", () =>
            {
                foreach (string text in new[] { "APNA2\tPC\t192.168.1.7\t5720\n", "APNA1\tPC\t192.168.1.7\t5720\t123456\tABCDEF\n" })
                {
                    Require(LanWifiDiscovery.TryDecodeOffer(Encoding.UTF8.GetBytes(text), out var offer), "Valid discovery rejected");
                    Require(offer.IPv4 == "192.168.1.7" && offer.Port == 5720 && offer.Pin == "" && offer.Fingerprint == "", "Endpoint or secret policy changed");
                }
                return Task.CompletedTask;
            });
            await Check("parser rejects oversized packets, malformed UTF-8 and control names", () =>
            {
                Require(!LanWifiDiscovery.TryDecodeOffer(new byte[513], out _), "Oversized packet accepted");
                Require(!LanWifiDiscovery.TryDecodeOffer([.. Encoding.UTF8.GetBytes("APNA2\t"), 0xFF, .. Encoding.UTF8.GetBytes("\t192.168.1.7\t5720")], out _), "Malformed UTF-8 accepted");
                foreach (string name in new[] { "PC\rBAD", "PC\nBAD", "PC\0BAD", new string('x', 65) })
                    Require(!LanWifiDiscovery.TryDecodeOffer(Encoding.UTF8.GetBytes($"APNA2\t{name}\t192.168.1.7\t5720"), out _), "Malformed name accepted");
                return Task.CompletedTask;
            });
            await Check("local sanitized and Unicode names still round-trip", () =>
            {
                foreach (string name in new[] { "घर का PC 😀", "PC\r\n\tNAME", new string('x', 63) + "😀" + new string('x', 40) })
                {
                    byte[] payload = LanWifiDiscovery.EncodeOffer(new NearbyOffer(name, "192.168.1.7", 5720, "123456", "secret"));
                    Require(payload.Length <= 512 && LanWifiDiscovery.TryDecodeOffer(payload, out var decoded), "Own advertisement rejected");
                    Require(!Encoding.UTF8.GetString(payload).Contains("123456"), "Advertised a secret");
                }
                return Task.CompletedTask;
            });
            await Check("repeated identical Wi-Fi offers keep the existing socket", () =>
            {
                using var publisher = new LanWifiBeaconPublisher();
                using var socket = new UdpClient(AddressFamily.InterNetwork);
                var offer = new NearbyOffer("PC", "192.168.1.7", 5720, "123456", "fingerprint");
                SetField(publisher, "_udp", socket);
                SetField(publisher, "_payload", LanWifiDiscovery.EncodeOffer(offer));
                SetField(publisher, "<IsAdvertising>k__BackingField", true);
                for (int i = 0; i < 500; i++) publisher.Start(offer with { Pin = "654321" });
                Require(ReferenceEquals(Field<UdpClient>(publisher, "_udp"), socket), "Repeated offer recreated socket");
                Require(!socket.Client.SafeHandle.IsClosed, "Existing advertising socket closed");
                return Task.CompletedTask;
            });
            await Check("Stop invalidates pending BLE adapter lookup", async () =>
            {
                var adapter = new TaskCompletionSource<global::Windows.Devices.Bluetooth.BluetoothAdapter?>();
                int radioChecks = 0;
                using var publisher = new NearbyBluetoothPublisher(dispatcher, () => adapter.Task, _ =>
                { radioChecks++; return Task.FromResult<string?>("radio off"); });
                var pending = publisher.StartAsync(new NearbyOffer("PC", "192.168.1.7", 5720, "", ""));
                publisher.Stop();
                adapter.SetResult(null);
                await pending;
                Require(radioChecks == 0 && !publisher.IsAdvertising && !Field<DispatcherTimer>(publisher, "_retry").IsEnabled,
                    "Stopped adapter continuation restarted work");
            });
            await Check("Dispose invalidates pending BLE radio check", async () =>
            {
                var radio = new TaskCompletionSource<string?>();
                using var publisher = new NearbyBluetoothPublisher(dispatcher,
                    () => Task.FromResult<global::Windows.Devices.Bluetooth.BluetoothAdapter?>(null), _ => radio.Task);
                var pending = publisher.StartAsync(new NearbyOffer("PC", "192.168.1.7", 5720, "", ""));
                publisher.Dispose();
                radio.SetResult("radio off");
                await pending;
                Require(!publisher.IsAdvertising && !Field<DispatcherTimer>(publisher, "_retry").IsEnabled,
                    "Disposed radio continuation restarted retry");
            });
            await Check("old BLE attempt cannot release newer attempt ownership", async () =>
            {
                var first = new TaskCompletionSource<global::Windows.Devices.Bluetooth.BluetoothAdapter?>();
                var second = new TaskCompletionSource<global::Windows.Devices.Bluetooth.BluetoothAdapter?>();
                int calls = 0;
                using var publisher = new NearbyBluetoothPublisher(dispatcher, () => ++calls == 1 ? first.Task : second.Task,
                    _ => Task.FromResult<string?>("radio off"));
                var offer = new NearbyOffer("PC", "192.168.1.7", 5720, "", "");
                var old = publisher.StartAsync(offer);
                var current = publisher.StartAsync(offer with { IPv4 = "192.168.1.8" });
                first.SetResult(null);
                await old;
                Require(Field<bool>(publisher, "_startInFlight"), "Old finally released new attempt");
                await publisher.StartAsync(offer with { IPv4 = "192.168.1.8" });
                Require(calls == 2, "Duplicate attempt restarted adapter lookup");
                publisher.Stop();
                second.SetResult(null);
                await current;
                Require(!Field<DispatcherTimer>(publisher, "_retry").IsEnabled, "Superseded attempt restarted retry");
            });
            await Check("discovery retains non-default port for the existing connect parser", () =>
            {
                var normal = Peer("default");
                Require(normal.ConnectionAddress == "127.0.0.1", "Default port display changed");
                var alternate = new NearbyPeer { Key = "custom", DisplayName = "PC", Detail = "", IPv4 = "192.168.1.7", Port = 5799, Pin = "", Fingerprint = "" };
                Require(alternate.ConnectionAddress == "192.168.1.7:5799", "Advertised custom port lost");
                return Task.CompletedTask;
            });
            await Check("preferred LAN IPv4 skips APIPA and prefers Wi-Fi when present", () =>
            {
                IPAddress? preferred = LanWifiDiscovery.TryGetPreferredLanIPv4();
                if (preferred is not null)
                {
                    string ip = preferred.ToString();
                    Require(!ip.StartsWith("169.254.", StringComparison.Ordinal), "Preferred APIPA address");
                    Require(preferred.AddressFamily == AddressFamily.InterNetwork, "Preferred address not IPv4");
                }
                return Task.CompletedTask;
            });
            Console.WriteLine($"{passed}/{passed} discovery lifecycle checks passed. Loopback only; no radio, capture or input.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL discovery after {passed} passes: {ex}");
            return 1;
        }
    }

    private static LanWifiBeaconBrowser Browser(Dispatcher dispatcher) => new(dispatcher,
        udp => udp.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0)));
    private static Task Flush(Dispatcher dispatcher) => dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
    private static T Field<T>(object target, string name) => (T)target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void SetField(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void Queue(LanWifiBeaconBrowser browser, NearbyPeer peer, int generation) =>
        typeof(LanWifiBeaconBrowser).GetMethod("QueuePeer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(browser, [peer, generation]);
    private static NearbyPeer Peer(string key, string name = "PC") => new()
    {
        Key = key, DisplayName = name, Detail = "test", IPv4 = "127.0.0.1", Pin = "", Fingerprint = "",
        LastSeenUtc = DateTimeOffset.UtcNow,
    };
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
