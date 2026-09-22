using ApnaRemote.Protocol;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Radios;
using Windows.Storage.Streams;

namespace ApnaRemote.Windows.Lan;

/// <summary>
/// Publishes a compact nearby offer over Bluetooth LE while the host is listening.
/// Legacy BLE: max ~27 bytes manufacturer payload — always use the 23-byte compact offer.
/// </summary>
internal sealed class NearbyBluetoothPublisher : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _retry;
    private readonly Func<Task<BluetoothAdapter?>> _getAdapter;
    private readonly Func<BluetoothAdapter?, Task<string?>> _checkRadio;
    private int _publishGeneration;
    private global::Windows.Foundation.TypedEventHandler<BluetoothLEAdvertisementPublisher, BluetoothLEAdvertisementPublisherStatusChangedEventArgs>? _publisherStatusHandler;
    private BluetoothLEAdvertisementPublisher? _publisher;
    private NearbyOffer _lastOffer;
    private string _lastPayloadKey = "";
    private bool _hasOffer;
    private bool _startInFlight;
    private bool _disposed;
    private int _retryAttempt;

    public NearbyBluetoothPublisher(Dispatcher dispatcher,
        Func<Task<BluetoothAdapter?>>? getAdapter = null,
        Func<BluetoothAdapter?, Task<string?>>? checkRadio = null)
    {
        _dispatcher = dispatcher;
        _getAdapter = getAdapter ?? (async () => await BluetoothAdapter.GetDefaultAsync().AsTask());
        _checkRadio = checkRadio ?? CheckRadioAsync;
        _retry = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        _retry.Tick += async (_, _) =>
        {
            if (_disposed || !_hasOffer || IsAdvertising || _startInFlight) return;
            try { await StartAsync(_lastOffer).ConfigureAwait(true); }
            catch { /* status already set */ }
        };
    }

    public string Status { get; private set; } = "Nearby Bluetooth off.";
    public bool IsAdvertising { get; private set; }
    public event Action? Changed;

    public async Task StartAsync(NearbyOffer offer)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        string payloadKey = offer.IPv4 + "|" + offer.Port + "|discovery-v3";
        if (IsAdvertising && payloadKey == _lastPayloadKey && _publisher is not null)
            return;
        if (_startInFlight && payloadKey == _lastPayloadKey)
            return;

        int generation = ++_publishGeneration;
        _retry.Stop();
        StopPublisherOnly(clearHandlers: true);
        IsAdvertising = false;
        _lastPayloadKey = payloadKey;
        _lastOffer = offer;
        _hasOffer = true;
        _startInFlight = true;
        try
        {
            BluetoothAdapter? adapter = await _getAdapter().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            if (!IsCurrent(generation)) return;
            string? radioProblem = await _checkRadio(adapter).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            if (!IsCurrent(generation)) return;
            if (radioProblem is not null)
            {
                IsAdvertising = false;
                Status = radioProblem;
                _retry.Start();
                Raise();
                return;
            }

            bool extOk = adapter?.IsExtendedAdvertisingSupported == true;
            // Legacy compact first (worked in 0.18.8). Extended only if legacy fails —
            // extended-only advertise is often invisible to the other PC's scanner.
            Exception? last = null;
            foreach (bool useExtended in extOk ? new[] { false, true } : new[] { false })
            {
                try
                {
                    await AdvertiseOnceAsync(offer, useExtended).ConfigureAwait(true);
                    if (!IsCurrent(generation)) return;
                    _lastPayloadKey = payloadKey;
                    IsAdvertising = true;
                    _retryAttempt = 0;
                    _retry.Stop();
                    Status = useExtended
                        ? "Nearby Bluetooth on (extended) — other PC: Scan nearby."
                        : "Nearby Bluetooth on — other PC: Scan nearby (same room).";
                    Raise();
                    return;
                }
                catch (Exception ex)
                {
                    if (!IsCurrent(generation)) return;
                    last = ex;
                    StopPublisherOnly(clearHandlers: true);
                    IsAdvertising = false;
                }
            }

            Status = "Nearby Bluetooth unavailable: " + Friendly(last!) +
                     " — this PC may only scan, not advertise. Other PC can Share, or type IP/PIN here.";
            _retry.Interval = TimeSpan.FromSeconds(Math.Min(15, 3 + _retryAttempt * 2));
            _retryAttempt++;
            _retry.Start();
            Raise();
        }
        catch (Exception ex)
        {
            if (!IsCurrent(generation)) return;
            StopPublisherOnly(clearHandlers: true);
            IsAdvertising = false;
            Status = "Nearby Bluetooth unavailable: " + Friendly(ex);
            _retry.Start();
            Raise();
        }
        finally
        {
            if (generation == _publishGeneration) _startInFlight = false;
        }
    }

    private bool IsCurrent(int generation) => !_disposed && _hasOffer && generation == _publishGeneration;

    private async Task AdvertiseOnceAsync(NearbyOffer offer, bool useExtended)
    {
        // Discovery-safe: never broadcast PIN/fingerprint over BLE.
        byte[] payload = offer.ToDiscoveryAdvertisementPayload(includeHostName: false);
        if (payload.Length > 27)
            throw new InvalidOperationException("Nearby payload too large for Bluetooth LE.");

        StopPublisherOnly(clearHandlers: true);
        var publisher = new BluetoothLEAdvertisementPublisher();
        if (useExtended)
        {
            try { publisher.UseExtendedAdvertisement = true; }
            catch { throw new InvalidOperationException("Extended advertise not usable on this adapter."); }
        }

        using var writer = new DataWriter();
        writer.WriteBytes(payload);
        publisher.Advertisement.ManufacturerData.Clear();
        publisher.Advertisement.ManufacturerData.Add(
            new BluetoothLEManufacturerData(NearbyOffer.ManufacturerCompanyId, writer.DetachBuffer()));

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStatus(BluetoothLEAdvertisementPublisher sender, BluetoothLEAdvertisementPublisherStatusChangedEventArgs args)
        {
            if (args.Status is BluetoothLEAdvertisementPublisherStatus.Started)
                tcs.TrySetResult(true);
            else if (args.Status is BluetoothLEAdvertisementPublisherStatus.Aborted
                     or BluetoothLEAdvertisementPublisherStatus.Stopped)
            {
                tcs.TrySetException(new InvalidOperationException(
                    args.Error == BluetoothError.Success
                        ? "Bluetooth advertisement stopped."
                        : "Bluetooth advertisement failed: " + args.Error));
                _ = _dispatcher.BeginInvoke(() =>
                {
                    if (_disposed || !_hasOffer || !ReferenceEquals(_publisher, sender)) return;
                    IsAdvertising = false;
                    Status = "Nearby Bluetooth paused — retrying… (Wi‑Fi Share / IP / QR still work.)";
                    _retry.Interval = TimeSpan.FromSeconds(Math.Min(15, 3 + _retryAttempt * 2));
                    _retryAttempt++;
                    _retry.Start();
                    Changed?.Invoke();
                });
            }
        }

        _publisherStatusHandler = OnStatus;
        publisher.StatusChanged += _publisherStatusHandler;
        _publisher = publisher;
        publisher.Start();
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
    }

    public void Stop()
    {
        _dispatcher.VerifyAccess();
        _publishGeneration++;
        _startInFlight = false;
        _retry.Stop();
        _hasOffer = false;
        _lastPayloadKey = "";
        _retryAttempt = 0;
        StopPublisherOnly(clearHandlers: true);
        bool wasOn = IsAdvertising;
        IsAdvertising = false;
        if (_disposed) return;
        if (wasOn || !Status.StartsWith("Nearby Bluetooth unavailable", StringComparison.Ordinal))
            Status = "Nearby Bluetooth off — Start sharing to advertise.";
        Raise();
    }

    private void StopPublisherOnly(bool clearHandlers)
    {
        var publisher = _publisher;
        _publisher = null;
        if (publisher is null) return;
        if (clearHandlers && _publisherStatusHandler is not null)
            publisher.StatusChanged -= _publisherStatusHandler;
        _publisherStatusHandler = null;
        try { publisher.Stop(); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private void Raise() => _ = _dispatcher.BeginInvoke(() => Changed?.Invoke());

    private static async Task<string?> CheckRadioAsync(BluetoothAdapter? adapter)
    {
        try
        {
            if (adapter is null)
                return "No Bluetooth adapter — turn on Bluetooth or use Phone QR / type address.";
            if (!adapter.IsLowEnergySupported)
                return "This Bluetooth adapter has no BLE — use Phone QR / type address.";
            Radio radio = await adapter.GetRadioAsync().AsTask().ConfigureAwait(true);
            if (radio.State != RadioState.On)
                return "Bluetooth is off — turn it on in Windows settings, then Start sharing again.";
            return null;
        }
        catch (Exception ex)
        {
            return "Bluetooth check failed: " + Friendly(ex);
        }
    }

    private static string Friendly(Exception ex)
    {
        if (ex is ArgumentOutOfRangeException or ArgumentException)
            return "adapter limit";
        if (ex is TimeoutException)
            return "Bluetooth did not start in time";
        string msg = ex.Message;
        if (msg.Contains("advertisement stopped", StringComparison.OrdinalIgnoreCase))
            return "adapter interrupted advertise";
        return msg.Length > 100 ? msg[..100] + "…" : msg;
    }
}

public sealed class NearbyPeer
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public required string Detail { get; init; }
    public required string IPv4 { get; init; }
    public int Port { get; init; } = ArlProtocol.DefaultPort;
    public string ConnectionAddress => Port == ArlProtocol.DefaultPort ? IPv4 : $"{IPv4}:{Port}";
    public required string Pin { get; init; }
    public required string Fingerprint { get; init; }
    public short Rssi { get; init; }
    public DateTimeOffset LastSeenUtc { get; init; }
    public string TkrId { get; init; } = "";
}

