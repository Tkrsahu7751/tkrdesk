using ApnaRemote.Windows.HardwareHub.Models;

namespace ApnaRemote.Windows.HardwareHub.Services;

public sealed class UsbShareHostService : IDisposable
{
    private readonly List<UsbDeviceItem> _sharedDevices = new();
    private bool _disposed;

    public IReadOnlyList<UsbDeviceItem> SharedDevices => _sharedDevices;
    public bool IsSharing => _sharedDevices.Count > 0;
    public event Action<string>? Log;
    public event Action? StateChanged;

    public async Task<IReadOnlyList<UsbDeviceItem>> RefreshDevicesAsync(CancellationToken ct = default)
    {
        try
        {
            var devices = await UsbipCli.ListUsbDevicesAsync(ct);
            return devices;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"USB listing error: {ex.Message}");
            return Array.Empty<UsbDeviceItem>();
        }
    }

    public async Task<bool> ShareDeviceAsync(UsbDeviceItem device, CancellationToken ct = default)
    {
        Log?.Invoke($"Binding USB device {device.BusId} ({device.Name})…");
        var result = await UsbipCli.BindAsync(device.BusId, ct);
        if (result.Ok)
        {
            device.State = "Shared";
            if (!_sharedDevices.Any(d => d.BusId == device.BusId))
                _sharedDevices.Add(device);

            Log?.Invoke($"USB {device.BusId} is now shared on LAN (TCP port 3240).");
            StateChanged?.Invoke();
            return true;
        }

        Log?.Invoke($"Failed to share USB {device.BusId}: {result.Output}");
        return false;
    }

    public async Task<bool> UnshareDeviceAsync(UsbDeviceItem device, CancellationToken ct = default)
    {
        Log?.Invoke($"Unbinding USB device {device.BusId}…");
        var result = await UsbipCli.UnbindAsync(device.BusId, ct);
        if (result.Ok)
        {
            device.State = "Unbound";
            _sharedDevices.RemoveAll(d => d.BusId == device.BusId);
            Log?.Invoke($"USB {device.BusId} released.");
            StateChanged?.Invoke();
            return true;
        }

        Log?.Invoke($"Failed to unbind USB {device.BusId}: {result.Output}");
        return false;
    }

    public async Task UnshareAllAsync(CancellationToken ct = default)
    {
        foreach (var dev in _sharedDevices.ToList())
        {
            await UnshareDeviceAsync(dev, ct);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
