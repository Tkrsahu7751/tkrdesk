using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ApnaRemote.Windows.HardwareHub.Services;

public static class NativeDocumentPrinter
{
    public static async Task<(bool Success, string Message, int JobId)> PrintFileAsync(
        string printerName,
        string filePath,
        string documentName = "ApnaRemote_Print",
        string pageRange = "All",
        string paperSize = "A4",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(printerName))
            return (false, "Printer name is required.", 0);

        if (!File.Exists(filePath))
            return (false, $"File not found: {filePath}", 0);

        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        if (ext == ".pdf")
        {
            try
            {
                var pageBuffers = await PreRenderPdfPagesAsync(filePath, pageRange, ct);
                if (pageBuffers.Count == 0)
                    return (false, "No matching pages found in PDF for specified page range.", 0);

                return await RunSynchronousStaAsync(() => PrintPagesSynchronous(printerName, pageBuffers, documentName, paperSize));
            }
            catch (Exception ex)
            {
                return (false, $"PDF rendering error: {ex.Message}", 0);
            }
        }
        else if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif")
        {
            try
            {
                var imageBytes = await File.ReadAllBytesAsync(filePath, ct);
                return await RunSynchronousStaAsync(() => PrintPagesSynchronous(printerName, new[] { imageBytes }, documentName, paperSize));
            }
            catch (Exception ex)
            {
                return (false, $"Image print error: {ex.Message}", 0);
            }
        }
        else
        {
            // Fallback to raw spooler for pre-rendered PRN / RAW streams
            var bytes = await File.ReadAllBytesAsync(filePath, ct);
            var (ok, msg) = RawPrinterSpooler.SendBytesToPrinter(printerName, bytes, documentName);
            return (ok, msg, 0);
        }
    }

    private static Task<T> RunSynchronousStaAsync<T>(Func<T> action)
    {
        var tcs = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try
            {
                var result = action();
                tcs.SetResult(result);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    public static List<int> ParsePageRange(string rangeText, int totalPages)
    {
        var result = new SortedSet<int>();
        if (string.IsNullOrWhiteSpace(rangeText) || rangeText.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 0; i < totalPages; i++) result.Add(i);
            return result.ToList();
        }

        var parts = rangeText.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Contains('-'))
            {
                var sub = trimmed.Split('-');
                if (sub.Length == 2 && int.TryParse(sub[0].Trim(), out int start) && int.TryParse(sub[1].Trim(), out int end))
                {
                    var low = Math.Max(1, Math.Min(start, end));
                    var high = Math.Min(totalPages, Math.Max(start, end));
                    for (int p = low; p <= high; p++)
                    {
                        result.Add(p - 1);
                    }
                }
            }
            else if (int.TryParse(trimmed, out int singlePage))
            {
                if (singlePage >= 1 && singlePage <= totalPages)
                {
                    result.Add(singlePage - 1);
                }
            }
        }

        return result.ToList();
    }

    private static async Task<List<byte[]>> PreRenderPdfPagesAsync(string filePath, string pageRange, CancellationToken ct)
    {
        var list = new List<byte[]>();
        var storageFile = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(filePath));
        var pdfDoc = await PdfDocument.LoadFromFileAsync(storageFile);
        var totalCount = (int)pdfDoc.PageCount;

        var targetPages = ParsePageRange(pageRange, totalCount);

        foreach (var pageIndex in targetPages)
        {
            if (ct.IsCancellationRequested) break;
            using var page = pdfDoc.GetPage((uint)pageIndex);
            using var ras = new InMemoryRandomAccessStream();

            // Calculate High-DPI dimensions dynamically based on page's natural dimensions and aspect ratio (300 DPI: 300 / 72 = 4.16667)
            const double scale = 300.0 / 72.0;
            var targetWidth = Math.Max(100u, (uint)Math.Round(page.Size.Width * scale));
            var targetHeight = Math.Max(100u, (uint)Math.Round(page.Size.Height * scale));

            var renderOptions = new PdfPageRenderOptions
            {
                DestinationWidth = targetWidth,
                DestinationHeight = targetHeight
            };

            await page.RenderToStreamAsync(ras, renderOptions);

            using var netStream = ras.AsStream();
            using var ms = new MemoryStream();
            await netStream.CopyToAsync(ms, ct);
            list.Add(ms.ToArray());
        }

        return list;
    }

    private static Size GetPaperDimensions(string paperSize, PrintTicket? ticket)
    {
        return paperSize.Trim().ToLowerInvariant() switch
        {
            "4x6 photo" or "4x6" or "photo" => new Size(384.0, 576.0), // 4x6 in @ 96 dpi
            "a5" => new Size(559.4, 793.7),                          // 148 x 210 mm
            "letter" => new Size(816.0, 1056.0),                     // 8.5 x 11 in
            "legal" => new Size(816.0, 1344.0),                      // 8.5 x 14 in
            _ => new Size(ticket?.PageMediaSize?.Width ?? 793.7, ticket?.PageMediaSize?.Height ?? 1122.5) // A4 default
        };
    }

    private static (bool Success, string Message, int JobId) PrintPagesSynchronous(
        string printerName,
        IReadOnlyList<byte[]> pageBuffers,
        string documentName,
        string paperSize = "A4")
    {
        using var server = new LocalPrintServer();
        PrintQueue queue;
        try
        {
            queue = server.GetPrintQueue(printerName);
        }
        catch
        {
            return (false, $"Printer '{printerName}' not found in Windows.", 0);
        }

        using (queue)
        {
            if (queue.IsOffline)
            {
                return (false, $"Printer '{printerName}' is currently Offline / Cable Unplugged.", 0);
            }

            var ticket = queue.UserPrintTicket ?? queue.DefaultPrintTicket;
            var pageSize = GetPaperDimensions(paperSize, ticket);

            var paginator = new InMemoryImagesPaginator(pageBuffers, pageSize);
            var writer = PrintQueue.CreateXpsDocumentWriter(queue);

            writer.Write(paginator);

            queue.Refresh();
            var latestJob = queue.GetPrintJobInfoCollection()
                .OrderByDescending(j => j.TimeJobSubmitted)
                .FirstOrDefault();

            var jobId = latestJob?.JobIdentifier ?? 0;
            return (true, $"Dispatched {pageBuffers.Count} page(s) ({paperSize}) to '{queue.Name}'.", jobId);
        }
    }

    public static PrintJobStatusSummary GetJobStatus(string printerName, int jobId)
    {
        try
        {
            using var server = new LocalPrintServer();
            using var queue = server.GetPrintQueue(printerName);
            queue.Refresh();
            var job = queue.GetPrintJobInfoCollection().FirstOrDefault(j => j.JobIdentifier == jobId);
            if (job is null)
            {
                return new PrintJobStatusSummary
                {
                    JobId = jobId,
                    IsCompletedOrGone = true,
                    Description = "Job completed and cleared from spooler."
                };
            }

            var jobStatus = job.JobStatus;
            var queueStatus = queue.QueueStatus;

            var isPaperOut = jobStatus.HasFlag(PrintJobStatus.PaperOut) || queueStatus.HasFlag(PrintQueueStatus.PaperOut);
            var isJam = queueStatus.HasFlag(PrintQueueStatus.PaperJam);
            var isError = jobStatus.HasFlag(PrintJobStatus.Error) || jobStatus.HasFlag(PrintJobStatus.Blocked) || queueStatus.HasFlag(PrintQueueStatus.Error);
            var isOffline = queue.IsOffline || jobStatus.HasFlag(PrintJobStatus.Offline) || queueStatus.HasFlag(PrintQueueStatus.Offline);
            var isPrinting = jobStatus.HasFlag(PrintJobStatus.Printing);

            var desc = isPaperOut ? "Printer is Out of Paper!"
                : isJam ? "Paper Jam detected in printer!"
                : isError ? "Printer hardware error."
                : isOffline ? "Printer is Offline / Cable Unplugged."
                : isPrinting ? "Printing pages on hardware..."
                : "Spooling / In print queue";

            return new PrintJobStatusSummary
            {
                JobId = jobId,
                IsPrinting = isPrinting,
                IsPaperOut = isPaperOut,
                IsPaperJam = isJam,
                IsError = isError,
                IsOffline = isOffline,
                IsCompletedOrGone = false,
                Description = desc
            };
        }
        catch (Exception ex)
        {
            return new PrintJobStatusSummary
            {
                JobId = jobId,
                IsCompletedOrGone = true,
                Description = $"Spooler status query: {ex.Message}"
            };
        }
    }
}

