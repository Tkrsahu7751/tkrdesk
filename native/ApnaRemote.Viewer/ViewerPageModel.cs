using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ApnaRemote.Core;
using ApnaRemote.Protocol;

namespace ApnaRemote.Viewer;

public sealed class ViewerPageModel : INotifyPropertyChanged, IDisposable
{
    private readonly ArlViewerClient _client;
    private string _host = "";
    private string _port = ArlProtocol.DefaultPort.ToString();
    private string _pin = "";
    private string _fingerprint = "";
    private string _pasteUri = "";
    private byte[]? _frameJpeg;
    private bool _disposed;
    private long _lastStatsNotifyTick;
    private bool _scanning;
    private bool _keyboardVisible;
    private string _keyboardText = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public ViewerPageModel()
    {
        _client = new ArlViewerClient(peerDisplayName: DeviceInfo.Current.Name);
        _client.SetSynchronizationContext(SynchronizationContext.Current);
        _client.Changed += OnClientChanged;
        _client.FrameReceived += OnFrameReceived;

        ConnectCommand = new Command(Connect, () => CanConnect);
        DisconnectCommand = new Command(() =>
        {
            _client.ReleaseHeldInput();
            _client.Disconnect();
        }, () => CanDisconnect);
        ApplyUriCommand = new Command(ApplyUri);
        ScanQrCommand = new Command(async () => await ScanQrAsync(), () => !_scanning && !_client.IsBusy);
        ToggleKeyboardCommand = new Command(() =>
        {
            KeyboardVisible = !KeyboardVisible;
            if (!KeyboardVisible) KeyboardText = "";
        }, () => ControlEnabled);
        SendKeyboardCommand = new Command(SendKeyboard, () => ControlEnabled && !string.IsNullOrEmpty(KeyboardText));
    }

    public string VersionLabel { get; } = "Apna Remote Viewer 0.17.0-lab";
    public string BrandTitle => "Apna Remote";
    public string BrandSub => "Phone viewer · same Wi‑Fi lab";
    public string Host { get => _host; set { _host = value; Notify(); NotifyCanConnect(); } }
    public string Port { get => _port; set { _port = value; Notify(); NotifyCanConnect(); } }
    public string Pin { get => _pin; set { _pin = value; Notify(); NotifyCanConnect(); } }
    public string Fingerprint { get => _fingerprint; set { _fingerprint = value; Notify(); NotifyCanConnect(); } }
    public string PasteUri { get => _pasteUri; set { _pasteUri = value; Notify(); } }
    public string Status => _client.Status;
    public string PhaseLabel => _client.Phase switch
    {
        ArlViewerPhase.Idle => "READY",
        ArlViewerPhase.Connecting => "CONNECTING",
        ArlViewerPhase.WaitingApproval => "AWAITING APPROVAL",
        ArlViewerPhase.WaitingFrame => "WAITING FOR SCREEN",
        ArlViewerPhase.Viewing => "LIVE · VIEW",
        ArlViewerPhase.Controlling => "LIVE · CONTROL",
        ArlViewerPhase.Ended => "ENDED",
        _ => _client.Phase.ToString().ToUpperInvariant(),
    };
    public byte[]? FrameJpeg => _frameJpeg;
    public int RemoteWidth => _client.RemoteWidth;
    public int RemoteHeight => _client.RemoteHeight;
    public bool HasRemoteImage => _frameJpeg is { Length: > 0 };
    public bool IsBusy => _client.IsBusy;
    public bool ControlEnabled => _client.ControlEnabled;
    public bool ShowConnect => !_client.IsConnected && !HasRemoteImage;
    public bool ShowSession => _client.IsBusy || HasRemoteImage;
    public bool ShowWaiting => ShowSession && !HasRemoteImage;
    public bool CanDisconnect => _client.IsBusy;
    public bool CanConnect => !_client.IsBusy
        && !string.IsNullOrWhiteSpace(Host)
        && Pin.Trim().Length == 6
        && LanTls.IsValidFingerprintInput(Fingerprint);

