using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ApnaRemote.Windows.Input;

/// <summary>
/// Bi-directional clipboard synchronization between host and viewer over active session.
/// Synchronizes plain text, Unicode, and URLs with echo-suppression.
/// </summary>
public sealed class ClipboardSyncBridge : IDisposable
{
    private readonly DispatcherTimer _pollTimer;
    private readonly Func<string, Task> _sendClipboardAsync;
    private string _lastSentHash = "";
    private string _lastReceivedHash = "";
    private bool _disposed;

    public ClipboardSyncBridge(Func<string, Task> sendClipboardAsync)
    {
        _sendClipboardAsync = sendClipboardAsync;
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _pollTimer.Tick += OnPollTick;
    }

    public void Start()
    {
        if (_disposed) return;
        _pollTimer.Start();
    }

    public void Stop()
    {
        _pollTimer.Stop();
    }

    private void OnPollTick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        try
        {
            if (!Clipboard.ContainsText()) return;
            string text = Clipboard.GetText();
            if (string.IsNullOrEmpty(text) || text.Length > 256 * 1024) return;

            string hash = ComputeHash(text);
            if (hash == _lastSentHash || hash == _lastReceivedHash) return;

            _lastSentHash = hash;
            _ = _sendClipboardAsync(text);
        }
        catch
        {
            // Clipboard access can throw if locked by another application
        }
    }

    public void ApplyRemoteText(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 256 * 1024) return;
        string hash = ComputeHash(text);
        if (hash == _lastSentHash || hash == _lastReceivedHash) return;

        _lastReceivedHash = hash;
        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            try
            {
                Clipboard.SetText(text);
            }
            catch
            {
                // Clipboard momentarily busy
            }
        });
    }

    private static string ComputeHash(string input)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }

    public void Dispose()
    {
        _disposed = true;
        _pollTimer.Stop();
    }
}