internal sealed class InMemoryImagesPaginator : System.Windows.Documents.DocumentPaginator
{
    private readonly List<BitmapSource> _pages = new();
    private readonly Size _pageSize;

    public InMemoryImagesPaginator(IReadOnlyList<byte[]> imageBuffers, Size pageSize)
    {
        _pageSize = pageSize;
        foreach (var buf in imageBuffers)
        {
            using var ms = new MemoryStream(buf);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            _pages.Add(frame);
        }
    }

    public override System.Windows.Documents.DocumentPage GetPage(int pageNumber)
    {
        if (pageNumber < 0 || pageNumber >= _pages.Count)
            return System.Windows.Documents.DocumentPage.Missing;

        var bmp = _pages[pageNumber];
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Fit image maintaining aspect ratio
            double scale = Math.Min(_pageSize.Width / bmp.PixelWidth, _pageSize.Height / bmp.PixelHeight);
            double drawWidth = bmp.PixelWidth * scale;
            double drawHeight = bmp.PixelHeight * scale;
            double offsetX = (_pageSize.Width - drawWidth) / 2.0;
            double offsetY = (_pageSize.Height - drawHeight) / 2.0;

            dc.DrawImage(bmp, new Rect(offsetX, offsetY, drawWidth, drawHeight));
        }

        return new System.Windows.Documents.DocumentPage(visual, _pageSize, new Rect(0, 0, _pageSize.Width, _pageSize.Height), new Rect(0, 0, _pageSize.Width, _pageSize.Height));
    }

    public override bool IsPageCountValid => true;
    public override int PageCount => _pages.Count;
    public override Size PageSize
    {
        get => _pageSize;
        set { }
    }
    public override System.Windows.Documents.IDocumentPaginatorSource Source => null!;
}

public sealed class PrintJobStatusSummary
{
    public int JobId { get; set; }
    public bool IsPrinting { get; set; }
    public bool IsPaperOut { get; set; }
    public bool IsPaperJam { get; set; }
    public bool IsError { get; set; }
    public bool IsOffline { get; set; }
    public bool IsCompletedOrGone { get; set; }
    public string Description { get; set; } = "";
}
