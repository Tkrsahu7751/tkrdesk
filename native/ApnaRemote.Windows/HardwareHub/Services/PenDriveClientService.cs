using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ApnaRemote.Windows.HardwareHub.Models;

namespace ApnaRemote.Windows.HardwareHub.Services;

public sealed class PenDriveClientService : IDisposable
{
    private string _hostIp = "";
    private int _port = PenDriveServerService.DefaultPort;
    private string _sessionToken = "";
    private string _currentPath = "";
    private bool _disposed;

    public string HostIp => _hostIp;
    public bool IsConnected { get; private set; }
    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_sessionToken);
    public bool IsReadOnly { get; private set; }
    public string VolumeLabel { get; private set; } = "Shared Pen Drive";
    public string WindowsShareUnc { get; private set; } = "";
    public string CurrentPath => _currentPath;

    public event Action<bool>? ConnectionStateChanged;
    public event Action<IReadOnlyList<FileEntry>>? FilesUpdated;
    public event Action<string>? Log;

    public async Task<PenDriveAccessResponse> ConnectAndAuthAsync(
        string hostIp,
        string pin,
        int port = PenDriveServerService.DefaultPort,
        bool remember = false,
        CancellationToken ct = default)
    {
        Disconnect();
        _hostIp = hostIp.Trim();
        _port = port;

        try
        {
            using var client = new TcpClient();
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(4000);

            await client.ConnectAsync(_hostIp, _port, connectCts.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            await using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            var req = new PenDriveAccessRequest
            {
                ClientMachineName = Environment.MachineName,
                ClientIp = "",
                Pin = pin,
                RememberMe = remember
            };

            await writer.WriteLineAsync($"AUTH {JsonSerializer.Serialize(req)}");

            var responseLine = await reader.ReadLineAsync(connectCts.Token);
            if (string.IsNullOrWhiteSpace(responseLine))
                return new PenDriveAccessResponse { Granted = false, Message = "Server returned empty response." };

            var resp = JsonSerializer.Deserialize<PenDriveAccessResponse>(responseLine);
            if (resp is null)
                return new PenDriveAccessResponse { Granted = false, Message = "Failed to parse server response." };

            if (resp.Granted)
            {
                _sessionToken = resp.SessionToken;
                IsReadOnly = resp.IsReadOnly;
                VolumeLabel = resp.VolumeLabel;
                WindowsShareUnc = resp.WindowsShareUnc;
                IsConnected = true;
                ConnectionStateChanged?.Invoke(true);
                Log?.Invoke($"Connected to Pen Drive on {_hostIp} ({VolumeLabel}). Read-Only: {IsReadOnly}");
            }
            else
            {
                Log?.Invoke($"Access denied by {_hostIp}: {resp.Message}");
            }

            return resp;
        }
        catch (Exception ex)
        {
            Disconnect();
            Log?.Invoke($"Failed to connect to Pen Drive on {_hostIp}: {ex.Message}");
            return new PenDriveAccessResponse { Granted = false, Message = $"Connection failed: {ex.Message}" };
        }
    }

    public async Task<IReadOnlyList<FileEntry>> ListFilesAsync(string relativePath = "", CancellationToken ct = default)
    {
        if (!IsAuthenticated)
            throw new InvalidOperationException("Not authenticated with Host Pen Drive.");

        try
        {
            using var client = new TcpClient();
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(5000);
                await client.ConnectAsync(_hostIp, _port, connectCts.Token);
            }
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            await using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            await writer.WriteLineAsync($"LIST {_sessionToken} {relativePath}");

            var responseLine = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(responseLine) || !responseLine.StartsWith("OK "))
            {
                Log?.Invoke($"List error: {responseLine}");
                return Array.Empty<FileEntry>();
            }

            var json = responseLine[3..];
            var files = JsonSerializer.Deserialize<List<FileEntry>>(json) ?? new List<FileEntry>();

            _currentPath = relativePath;
            FilesUpdated?.Invoke(files);
            return files;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Failed to list files on Pen Drive: {ex.Message}");
            return Array.Empty<FileEntry>();
        }
    }

    public async Task<bool> DownloadFileAsync(string remoteRelativePath, string localDestinationPath, CancellationToken ct = default)
    {
        if (!IsAuthenticated)
            throw new InvalidOperationException("Not authenticated with Host Pen Drive.");

        try
        {
            using var client = new TcpClient();
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(5000);
                await client.ConnectAsync(_hostIp, _port, connectCts.Token);
            }
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            await using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            await writer.WriteLineAsync($"DOWNLOAD {_sessionToken} {remoteRelativePath}");

            var header = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("OK "))
            {
                Log?.Invoke($"Download error: {header}");
                return false;
            }

            if (!long.TryParse(header[3..], out var fileSize))
            {
                Log?.Invoke("Invalid file size in download response.");
                return false;
            }

            var dir = Path.GetDirectoryName(localDestinationPath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            await using (var fileStream = File.Create(localDestinationPath))
            {
                var buffer = new byte[65536];
                long bytesRemaining = fileSize;
                while (bytesRemaining > 0)
                {
                    var toRead = (int)Math.Min(buffer.Length, bytesRemaining);
                    var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), ct);
                    if (read == 0) break;
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    bytesRemaining -= read;
                }
            }

            Log?.Invoke($"Downloaded '{Path.GetFileName(localDestinationPath)}' ({fileSize} bytes) successfully.");
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Download failed: {ex.Message}");
            return false;
        }
    }

    public void NavigateUp()
    {
        if (string.IsNullOrWhiteSpace(_currentPath)) return;
        var parts = _currentPath.Replace('\\', '/').Trim('/').Split('/');
        if (parts.Length <= 1)
            _currentPath = "";
        else
            _currentPath = string.Join('/', parts.Take(parts.Length - 1));
    }

    public bool OpenInWindowsExplorer()
    {
        if (string.IsNullOrWhiteSpace(WindowsShareUnc)) return false;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = WindowsShareUnc,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Could not open Windows Explorer: {ex.Message}");
            return false;
        }
    }

    public void Disconnect()
    {
        _sessionToken = "";
        _currentPath = "";
        IsConnected = false;
        ConnectionStateChanged?.Invoke(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }
}
