using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ApnaRemote.Windows.HardwareHub.Models;

public sealed class UsbDeviceItem : INotifyPropertyChanged
{
    private string _busId = "";
    private string _vidPid = "";
    private string _name = "";
    private string _state = "";
    private string _instanceId = "";
    private string? _persistedGuid;
    private bool _isSelected;

    public string BusId
    {
        get => _busId;
        set => SetField(ref _busId, value);
    }

    public string VidPid
    {
        get => _vidPid;
        set => SetField(ref _vidPid, value);
    }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string State
    {
        get => _state;
        set => SetField(ref _state, value);
    }

    public string InstanceId
    {
        get => _instanceId;
        set => SetField(ref _instanceId, value);
    }

    public string? PersistedGuid
    {
        get => _persistedGuid;
        set => SetField(ref _persistedGuid, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public string DisplayText => $"{BusId}: {Name} [{VidPid}] ({State})";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? prop = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? prop = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(prop);
        return true;
    }
}
