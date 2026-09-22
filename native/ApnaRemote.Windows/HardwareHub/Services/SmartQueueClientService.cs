using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ApnaRemote.Windows.HardwareHub.Models;

namespace ApnaRemote.Windows.HardwareHub.Services;

public sealed class SmartQueueClientService : IDisposable
{
    private string _serverIp = "127.0.0.1";
    private int _port = NetworkSpoolServer.DefaultPort;
    private bool _disposed;

    public string ServerIp => _serverIp;
    public int Port => _port;
    public event Action<string>? Log;

    public async Task<QueueStatusMessage?> GetStatusAsync(string serverIp, int port = NetworkSpoolServer.DefaultPort, CancellationToken ct = default)
    {
        _serverIp = serverIp.Trim();
        _port = port;

        return await SendCommandAsync("GET_STATUS", "", ct);
    }

    public async Task<QueueStatusMessage?> SubmitJobAsync(
        string serverIp,
        string docName,
        int pages,
        DeviceCategory category = DeviceCategory.Printer,
        string targetPrinter = "",
        byte[]? fileBytes = null,
        string pageRange = "All",
        string paperSize = "A4",
        string printOrigin = "App Upload",
        int port = NetworkSpoolServer.DefaultPort,
        CancellationToken ct = default)
    {
        _serverIp = serverIp.Trim();
        _port = port;

        var payload = JsonSerializer.Serialize(new
        {
            DocumentName = string.IsNullOrWhiteSpace(docName) ? "Job" : docName.Trim(),
            PageCount = Math.Max(1, pages),
            ClientMachineName = Environment.MachineName,
            Category = (int)category,
            TargetPrinter = targetPrinter?.Trim() ?? "",
            EstimatedSeconds = 0,
            PageRange = string.IsNullOrWhiteSpace(pageRange) ? "All" : pageRange.Trim(),
            PaperSize = string.IsNullOrWhiteSpace(paperSize) ? "A4" : paperSize.Trim(),
            PrintOrigin = string.IsNullOrWhiteSpace(printOrigin) ? "App Upload" : printOrigin.Trim(),
            FileBytesBase64 = fileBytes != null && fileBytes.Length > 0 ? Convert.ToBase64String(fileBytes) : null
        });

        return await SendCommandAsync("SUBMIT", payload, ct);
    }

    public async Task<QueueStatusMessage?> CancelJobAsync(
        string serverIp,
        string jobId,
        int port = NetworkSpoolServer.DefaultPort,
        CancellationToken ct = default)
    {
        _serverIp = serverIp.Trim();
        _port = port;

        var payload = JsonSerializer.Serialize(new { JobId = jobId.Trim() });
        return await SendCommandAsync("CANCEL", payload, ct);
    }

    private async Task<QueueStatusMessage?> SendCommandAsync(string command, string payload, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(5000);
                await client.ConnectAsync(_serverIp, _port, connectCts.Token);
            }

            using var transferCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            transferCts.CancelAfter(60000); // 60s for multi-megabyte payloads over Wi-Fi

            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            await using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            var lineToSend = string.IsNullOrWhiteSpace(payload) ? command : $"{command} {payload}";
            await writer.WriteLineAsync(lineToSend.AsMemory(), transferCts.Token);

            var respLine = await reader.ReadLineAsync(transferCts.Token);
            if (string.IsNullOrWhiteSpace(respLine)) return null;

            return JsonSerializer.Deserialize<QueueStatusMessage>(respLine);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Queue client communication error ({_serverIp}:{_port}): {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
