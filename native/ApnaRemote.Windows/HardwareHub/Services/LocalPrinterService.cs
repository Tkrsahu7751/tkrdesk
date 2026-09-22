using System.ComponentModel;
using System.Printing;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ApnaRemote.Windows.HardwareHub.Services;

public sealed class InstalledPrinterItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public string Name { get; set; } = "";
    public string PortName { get; set; } = "";
    public bool IsDefault { get; set; }
    public bool IsPhysical { get; set; }
    public bool IsConnected { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }

    public string DisplayText
    {
        get
        {
            if (IsPhysical)
            {
                if (IsConnected)
                {
                    return $"🖨️ {Name} ({PortName}) [Online / Ready]";
                }
                else
                {
                    return $"⚠️ 🖨️ {Name} ({PortName}) [Offline / Unplugged]";
                }
            }
            return $"📄 {Name} ({PortName})";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal static class PrinterHardwarePresenceDetector
{
    private const int CR_SUCCESS = 0;
    private const int DIGCF_PRESENT = 0x00000002;
    private const int DIGCF_ALLCLASSES = 0x00000004;
    private const int SPDRP_FRIENDLYNAME = 0x0000000C;
    private const int SPDRP_DEVICEDESC = 0x00000000;

    [DllImport("cfgmgr32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int CM_Locate_DevNode(out uint dnDevInst, string pDeviceID, int ulFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public int DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(IntPtr ClassGuid, string? Enumerator, IntPtr hwndParent, int Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, int MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData,
        int Property,
        out int PropertyRegDataType,
        StringBuilder PropertyBuffer,
        int PropertyBufferSize,
        out int RequiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    public static HashSet<string> GetPresentPrinterDeviceNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectPresentNames(names, "USBPRINT");
        CollectPresentNames(names, "USB");
        return names;
    }

    private static void CollectPresentNames(HashSet<string> target, string enumerator)
    {
        try
        {
            IntPtr hDevInfo = SetupDiGetClassDevs(IntPtr.Zero, enumerator, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (hDevInfo == new IntPtr(-1)) return;

            try
            {
                SP_DEVINFO_DATA devData = new SP_DEVINFO_DATA();
                devData.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));

                int index = 0;
                StringBuilder sb = new StringBuilder(1024);

                while (SetupDiEnumDeviceInfo(hDevInfo, index++, ref devData))
                {
                    if (SetupDiGetDeviceRegistryProperty(hDevInfo, ref devData, SPDRP_FRIENDLYNAME, out _, sb, sb.Capacity, out _) ||
                        SetupDiGetDeviceRegistryProperty(hDevInfo, ref devData, SPDRP_DEVICEDESC, out _, sb, sb.Capacity, out _))
                    {
                        var text = sb.ToString().Trim();
                        if (!string.IsNullOrEmpty(text))
                        {
                            target.Add(text);
                        }
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(hDevInfo);
            }
        }
        catch
        {
            // Ignore SetupAPI failures
        }
    }

    public static bool IsPrinterHardwarePresent(string printerName, string portName, HashSet<string> presentDeviceNames)
    {
        if (portName.StartsWith("USB", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Print\Printers\{printerName}\PnpData");
                var pnpId = key?.GetValue("DeviceInstanceId")?.ToString();
                if (!string.IsNullOrWhiteSpace(pnpId))
                {
                    int locateRes = CM_Locate_DevNode(out _, pnpId, 0);
                    if (locateRes == CR_SUCCESS)
                    {
                        return true;
                    }
                    return false;
                }
            }
            catch
            {
                // Fallback to name check
            }

            foreach (var presentName in presentDeviceNames)
            {
                if (printerName.IndexOf(presentName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    presentName.IndexOf(printerName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        if (portName.StartsWith("WSD", StringComparison.OrdinalIgnoreCase) ||
            portName.StartsWith("IP_", StringComparison.OrdinalIgnoreCase) ||
            portName.StartsWith("TCPIP", StringComparison.OrdinalIgnoreCase) ||
            portName.StartsWith("192.168.", StringComparison.OrdinalIgnoreCase) ||
            portName.StartsWith("10.", StringComparison.OrdinalIgnoreCase) ||
            portName.StartsWith("172.", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Print\Printers\{printerName}\PnpData");
                var pnpId = key?.GetValue("DeviceInstanceId")?.ToString();
                if (!string.IsNullOrWhiteSpace(pnpId))
                {
                    int locateRes = CM_Locate_DevNode(out _, pnpId, 0);
                    return locateRes == CR_SUCCESS;
                }
            }
            catch { }

            return true;
        }

        return true;
    }
}

public static class LocalPrinterService
{
    public static List<InstalledPrinterItem> GetInstalledPrinters()
    {
        var result = new List<InstalledPrinterItem>();
        HashSet<string> presentDeviceNames;
        try
        {
            presentDeviceNames = PrinterHardwarePresenceDetector.GetPresentPrinterDeviceNames();
        }
        catch
        {
            presentDeviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            using var server = new LocalPrintServer();
            var printQueues = server.GetPrintQueues();

            foreach (var q in printQueues)
            {
                try
                {
                    var portName = q.QueuePort?.Name ?? "";
                    var isPhysical = IsPhysicalPort(portName);
                    var isConnected = isPhysical 
                        ? PrinterHardwarePresenceDetector.IsPrinterHardwarePresent(q.Name, portName, presentDeviceNames)
                        : true;

                    result.Add(new InstalledPrinterItem
                    {
                        Name = q.Name,
                        PortName = string.IsNullOrWhiteSpace(portName) ? "Unknown" : portName,
                        IsDefault = q.Name.Equals(server.DefaultPrintQueue?.Name, StringComparison.OrdinalIgnoreCase),
                        IsPhysical = isPhysical,
                        IsConnected = isConnected,
                        IsSelected = false
                    });
                }
                catch
                {
                    // Ignore single printer enumeration error
                }
            }
        }
        catch
        {
            // Fallback: empty list if print server fails
        }

        // Sort: Active/Connected physical printers first, then other connected, then default, then disconnected last
        result = result
            .OrderByDescending(p => p.IsConnected && p.IsPhysical)
            .ThenByDescending(p => p.IsConnected)
            .ThenByDescending(p => p.IsDefault)
            .ThenBy(p => p.Name)
            .ToList();

        // Host Auto-Select Logic:
        // - NEVER auto-select a disconnected/unplugged phantom printer!
        // - Only active, physically connected printers qualify for auto-selection.
        var activePhysicalPrinters = result.Where(p => p.IsPhysical && p.IsConnected).ToList();
        if (activePhysicalPrinters.Count == 1)
        {
            // Exactly 1 physical printer connected -> auto-select it!
            activePhysicalPrinters[0].IsSelected = true;
        }
        else if (activePhysicalPrinters.Count > 1)
        {
            // Multiple physical printers connected -> select all active physical printers
            foreach (var p in activePhysicalPrinters)
            {
                p.IsSelected = true;
            }
        }
        // If 0 physical printers connected, nothing is auto-selected! (All stay unchecked)

        return result;
    }

    private static bool IsPhysicalPort(string port)
    {
        if (string.IsNullOrWhiteSpace(port)) return false;
        var p = port.Trim().ToUpperInvariant();

        // Common physical printer ports: USB, WSD, IP network, LPT, COM, DOT4
        if (p.StartsWith("USB", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("WSD", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("IP_", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("LPT", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("DOT4", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("TCPIP", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("192.168.", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("10.", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("172.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Exclude known virtual ports
        if (p.Equals("PORTPROMPT:", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("NUL:", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("FILE:", StringComparison.OrdinalIgnoreCase) ||
            p.Contains("AD_PORT", StringComparison.OrdinalIgnoreCase) ||
            p.Contains("ONENOTE", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return false;
    }
}
