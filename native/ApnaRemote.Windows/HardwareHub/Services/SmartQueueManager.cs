using System.Collections.Concurrent;
using ApnaRemote.Windows.HardwareHub.Models;

namespace ApnaRemote.Windows.HardwareHub.Services;

public sealed class SmartQueueManager : IDisposable
{
    private readonly object _gate = new();
    private readonly List<QueueItem> _jobs = new();
    private readonly ConcurrentDictionary<string, DateTime> _connectedClients = new();
    private QueueItem? _activeJob;
    private CancellationTokenSource? _workerCts;
    private Task? _workerTask;
    private bool _disposed;

    public string DeviceName { get; set; } = "Shared Office Device";
    public DeviceCategory Category { get; set; } = DeviceCategory.Printer;
    public bool AutoProcess { get; set; } = true;
    public int SimulatedPageDelayMs { get; set; } = 3000;
    public List<string> SharedPrinters { get; } = new();

    public void SetSharedPrinters(IEnumerable<string> printers)
    {
        lock (_gate)
        {
            SharedPrinters.Clear();
            SharedPrinters.AddRange(printers);
        }
        QueueChanged?.Invoke();
    }

    public event Action<QueueItem>? JobStarted;
    public event Action<QueueItem>? JobCompleted;
    public event Action<QueueItem>? JobCancelled;
    public event Action? QueueChanged;
    public event Action<string>? Log;

    public int TotalConnectedClients => Math.Max(1, _connectedClients.Count);
    public QueueItem? ActiveJob
    {
        get { lock (_gate) return _activeJob; }
    }

    public void RegisterClientPing(string clientIp)
    {
        if (string.IsNullOrWhiteSpace(clientIp)) return;
        _connectedClients[clientIp] = DateTime.Now;
        var cutoff = DateTime.Now.AddSeconds(-60);
        foreach (var (ip, time) in _connectedClients)
        {
            if (time < cutoff)
                _connectedClients.TryRemove(ip, out _);
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_workerTask is not null && !_workerTask.IsCompleted)
                return;

            _workerCts = new CancellationTokenSource();
            _workerTask = Task.Run(() => WorkerLoopAsync(_workerCts.Token));
            Log?.Invoke($"Smart Queue Engine STARTED for {DeviceName} ({Category}).");
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_gate)
        {
            cts = _workerCts;
            task = _workerTask;
            _workerCts = null;
            _workerTask = null;
        }

