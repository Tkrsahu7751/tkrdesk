using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ApnaRemote.Windows.HardwareHub.Services;

public sealed class DiscoveredHub
{
    public string MachineName { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public int QueuePort { get; set; } = NetworkSpoolServer.DefaultPort;
    public int PenDrivePort { get; set; } = PenDriveServerService.DefaultPort;
    public string DisplayText => $"{MachineName} ({IpAddress})";
}

public sealed class HubDiscoveryHost : IDisposable
{
    public const int DiscoveryPort = 8991;
    private const string QueryHeader = "DISCOVER_APNA_HUB";
    private const string ReplyPrefix = "APNA_HUB_REPLY:";
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private bool _disposed;

    public bool IsRunning => _udp is not null;

    public void Start()
    {
        if (_udp is not null) return;

        try
        {
            _cts = new CancellationTokenSource();
            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
        }
        catch
        {
            Stop();
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _udp?.Close(); } catch { /* ignore */ }
        _udp = null;
        _cts = null;
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udp is not null)
        {
            try
            {
                var result = await _udp.ReceiveAsync(ct);
                var text = Encoding.UTF8.GetString(result.Buffer).Trim();
                if (text.StartsWith(QueryHeader, StringComparison.Ordinal))
                {
                    var replyObj = new DiscoveredHub
                    {
                        MachineName = Environment.MachineName,
                        IpAddress = GetLocalIpForDestination(result.RemoteEndPoint.Address),
                        QueuePort = NetworkSpoolServer.DefaultPort,
                        PenDrivePort = PenDriveServerService.DefaultPort
                    };
                    var json = JsonSerializer.Serialize(replyObj);
                    var replyBytes = Encoding.UTF8.GetBytes(ReplyPrefix + json);
                    await _udp.SendAsync(replyBytes, replyBytes.Length, result.RemoteEndPoint);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* ignore transient receive errors */ }
        }
    }

    private static string GetLocalIpForDestination(IPAddress dest)
    {
        if (IPAddress.IsLoopback(dest)) return "127.0.0.1";
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            s.Connect(dest, 65530);
            if (s.LocalEndPoint is IPEndPoint ep) return ep.Address.ToString();
        }
        catch { /* ignore */ }

        // Fallback to first non-loopback IPv4
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ip in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip.Address))
                        return ip.Address.ToString();
                }
            }
        }
        catch { /* ignore */ }

        return "127.0.0.1";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}

public static class HubDiscoveryClient
{
    private const string QueryHeader = "DISCOVER_APNA_HUB";
    private const string ReplyPrefix = "APNA_HUB_REPLY:";

    public static async Task<IReadOnlyList<DiscoveredHub>> ScanAsync(int timeoutMs = 1200, CancellationToken ct = default)
    {
        var found = new Dictionary<string, DiscoveredHub>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var udp = new UdpClient();
            udp.EnableBroadcast = true;
            udp.Client.ReceiveTimeout = timeoutMs;

            var queryBytes = Encoding.UTF8.GetBytes(QueryHeader);

            // Broadcast to 255.255.255.255
            try
            {
                await udp.SendAsync(queryBytes, queryBytes.Length, new IPEndPoint(IPAddress.Broadcast, HubDiscoveryHost.DiscoveryPort));
            }
            catch { /* ignore */ }

            // Also send to loopback for single-PC / local testing
            try
            {
                await udp.SendAsync(queryBytes, queryBytes.Length, new IPEndPoint(IPAddress.Loopback, HubDiscoveryHost.DiscoveryPort));
            }
            catch { /* ignore */ }

            // Also broadcast on each active NIC's subnet broadcast address
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;

                    foreach (var u in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.IPv4Mask is null)
                            continue;

                        var ipBytes = u.Address.GetAddressBytes();
                        var maskBytes = u.IPv4Mask.GetAddressBytes();
                        var bcastBytes = new byte[4];
                        for (int i = 0; i < 4; i++)
                            bcastBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);

                        var bcastIp = new IPAddress(bcastBytes);
                        try
                        {
                            await udp.SendAsync(queryBytes, queryBytes.Length, new IPEndPoint(bcastIp, HubDiscoveryHost.DiscoveryPort));
                        }
                        catch { /* ignore */ }
                    }
                }
            }
            catch { /* ignore */ }

            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            while (!linked.Token.IsCancellationRequested)
            {
                try
                {
                    var result = await udp.ReceiveAsync(linked.Token);
                    var text = Encoding.UTF8.GetString(result.Buffer).Trim();
                    if (text.StartsWith(ReplyPrefix, StringComparison.Ordinal))
                    {
                        var json = text[ReplyPrefix.Length..];
                        var hub = JsonSerializer.Deserialize<DiscoveredHub>(json);
                        if (hub is not null && !string.IsNullOrWhiteSpace(hub.IpAddress))
                        {
                            // If sender is loopback, keep loopback; else use reported or actual endpoint IP
                            var actualIp = result.RemoteEndPoint.Address.ToString();
                            if (actualIp != "127.0.0.1" && !string.IsNullOrWhiteSpace(hub.IpAddress))
                                actualIp = hub.IpAddress;

                            hub.IpAddress = actualIp;
                            found[actualIp] = hub;
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { break; }
            }
        }
        catch { /* ignore */ }

        return found.Values.OrderBy(h => h.MachineName).ToList();
    }
}
