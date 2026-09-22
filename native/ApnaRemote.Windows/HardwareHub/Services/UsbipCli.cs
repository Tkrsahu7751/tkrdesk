using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ApnaRemote.Windows.HardwareHub.Models;

namespace ApnaRemote.Windows.HardwareHub.Services;

public static class UsbipCli
{
    private static readonly Regex BusIdRegex = new(@"^\d+-\d+(?:\.\d+)*$", RegexOptions.Compiled);

    public static string? FindUsbipd()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "usbipd-win", "usbipd.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "usbipd", "usbipd.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public static string? FindUsbipClient()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "USBip", "usbip.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "usbip-win2", "usbip.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public static async Task<IReadOnlyList<UsbDeviceItem>> ListUsbDevicesAsync(CancellationToken ct = default)
    {
        var exe = FindUsbipd();
        if (exe is not null)
        {
            var result = await RunProcessAsync(exe, "list", ct);
            if (result.Ok)
            {
                var list = ParseUsbipdList(result.Output);
                if (list.Count > 0) return list;
            }
        }

        // Fallback: Query Windows Registry for connected USB devices so user can see their hardware
        return ListViaRegistry();
    }

    public static async Task<(bool Ok, string Output)> BindAsync(string busId, CancellationToken ct = default)
    {
        var exe = FindUsbipd();
        if (exe is null)
            return (false, "usbipd-win is not installed. Please install usbipd-win to share USB peripherals.");

        return await RunProcessAsync(exe, $"bind --busid {busId}", ct, runAsAdmin: true);
    }

    public static async Task<(bool Ok, string Output)> UnbindAsync(string busId, CancellationToken ct = default)
    {
        var exe = FindUsbipd();
        if (exe is null)
            return (false, "usbipd-win is not installed.");

        return await RunProcessAsync(exe, $"unbind --busid {busId}", ct, runAsAdmin: true);
    }

    public static async Task<(bool Ok, string Output)> AttachAsync(string hostIp, string busId, CancellationToken ct = default)
    {
        var clientExe = FindUsbipClient();
        if (clientExe is not null)
        {
            return await RunProcessAsync(clientExe, $"attach -r {hostIp} -b {busId}", ct, runAsAdmin: true);
        }

        var hostExe = FindUsbipd();
        if (hostExe is not null)
        {
            return await RunProcessAsync(hostExe, $"attach --remote {hostIp} --busid {busId}", ct, runAsAdmin: true);
        }

        return (false, "Neither usbip client nor usbipd found. Please install usbip-win2 or usbipd-win.");
    }

    public static async Task<(bool Ok, string Output)> DetachAsync(string portOrBusId, CancellationToken ct = default)
    {
        var clientExe = FindUsbipClient();
        if (clientExe is not null)
        {
            return await RunProcessAsync(clientExe, $"detach -p {portOrBusId}", ct, runAsAdmin: true);
        }

        var hostExe = FindUsbipd();
        if (hostExe is not null)
        {
            return await RunProcessAsync(hostExe, $"detach --busid {portOrBusId}", ct, runAsAdmin: true);
        }

        return (false, "Neither usbip client nor usbipd found.");
    }

    private static IReadOnlyList<UsbDeviceItem> ParseUsbipdList(string output)
    {
        var list = new List<UsbDeviceItem>();
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        bool inDeviceList = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("BUSID", StringComparison.OrdinalIgnoreCase))
            {
                inDeviceList = true;
                continue;
            }

            if (!inDeviceList) continue;

            var parts = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;

            var busId = parts[0];
            if (!BusIdRegex.IsMatch(busId)) continue;

            var vidPid = parts[1];
            var state = parts[^1];
            var name = string.Join(' ', parts.Skip(2).Take(parts.Length - 3));

            list.Add(new UsbDeviceItem
            {
                BusId = busId,
                VidPid = vidPid,
                Name = name,
                State = state
            });
        }

        return list;
    }

    private static IReadOnlyList<UsbDeviceItem> ListViaRegistry()
    {
        var list = new List<UsbDeviceItem>();
        try
        {
            using var usbKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usbKey is null) return list;

            int index = 1;
            foreach (var subKeyName in usbKey.GetSubKeyNames())
            {
                if (!subKeyName.StartsWith("VID_", StringComparison.OrdinalIgnoreCase)) continue;

                using var devKey = usbKey.OpenSubKey(subKeyName);
                if (devKey is null) continue;

                foreach (var instName in devKey.GetSubKeyNames())
                {
                    using var instKey = devKey.OpenSubKey(instName);
                    if (instKey is null) continue;

                    var desc = instKey.GetValue("DeviceDesc")?.ToString();
                    var friendly = instKey.GetValue("FriendlyName")?.ToString();
                    var name = !string.IsNullOrWhiteSpace(friendly) ? friendly : desc;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    // Clean string e.g. "@oem12.inf,%DeviceDesc%;USB Composite Device"
                    if (name.Contains(';')) name = name.Split(';')[^1];

                    var vidPid = "USB";
                    var match = Regex.Match(subKeyName, @"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})");
                    if (match.Success)
                        vidPid = $"{match.Groups[1].Value}:{match.Groups[2].Value}";

                    list.Add(new UsbDeviceItem
                    {
                        BusId = $"1-{index++}",
                        VidPid = vidPid,
                        Name = name,
                        State = "Plugged In",
                        InstanceId = $"{subKeyName}\\{instName}"
                    });
                }
            }
        }
        catch { /* ignore registry access errors */ }

        return list;
    }

    private static async Task<(bool Ok, string Output)> RunProcessAsync(
        string fileName,
        string arguments,
        CancellationToken ct,
        bool runAsAdmin = false)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = runAsAdmin,
                Verb = runAsAdmin ? "runas" : "",
                RedirectStandardOutput = !runAsAdmin,
                RedirectStandardError = !runAsAdmin,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            if (!runAsAdmin)
            {
                var output = await process.StandardOutput.ReadToEndAsync(ct);
                var error = await process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);

                var combined = (output + "\n" + error).Trim();
                return (process.ExitCode == 0, combined);
            }
            else
            {
                await process.WaitForExitAsync(ct);
                return (process.ExitCode == 0, "Elevated command executed.");
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