/// <summary>Scans BLE advertisements for Apna Remote nearby offers.</summary>
internal sealed class NearbyBluetoothScanner : IDisposable
{
    private const int MaxVisiblePeers = 64;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _expire;
    private readonly DispatcherTimer _hint;
    private readonly object _pendingGate = new();
    private readonly Dictionary<string, NearbyPeer> _pendingPeers = new(StringComparer.Ordinal);
    private BluetoothLEAdvertisementWatcher? _watcher;
    private global::Windows.Foundation.TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementReceivedEventArgs>? _receivedHandler;
    private global::Windows.Foundation.TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementWatcherStoppedEventArgs>? _stoppedHandler;
    private DateTimeOffset _scanStartedUtc;
    private int _scanGeneration;
    private int _pendingGeneration;
    private BluetoothLEAdvertisementWatcher? _pendingWatcher;
    private bool _pendingDispatchQueued;
    private bool _disposed;

    public NearbyBluetoothScanner(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _expire = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        _expire.Tick += (_, _) => ExpireStale();
        _hint = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(8) };
        _hint.Tick += (_, _) => ShowEmptyHint();
    }

    public string Status { get; private set; } = "Nearby scan off.";
    public bool IsScanning { get; private set; }
    public ObservableCollection<NearbyPeer> Peers { get; } = new();
    public event Action? Changed;

    public void Start()
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        int generation = Interlocked.Increment(ref _scanGeneration);
        StopWatcherOnly(clearPeers: true);
        try
        {
            var watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active,
            };
            try
            {
                // Receive extended ads if the other PC falls back to extended; legacy still works.
                watcher.AllowExtendedAdvertisements = true;
            }
            catch { /* older OS */ }
            try
            {
                watcher.SignalStrengthFilter.InRangeThresholdInDBm = -127;
                watcher.SignalStrengthFilter.OutOfRangeThresholdInDBm = -128;
                watcher.SignalStrengthFilter.OutOfRangeTimeout = TimeSpan.FromSeconds(5);
            }
            catch { /* older stacks */ }

            _receivedHandler = (sender, args) => OnReceived(sender, args, generation);
            _stoppedHandler = (sender, args) => OnStopped(sender, args, generation);
            watcher.Received += _receivedHandler;
            watcher.Stopped += _stoppedHandler;
            _watcher = watcher;
            watcher.Start();
            IsScanning = true;
            _scanStartedUtc = DateTimeOffset.UtcNow;
            Status = "Scanning… Keep host on Start sharing. Same room helps.";
            _expire.Start();
            _hint.Start();
            Raise();
        }
        catch (Exception ex)
        {
            StopWatcherOnly(clearPeers: true);
            Status = "Nearby scan failed: " + ex.Message + " — type address/PIN or use QR.";
            Raise();
        }
    }

    public void Stop()
    {
        Interlocked.Increment(ref _scanGeneration);
        _expire.Stop();
        _hint.Stop();
        StopWatcherOnly(clearPeers: true);
        IsScanning = false;
        if (!_disposed)
        {
            Status = "Nearby scan off.";
            Raise();
        }
    }

    private void StopWatcherOnly(bool clearPeers)
    {
        _dispatcher.VerifyAccess();
        IsScanning = false;
        _expire.Stop();
        _hint.Stop();
        try
        {
            if (_watcher is { } w)
            {
                if (_receivedHandler is not null) w.Received -= _receivedHandler;
                if (_stoppedHandler is not null) w.Stopped -= _stoppedHandler;
                w.Stop();
            }
        }
        catch { /* ignore */ }

        _watcher = null;
        _receivedHandler = null;
        _stoppedHandler = null;
        lock (_pendingGate)
        {
            _pendingPeers.Clear();
            _pendingGeneration = Volatile.Read(ref _scanGeneration);
            _pendingWatcher = null;
            _pendingDispatchQueued = false;
        }
        if (clearPeers)
        {
            Peers.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _expire.Stop();
        _hint.Stop();
        StopWatcherOnly(clearPeers: true);
    }

    private void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args, int generation)
    {
        if (Volatile.Read(ref _scanGeneration) != generation || !ReferenceEquals(_watcher, sender)) return;
        _ = _dispatcher.BeginInvoke(() =>
        {
            if (Volatile.Read(ref _scanGeneration) != generation || !ReferenceEquals(_watcher, sender)) return;
            IsScanning = false;
            _expire.Stop();
            _hint.Stop();
            Status = args.Error == BluetoothError.Success
                ? "Nearby scan off."
                : "Nearby scan stopped: " + args.Error + " — Scan again.";
            Changed?.Invoke();
        });
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args, int generation)
    {
        if (_disposed || !IsScanning || Volatile.Read(ref _scanGeneration) != generation || !ReferenceEquals(_watcher, sender)) return;
        try
        {
            foreach (byte[] bytes in ExtractManufacturerPayloads(args.Advertisement))
            {
                if (!NearbyOffer.TryFromAdvertisementPayload(bytes, args.Advertisement.LocalName, out NearbyOffer offer))
                    continue;

                string key = offer.IPv4 + "|" + offer.Port;
                short rssi = args.RawSignalStrengthInDBm;
                string signal = rssi >= -60 ? "strong" : rssi >= -75 ? "ok" : "weak";
                string name = string.IsNullOrWhiteSpace(offer.HostName) || offer.HostName == offer.IPv4
                    ? "PC · " + offer.IPv4
                    : offer.HostName;
                var peer = new NearbyPeer
                {
                    Key = key,
                    DisplayName = name,
                    Detail = $"{offer.IPv4}:{offer.Port} · {signal} ({rssi} dBm) · tap fills IP only",
                    IPv4 = offer.IPv4,
                    Port = offer.Port,
                    Pin = "",
                    Fingerprint = "",
                    Rssi = rssi,
                    LastSeenUtc = DateTimeOffset.UtcNow,
                };
                QueuePeer(peer, sender, generation);
            }
        }
        catch { /* ignore malformed */ }
    }

    private static List<byte[]> ExtractManufacturerPayloads(BluetoothLEAdvertisement advertisement)
    {
        var list = new List<byte[]>();
        foreach (BluetoothLEManufacturerData md in advertisement.ManufacturerData)
        {
            if (md.CompanyId != NearbyOffer.ManufacturerCompanyId) continue;
            list.Add(md.Data.ToArray());
        }

        if (list.Count == 0)
        {
            foreach (BluetoothLEAdvertisementDataSection section in advertisement.DataSections)
            {
                if (section.DataType != 0xFF) continue;
                byte[] raw = section.Data.ToArray();
                if (raw.Length < 2) continue;
                ushort company = (ushort)(raw[0] | (raw[1] << 8));
                if (company != NearbyOffer.ManufacturerCompanyId) continue;
                if (raw.Length == 2) continue;
                byte[] payload = new byte[raw.Length - 2];
                System.Buffer.BlockCopy(raw, 2, payload, 0, payload.Length);
                list.Add(payload);
            }
        }

        return list;
    }

    private void Upsert(NearbyPeer peer)
    {
        if (_disposed) return;
        _hint.Stop();
        for (int i = 0; i < Peers.Count; i++)
        {
            if (Peers[i].Key == peer.Key)
            {
                Peers.RemoveAt(i);
                InsertSorted(peer);
                Status = $"Found {Peers.Count} nearby host(s). Tap one — host must Accept.";
                return;
            }
        }

        InsertSorted(peer);
        if (Peers.Count > MaxVisiblePeers)
            Peers.RemoveAt(Peers.Count - 1);
        Status = $"Found {Peers.Count} nearby host(s). Tap one — host must Accept.";
    }

    private void InsertSorted(NearbyPeer peer)
    {
        int i = 0;
        while (i < Peers.Count && Peers[i].Rssi >= peer.Rssi) i++;
        Peers.Insert(i, peer);
    }

    private void ShowEmptyHint()
    {
        if (_disposed || !IsScanning || Peers.Count > 0) return;
        if ((DateTimeOffset.UtcNow - _scanStartedUtc).TotalSeconds < 7) return;
        Status = "Still scanning — host Share must stay on. If host shows Nearby paused/unavailable, wait for retry or use IP/PIN.";
        Changed?.Invoke();
    }

    private void ExpireStale()
    {
        if (_disposed || !IsScanning) return;
        DateTimeOffset cut = DateTimeOffset.UtcNow.AddSeconds(-40);
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
                ? "Scanning… host signal lost — keep Share running."
                : $"Found {Peers.Count} nearby host(s). Tap one — host must Accept.";
            Changed?.Invoke();
        }
    }

    private void QueuePeer(NearbyPeer peer, BluetoothLEAdvertisementWatcher watcher, int generation)
    {
        bool schedule;
        lock (_pendingGate)
        {
            if (_disposed || !IsScanning || Volatile.Read(ref _scanGeneration) != generation || !ReferenceEquals(_watcher, watcher)) return;
            if (_pendingPeers.Count >= MaxVisiblePeers && !_pendingPeers.ContainsKey(peer.Key)) return;
            _pendingPeers[peer.Key] = peer;
            schedule = !_pendingDispatchQueued || _pendingGeneration != generation || !ReferenceEquals(_pendingWatcher, watcher);
            _pendingGeneration = generation;
            _pendingWatcher = watcher;
            _pendingDispatchQueued = true;
        }

        if (schedule)
            _ = _dispatcher.InvokeAsync(() => DrainPendingPeers(watcher, generation), DispatcherPriority.Background);
    }

    private void DrainPendingPeers(BluetoothLEAdvertisementWatcher watcher, int generation)
    {
        List<NearbyPeer> batch;
        lock (_pendingGate)
        {
            if (_pendingGeneration != generation || !ReferenceEquals(_pendingWatcher, watcher) ||
                Volatile.Read(ref _scanGeneration) != generation)
                return;
            batch = _pendingPeers.Values.ToList();
            _pendingPeers.Clear();
            _pendingDispatchQueued = false;
        }

        if (!_disposed && IsScanning && Volatile.Read(ref _scanGeneration) == generation && ReferenceEquals(_watcher, watcher))
        {
            foreach (NearbyPeer peer in batch) Upsert(peer);
            if (batch.Count > 0) Changed?.Invoke();
        }
    }

    private void Raise() => _ = _dispatcher.BeginInvoke(() => Changed?.Invoke());
}
