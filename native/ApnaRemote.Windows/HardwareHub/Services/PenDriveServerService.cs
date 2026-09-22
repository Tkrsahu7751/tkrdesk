using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ApnaRemote.Windows.HardwareHub.Models;

namespace ApnaRemote.Windows.HardwareHub.Services;

public sealed class PenDriveServerService : IDisposable
{
    public const int DefaultPort = 8990;
    private readonly object _gate = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;
    private readonly ConcurrentDictionary<string, string> _authenticatedSessions = new();
    private readonly ConcurrentDictionary<string, AllowedClientItem> _allowedClients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _activeClients = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public int Port { get; set; } = DefaultPort;
    public string DriveRoot { get; set; } = "";
    public string Pin { get; set; } = "1234";
    public bool IsReadOnly { get; set; }
    public string ShareName { get; set; } = "ApnaPenDrive";
    public bool IsRunning => _listener is not null;

    public int TotalConnectedClients
    {
        get
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-60);
            return _activeClients.Values.Count(v => v >= cutoff);
        }
    }

    public event Action? ClientsChanged;
    public event Action<string>? Log;

    public static IReadOnlyList<PenDriveInfo> GetRemovableDrives()
    {
        var list = new List<PenDriveInfo>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    if (d.DriveType is DriveType.Removable or DriveType.Fixed)
                    {
                        var isSystem = d.RootDirectory.FullName.StartsWith("C:", StringComparison.OrdinalIgnoreCase);
                        if (d.DriveType == DriveType.Removable || !isSystem)
                        {
                            list.Add(new PenDriveInfo
                            {
                                DriveLetter = d.RootDirectory.FullName,
                                VolumeLabel = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "USB Drive" : d.VolumeLabel,
                                DriveFormat = d.DriveFormat,
                                TotalSizeBytes = d.TotalSize,
                                FreeSpaceBytes = d.AvailableFreeSpace
                            });
                        }
                    }
                }
                catch { /* ignore drive access errors */ }
            }
        }
        catch { /* ignore */ }

        return list;
    }

    public IReadOnlyList<AllowedClientItem> GetAllowedClients()
    {
        return _allowedClients.Values.OrderBy(c => c.MachineName).ToList();
    }

    public void SetClientAllowed(string clientIpOrName, bool allowed)
    {
        if (_allowedClients.TryGetValue(clientIpOrName, out var item))
        {
            item.IsAllowed = allowed;
            ClientsChanged?.Invoke();
        }
    }

    public void RegisterOrUpdateClient(string ip, string machineName)
    {
        if (string.IsNullOrWhiteSpace(ip)) return;
        var key = ip.Trim();
        _allowedClients.AddOrUpdate(key,
            _ => new AllowedClientItem { IpAddress = key, MachineName = machineName, IsAllowed = true },
            (_, existing) =>
            {
                existing.MachineName = machineName;
                existing.LastSeenUtc = DateTime.UtcNow;
                return existing;
            });
        ClientsChanged?.Invoke();
    }

    public void Start(string driveRoot, string pin, bool isReadOnly = false, int port = DefaultPort)
    {
        lock (_gate)
        {
            Stop();

            if (string.IsNullOrWhiteSpace(driveRoot))
                throw new ArgumentException("Drive root cannot be empty.", nameof(driveRoot));

            DriveRoot = driveRoot;
            Pin = string.IsNullOrWhiteSpace(pin) ? "1234" : pin.Trim();
            IsReadOnly = isReadOnly;
            Port = port;

            try
            {
                _cts = new CancellationTokenSource();
                _listener = new TcpListener(IPAddress.Any, Port);
                _listener.Start();

                _listenerTask = Task.Run(() => AcceptLoopAsync(_listener, _cts.Token));
                Log?.Invoke($"[PenDrive] Broadcaster started on TCP {Port} for '{DriveRoot}' (PIN: {Pin}, Read-Only: {IsReadOnly})");

                _ = Task.Run(() => CreateWindowsShare(DriveRoot, ShareName, IsReadOnly));
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[PenDrive] Failed to start on TCP {Port}: {ex.Message}");
                Stop();
                throw;
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            try { _listener?.Stop(); } catch { /* ignore */ }
            try { _listenerTask?.Wait(1000); } catch { /* ignore */ }

            _cts?.Dispose();
            _cts = null;
            _listener = null;
            _listenerTask = null;
            _authenticatedSessions.Clear();
            _activeClients.Clear();

            _ = Task.Run(() => RemoveWindowsShare(ShareName));
            Log?.Invoke("[PenDrive] Broadcaster stopped.");
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                Log?.Invoke($"[PenDrive] Accept error: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            var endpoint = client.Client.RemoteEndPoint as IPEndPoint;
            var clientIp = endpoint?.Address.ToString() ?? "unknown";

            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

                while (!ct.IsCancellationRequested && client.Connected)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (string.IsNullOrWhiteSpace(line)) break;

                    var parts = line.Split(' ', 2);
                    var cmd = parts[0].ToUpperInvariant();
                    var arg = parts.Length > 1 ? parts[1].Trim() : "";

                    bool isNew = !_activeClients.TryGetValue(clientIp, out var last) || (DateTime.UtcNow - last).TotalSeconds > 60;
                    _activeClients[clientIp] = DateTime.UtcNow;
                    if (isNew) ClientsChanged?.Invoke();

                    switch (cmd)
                    {
                        case "PING":
                            await writer.WriteLineAsync($"PONG {GetDriveSummaryJson()}");
                            break;

                        case "AUTH":
                            var authResp = HandleAuth(arg, clientIp);
                            await writer.WriteLineAsync(JsonSerializer.Serialize(authResp));
                            break;

                        case "LIST":
                            var listResp = HandleList(arg, clientIp);
                            await writer.WriteLineAsync(listResp);
                            break;

                        case "DOWNLOAD":
                            await HandleDownloadAsync(arg, clientIp, stream, writer, ct);
                            return;

                        case "UPLOAD":
                            await HandleUploadAsync(arg, clientIp, stream, writer, ct);
                            return;

                        default:
                            await writer.WriteLineAsync("ERROR Unknown command");
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    Log?.Invoke($"[PenDrive] Client {clientIp} handler error: {ex.Message}");
            }
            finally
            {
                // Prune stale client entries older than 5 minutes
                var staleCutoff = DateTime.UtcNow.AddMinutes(-5);
                foreach (var pair in _activeClients)
                {
                    if (pair.Value < staleCutoff)
                        _activeClients.TryRemove(pair.Key, out _);
                }
            }
        }
    }

    private PenDriveAccessResponse HandleAuth(string jsonArg, string clientIp)
    {
        try
        {
            var req = JsonSerializer.Deserialize<PenDriveAccessRequest>(jsonArg) ?? new PenDriveAccessRequest();
            RegisterOrUpdateClient(clientIp, req.ClientMachineName);

            if (_allowedClients.TryGetValue(clientIp, out var allowedItem) && !allowedItem.IsAllowed)
            {
                Log?.Invoke($"[PenDrive] BLOCKED connection from {req.ClientMachineName} ({clientIp}) - not in allowed whitelist.");
                return new PenDriveAccessResponse
                {
                    Granted = false,
                    Message = "Access Denied: This computer has been blocked in Allowed PCs by the Host."
                };
            }

            if (!string.Equals(req.Pin?.Trim(), Pin, StringComparison.Ordinal))
            {
                Log?.Invoke($"[PenDrive] Failed PIN attempt from {req.ClientMachineName} ({clientIp}).");
                return new PenDriveAccessResponse
                {
                    Granted = false,
                    Message = "Incorrect Security PIN. Please enter the valid 4-digit PIN."
                };
            }

            var token = Guid.NewGuid().ToString("N");
            _authenticatedSessions[token] = clientIp;

            var dInfo = new DriveInfo(DriveRoot);
            Log?.Invoke($"[PenDrive] Access GRANTED to {req.ClientMachineName} ({clientIp}).");

            return new PenDriveAccessResponse
            {
                Granted = true,
                Message = "Access Granted! Pen Drive is ready.",
                SessionToken = token,
                IsReadOnly = IsReadOnly,
                VolumeLabel = string.IsNullOrWhiteSpace(dInfo.VolumeLabel) ? "USB Drive" : dInfo.VolumeLabel,
                WindowsShareUnc = $"\\\\{GetLocalIp()}\\{ShareName}",
                TotalSizeBytes = dInfo.TotalSize,
                FreeSpaceBytes = dInfo.AvailableFreeSpace
            };
        }
        catch (Exception ex)
        {
            return new PenDriveAccessResponse { Granted = false, Message = $"Error: {ex.Message}" };
        }
    }

    private string HandleList(string arg, string clientIp)
    {
        var split = arg.Split(' ', 2);
        if (split.Length < 1 || !ValidateSession(split[0], clientIp))
            return "ERROR Unauthorized";

        var relPath = split.Length > 1 ? split[1] : "";
        var fullPath = GetSafeFullPath(relPath);
        if (fullPath is null || !Directory.Exists(fullPath))
            return "ERROR Directory not found";

        var list = new List<FileEntry>();
        try
        {
            var dir = new DirectoryInfo(fullPath);
            foreach (var subDir in dir.GetDirectories())
            {
                if ((subDir.Attributes & FileAttributes.Hidden) != 0) continue;
                list.Add(new FileEntry
                {
                    Name = subDir.Name,
                    RelativePath = Path.GetRelativePath(DriveRoot, subDir.FullName),
                    IsDirectory = true,
                    SizeBytes = 0,
                    ModifiedTime = subDir.LastWriteTime,
                    Extension = ""
                });
            }

            foreach (var file in dir.GetFiles())
            {
                if ((file.Attributes & FileAttributes.Hidden) != 0) continue;
                list.Add(new FileEntry
                {
                    Name = file.Name,
                    RelativePath = Path.GetRelativePath(DriveRoot, file.FullName),
                    IsDirectory = false,
                    SizeBytes = file.Length,
                    ModifiedTime = file.LastWriteTime,
                    Extension = file.Extension
                });
            }
        }
        catch (Exception ex)
        {
            return $"ERROR {ex.Message}";
        }

        return "OK " + JsonSerializer.Serialize(list);
    }

    private async Task HandleDownloadAsync(string arg, string clientIp, NetworkStream stream, StreamWriter writer, CancellationToken ct)
    {
        var split = arg.Split(' ', 2);
        if (split.Length < 2 || !ValidateSession(split[0], clientIp))
        {
            await writer.WriteLineAsync("ERROR Unauthorized");
            return;
        }

        var relPath = split[1];
        var fullPath = GetSafeFullPath(relPath);
        if (fullPath is null || !File.Exists(fullPath))
        {
            await writer.WriteLineAsync("ERROR File not found");
            return;
        }

        var fileInfo = new FileInfo(fullPath);
        await writer.WriteLineAsync($"OK {fileInfo.Length}");
        await writer.FlushAsync(ct);

        await using var fileStream = File.OpenRead(fullPath);
        await fileStream.CopyToAsync(stream, ct);
        await stream.FlushAsync(ct);
        Log?.Invoke($"[PenDrive] Transferred '{fileInfo.Name}' ({fileInfo.Length} bytes) to {clientIp}.");
    }

    private async Task HandleUploadAsync(string arg, string clientIp, NetworkStream stream, StreamWriter writer, CancellationToken ct)
    {
        if (IsReadOnly)
        {
            await writer.WriteLineAsync("ERROR Read-only mode is active on Host. Cannot upload files.");
            return;
        }

        var split = arg.Split(' ', 3);
        if (split.Length < 3 || !ValidateSession(split[0], clientIp))
        {
            await writer.WriteLineAsync("ERROR Unauthorized");
            return;
        }

        if (!long.TryParse(split[1], out var fileSize) || fileSize < 0)
        {
            await writer.WriteLineAsync("ERROR Invalid file size");
            return;
        }

        var relPath = split[2];
        var fullPath = GetSafeFullPath(relPath);
        if (fullPath is null)
        {
            await writer.WriteLineAsync("ERROR Invalid path");
            return;
        }

        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await writer.WriteLineAsync("READY");
        await writer.FlushAsync(ct);

        await using (var fileStream = File.Create(fullPath))
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

        await writer.WriteLineAsync("OK Upload complete");
        Log?.Invoke($"[PenDrive] Received uploaded file '{Path.GetFileName(fullPath)}' ({fileSize} bytes) from {clientIp}.");
    }

    private bool ValidateSession(string token, string clientIp)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        if (_authenticatedSessions.TryGetValue(token, out var ip))
        {
            if (ip == clientIp || clientIp == "127.0.0.1" || clientIp == "localhost") return true;
        }
        return false;
    }

    public string? GetSafeFullPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(DriveRoot)) return null;
        var fullRoot = Path.GetFullPath(DriveRoot);
        var safeRoot = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalized = (relativePath ?? "").Trim().TrimStart('/', '\\');
        var combined = Path.GetFullPath(Path.Combine(safeRoot, normalized));
        if (!combined.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(combined + Path.DirectorySeparatorChar, safeRoot, StringComparison.OrdinalIgnoreCase))
            return null;
        return combined;
    }

    private string GetDriveSummaryJson()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(DriveRoot)) return "{}";
            var d = new DriveInfo(DriveRoot);
            return JsonSerializer.Serialize(new
            {
                DriveRoot,
                VolumeLabel = d.VolumeLabel,
                TotalSize = d.TotalSize,
                FreeSpace = d.AvailableFreeSpace,
                IsReadOnly
            });
        }
        catch { return "{}"; }
    }

    private static string GetLocalIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            if (socket.LocalEndPoint is IPEndPoint endPoint)
                return endPoint.Address.ToString();
        }
        catch { /* ignore */ }
        return "127.0.0.1";
    }

    private static void CreateWindowsShare(string folderPath, string shareName, bool isReadOnly)
    {
        try
        {
            var perm = isReadOnly ? "READ" : "FULL";
            var cleanPath = folderPath.TrimEnd('\\');
            var psi = new ProcessStartInfo
            {
                FileName = "net.exe",
                Arguments = $"share {shareName}=\"{cleanPath}\" /grant:Everyone,{perm}",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
        }
        catch { /* ignore best effort */ }
    }

    private static void RemoveWindowsShare(string shareName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "net.exe",
                Arguments = $"share {shareName} /delete /y",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
        }
        catch { /* ignore best effort */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