    public bool KeyboardVisible
    {
        get => _keyboardVisible;
        set { _keyboardVisible = value; Notify(); ((Command)SendKeyboardCommand).ChangeCanExecute(); }
    }

    public string KeyboardText
    {
        get => _keyboardText;
        set { _keyboardText = value; Notify(); ((Command)SendKeyboardCommand).ChangeCanExecute(); }
    }

    public string StatsText => _client.IsConnected
        ? $"{_client.RemoteWidth}×{_client.RemoteHeight}  ·  {_client.ReceiveFps} FPS  ·  {_client.ReceiveKbps} kbps"
            + (_client.ControlEnabled ? "  ·  touch on" : "  ·  view-only")
        : "Scan the PC QR, or paste the phone link. Host must still Accept.";

    public string SessionHint => _client.Phase switch
    {
        ArlViewerPhase.WaitingApproval => "Approve on the PC — pick a display. Tick Allow mouse and keyboard for phone control.",
        ArlViewerPhase.WaitingFrame => "Host approved — first frame coming…",
        ArlViewerPhase.Controlling => "Tap = click · drag = move · long-press = right-click · two fingers = scroll.",
        ArlViewerPhase.Viewing => "View-only. On the PC, Grant control (or Accept with keyboard/mouse allowed).",
        _ => Status,
    };

    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand ApplyUriCommand { get; }
    public ICommand ScanQrCommand { get; }
    public ICommand ToggleKeyboardCommand { get; }
    public ICommand SendKeyboardCommand { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Changed -= OnClientChanged;
        _client.FrameReceived -= OnFrameReceived;
        _client.Dispose();
    }

    public void HandlePointer(RemotePointerEventArgs e)
    {
        if (!ControlEnabled) return;
        if (e.Kind != RemotePointerKind.Scroll)
        {
            if (!PointerMapping.TryFromViewport(e.X, e.Y, e.ViewportWidth, e.ViewportHeight,
                    RemoteWidth, RemoteHeight, out var n))
                return;
            switch (e.Kind)
            {
                case RemotePointerKind.Move:
                    _client.TrySendPointerMove(n.X, n.Y);
                    break;
                case RemotePointerKind.LeftDown:
                    _client.TrySendPointerMove(n.X, n.Y, force: true);
                    _client.TrySendButton(PointerButton.Left, down: true);
                    break;
                case RemotePointerKind.LeftUp:
                    _client.TrySendPointerMove(n.X, n.Y, force: true);
                    _client.TrySendButton(PointerButton.Left, down: false);
                    break;
                case RemotePointerKind.RightClick:
                    _client.TrySendPointerMove(n.X, n.Y, force: true);
                    _client.TrySendButton(PointerButton.Right, down: true);
                    _client.TrySendButton(PointerButton.Right, down: false);
                    break;
            }
            return;
        }

        if (e.ScrollDelta != 0)
            _client.TrySendScroll(e.ScrollDelta);
    }

    public bool TryApplyOfferText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (!NearbyOffer.TryParseConnectUri(text, out NearbyOffer offer))
        {
            int start = text.IndexOf("apnaremote://", StringComparison.OrdinalIgnoreCase);
            if (start < 0) return false;
            string slice = text[start..].Trim();
            int end = slice.IndexOfAny([' ', '\n', '\r', '"']);
            if (end > 0) slice = slice[..end];
            if (!NearbyOffer.TryParseConnectUri(slice, out offer)) return false;
            text = slice;
        }

