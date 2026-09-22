using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ApnaRemote.Windows.HardwareHub.Models;

public sealed class PenDriveInfo
{
    public string DriveLetter { get; set; } = "";
    public string VolumeLabel { get; set; } = "";
    public string DriveFormat { get; set; } = "";
    public long TotalSizeBytes { get; set; }
    public long FreeSpaceBytes { get; set; }

    public string DisplayName =>
        string.IsNullOrWhiteSpace(VolumeLabel)
            ? $"{DriveLetter} Removable Drive ({FormatBytes(TotalSizeBytes)})"
            : $"{DriveLetter} {VolumeLabel} ({FormatBytes(TotalSizeBytes)})";

    public string FormattedFreeSpace =>
        $"{FormatBytes(FreeSpaceBytes)} free of {FormatBytes(TotalSizeBytes)}";

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        double d = bytes;
        while (d >= 1024 && i < units.Length - 1)
        {
            d /= 1024;
            i++;
        }
        return $"{d:0.#} {units[i]}";
    }
}

public sealed class FileEntry
{
    public string Name { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long SizeBytes { get; set; }
    public DateTime ModifiedTime { get; set; }
    public string Extension { get; set; } = "";

    public string FormattedSize => IsDirectory ? "Folder" : FormatBytes(SizeBytes);
    public string FormattedDate => ModifiedTime.ToString("yyyy-MM-dd HH:mm");
    public string TypeIcon => IsDirectory ? "📁" : GetIconForExt(Extension);
    public string ActionLabel => IsDirectory ? "Open 📂" : "Download ⬇️";

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        double d = bytes;
        while (d >= 1024 && i < units.Length - 1)
        {
            d /= 1024;
            i++;
        }
        return $"{d:0.#} {units[i]}";
    }

    private static string GetIconForExt(string ext) => ext.ToLowerInvariant() switch
    {
        ".pdf" => "📕",
        ".doc" or ".docx" => "📘",
        ".xls" or ".xlsx" or ".csv" => "📊",
        ".ppt" or ".pptx" => "📙",
        ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" => "🖼️",
        ".zip" or ".rar" or ".7z" or ".tar" => "📦",
        ".mp4" or ".mkv" or ".avi" or ".mov" => "🎬",
        ".mp3" or ".wav" => "🎵",
        ".txt" or ".log" or ".json" or ".xml" => "📝",
        ".exe" or ".msi" => "⚙️",
        _ => "📄"
    };
}

public sealed class PenDriveAccessRequest
{
    public string ClientMachineName { get; set; } = "";
    public string ClientIp { get; set; } = "";
    public string Pin { get; set; } = "";
    public bool RememberMe { get; set; }
}

public sealed class PenDriveAccessResponse
{
    public bool Granted { get; set; }
    public string Message { get; set; } = "";
    public string SessionToken { get; set; } = "";
    public bool IsReadOnly { get; set; }
    public string VolumeLabel { get; set; } = "";
    public string WindowsShareUnc { get; set; } = "";
    public long TotalSizeBytes { get; set; }
    public long FreeSpaceBytes { get; set; }
}

public sealed class AllowedClientItem : INotifyPropertyChanged
{
    private bool _isAllowed = true;
    private DateTime _lastSeenUtc = DateTime.UtcNow;

    public string MachineName { get; set; } = "";
    public string IpAddress { get; set; } = "";

    public bool IsAllowed
    {
        get => _isAllowed;
        set
        {
            if (_isAllowed != value)
            {
                _isAllowed = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusBadge));
            }
        }
    }

    public DateTime LastSeenUtc
    {
        get => _lastSeenUtc;
        set { _lastSeenUtc = value; OnPropertyChanged(); }
    }

    public string DisplayText => $"{MachineName} ({IpAddress})";
    public string StatusBadge => IsAllowed ? "Access ALLOWED ✅" : "BLOCKED ❌";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? prop = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}
