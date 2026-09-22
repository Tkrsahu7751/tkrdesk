namespace ApnaRemote.Windows.HardwareHub.Models;

public sealed class QueueItemSnapshot
{
    public string JobId { get; set; } = "";
    public string ClientMachineName { get; set; } = "";
    public string ClientIp { get; set; } = "";
    public string DocumentName { get; set; } = "";
    public int PageCount { get; set; } = 1;
    public QueueStatus Status { get; set; }
    public string TargetPrinter { get; set; } = "";
    public string HardwareStatusText { get; set; } = "";
    public string PageRange { get; set; } = "All";
    public string PaperSize { get; set; } = "A4";
    public string PrintOrigin { get; set; } = "App Upload";
    public int EstimatedDurationSeconds { get; set; } = 15;
    public DateTime SubmittedAt { get; set; }

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
            var originTag = !string.IsNullOrWhiteSpace(PrintOrigin) && PrintOrigin.Contains("Ctrl+P", StringComparison.OrdinalIgnoreCase)
                ? "⚡ [Ctrl+P Wireless 🖨️]"
                : "📁 [App Upload]";

            var pagesTag = string.IsNullOrWhiteSpace(PageRange) || PageRange.Equals("All", StringComparison.OrdinalIgnoreCase)
                ? $"{PageCount} pgs"
                : $"Pages {PageRange}";

            var sizeTag = !string.IsNullOrWhiteSpace(PaperSize) ? $" | {PaperSize}" : "";

            var baseSummary = string.IsNullOrWhiteSpace(TargetPrinter)
                ? $"{originTag} [{JobId}] {ClientMachineName} — {DocumentName} ({pagesTag}{sizeTag}) — {StatusBadge}"
                : $"{originTag} [{JobId}] {ClientMachineName} ➔ {TargetPrinter} — {DocumentName} ({pagesTag}{sizeTag}) — {StatusBadge}";

            return string.IsNullOrWhiteSpace(HardwareStatusText)
                ? baseSummary
                : $"{baseSummary} ({HardwareStatusText})";
        }
    }
}

public sealed class QueueStatusMessage
{
    public string ServerMachineName { get; set; } = "";
    public string DeviceName { get; set; } = "Office Shared Device";
    public DeviceCategory Category { get; set; } = DeviceCategory.Printer;
    public bool IsDeviceOnline { get; set; } = true;
    public int TotalConnectedClients { get; set; } = 1;
    public string? ActiveJobId { get; set; }
    public string? ActiveClientName { get; set; }
    public string? ActiveClientIp { get; set; }
    public string? ActiveHardwareStatus { get; set; }
    public int QueueDepth { get; set; }
    public List<QueueItemSnapshot> Queue { get; set; } = new();
    public List<string> SharedPrinters { get; set; } = new();

    // Client-specific computed fields (filled by client evaluator or server)
    public int YourPosition { get; set; } = -1; // -1: not in queue, 0: active now, 1+: position in queue
    public string? YourJobId { get; set; }
    public int EstimatedWaitSeconds { get; set; }
    public bool CanCancel { get; set; }
    public bool IsYourTurnNow { get; set; }

    public string FormattedPosition => YourPosition switch
    {
        0 => "⚡ YOUR TURN NOW — Processing on device",
        > 0 => $"Position #{YourPosition} in line ({QueueDepth} total in queue)",
        _ => "Idle — No pending request"
    };

    public string FormattedWaitTime => YourPosition switch
    {
        0 => "Active now",
        > 0 => EstimatedWaitSeconds switch
        {
            < 60 => $"~{EstimatedWaitSeconds} sec remaining",
            _ => $"~{EstimatedWaitSeconds / 60}m {EstimatedWaitSeconds % 60}s remaining"
        },
        _ => "Ready immediately"
    };
}
