using System.IO;
using System.Runtime.InteropServices;

namespace ApnaRemote.Windows.HardwareHub.Services;

public static class RawPrinterSpooler
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public class DOCINFOA
    {
        [MarshalAs(UnmanagedType.LPStr)]
        public string pDocName = "ApnaRemote_Print";
        [MarshalAs(UnmanagedType.LPStr)]
        public string? pOutputFile = null;
        [MarshalAs(UnmanagedType.LPStr)]
        public string pDataType = "RAW";
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterA", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool OpenPrinter([MarshalAs(UnmanagedType.LPStr)] string szPrinter, out IntPtr hPrinter, IntPtr pd);

    [DllImport("winspool.drv", EntryPoint = "ClosePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterA", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool StartDocPrinter(IntPtr hPrinter, int level, [In, MarshalAs(UnmanagedType.LPStruct)] DOCINFOA di);

    [DllImport("winspool.drv", EntryPoint = "EndDocPrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "StartPagePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "EndPagePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "WritePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

    public static (bool Success, string Message) SendBytesToPrinter(string printerName, byte[] bytes, string documentName = "ApnaRemote_Print")
    {
        if (string.IsNullOrWhiteSpace(printerName))
            return (false, "Target printer name is empty.");

        if (bytes == null || bytes.Length == 0)
            return (false, "Empty print payload.");

        var pUnmanagedBytes = Marshal.AllocCoTaskMem(bytes.Length);
        Marshal.Copy(bytes, 0, pUnmanagedBytes, bytes.Length);

        try
        {
            if (!OpenPrinter(printerName, out var hPrinter, IntPtr.Zero))
            {
                var err = Marshal.GetLastWin32Error();
                return (false, $"Cannot open printer '{printerName}'. Win32 error: {err}");
            }

            try
            {
                var di = new DOCINFOA
                {
                    pDocName = string.IsNullOrWhiteSpace(documentName) ? "ApnaRemote_Print" : documentName,
                    pDataType = "RAW"
                };

                if (!StartDocPrinter(hPrinter, 1, di))
                {
                    var err = Marshal.GetLastWin32Error();
                    return (false, $"StartDocPrinter failed. Win32 error: {err}");
                }

                try
                {
                    if (!StartPagePrinter(hPrinter))
                    {
                        var err = Marshal.GetLastWin32Error();
                        return (false, $"StartPagePrinter failed. Win32 error: {err}");
                    }

                    try
                    {
                        int totalWritten = 0;
                        while (totalWritten < bytes.Length)
                        {
                            int toWrite = bytes.Length - totalWritten;
                            IntPtr ptr = IntPtr.Add(pUnmanagedBytes, totalWritten);
                            bool writeSuccess = WritePrinter(hPrinter, ptr, toWrite, out int written);
                            if (!writeSuccess || written <= 0)
                            {
                                var err = Marshal.GetLastWin32Error();
                                return (false, $"WritePrinter failed at offset {totalWritten}/{bytes.Length} (written {written}). Win32 error: {err}");
                            }
                            totalWritten += written;
                        }
                    }
                    finally
                    {
                        EndPagePrinter(hPrinter);
                    }
                }
                finally
                {
                    EndDocPrinter(hPrinter);
                }

                return (true, $"Spool job dispatched successfully ({bytes.Length} bytes).");
            }
            finally
            {
                ClosePrinter(hPrinter);
            }
        }
        catch (Exception ex)
        {
            return (false, $"Exception writing to printer: {ex.Message}");
        }
        finally
        {
            Marshal.FreeCoTaskMem(pUnmanagedBytes);
        }
    }

    public static (bool Success, string Message) SendFileToPrinter(string printerName, string filePath, string documentName = "ApnaRemote_Print")
    {
        if (!File.Exists(filePath))
            return (false, $"File not found: {filePath}");

        try
        {
            var bytes = File.ReadAllBytes(filePath);
            return SendBytesToPrinter(printerName, bytes, documentName);
        }
        catch (Exception ex)
        {
            return (false, $"Error reading file for spooling: {ex.Message}");
        }
    }
}
