using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ApnaRemote.Windows.Lan;

namespace ApnaRemote.Windows.LabGrid;

public sealed class LabGridPcItem
{
    public required string HostName { get; init; }
    public required string TkrId { get; init; }
    public required string IPv4 { get; init; }
    public int Port { get; init; } = 5720;
    public string StatusText { get; set; } = "Ready · Online";
    public string BadgeColor { get; set; } = "#10b981"; // Emerald green
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    public bool IsSharing { get; set; }

    public string DisplaySubtitle => $"TKR ID: {TkrId} · IP: {IPv4}";
}

/// <summary>
/// Central Multi-PC Lab Grid Service.
/// Continuously tracks all online workstations on the local network in a live CCTV-style grid,
/// enables one-click remote desktop takeover, and distributes broadcast announcements.
/// </summary>
public sealed class LabGridService : IDisposable
{
    public const int LabNoticePort = 5728;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _pollTimer;
    private UdpClient? _noticeListener;
    private bool _disposed;

    public ObservableCollection<LabGridPcItem> Workstations { get; } = new();

    public event Action<string, string>? NoticeReceived;

    public LabGridService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _pollTimer.Tick += (_, _) => RefreshFromDiscovery();
        _pollTimer.Start();

        StartNoticeListener();
    }

    private void StartNoticeListener()
    {
        try
        {
            _noticeListener = new UdpClient();
            _noticeListener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _noticeListener.Client.Bind(new IPEndPoint(IPAddress.Any, LabNoticePort));
            _ = Task.Run(ListenForNoticesAsync);
        }
        catch
        {
            // Port might be in use
        }
    }

    private async Task ListenForNoticesAsync()
    {
        while (!_disposed && _noticeListener is not null)
        {
            try
            {
                UdpReceiveResult res = await _noticeListener.ReceiveAsync();
                string text = Encoding.UTF8.GetString(res.Buffer);
                if (text.StartsWith("TKR_NOTICE\t", StringComparison.Ordinal))
                {
                    string[] parts = text.Split('\t', 3);
                    if (parts.Length >= 3)
                    {
                        string sender = parts[1];
                        string message = parts[2];
                        _ = _dispatcher.BeginInvoke(() => NoticeReceived?.Invoke(sender, message));
                    }
                }
            }
            catch
            {
                break;
            }
        }
    }

    public void RefreshFromDiscovery()
    {
        if (_disposed) return;
        var peers = TkrIdService.GetAllDiscoveredPeers();

        // Also add local machine so teacher/admin sees their own master status
        string localTkrId = TkrIdService.GetLocalTkrId();
        string localName = Environment.MachineName + " (This PC)";

        var allItems = peers.ToList();

        // Upsert or update in Workstations
        foreach (var p in allItems)
        {
            var existing = Workstations.FirstOrDefault(w => w.TkrId == p.TkrId || w.IPv4 == p.IPv4);
            if (existing is null)
            {
                Workstations.Add(new LabGridPcItem
                {
                    HostName = p.HostName,
                    TkrId = p.TkrId,
                    IPv4 = p.IPv4,
                    Port = p.Port,
                    StatusText = "Online · Ready",
                    BadgeColor = "#10b981",
                    LastSeen = p.LastSeen
                });
            }
            else
            {
                existing.LastSeen = p.LastSeen;
                existing.StatusText = "Online · Ready";
                existing.BadgeColor = "#10b981";
            }
        }

        // Remove PCs not seen for > 10 minutes
        var cutoff = DateTime.UtcNow.AddMinutes(-10);
        for (int i = Workstations.Count - 1; i >= 0; i--)
        {
            if (Workstations[i].LastSeen < cutoff)
                Workstations.RemoveAt(i);
        }
    }

    /// <summary>
    /// Broadcast an alert or announcement to all PCs across the lab/cyber cafe network.
    /// </summary>
    public async Task BroadcastAnnouncementAsync(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        try
        {
            using var udp = new UdpClient();
            udp.EnableBroadcast = true;
            byte[] payload = Encoding.UTF8.GetBytes($"TKR_NOTICE\t{Environment.MachineName}\t{message.Trim()}");
            await udp.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Broadcast, LabNoticePort));
        }
        catch
        {
            // Broadcast transmission best-effort
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _pollTimer.Stop();
        try { _noticeListener?.Dispose(); } catch { }
        _noticeListener = null;
    }
}
