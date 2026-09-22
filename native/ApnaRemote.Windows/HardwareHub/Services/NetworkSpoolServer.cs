using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ApnaRemote.Windows.HardwareHub.Models;

namespace ApnaRemote.Windows.HardwareHub.Services;

public sealed class NetworkSpoolServer : IDisposable
{
    public const int DefaultPort = 8989;
    private readonly SmartQueueManager _queueManager;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private bool _disposed;

    public int Port { get; set; } = DefaultPort;
    public bool IsRunning => _listener is not null;
    public event Action<string>? Log;

    public NetworkSpoolServer(SmartQueueManager queueManager)
    {
        _queueManager = queueManager;
    }

    public void Start()
    {
        if (_listener is not null)
            return;

        _cts = new CancellationTokenSource();
        _queueManager.Start();

        try
        {
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start();
            _acceptTask = Task.Run(() => AcceptClientsLoopAsync(_cts.Token));
            Log?.Invoke($"Office Queue Server listening on TCP port {Port}…");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Could not start Office Queue Server on port {Port}: {ex.Message}");
            Stop();
            throw;
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch { /* ignore */ }

        _listener = null;
        _queueManager.Stop();
        Log?.Invoke("Office Queue Server stopped.");
    }

    private async Task AcceptClientsLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    Log?.Invoke($"Queue Server accept error: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            var clientIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "127.0.0.1";
            _queueManager.RegisterClientPing(clientIp);

            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                while (!ct.IsCancellationRequested && client.Connected)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (string.IsNullOrWhiteSpace(line))
                        break;

                    var response = ProcessCommand(line.Trim(), clientIp);
                    await writer.WriteLineAsync(response);
                }
            }
            catch (Exception)
            {
                // Normal disconnect
            }
        }
    }

    private string ProcessCommand(string line, string clientIp)
    {
        try
        {
            var parts = line.Split(' ', 2);
            var cmd = parts[0].ToUpperInvariant();
            var payload = parts.Length > 1 ? parts[1] : "";

            switch (cmd)
            {
                case "PING":
                    _queueManager.RegisterClientPing(clientIp);
                    return JsonSerializer.Serialize(_queueManager.GetStatusSnapshot(clientIp));

                case "GET_STATUS":
                    return JsonSerializer.Serialize(_queueManager.GetStatusSnapshot(clientIp));

                case "SUBMIT":
                    using (var doc = JsonDocument.Parse(payload))
                    {
                        var root = doc.RootElement;
                        var docName = root.TryGetProperty("DocumentName", out var d) ? d.GetString() ?? "Document" : "Document";
                        var pages = root.TryGetProperty("PageCount", out var p) ? p.GetInt32() : 1;
                        var machineName = root.TryGetProperty("ClientMachineName", out var m) ? m.GetString() ?? clientIp : clientIp;
                        var cat = root.TryGetProperty("Category", out var c) ? (DeviceCategory)c.GetInt32() : DeviceCategory.Printer;
                        var est = root.TryGetProperty("EstimatedSeconds", out var e) ? e.GetInt32() : 15;
                        var targetPrinter = root.TryGetProperty("TargetPrinter", out var tp) ? tp.GetString() ?? "" : "";
                        var pageRange = root.TryGetProperty("PageRange", out var pr) ? pr.GetString() ?? "All" : "All";
                        var paperSize = root.TryGetProperty("PaperSize", out var ps) ? ps.GetString() ?? "A4" : "A4";
                        var printOrigin = root.TryGetProperty("PrintOrigin", out var po) ? po.GetString() ?? "App Upload" : "App Upload";

                        string? payloadPath = null;
                        if (root.TryGetProperty("FileBytesBase64", out var fb) && fb.ValueKind == JsonValueKind.String)
                        {
                            var base64 = fb.GetString();
                            if (!string.IsNullOrWhiteSpace(base64))
                            {
                                try
                                {
                                    var bytes = Convert.FromBase64String(base64);
                                    var spoolDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ApnaRemote", "Spool");
                                    Directory.CreateDirectory(spoolDir);

                                    var rawDocName = string.IsNullOrWhiteSpace(docName) ? "Document" : docName;
                                    var invalidChars = Path.GetInvalidFileNameChars();
                                    var safeDoc = string.Concat(rawDocName.Select(ch => invalidChars.Contains(ch) ? '_' : ch)).Trim();
                                    if (string.IsNullOrWhiteSpace(safeDoc)) safeDoc = "Document";

                                    var ext = Path.GetExtension(safeDoc);
                                    var nameWithoutExt = Path.GetFileNameWithoutExtension(safeDoc);
                                    if (string.IsNullOrWhiteSpace(nameWithoutExt)) nameWithoutExt = "PrintJob";
                                    if (string.IsNullOrWhiteSpace(ext)) ext = ".pdf";

                                    payloadPath = Path.Combine(spoolDir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}_{nameWithoutExt}{ext}");
                                    File.WriteAllBytes(payloadPath, bytes);
                                }
                                catch (Exception ex)
                                {
                                    payloadPath = null;
                                    Log?.Invoke($"[SpoolServer] Failed to write spool file for '{docName}': {ex.Message}");
                                }
                            }
                        }

                        _ = _queueManager.Enqueue(
                            clientIp,
                            machineName,
                            docName,
                            pages,
                            cat,
                            est,
                            payloadPath: payloadPath,
                            targetPrinter: targetPrinter,
                            pageRange: pageRange,
                            paperSize: paperSize,
                            printOrigin: printOrigin);
                        return JsonSerializer.Serialize(_queueManager.GetStatusSnapshot(clientIp));
                    }

                case "CANCEL":
                    using (var doc = JsonDocument.Parse(payload))
                    {
                        var root = doc.RootElement;
                        var jobId = root.TryGetProperty("JobId", out var j) ? j.GetString() ?? "" : "";
                        _queueManager.CancelJob(jobId, clientIp, out _);
                        return JsonSerializer.Serialize(_queueManager.GetStatusSnapshot(clientIp));
                    }

                default:
                    return JsonSerializer.Serialize(new { error = "Unknown command" });
            }
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