        cts?.Cancel();
        try { task?.Wait(1000); } catch { /* ignore */ }
        cts?.Dispose();
        Log?.Invoke("Smart Queue Engine STOPPED.");
    }

    public QueueItem Enqueue(
        string clientIp,
        string clientMachineName,
        string documentName,
        int pageCount = 1,
        DeviceCategory category = DeviceCategory.Printer,
        int estimatedSecs = 0,
        string? payloadPath = null,
        string targetPrinter = "",
        string pageRange = "All",
        string paperSize = "A4",
        string printOrigin = "App Upload")
    {
        RegisterClientPing(clientIp);
        var pgs = Math.Max(1, pageCount);
        if (estimatedSecs <= 0)
        {
            estimatedSecs = category switch
            {
                DeviceCategory.Printer => pgs * 3,
                DeviceCategory.Scanner => pgs * 6,
                DeviceCategory.PlotterOrCutter => 60,
                _ => 15
            };
        }

        var item = new QueueItem
        {
            JobId = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            ClientIp = clientIp,
            ClientMachineName = string.IsNullOrWhiteSpace(clientMachineName) ? clientIp : clientMachineName,
            Category = category,
            DeviceName = DeviceName,
            DocumentName = string.IsNullOrWhiteSpace(documentName) ? "Job" : documentName,
            PageCount = pgs,
            TargetPrinter = targetPrinter,
            PageRange = string.IsNullOrWhiteSpace(pageRange) ? "All" : pageRange.Trim(),
            PaperSize = string.IsNullOrWhiteSpace(paperSize) ? "A4" : paperSize.Trim(),
            PrintOrigin = string.IsNullOrWhiteSpace(printOrigin) ? "App Upload" : printOrigin.Trim(),
            Status = QueueStatus.Queued,
            SubmittedAt = DateTime.Now,
            EstimatedDurationSeconds = Math.Max(3, estimatedSecs),
            PayloadPath = payloadPath
        };

        lock (_gate)
        {
            _jobs.Add(item);
        }

        var targetInfo = string.IsNullOrWhiteSpace(targetPrinter) ? "" : $" ➔ {targetPrinter}";
        Log?.Invoke($"[Queue] Added Job {item.JobId} from {item.ClientMachineName}{targetInfo} ({item.DocumentName}, {item.PageCount} pgs).");
        QueueChanged?.Invoke();
        return item;
    }

    public bool CancelJob(string jobId, string requestingClientIp, out string message)
    {
        RegisterClientPing(requestingClientIp);
        QueueItem? itemToCancel = null;
        lock (_gate)
        {
            var item = _jobs.FirstOrDefault(j => j.JobId.Equals(jobId, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                message = "Job not found or already finished.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(requestingClientIp)
                && !item.ClientIp.Equals(requestingClientIp, StringComparison.OrdinalIgnoreCase)
                && requestingClientIp != "127.0.0.1" && requestingClientIp != "localhost")
            {
                message = "You can only cancel your own jobs.";
                return false;
            }

            if (item.Status == QueueStatus.Processing)
            {
                item.Status = QueueStatus.Cancelled;
                item.CompletedAt = DateTime.Now;
                item.CancelReason = $"Cancelled by {requestingClientIp}";
                _activeJob = null;
                message = $"Job {jobId} cancelled during processing.";
                itemToCancel = item;
            }
            else if (item.Status == QueueStatus.Queued)
            {
                item.Status = QueueStatus.Cancelled;
                item.CompletedAt = DateTime.Now;
                item.CancelReason = $"Cancelled by {requestingClientIp}";
                message = $"Job {jobId} cancelled successfully.";
                itemToCancel = item;
            }
            else
            {
                message = $"Job {jobId} is already {item.Status}.";
                return false;
            }
        }

        if (itemToCancel is not null)
        {
            Log?.Invoke($"[Queue] Job {jobId} cancelled by {requestingClientIp}.");
            JobCancelled?.Invoke(itemToCancel);
            QueueChanged?.Invoke();
            return true;
        }

        return false;
    }

    public QueueStatusMessage GetStatusSnapshot(string forClientIp = "")
    {
        lock (_gate)
        {
            var msg = new QueueStatusMessage
            {
                ServerMachineName = Environment.MachineName,
                DeviceName = DeviceName,
                Category = Category,
                IsDeviceOnline = true,
                TotalConnectedClients = TotalConnectedClients,
                ActiveJobId = _activeJob?.JobId,
                ActiveClientName = _activeJob?.ClientMachineName,
                ActiveClientIp = _activeJob?.ClientIp,
                ActiveHardwareStatus = _activeJob?.HardwareStatusText,
                QueueDepth = _jobs.Count(j => j.Status == QueueStatus.Queued)
            };

            msg.SharedPrinters.AddRange(SharedPrinters);

            var pending = _jobs
                .Where(j => j.Status is QueueStatus.Queued or QueueStatus.Processing)
                .OrderBy(j => j.SubmittedAt)
                .ToList();

            foreach (var j in pending)
            {
                msg.Queue.Add(new QueueItemSnapshot
                {
                    JobId = j.JobId,
                    ClientMachineName = j.ClientMachineName,
                    ClientIp = j.ClientIp,
                    DocumentName = j.DocumentName,
                    PageCount = j.PageCount,
                    PageRange = j.PageRange,
                    PaperSize = j.PaperSize,
                    PrintOrigin = j.PrintOrigin,
                    TargetPrinter = j.TargetPrinter,
                    HardwareStatusText = j.HardwareStatusText,
                    Status = j.Status,
                    EstimatedDurationSeconds = j.EstimatedDurationSeconds,
                    SubmittedAt = j.SubmittedAt
                });
            }

            if (!string.IsNullOrWhiteSpace(forClientIp))
            {
                if (_activeJob is not null && _activeJob.ClientIp.Equals(forClientIp, StringComparison.OrdinalIgnoreCase))
                {
                    msg.YourPosition = 0;
                    msg.YourJobId = _activeJob.JobId;
                    msg.EstimatedWaitSeconds = 0;
                    msg.CanCancel = true;
                    msg.IsYourTurnNow = true;
                }
                else
                {
                    var index = pending.FindIndex(j => j.ClientIp.Equals(forClientIp, StringComparison.OrdinalIgnoreCase) && j.Status == QueueStatus.Queued);
                    if (index >= 0)
                    {
                        var job = pending[index];
                        msg.YourPosition = index + 1;
                        msg.YourJobId = job.JobId;
                        msg.CanCancel = true;
                        msg.IsYourTurnNow = false;

                        var waitSecs = 0;
                        for (int i = 0; i < index; i++)
                        {
                            waitSecs += pending[i].EstimatedDurationSeconds;
                        }
                        msg.EstimatedWaitSeconds = Math.Max(5, waitSecs);
                    }
                    else
                    {
                        msg.YourPosition = -1;
                        msg.YourJobId = null;
                        msg.EstimatedWaitSeconds = 0;
                        msg.CanCancel = false;
                        msg.IsYourTurnNow = false;
                    }
                }
            }

            return msg;
        }
    }

    public IReadOnlyList<QueueItem> GetAllJobs()
    {
        lock (_gate)
        {
            return _jobs.ToList();
        }
    }

    public void ClearFinishedJobs()
    {
        lock (_gate)
        {
            _jobs.RemoveAll(j => j.Status is QueueStatus.Completed or QueueStatus.Cancelled or QueueStatus.Failed);
        }
        QueueChanged?.Invoke();
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            QueueItem? nextJob = null;
            lock (_gate)
            {
                if (_activeJob is null)
                {
                    nextJob = _jobs.FirstOrDefault(j => j.Status == QueueStatus.Queued);
                    if (nextJob is not null)
                    {
                        nextJob.Status = QueueStatus.Processing;
                        nextJob.StartedAt = DateTime.Now;
                        _activeJob = nextJob;
                    }
                }
            }

            if (nextJob is not null)
            {
                Log?.Invoke($"[Queue] Starting Job {nextJob.JobId} for {nextJob.ClientMachineName} ({nextJob.DocumentName})…");
                JobStarted?.Invoke(nextJob);
                QueueChanged?.Invoke();

                if (AutoProcess)
                {
                    var payloadPath = nextJob.PayloadPath;
                    var printerName = nextJob.TargetPrinter;
                    if (string.IsNullOrWhiteSpace(printerName) && SharedPrinters.Count > 0)
                    {
                        printerName = SharedPrinters[0];
                    }

                    // If physical document and printer target exist, do REAL native hardware printing!
                    if (!string.IsNullOrWhiteSpace(payloadPath) && System.IO.File.Exists(payloadPath) && !string.IsNullOrWhiteSpace(printerName))
                    {
                        nextJob.HardwareStatusText = $"Spooling to {printerName}…";
                        QueueChanged?.Invoke();

                        Log?.Invoke($"[Physical Print] Rendering & dispatching '{nextJob.DocumentName}' to '{printerName}'…");
                        var (spoolOk, spoolMsg, jobId) = await NativeDocumentPrinter.PrintFileAsync(
                            printerName,
                            payloadPath,
                            nextJob.DocumentName,
                            pageRange: nextJob.PageRange,
                            paperSize: nextJob.PaperSize,
                            ct: ct);
                        Log?.Invoke($"[Physical Print] Dispatch result: {(spoolOk ? "SUCCESS" : "FAILED")} ({spoolMsg}, Spooler JobId: {jobId})");

                        if (!spoolOk)
                        {
                            lock (_gate)
                            {
                                if (nextJob.Status == QueueStatus.Processing)
                                {
                                    nextJob.Status = QueueStatus.Failed;
                                    nextJob.CancelReason = spoolMsg;
                                    nextJob.HardwareStatusText = $"❌ Failed: {spoolMsg}";
                                    nextJob.CompletedAt = DateTime.Now;
                                    _activeJob = null;
                                }
                            }
                            Log?.Invoke($"[Queue] Job {nextJob.JobId} status: {nextJob.Status} ({spoolMsg})");
                            JobCompleted?.Invoke(nextJob);
                            QueueChanged?.Invoke();
                        }
                        else
                        {
                            // Spooled successfully into Windows Print Spooler!
                            // Monitor real hardware status until print job finishes or error occurs
                            if (jobId > 0)
                            {
                                nextJob.HardwareStatusText = "🖨️ Printing on Hardware…";
                                QueueChanged?.Invoke();

                                for (int poll = 0; poll < 60; poll++)
                                {
                                    if (ct.IsCancellationRequested) break;
                                    await Task.Delay(1000, ct);

                                    lock (_gate)
                                    {
                                        if (nextJob.Status == QueueStatus.Cancelled) break;
                                    }

                                    var status = NativeDocumentPrinter.GetJobStatus(printerName, jobId);
                                    if (status.IsCompletedOrGone)
                                    {
                                        // Job has left the Windows spooler queue -> It is fully transferred to printer!
                                        Log?.Invoke($"[Spooler Tracker] Job {jobId} completed and cleared from Windows spooler.");
                                        break;
                                    }

                                    if (status.IsPaperOut)
                                    {
                                        nextJob.HardwareStatusText = "⚠️ Printer Out of Paper! Load tray.";
                                        QueueChanged?.Invoke();
                                    }
                                    else if (status.IsPaperJam)
                                    {
                                        nextJob.HardwareStatusText = "⚠️ Paper Jam detected!";
                                        QueueChanged?.Invoke();
                                    }
                                    else if (status.IsError)
                                    {
                                        nextJob.HardwareStatusText = "⚠️ Printer reported an error.";
                                        QueueChanged?.Invoke();
                                    }
                                    else if (status.IsOffline)
                                    {
                                        nextJob.HardwareStatusText = "⚠️ Printer is Offline.";
                                        QueueChanged?.Invoke();
                                    }
                                    else if (status.IsPrinting)
                                    {
                                        nextJob.HardwareStatusText = "🖨️ Printing pages on hardware…";
                                        QueueChanged?.Invoke();
                                    }
                                }
                            }
                            else
                            {
                                await Task.Delay(1500, ct);
                            }

                            QueueItem? completedJob = null;
                            lock (_gate)
                            {
                                if (nextJob.Status == QueueStatus.Processing)
                                {
                                    nextJob.Status = QueueStatus.Completed;
                                    nextJob.HardwareStatusText = "✓ Printed on hardware";
                                    nextJob.CompletedAt = DateTime.Now;
                                    _activeJob = null;
                                    completedJob = nextJob;
                                }
                            }

                            if (completedJob is not null)
                            {
                                Log?.Invoke($"[Queue] Job {completedJob.JobId} COMPLETED successfully on {printerName}.");
                                JobCompleted?.Invoke(completedJob);
                                QueueChanged?.Invoke();
                            }
                        }
                    }
                    else
                    {
                        // Fallback simulated processing for dummy / non-payload jobs
                        var totalTime = Math.Min(15000, nextJob.PageCount * SimulatedPageDelayMs);
                        var slices = 10;
                        var sliceDelay = Math.Max(100, totalTime / slices);

                        for (int s = 0; s < slices; s++)
                        {
                            if (ct.IsCancellationRequested) break;
                            await Task.Delay(sliceDelay, ct);

                            lock (_gate)
                            {
                                if (nextJob.Status == QueueStatus.Cancelled)
                                    break;
                            }
                        }

                        QueueItem? completedJob = null;
                        lock (_gate)
                        {
                            if (nextJob.Status == QueueStatus.Processing)
                            {
                                nextJob.Status = QueueStatus.Completed;
                                nextJob.CompletedAt = DateTime.Now;
                                _activeJob = null;
                                completedJob = nextJob;
                            }
                        }

                        if (completedJob is not null)
                        {
                            Log?.Invoke($"[Queue] Job {completedJob.JobId} COMPLETED successfully for {completedJob.ClientMachineName}.");
                            JobCompleted?.Invoke(completedJob);
                            QueueChanged?.Invoke();
                        }
                    }
                }
            }

            try
            {
                await Task.Delay(500, ct);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
