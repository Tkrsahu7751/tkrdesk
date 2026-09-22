using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Printing;

namespace ApnaRemote.Windows.HardwareHub.Services;

public sealed class VirtualPrinterSpoolBridge : IDisposable
{
    public const string PortName = "TkrDeskPort";
    public const string DefaultPrinterName = "TKR Desk Wireless Printer";
    public static string ActivePrinterName { get; set; } = DefaultPrinterName;
    public const int RawPrintPort = 9100;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private bool _disposed;

    public bool IsListening => _listener is not null;
    public event Action<byte[], string>? JobIntercepted;
    public event Action<string>? Log;

    public static bool CheckIfPrinterInstalled(string? name = null)
    {
        var targetName = !string.IsNullOrWhiteSpace(name) ? name : ActivePrinterName;
        try
        {
            using var server = new LocalPrintServer();
            using var queue = server.GetPrintQueue(targetName);
            return queue is not null;
        }
        catch
        {
            try
            {
                using var server = new LocalPrintServer();
                using var queue = server.GetPrintQueue("TKR Desk Printer");
                return queue is not null;
            }
            catch
            {
                return false;
            }
        }
    }

    public static async Task<(bool Ok, string Message)> EnsurePrinterInstalledAsync(string? targetName = null, CancellationToken ct = default)
    {
        var printerName = !string.IsNullOrWhiteSpace(targetName) ? targetName : ActivePrinterName;

        try
        {
            // Use PowerShell with built-in PrintManagement cmdlets to configure port and printer safely
            var script = $@"
$ErrorActionPreference = 'SilentlyContinue'
$port = Get-PrinterPort -Name '{PortName}'
if (-not $port) {{
    Add-PrinterPort -Name '{PortName}' -PrinterHostAddress '127.0.0.1' -PortNumber {RawPrintPort}
}}

# Choose best available native or raster driver
$installedDrivers = Get-PrinterDriver | Select-Object -ExpandProperty Name
$driver = 'Generic / Text Only'
if ($installedDrivers -contains 'Brother DCP-T220 Printer') {{
    $driver = 'Brother DCP-T220 Printer'
}} elseif ($installedDrivers -contains 'Brother DCP-T220') {{
    $driver = 'Brother DCP-T220'
}} elseif ($installedDrivers -contains 'HP LaserJet Pro MFP M125-M126 PCLmS') {{
    $driver = 'HP LaserJet Pro MFP M125-M126 PCLmS'
}} elseif ($installedDrivers -contains 'Microsoft XPS Document Writer v4') {{
    $driver = 'Microsoft XPS Document Writer v4'
}} elseif ($installedDrivers -contains 'Microsoft enhanced Point and Print compatibility driver') {{
    $driver = 'Microsoft enhanced Point and Print compatibility driver'
}} elseif ($installedDrivers -contains 'Universal Print Class Driver') {{
    $driver = 'Universal Print Class Driver'
}} else {{
    $fallback = $installedDrivers | Where-Object {{ $_ -like '*Brother*' -or $_ -like '*XPS*' -or $_ -like '*Generic*' }} | Select-Object -First 1
    if ($fallback) {{ $driver = $fallback }}
}}

# Remove old confusing dummy printer if it existed
Remove-Printer -Name 'Apna Remote Printer' -ErrorAction SilentlyContinue
Remove-Printer -Name 'TKR Desk Printer' -ErrorAction SilentlyContinue

$printer = Get-Printer -Name '{printerName}'
if (-not $printer) {{
    Add-Printer -Name '{printerName}' -PortName '{PortName}' -DriverName $driver
}} else {{
    Set-Printer -Name '{printerName}' -PortName '{PortName}' -DriverName $driver
}}
";
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return (false, "Failed to launch PowerShell for printer installation.");

            await proc.WaitForExitAsync(ct);

            if (CheckIfPrinterInstalled(printerName))
            {
                return (true, $"'{printerName}' is installed and ready in all Windows Ctrl+P dialogs!");
            }

            var err = await proc.StandardError.ReadToEndAsync(ct);
            return (false, string.IsNullOrWhiteSpace(err) ? "Could not verify printer installation." : err.Trim());
        }
        catch (Exception ex)
        {
            return (false, $"Error setting up Windows Ctrl+P printer: {ex.Message}");
        }
    }

    public static async Task<(bool Ok, string Message)> UninstallPrinterAsync(string? targetName = null, CancellationToken ct = default)
    {
        var printerName = !string.IsNullOrWhiteSpace(targetName) ? targetName : ActivePrinterName;
        try
        {
            var script = $@"
$ErrorActionPreference = 'SilentlyContinue'
Remove-Printer -Name '{printerName}'
Remove-Printer -Name 'TKR Desk Printer'
Remove-Printer -Name 'Apna Remote Printer'
Remove-PrinterPort -Name '{PortName}'
Remove-PrinterPort -Name 'ApnaRemotePort'
";
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                CreateNoWindow = true,
                UseShellExecute = false
            };

            using var proc = Process.Start(psi);
            if (proc is not null)
                await proc.WaitForExitAsync(ct);

            return (true, $"'{printerName}' removed from Windows.");
        }
        catch (Exception ex)
        {
            return (false, $"Error uninstalling: {ex.Message}");
        }
    }

    public void Start()
    {
        if (_listener is not null) return;

        _cts = new CancellationTokenSource();
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, RawPrintPort);
            _listener.Start();
            _listenTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
            Log?.Invoke($"Virtual Spooler Bridge active on 127.0.0.1:{RawPrintPort} (Capturing Windows Ctrl+P prints).");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Virtual Spooler Bridge failed to bind port {RawPrintPort}: {ex.Message}");
            Stop();
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch { }

        _listener = null;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => ProcessRawPrintStreamAsync(client, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    Log?.Invoke($"Spooler Bridge accept error: {ex.Message}");
            }
        }
    }

    private async Task ProcessRawPrintStreamAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                using var ms = new MemoryStream();

                var buffer = new byte[8192];
                int read;

                // Read all bytes sent by Windows Print Spooler
                while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                {
                    ms.Write(buffer, 0, read);
                }

                var bytes = ms.ToArray();
                if (bytes.Length > 0)
                {
                    var docName = $"CtrlP_{DateTime.Now:HHmmss}.prn";
                    Log?.Invoke($"[Ctrl+P Bridge] Intercepted print job from Windows Spooler ({bytes.Length} bytes).");
                    JobIntercepted?.Invoke(bytes, docName);
                }
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    Log?.Invoke($"[Ctrl+P Bridge] Error reading print stream: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