        Host = offer.IPv4;
        Port = offer.Port.ToString();
        Pin = offer.Pin;
        Fingerprint = offer.Fingerprint;
        PasteUri = text;
        return true;
    }

    private void SendKeyboard()
    {
        if (!ControlEnabled || string.IsNullOrEmpty(KeyboardText)) return;
        if (_client.TrySendText(KeyboardText))
            KeyboardText = "";
    }

    private void Connect()
    {
        if (!CanConnect) return;
        if (!int.TryParse(Port.Trim(), out int port))
            port = ArlProtocol.DefaultPort;
        _client.Connect(Host, port, Pin, Fingerprint);
        RefreshCommands();
    }

    private void ApplyUri() => TryApplyOfferText(PasteUri);

    private async Task ScanQrAsync()
    {
        if (_scanning) return;
        _scanning = true;
        ((Command)ScanQrCommand).ChangeCanExecute();
        try
        {
            PermissionStatus status = await Permissions.RequestAsync<Permissions.Camera>();
            if (status != PermissionStatus.Granted)
            {
                await Shell.Current.DisplayAlert("Camera", "Camera permission is required to scan the host QR code.", "OK");
                return;
            }

            var page = new ScanQrPage();
            await Shell.Current.Navigation.PushAsync(page);
            string? value = await page.Result;
            if (string.IsNullOrWhiteSpace(value)) return;
            if (!TryApplyOfferText(value))
            {
                await Shell.Current.DisplayAlert("QR", "That QR is not an Apna Remote phone link.", "OK");
                return;
            }

            if (CanConnect)
                Connect();
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("QR scan", ex.Message, "OK");
        }
        finally
        {
            _scanning = false;
            ((Command)ScanQrCommand).ChangeCanExecute();
        }
    }

    private void OnClientChanged()
    {
        Notify(nameof(Status));
        Notify(nameof(PhaseLabel));
        Notify(nameof(IsBusy));
        Notify(nameof(ControlEnabled));
        Notify(nameof(ShowConnect));
        Notify(nameof(ShowSession));
        Notify(nameof(ShowWaiting));
        Notify(nameof(CanConnect));
        Notify(nameof(CanDisconnect));
        Notify(nameof(StatsText));
        Notify(nameof(SessionHint));
        Notify(nameof(RemoteWidth));
        Notify(nameof(RemoteHeight));
        if (!_client.ControlEnabled)
        {
            KeyboardVisible = false;
            KeyboardText = "";
        }
        if (!_client.IsBusy && _client.Phase == ArlViewerPhase.Ended)
        {
            _frameJpeg = null;
            Notify(nameof(FrameJpeg));
            Notify(nameof(HasRemoteImage));
            Notify(nameof(ShowConnect));
            Notify(nameof(ShowSession));
            Notify(nameof(ShowWaiting));
        }
        RefreshCommands();
    }

    private void OnFrameReceived(byte[] jpeg)
    {
        bool first = _frameJpeg is null;
        _frameJpeg = jpeg;
        if (first)
        {
            Notify(nameof(HasRemoteImage));
            Notify(nameof(ShowConnect));
            Notify(nameof(ShowSession));
            Notify(nameof(ShowWaiting));
            Notify(nameof(RemoteWidth));
            Notify(nameof(RemoteHeight));
        }
        Notify(nameof(FrameJpeg));
        long now = Environment.TickCount64;
        if (now - _lastStatsNotifyTick >= 1000)
        {
            _lastStatsNotifyTick = now;
            Notify(nameof(StatsText));
            Notify(nameof(SessionHint));
            Notify(nameof(ControlEnabled));
        }
    }

    private void NotifyCanConnect()
    {
        Notify(nameof(CanConnect));
        ((Command)ConnectCommand).ChangeCanExecute();
    }

    private void RefreshCommands()
    {
        ((Command)ConnectCommand).ChangeCanExecute();
        ((Command)DisconnectCommand).ChangeCanExecute();
        ((Command)ScanQrCommand).ChangeCanExecute();
        ((Command)ToggleKeyboardCommand).ChangeCanExecute();
        ((Command)SendKeyboardCommand).ChangeCanExecute();
    }

    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
