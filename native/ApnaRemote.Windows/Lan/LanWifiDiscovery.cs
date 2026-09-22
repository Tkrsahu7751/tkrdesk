using ApnaRemote.Protocol;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Windows.Threading;

namespace ApnaRemote.Windows.Lan;

/// <summary>
/// Same-Wi‑Fi discovery via UDP multicast (industry-style LAN find).
/// Beacons advertise name+IP+port only — never PIN or fingerprint.
/// Session remains TCP/TLS on 5720 with typed/QR secrets + host Accept.
/// </summary>
internal static class LanWifiDiscovery
{
    public const string MulticastAddress = "239.255.90.91";
    public const int MulticastPort = 5721;
    internal const int MaximumAdvertisementBytes = 512;
    private const int MaximumHostNameCharacters = 64;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const string MagicV2 = "APNA2"; // discovery-safe (no secrets)
    private const string MagicLegacy = "APNA1"; // old peers may still send PIN+FP; ignored on connect

    public static byte[] EncodeOffer(NearbyOffer offer)
    {
        string line = string.Join('\t',
            MagicV2,
            Sanitize(offer.HostName),
            offer.IPv4,
            offer.Port.ToString());
        return Encoding.UTF8.GetBytes(line + "\n");
    }

    public static byte[] EncodeOffer(NearbyOffer offer, string? tkrId)
    {
        if (string.IsNullOrWhiteSpace(tkrId)) return EncodeOffer(offer);
        string line = string.Join('\t',
            MagicV2,
            Sanitize(offer.HostName),
            offer.IPv4,
            offer.Port.ToString(),
            tkrId.Trim());
        return Encoding.UTF8.GetBytes(line + "\n");
    }

    public static bool TryDecodeOffer(ReadOnlySpan<byte> bytes, out NearbyOffer offer)
        => TryDecodeOffer(bytes, out offer, out _);

    public static bool TryDecodeOffer(ReadOnlySpan<byte> bytes, out NearbyOffer offer, out string tkrId)
    {
        offer = default;
        tkrId = "";
        if (bytes.Length == 0 || bytes.Length > MaximumAdvertisementBytes) return false;
        string text;
        try { text = StrictUtf8.GetString(bytes).TrimEnd('\r', '\n'); }
        catch (DecoderFallbackException) { return false; }
        string[] parts = text.Split('\t');
        if (parts.Length < 4) return false;
        bool legacy = string.Equals(parts[0], MagicLegacy, StringComparison.Ordinal);
        bool modern = string.Equals(parts[0], MagicV2, StringComparison.Ordinal);
        if (!legacy && !modern) return false;
        if (parts[1].Length > MaximumHostNameCharacters || parts[1].Any(char.IsControl)) return false;
        if (!IPAddress.TryParse(parts[2], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        if (!int.TryParse(parts[3], out int port) || port is < 1 or > 65535) return false;
        string name = string.IsNullOrWhiteSpace(parts[1]) ? parts[2] : parts[1].Trim();
        if (parts.Length >= 5 && !string.IsNullOrWhiteSpace(parts[4]))
        {
            tkrId = parts[4].Trim();
        }
        // Never surface over-the-air PIN/FP into the connect form — even if legacy APNA1 included them.
        offer = new NearbyOffer(name, parts[2], port, "", "");
        return true;
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "PC";
        string name = new(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        if (name.Length > MaximumHostNameCharacters)
        {
            name = name[..MaximumHostNameCharacters];
            if (char.IsHighSurrogate(name[^1])) name = name[..^1];
        }
        return string.IsNullOrWhiteSpace(name) ? "PC" : name.Trim();
    }

    /// <summary>
    /// Prefer an up Wi-Fi IPv4 for multicast membership so VPN/Ethernet defaults do not own IGMP joins.
    /// Falls back to other LAN IPv4, then null (caller uses OS default join).
    /// </summary>
    internal static IPAddress? TryGetPreferredLanIPv4()
    {
        IPAddress? wifi = null;
        IPAddress? other = null;
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            bool isWifi = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
                || nic.Description.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase)
                || nic.Description.Contains("Wireless", StringComparison.OrdinalIgnoreCase)
                || nic.Name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase);
            foreach (UnicastIPAddressInformation addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                string ip = addr.Address.ToString();
                if (ip.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                if (isWifi) { wifi ??= addr.Address; }
                else { other ??= addr.Address; }
            }
        }
        return wifi ?? other;
    }

    internal static void JoinPreferredMulticastGroup(UdpClient udp)
    {
        IPAddress mcast = IPAddress.Parse(MulticastAddress);
        IPAddress? local = TryGetPreferredLanIPv4();
        if (local is not null)
        {
            udp.JoinMulticastGroup(mcast, local);
            return;
        }
        udp.JoinMulticastGroup(mcast);
    }
}

internal sealed class LanWifiBeaconPublisher : IDisposable
{
    private readonly DispatcherTimer _timer;
    private UdpClient? _udp;
    private byte[] _payload = [];
    private bool _disposed;

