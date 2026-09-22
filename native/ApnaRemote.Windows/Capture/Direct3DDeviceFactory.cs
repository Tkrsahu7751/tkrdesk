using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace ApnaRemote.Windows.Capture;

/// <summary>
/// Creates a WinRT <see cref="IDirect3DDevice"/> for Windows.Graphics.Capture frame pools.
/// Uses OS D3D11 APIs only — no third-party graphics package.
/// </summary>
internal static class Direct3DDeviceFactory
{
    private const uint D3D11_SDK_VERSION = 7;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private static readonly Guid IidId3d11Device = new("db6f6ddb-ac77-4e88-8253-819df9bbf140");
    private static readonly Guid IidIdxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    public static IDirect3DDevice Create()
    {
        int hr = D3D11CreateDevice(
            IntPtr.Zero,
            D3D_DRIVER_TYPE_HARDWARE,
            IntPtr.Zero,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            null,
            0,
            D3D11_SDK_VERSION,
            out IntPtr d3dDevice,
            out _,
            out IntPtr d3dContext);

        if (hr < 0 || d3dDevice == IntPtr.Zero)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        try
        {
            Marshal.Release(d3dContext);

            hr = Marshal.QueryInterface(d3dDevice, IidIdxgiDevice, out IntPtr dxgiDevice);
            if (hr < 0 || dxgiDevice == IntPtr.Zero)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            try
            {
                hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out IntPtr inspectable);
                if (hr < 0 || inspectable == IntPtr.Zero)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                try
                {
                    return MarshalInspectable<IDirect3DDevice>.FromAbi(inspectable);
                }
                finally
                {
                    MarshalInspectable<object>.DisposeAbi(inspectable);
                }
            }
            finally
            {
                Marshal.Release(dxgiDevice);
            }
        }
        finally
        {
            Marshal.Release(d3dDevice);
        }
    }

    private const int D3D_DRIVER_TYPE_HARDWARE = 1;

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr software,
        uint flags,
        int[]? featureLevels,
        uint featureLevelsCount,
        uint sdkVersion,
        out IntPtr device,
        out int featureLevel,
        out IntPtr immediateContext);

    [DllImport(
        "d3d11.dll",
        EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice",
        ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
}
