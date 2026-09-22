using System.Runtime.InteropServices;
using ApnaRemote.Windows.Input;
using Windows.Graphics.Capture;
using WinRT;

namespace ApnaRemote.Windows.Capture;

internal static class MonitorCaptureSource
{
    // IGraphicsCaptureItemInterop derives from IUnknown; CreateForWindow is slot 3, CreateForMonitor slot 4.
    // https://learn.microsoft.com/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createformonitor
    public static GraphicsCaptureItem Create(CaptureMonitor monitor)
    {
        if (!CaptureDisplayBounds.IsCurrent(monitor)) throw new InvalidOperationException("Selected monitor changed. Choose it again.");
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out IntPtr name));
        IntPtr factory = IntPtr.Zero, item = IntPtr.Zero;
        try
        {
            Guid interop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(name, ref interop, out factory));
            var create = Marshal.GetDelegateForFunctionPointer<CreateForMonitor>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), 4 * IntPtr.Size));
            Guid iid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            Marshal.ThrowExceptionForHR(create(factory, monitor.Handle, ref iid, out item));
            return MarshalInspectable<GraphicsCaptureItem>.FromAbi(item);
        }
        finally
        {
            if (item != IntPtr.Zero) Marshal.Release(item);
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            _ = WindowsDeleteString(name);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateForMonitor(IntPtr factory, IntPtr monitor, ref Guid iid, out IntPtr item);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr value);
    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr value);
    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr name, ref Guid iid, out IntPtr factory);
}