    public LanWifiBeaconPublisher()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Send();
    }

    public string Status { get; private set; } = "Wi‑Fi find off.";
    public bool IsAdvertising { get; private set; }

    public void Start(NearbyOffer offer) => Start(offer, null);

    public void Start(NearbyOffer offer, string? tkrId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] payload = LanWifiDiscovery.EncodeOffer(offer, tkrId);
        if (IsAdvertising && _udp is not null && _payload.AsSpan().SequenceEqual(payload)) return;
        Stop();
        try
        {
            _payload = payload;
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.MulticastLoopback = true;
            IPAddress? local = LanWifiDiscovery.TryGetPreferredLanIPv4();
            if (local is not null)
            {
                // Bind outbound beacons to the preferred LAN NIC when available.
                _udp.Client.Bind(new IPEndPoint(local, 0));
            }
            LanWifiDiscovery.JoinPreferredMulticastGroup(_udp);
            IsAdvertising = true;
            Status = "Wi‑Fi find on — other PCs can Find on Wi‑Fi (same network).";
            _timer.Start();
            Send();
        }
        catch (Exception ex)
        {
            Stop();
            Status = "Wi‑Fi find unavailable: " + ex.Message;
        }
    }

    public void Stop()
    {
        _timer.Stop();
        try { _udp?.Close(); } catch { /* ignore */ }
        _udp = null;
        IsAdvertising = false;
        if (!_disposed) Status = "Wi‑Fi find off.";
    }

    private void Send()
    {
        if (_udp is null || _payload.Length == 0) return;
        try
        {
            _udp.Send(_payload, _payload.Length,
                new IPEndPoint(IPAddress.Parse(LanWifiDiscovery.MulticastAddress), LanWifiDiscovery.MulticastPort));
        }
        catch
        {
            /* transient */
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}

internal sealed class LanWifiBeaconBrowser : IDisposable
{
    private const int MaxVisiblePeers = 64;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _expire;
    private readonly Action<UdpClient> _configureSocket;
    internal Task Completion { get; private set; } = Task.CompletedTask;
    private readonly object _pendingGate = new();
    private readonly Dictionary<string, NearbyPeer> _pendingPeers = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cts;
    private UdpClient? _udp;
    private int _scanGeneration;
    private int _pendingGeneration;
    private bool _pendingDispatchQueued;
    private bool _disposed;

    public LanWifiBeaconBrowser(Dispatcher dispatcher, Action<UdpClient>? configureSocket = null)
    {
        _dispatcher = dispatcher;
        _configureSocket = configureSocket ?? (udp =>
        {
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, LanWifiDiscovery.MulticastPort));
            LanWifiDiscovery.JoinPreferredMulticastGroup(udp);
        });
        _expire = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        _expire.Tick += (_, _) => Expire();
    }

    public string Status { get; private set; } = "Wi‑Fi find off.";
    public bool IsBrowsing { get; private set; }
    public ObservableCollection<NearbyPeer> Peers { get; } = new();
    public event Action? Changed;

    public void Start()
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop(clearPeers: true);
        int generation = Volatile.Read(ref _scanGeneration);
        try
        {
            var cts = _cts = new CancellationTokenSource();
            var udp = _udp = new UdpClient(AddressFamily.InterNetwork);
            _configureSocket(udp);
            CancellationToken token = cts.Token;
            IsBrowsing = true;
            Status = "Finding on Wi‑Fi… host must be Start sharing on the same network.";
            _expire.Start();
            Raise();
            Completion = Task.Run(async () =>
            {
                try { await ReceiveLoop(udp, token, generation).ConfigureAwait(false); }
                finally { cts.Dispose(); }
            });
        }
        catch (Exception ex)
        {
            var failedCts = _cts;
            Stop(clearPeers: true);
            failedCts?.Dispose();
            Status = "Wi‑Fi find failed: " + ex.Message;
            Raise();
        }
    }

    public void Stop(bool clearPeers = true)
    {
        _dispatcher.VerifyAccess();
        int generation = Interlocked.Increment(ref _scanGeneration);
        _expire.Stop();
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts = null;
        try { _udp?.Close(); } catch { /* ignore */ }
        _udp = null;
        IsBrowsing = false;
        lock (_pendingGate)
        {
            _pendingPeers.Clear();
            _pendingGeneration = generation;
            _pendingDispatchQueued = false;
        }
        if (clearPeers)
        {
            Peers.Clear();
        }
        if (!_disposed)
        {
            Status = "Wi‑Fi find off.";
            Raise();
        }
    }

    private async Task ReceiveLoop(UdpClient udp, CancellationToken ct, int generation)
    {
        while (!ct.IsCancellationRequested && Volatile.Read(ref _scanGeneration) == generation)
        {
            try
            {
                UdpReceiveResult result = await udp.ReceiveAsync(ct);
                if (!LanWifiDiscovery.TryDecodeOffer(result.Buffer, out NearbyOffer offer, out string tkrId)) continue;
                if (!string.IsNullOrEmpty(tkrId))
                {
                    TkrIdService.RegisterPeer(tkrId, offer.HostName, offer.IPv4, offer.Port);
                }
                string detail = string.IsNullOrEmpty(tkrId)
                    ? $"{offer.IPv4}:{offer.Port} · Wi‑Fi find · tap fills IP only"
                    : $"TKR ID: {TkrIdService.FormatTkrId(tkrId)} · {offer.IPv4}:{offer.Port} · Wi‑Fi find";

                var peer = new NearbyPeer
                {
                    Key = "wifi|" + offer.IPv4 + "|" + offer.Port,
                    DisplayName = string.IsNullOrWhiteSpace(offer.HostName) ? offer.IPv4 : offer.HostName,
                    Detail = detail,
                    IPv4 = offer.IPv4,
                    Port = offer.Port,
                    Pin = "",
                    Fingerprint = "",
                    Rssi = 0,
                    LastSeenUtc = DateTimeOffset.UtcNow,
                    TkrId = tkrId,
                };
                QueuePeer(peer, generation);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    _ = _dispatcher.BeginInvoke(() =>
                    {
                        if (_disposed || Volatile.Read(ref _scanGeneration) != generation) return;
                        Stop();
                        Status = "Wi‑Fi find stopped: " + ex.Message + " — Scan again.";
                        Changed?.Invoke();
                    });
                break;
            }
        }
    }

    private void QueuePeer(NearbyPeer peer, int generation)
    {
        bool schedule;
        lock (_pendingGate)
        {
            if (_disposed || !IsBrowsing || Volatile.Read(ref _scanGeneration) != generation) return;
            if (_pendingPeers.Count >= MaxVisiblePeers && !_pendingPeers.ContainsKey(peer.Key)) return;
            _pendingPeers[peer.Key] = peer;
            schedule = !_pendingDispatchQueued || _pendingGeneration != generation;
            _pendingGeneration = generation;
            _pendingDispatchQueued = true;
        }

        if (schedule)
            _ = _dispatcher.InvokeAsync(() => DrainPendingPeers(generation), DispatcherPriority.Background);
    }

    private void DrainPendingPeers(int generation)
    {
        List<NearbyPeer> batch;
        lock (_pendingGate)
        {
            if (_pendingGeneration != generation || Volatile.Read(ref _scanGeneration) != generation)
                return;
            batch = _pendingPeers.Values.ToList();
            _pendingPeers.Clear();
            _pendingDispatchQueued = false;
        }

        if (!_disposed && IsBrowsing && Volatile.Read(ref _scanGeneration) == generation)
        {
            foreach (NearbyPeer peer in batch) Upsert(peer);
            if (batch.Count > 0) Changed?.Invoke();
        }
    }

    private void Upsert(NearbyPeer peer)
    {
        if (_disposed) return;
        for (int i = 0; i < Peers.Count; i++)
        {
            if (Peers[i].Key == peer.Key)
            {
                Peers.RemoveAt(i);
                Peers.Insert(i, peer);
                Status = $"Found {Peers.Count} on Wi‑Fi. Tap fills IP — type PIN + fingerprint from host, then Connect.";
                return;
            }
        }

        Peers.Insert(0, peer);
        if (Peers.Count > MaxVisiblePeers)
            Peers.RemoveAt(Peers.Count - 1);
        Status = $"Found {Peers.Count} on Wi‑Fi. Tap one — host must Accept.";
    }

    private void Expire()
    {
        if (_disposed || !IsBrowsing) return;
        DateTimeOffset cut = DateTimeOffset.UtcNow.AddSeconds(-8);
        bool removed = false;
        for (int i = Peers.Count - 1; i >= 0; i--)
        {
            if (Peers[i].LastSeenUtc < cut)
            {
                Peers.RemoveAt(i);
                removed = true;
            }
        }

        if (removed)
        {
            Status = Peers.Count == 0
                ? "Finding on Wi‑Fi… no host yet — Start sharing on the other PC."
                : $"Found {Peers.Count} on Wi‑Fi. Tap one — host must Accept.";
            Changed?.Invoke();
        }
    }

    private void Raise() => _ = _dispatcher.BeginInvoke(() => Changed?.Invoke());

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop(clearPeers: true);
    }
}
