using System.Runtime.InteropServices;
using ApnaRemote.Core;

namespace ApnaRemote.Windows.Input;

internal sealed record CaptureMonitor(IntPtr Handle, string Device, DisplayBounds Bounds, bool Primary)
{
    public string Label => $"{Device} · {Bounds.Width} × {Bounds.Height} · ({Bounds.Left}, {Bounds.Top}){(Primary ? " · primary" : "")}";
}

/// <summary>Identifies an actual monitor; never resolves a screen from dimensions alone.</summary>
internal static class CaptureDisplayBounds
{
    public static IReadOnlyList<CaptureMonitor> Enumerate()
    {
        var monitors = new List<CaptureMonitor>();
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr handle, IntPtr hdc, ref Rect rect, IntPtr data) =>
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
                if (GetMonitorInfo(handle, ref info))
                    monitors.Add(new(handle, info.Device, new(info.Monitor.Left, info.Monitor.Top,
                        info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top), (info.Flags & 1) != 0));
                return true;
            }, IntPtr.Zero))
            throw new InvalidOperationException("Windows could not enumerate displays.");
        return monitors;
    }

    public static bool IsCurrent(CaptureMonitor selected)
        => Matches(selected, Enumerate());
    internal static bool Matches(CaptureMonitor selected, IEnumerable<CaptureMonitor> monitors)
        => monitors.Any(m => m.Handle == selected.Handle && m.Device == selected.Device && m.Bounds == selected.Bounds);

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data);
    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
}
