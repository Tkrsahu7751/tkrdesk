using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ApnaRemote.Windows.HardwareHub.Models;

public enum DeviceCategory
{
    Printer = 0,
    Scanner = 1,
    PlotterOrCutter = 2,
    Other = 3
}

public enum QueueStatus
{
    Queued = 0,
    Processing = 1,
    Completed = 2,
    Cancelled = 3,
    Failed = 4
}

public sealed class QueueItem : INotifyPropertyChanged
{
    private string _jobId = "";
    private string _clientIp = "";
    private string _clientMachineName = "";
    private DeviceCategory _category = DeviceCategory.Printer;
    private string _deviceName = "";
    private string _documentName = "Document";
    private QueueStatus _status = QueueStatus.Queued;
    private DateTime _submittedAt = DateTime.Now;
    private DateTime? _startedAt;
    private DateTime? _completedAt;
    private int _pageCount = 1;
    private int _estimatedDurationSeconds = 15;
    private string? _payloadPath;
    private string? _cancelReason;

    public string JobId
    {
        get => _jobId;
        set => SetField(ref _jobId, value);
    }

    public string ClientIp
    {
        get => _clientIp;
        set => SetField(ref _clientIp, value);
    }

    public string ClientMachineName
    {
        get => _clientMachineName;
        set => SetField(ref _clientMachineName, value);
    }

    public DeviceCategory Category
    {
        get => _category;
        set => SetField(ref _category, value);
    }

    public string DeviceName
    {
        get => _deviceName;
        set => SetField(ref _deviceName, value);
    }

    public string DocumentName
    {
        get => _documentName;
        set => SetField(ref _documentName, value);
    }

    public QueueStatus Status
    {
        get => _status;
        set
        {
            if (SetField(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusBadge));
                OnPropertyChanged(nameof(IsActiveOrQueued));
            }
        }
    }

    public DateTime SubmittedAt
    {
        get => _submittedAt;
        set => SetField(ref _submittedAt, value);
    }

    public DateTime? StartedAt
    {
        get => _startedAt;
        set => SetField(ref _startedAt, value);
    }

    public DateTime? CompletedAt
    {
        get => _completedAt;
        set => SetField(ref _completedAt, value);
    }

    public int PageCount
    {
        get => _pageCount;
        set => SetField(ref _pageCount, value);
    }

    public int EstimatedDurationSeconds
    {
        get => _estimatedDurationSeconds;
        set => SetField(ref _estimatedDurationSeconds, value);
    }

    public string? PayloadPath
    {
        get => _payloadPath;
        set => SetField(ref _payloadPath, value);
    }

    public string? CancelReason
    {
        get => _cancelReason;
        set => SetField(ref _cancelReason, value);
    }

    private string _targetPrinter = "";
    public string TargetPrinter
    {
        get => _targetPrinter;
        set => SetField(ref _targetPrinter, value);
    }

    private string _hardwareStatusText = "";
    public string HardwareStatusText
    {
        get => _hardwareStatusText;
        set
        {
            if (SetField(ref _hardwareStatusText, value))
            {
                OnPropertyChanged(nameof(DisplaySummary));
            }
        }
    }

    private string _pageRange = "All";
    public string PageRange
    {
        get => _pageRange;
        set
        {
            if (SetField(ref _pageRange, value))
            {
                OnPropertyChanged(nameof(DisplaySummary));
            }
        }
    }

    private string _paperSize = "A4";
    public string PaperSize
    {
        get => _paperSize;
        set
        {
            if (SetField(ref _paperSize, value))
            {
                OnPropertyChanged(nameof(DisplaySummary));
            }
        }
    }

    private string _printOrigin = "App Upload";
    public string PrintOrigin
    {
        get => _printOrigin;
        set
        {
            if (SetField(ref _printOrigin, value))
            {
                OnPropertyChanged(nameof(DisplaySummary));
            }
        }
    }

    public bool IsActiveOrQueued => Status is QueueStatus.Queued or QueueStatus.Processing;

    public string StatusBadge => Status switch
    {
        QueueStatus.Processing => "⚡ Processing Now",
        QueueStatus.Queued => "⏳ In Queue",
        QueueStatus.Completed => "✓ Completed",
        QueueStatus.Cancelled => "✕ Cancelled",
        QueueStatus.Failed => "⚠ Failed",
        _ => Status.ToString()
    };

    public string DisplaySummary
    {
        get
        {
            var originTag = PrintOrigin.Contains("Ctrl+P", StringComparison.OrdinalIgnoreCase)
                ? "⚡ [Ctrl+P Wireless 🖨️]"
                : "📁 [App Upload]";

            var pagesTag = string.IsNullOrWhiteSpace(PageRange) || PageRange.Equals("All", StringComparison.OrdinalIgnoreCase)
                ? $"{PageCount} pgs"
                : $"Pages {PageRange}";

            var sizeTag = !string.IsNullOrWhiteSpace(PaperSize) ? $" | {PaperSize}" : "";

            var baseSummary = string.IsNullOrWhiteSpace(TargetPrinter)
                ? $"{originTag} [{JobId}] {ClientMachineName} ({ClientIp}) — {DocumentName} ({pagesTag}{sizeTag}) — {StatusBadge}"
                : $"{originTag} [{JobId}] {ClientMachineName} ({ClientIp}) ➔ {TargetPrinter} — {DocumentName} ({pagesTag}{sizeTag}) — {StatusBadge}";

            return string.IsNullOrWhiteSpace(HardwareStatusText)
                ? baseSummary
                : $"{baseSummary} ({HardwareStatusText})";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
