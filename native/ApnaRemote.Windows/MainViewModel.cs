using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ApnaRemote.Core;
using ApnaRemote.Protocol;
using ApnaRemote.Windows.Capture;
using ApnaRemote.Windows.Lan;
using ApnaRemote.Windows.HardwareHub.Models;
using ApnaRemote.Windows.HardwareHub.Services;
using ApnaRemote.Windows.LabGrid;
using ApnaRemote.Windows.Broadcast;
using ApnaRemote.Windows.Services;
using Microsoft.Win32;
using QRCoder;

namespace ApnaRemote.Windows;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly LocalDesktopCapturePreview _localPreview;
    private readonly LanHostSession _lanHost;
    private readonly LanViewerSession _lanViewer;
    private readonly SessionMetricsRecorder _metrics;
    private readonly LabPreferences _prefs;
    private string _page = "Overview";
    private readonly NearbyBluetoothPublisher _nearbyPublisher;
    private readonly NearbyBluetoothScanner _nearbyScanner;
    private readonly LanWifiBeaconPublisher _wifiBeacon = new();
    private readonly LanWifiBeaconBrowser _wifiBrowser;
    private readonly LabGridService _labGridService;
    private readonly ScreenBroadcastManager _broadcastManager;
    private readonly DirectFileTransferService _fileTransferService;
    private readonly UpdateCheckerService _updateChecker;
    private string _lastNearbyOfferKey = "";
    private string? _nearbySyncInFlightKey;
    private int _nearbySyncGeneration;
    private ImageSource? _offerQrImage;
    private string _offerConnectUri = "";
    private string _remoteAddress = "";
    private string _remotePin = "";
    private string _remoteFingerprint = "";
    private string _assistBanner = "";
    private UiColorMode _colorMode = UiColorMode.SciFi;
    private bool _disposed;
    private bool _remoteFocused;
    private bool _wasViewerBusy;
    private bool _wasHostSharing;
    private bool _viewerFullscreen;
    private DateTimeOffset? _viewerStartedUtc;
    private DateTimeOffset? _hostShareStartedUtc;
    private string _viewerPeerForHistory = "";

    public string ComputerName { get; } = Environment.MachineName;
    public string VersionLabel { get; } = "TKR Desk " +
        (typeof(MainViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "v1.0");
    public string RemoteAddress { get => _remoteAddress; set { _remoteAddress = value; Notify(); } }
    public string RemotePin { get => _remotePin; set { _remotePin = value; Notify(); } }
    public string RemoteFingerprint { get => _remoteFingerprint; set { _remoteFingerprint = value; Notify(); } }
    public string Breadcrumb => _page switch
    {
        "Overview" => "Remote Desktop",
        "LabGrid" => "Lab Grid (CCTV Matrix)",
        "Broadcast" => "Screen Broadcast (1-to-1,000)",
        "Files" => "Direct File Transfer",
        "Devices" => "USB Hub & Hardware",
        _ => _page
    };
    public string PageTitle
    {
        get
        {
            if (_page == "Overview" && _lanViewer.IsBusy) return _lanViewer.Phase switch
            {
                ViewerPhase.Connecting => "Connecting",
                ViewerPhase.WaitingApproval => "Waiting for approval",
                ViewerPhase.WaitingFrame => "Waiting for screen",
                _ => "Live session"
            };
            if (_page == "Overview" && _lanHost.IsSharing) return "Sharing";
            if (_page == "Overview" && _lanHost.IsListening) return "Listening";
            return _page switch
            {
                "LabGrid" => "Lab Grid CCTV Monitor",
                "Broadcast" => "Screen Broadcast (1-to-1,000)",
                "Files" => "Direct PC-to-PC File Transfer",
                "Devices" => "USB Hub & Hardware",
                "History" => "History",
                "Settings" => "Settings",
                "Help" => "Help",
                _ => "Remote Desktop"
            };
        }
    }
    public string PageSubtitle
    {
        get
        {
            if (_page == "Overview" && _lanViewer.IsBusy && !_lanViewer.IsConnected)
                return "Session starts after host approval and the first frame.";
            if (_page == "Overview" && _lanViewer.IsConnected)
                return _lanViewer.ControlEnabled
                    ? "Click the remote screen to send keyboard input."
                    : "View-only. The host controls input permission.";
            if (_page == "Overview" && _lanHost.IsSharing)
                return "A peer is viewing the display you approved.";
            if (_page == "Overview" && _lanHost.IsListening)
                return "Share PIN and fingerprint with the other PC.";
            return _page switch
            {
                "LabGrid" => "Live CCTV matrix monitoring and one-click remote desktop takeover of all lab computers.",
                "Broadcast" => "Broadcast 1 presenter screen to hundreds of student screens simultaneously.",
                "Files" => "Send and receive files directly across Wi-Fi with automatic downloads folder saving.",
                "Devices" => "Share printers, scanners, plotters queue, pen drives with PIN, and USB peripherals across your LAN.",
                "History" => "Local session log. No PIN, fingerprint, or screenshots.",
                "Settings" => "Theme, quality, and local data.",
                "Help" => "What works in this lab build.",
                _ => "Connect to another PC or share this one."
            };
        }
    }
    public string EmptyTitle => "Hardware & Office Device Hub";
    public string EmptyDescription =>
        "Share office hardware queues, USB pen drives with PIN, and USB peripherals across your office network.";
    public Visibility OverviewVisibility => Visible(_page == "Overview");
    public Visibility OverviewHomeVisibility => Visible(_page == "Overview" && !_lanViewer.IsBusy);
    public Visibility LabGridVisibility => Visible(_page == "LabGrid");
    public Visibility BroadcastVisibility => Visible(_page == "Broadcast");
    public Visibility FilesVisibility => Visible(_page == "Files");
    public Visibility SecondaryPageVisibility => Visible(_page != "Overview");
    public Visibility EmptyVisibility => Visible(_page == "Devices");
    public Visibility HistoryVisibility => Visible(_page == "History");
    public Visibility SettingsVisibility => Visible(_page == "Settings");
    public Visibility HelpVisibility => Visible(_page == "Help");

    public Visibility NavOverviewTick => Visible(_page == "Overview");
    public Visibility NavLabGridTick => Visible(_page == "LabGrid");
    public Visibility NavBroadcastTick => Visible(_page == "Broadcast");
    public Visibility NavFilesTick => Visible(_page == "Files");
    public Visibility NavDevicesTick => Visible(_page == "Devices");
    public Visibility NavHistoryTick => Visible(_page == "History");
    public Visibility NavSettingsTick => Visible(_page == "Settings");
    public Visibility NavHelpTick => Visible(_page == "Help");

    public string LocalTkrId => TkrIdService.GetLocalTkrId();
    public ObservableCollection<LabGridPcItem> LabGridWorkstations => _labGridService.Workstations;
    public Visibility LabGridEmptyVisibility => Visible(_labGridService.Workstations.Count == 0);

    public ObservableCollection<BroadcastChannelItem> BroadcastChannels => _broadcastManager.ActiveChannels;
    public Visibility BroadcastChannelsEmptyVisibility => Visible(_broadcastManager.ActiveChannels.Count == 0);
    public string BroadcastStatus => _broadcastManager.BroadcastStatus;
    public string ViewerBroadcastStatus => _broadcastManager.ViewerStatus;
    public bool IsBroadcasting => _broadcastManager.IsBroadcasting;
    public bool IsWatchingBroadcast => _broadcastManager.IsWatching;
    public Visibility BroadcastStartBtnVisibility => Visible(!_broadcastManager.IsBroadcasting);
    public Visibility BroadcastStopBtnVisibility => Visible(_broadcastManager.IsBroadcasting);
    public string BroadcastTitle { get; set; } = "Classroom Presentation";
    public string LabNoticeText { get; set; } = "Attention: Class session starting now.";
    public string FileTransferTargetAddress { get; set; } = "";

    public string FileTransferStatus => _fileTransferService.Status;
    public double FileTransferProgress => _fileTransferService.ProgressPercentage;
    public Visibility FileTransferProgressVisibility => Visible(_fileTransferService.IsTransferring);

    public bool IsUpdateAvailable => _updateChecker.IsUpdateAvailable;
    public string UpdateBannerText => _updateChecker.UpdateBannerText;
    public Visibility UpdateBannerVisibility => Visible(_updateChecker.IsUpdateAvailable);
    public IReadOnlyList<LabHistoryEntry> HistoryEntries => _prefs.History;
    public Visibility HistoryEmptyVisibility => Visible(_page == "History" && _prefs.History.Count == 0);
    public Visibility HistoryListVisibility => Visible(_page == "History" && _prefs.History.Count > 0);
    public Visibility ViewerSessionVisibility => Visible(_page == "Overview" && _lanViewer.IsBusy);
    public Visibility ViewerWaitingVisibility => Visible(_lanViewer.IsBusy && !_lanViewer.IsConnected);
    public Visibility ViewerLiveVisibility => Visible(_lanViewer.IsConnected);
    public Visibility ViewerActionVisibility => Visible(_lanViewer.IsBusy);
    public Visibility HostActionVisibility => Visible(_lanHost.IsListening || _lanHost.IsSharing);
    /// <summary>Sidebar + title chrome. Hidden in viewer fullscreen so the remote fills the window.</summary>
    public Visibility AppChromeVisibility => Visible(!_viewerFullscreen);
    public bool IsViewerFullscreen => _viewerFullscreen;
    public bool CanToggleViewerFullscreen => _lanViewer.IsBusy;
    public string ViewerFullscreenLabel => _viewerFullscreen ? "Exit fullscreen" : "Fullscreen";
    public Thickness ViewerSurfaceMargin => _viewerFullscreen ? new Thickness(0) : new Thickness(20, 14, 20, 14);
    public CornerRadius ViewerSurfaceRadius => _viewerFullscreen ? new CornerRadius(0) : new CornerRadius(10);
    public string SessionBadgeColor => _lanViewer.IsConnected || _lanHost.IsSharing ? "#087A54" : "#6C591F";
    public Visibility HostHomeVisibility => Visible(_page == "Overview" && !_lanViewer.IsBusy);
    public Visibility LabToolsVisibility => Visibility.Collapsed;
    public string ViewerEndLabel => _lanViewer.IsConnected ? "End" : "Cancel";
    public string ViewerStageLabel => _lanViewer.Phase switch
    {
        ViewerPhase.Connecting => "CONNECTING",
        ViewerPhase.WaitingApproval => "AWAITING APPROVAL",
        ViewerPhase.WaitingFrame => "WAITING FOR SCREEN",
        _ => _lanViewer.ControlEnabled ? "LIVE · CONTROL" : "LIVE · VIEW"
    };
    public string SessionLiveLabel => _lanHost.IsSharing
        ? "SHARING"
        : _lanViewer.IsBusy
            ? ViewerStageLabel
            : _lanHost.IsListening
                ? "LISTENING"
                : "";
    public Visibility SessionLiveVisibility => Visible(_lanHost.IsSharing || _lanViewer.IsBusy || _lanHost.IsListening);
    public string ViewerStatsText =>
        $"{_lanViewer.ReceiveFps} FPS · {_lanViewer.ReceiveKbps:0} kbps · {_lanViewer.FramesReceived} total received · {(_lanViewer.ControlEnabled ? "control" : "view-only")}";
    public string HostStatsText => _lanHost.IsSharing
        ? $"{_lanHost.SendFps} FPS · {_lanHost.SendKbps:0} kbps · {_lanHost.FramesSent} total sent · {_lanHost.FramesDropped} dropped (old skipped)"
        : "Waiting for a viewer.";
    public IReadOnlyList<string> RecentHosts => _prefs.RecentHosts;
    public Visibility RecentHostsVisibility => Visible(_prefs.RecentHosts.Count > 0);
    public string ThemeStatusLabel =>
        "Glass UI · " + _colorMode switch
        {
            UiColorMode.Light => "light",
            UiColorMode.Dark => "dark",
            _ => "sci-fi",
        } + " (saved locally)";
    public string LightModeLabel => _colorMode == UiColorMode.Light ? "1 · Light ✓" : "1 · Light";
    public string DarkModeLabel => _colorMode == UiColorMode.Dark ? "2 · Dark ✓" : "2 · Dark";
    public string SciFiModeLabel => _colorMode == UiColorMode.SciFi ? "3 · Sci-Fi ✓" : "3 · Sci-Fi";
    public string AppearanceCycleLabel => _colorMode switch
    {
        UiColorMode.Light => "Light",
        UiColorMode.Dark => "Dark",
        _ => "Sci-Fi",
    };
    public Visibility SecondaryGlassVisibility => Visible(_page != "Overview");
    public bool HasHostSecrets => !string.IsNullOrEmpty(_lanHost.Pin) && !string.IsNullOrEmpty(_lanHost.FingerprintText);
    public Visibility HostSecretsVisibility => Visible(HasHostSecrets);
    public Visibility HostSecretsHintVisibility => Visible(!HasHostSecrets);
    public string HostSecretsHint => "Start sharing to show the PIN and fingerprint for this PC.";
    public string ClipboardStatus { get; private set; } = "";
    public Visibility ClipboardStatusVisibility => Visible(ClipboardStatus.Length > 0);
    public bool CanCopyAddress => PrimaryHostAddress is not ("No LAN address" or "");
    public bool CanCopyPin => HasHostSecrets;
    public bool CanCopyFingerprint => HasHostSecrets;
    public string NearbyHostStatus =>
        _nearbyPublisher.Status + " · " + _wifiBeacon.Status;
    public ImageSource? OfferQrImage => _offerQrImage;
    public Visibility OfferQrVisibility => Visible(_offerQrImage is not null);
    public string OfferConnectUri => _offerConnectUri;
    public bool CanCopyOfferUri => !string.IsNullOrEmpty(_offerConnectUri);
    public string NearbyScanStatus =>
        _wifiBrowser.IsBrowsing || _nearbyScanner.IsScanning
            ? _wifiBrowser.Status + " · " + _nearbyScanner.Status
            : "Find off — Find on Wi‑Fi / Bluetooth, or type address.";
    public bool IsNearbyAdvertising => _nearbyPublisher.IsAdvertising || _wifiBeacon.IsAdvertising;
    public bool IsNearbyScanning => _nearbyScanner.IsScanning || _wifiBrowser.IsBrowsing;
    public System.Collections.ObjectModel.ObservableCollection<NearbyPeer> NearbyPeers => _nearbyScanner.Peers;
    public System.Collections.ObjectModel.ObservableCollection<NearbyPeer> WifiPeers => _wifiBrowser.Peers;
    public Visibility NearbyPeersVisibility => Visible(_nearbyScanner.Peers.Count > 0);
    public Visibility WifiPeersVisibility => Visible(_wifiBrowser.Peers.Count > 0);
    public bool CanStartNearbyScan => !_nearbyScanner.IsScanning && !_wifiBrowser.IsBrowsing && CanConnect;
    public bool CanStopNearbyScan => _nearbyScanner.IsScanning || _wifiBrowser.IsBrowsing;
    public bool ControlEnabled => _lanViewer.ControlEnabled;
    public bool CanRevokeControl => _lanHost.CanRevoke;
    public bool CanPauseControl => _lanHost.CanPause;
    public bool CanGrantControl => _lanHost.CanGrant;
    public string HostControlStatus => _lanHost.ControlStatus;
    public Visibility HostControlVisibility => Visible(_lanHost.IsSharing);
    public string InputFocusHint => !_lanViewer.ControlEnabled
        ? (_lanViewer.ControlPaused ? "Control paused by host." : "View-only.")
        : _remoteFocused ? "Keyboard focused. Press Esc to release." : "Click the picture to focus the keyboard.";
    public void SetRemoteFocus(bool focused) { _remoteFocused = focused; if (!focused) ReleaseRemoteInput(); Notify(nameof(InputFocusHint)); }
    public void ReleaseRemoteInput() => _lanViewer.ReleaseHeldInput();
    public string PrimaryHostAddress
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_lanHost.LocalAddressesText)) return "No LAN address";
            string first = _lanHost.LocalAddressesText.Split(',')[0].Trim();
            return string.IsNullOrEmpty(first) ? "No LAN address" : first;
        }
    }
    public string HostPortHint => string.IsNullOrWhiteSpace(_lanHost.LocalAddressesText)
        ? "Connect this PC to Wi‑Fi or Ethernet."
        : $"Port {ArlProtocol.DefaultPort} · {_lanHost.LocalAddressesText}";
    public string ThisPcAddressHint => HostPortHint;
    public string HostAddressesText => HostPortHint;
    public ICommand RefreshAddressesCommand { get; }
    public bool IsLocalPreviewRunning => _localPreview.IsRunning;
    public bool CanStartLocalPreview => !_localPreview.IsRunning && _localPreview.IsSupported && !_lanHost.IsListening && !_lanViewer.IsBusy;
    public bool CanStopLocalPreview => _localPreview.IsRunning;
    public string LocalPreviewStatus => _localPreview.Status;
    public ImageSource? LocalPreviewImage => _localPreview.PreviewImage;
    public bool CanEnableReceiving => !_lanHost.IsListening && !_lanViewer.IsBusy && !_localPreview.IsRunning;
    public bool CanStopReceiving => _lanHost.IsListening || _lanHost.IsSharing;
    public bool CanConnect => !_lanViewer.IsBusy && !_lanHost.IsListening && !_localPreview.IsRunning;
    public bool CanDisconnectViewer => _lanViewer.IsBusy;
    public string HostPinText => string.IsNullOrEmpty(_lanHost.Pin) ? "—" : _lanHost.Pin;
    public string HostFingerprintText => string.IsNullOrEmpty(_lanHost.FingerprintText) ? "—" : _lanHost.FingerprintText;
    public string HostListenStatus =>
        _lanHost.IsSharing
            ? _lanHost.Status + " Tip: share another monitor, or minimize this window — capturing this Share panel causes flicker on the phone."
            : _lanHost.Status;
    public string ViewerStatus => _lanViewer.Status;
    public ImageSource? RemoteViewImage => _lanViewer.RemoteImage;
    public string BannerText
    {
        get
        {
            if (_lanViewer.IsBusy && !_lanViewer.IsConnected) return _lanViewer.Status;
            if (_lanViewer.IsConnected)
            {
                string live = _lanViewer.ControlEnabled
                    ? "Live with control. Click the remote screen for keyboard focus."
                    : "Live view-only.";
                return IsLikelySamePcLabViewer()
                    ? live + " Same-PC lab: lag and high “dropped” are normal — one CPU does share + view. Use the other monitor, or a second PC, for a fair test."
                    : live;
            }

            if (_lanHost.IsSharing)
            {
                string share = "Sharing · " + HostControlStatus;
                return IsLikelySamePcLabHost()
                    ? share + " Same-PC lab: one machine is both host and viewer — expect lower FPS and many dropped frames."
                    : share;
            }

            if (_lanHost.IsListening) return "Listening · approve the screen when a viewer connects.";
            if (_localPreview.IsRunning) return "Local preview only — nothing is sent on the network.";
            if (!string.IsNullOrEmpty(_assistBanner)) return _assistBanner;
            return "Attended LAN remote desktop over TLS.";
        }
    }
    public string FooterText
    {
        get
        {
            if (_lanViewer.IsBusy && !_lanViewer.IsConnected) return _lanViewer.Status;
            if (_lanHost.IsSharing)
            {
                return $"Sharing · {_lanHost.SendFps} FPS · {_lanHost.SendKbps:0} kbps · {_lanHost.FramesSent} total sent · {_lanHost.FramesDropped} dropped · port {_lanHost.Port}";
            }

            if (_lanViewer.IsConnected)
            {
                return $"Viewing · {_lanViewer.ReceiveFps} FPS · {_lanViewer.ReceiveKbps:0} kbps · {_lanViewer.FramesReceived} total received · {(_lanViewer.ControlEnabled ? "control on" : "view-only")}";
            }

            if (_lanHost.IsListening)
            {
                return $"Listening · PIN {_lanHost.Pin} · {PrimaryHostAddress}:{_lanHost.Port}";
            }

            if (!_localPreview.IsRunning)
            {
                return "TKR Desk · " + VersionLabel;
            }

            if (_localPreview.WantsEncoding && _localPreview.LastEncodeMetrics is { } m && _localPreview.IsEncoding)
            {
                return $"Local encode · preview {_localPreview.FramesPerSecond} FPS · encode {m.EncodeFramesPerSecond} FPS · {m.BitrateKbps:0} kbps · no network";
            }

            if (_localPreview.WantsEncoding)
            {
                return $"Local capture · preview {_localPreview.FramesPerSecond} FPS · encode warming up/failed · no network";
            }

            return $"Local preview · {_localPreview.FramesPerSecond} FPS · {_localPreview.FramesPresented} frames · no network";
        }
    }
    public ICommand NavigateCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand SetColorModeCommand { get; }
    public ICommand UseRecentHostCommand { get; }
    public ICommand ClearRecentHostsCommand { get; }
    public ICommand ClearHistoryCommand { get; }
    public ICommand StartLocalPreviewCommand { get; }
    public ICommand StartLocalEncodeCommand { get; }
    public ICommand StopLocalPreviewCommand { get; }
    public ICommand EnableReceivingCommand { get; }
    public ICommand StopReceivingCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectViewerCommand { get; }
    public ICommand RevokeControlCommand { get; }
    public ICommand PauseControlCommand { get; }
    public ICommand GrantControlCommand { get; }
    public ICommand SetQualityCommand { get; }
    public ICommand CopyAddressCommand { get; }
    public ICommand CopyPinCommand { get; }
    public ICommand CopyFingerprintCommand { get; }
    public ICommand CopyOfferUriCommand { get; }
    public ICommand StartNearbyScanCommand { get; }
    public ICommand StopNearbyScanCommand { get; }
    public ICommand UseNearbyPeerCommand { get; }
    public ICommand OpenMetricsFolderCommand { get; }
    public ICommand ToggleViewerFullscreenCommand { get; }
    public string MetricsStatusText => _metrics.StatusText;
    public string MetricsFolderHint => _metrics.MetricsFolder;
    public string QualityPresetLabel =>
        _lanHost.QualityPreset + " · " +
        _lanHost.QualityPreset.Profile().MaxWidth + "px · delay=" +
        _lanHost.QualityPreset.Profile().FrameDelayMs + "ms · H264 " +
        (_lanHost.QualityPreset.Profile().H264Bitrate / 1000) + "kbps@" +
        _lanHost.QualityPreset.Profile().H264Fps + "fps";
    public string StreamCodecHelp =>
        "Same Wi‑Fi only (not internet). JPEG is the supported Windows-to-Windows stream. H.264 stays disabled until a compatible, rebuilt wrapper passes native soak tests.";
    public string StreamCodecStatus =>
        "Preferred: " + _lanHost.StreamCodec +
        " · Active share: " + (_lanHost.IsSharing ? _lanHost.ActiveStreamCodec.ToString() : "(idle)") +
        " · H.264: " + (NetworkH264Bootstrap.IsReady ? "ready" : "disabled → JPEG");
    public ICommand SetStreamCodecCommand { get; }

    // Hardware Hub Navigation & Tabs
    private string _hardwareTab = "Queue";
    public string HardwareTab
    {
        get => _hardwareTab;
        set
        {
            if (_hardwareTab != value)
            {
                _hardwareTab = value;
                Notify();
                Notify(nameof(QueueTabVisibility));
                Notify(nameof(PenDriveTabVisibility));
                Notify(nameof(UsbTabVisibility));
                Notify(nameof(IsQueueTabActive));
                Notify(nameof(IsPenDriveTabActive));
                Notify(nameof(IsUsbTabActive));
                Notify(nameof(QueueTabButtonText));
                Notify(nameof(PenDriveTabButtonText));
                Notify(nameof(UsbTabButtonText));
            }
        }
    }
    public Visibility QueueTabVisibility => Visible(_hardwareTab == "Queue");
    public Visibility PenDriveTabVisibility => Visible(_hardwareTab == "PenDrive");
    public Visibility UsbTabVisibility => Visible(_hardwareTab == "Usb");
    public bool IsQueueTabActive => _hardwareTab == "Queue";
    public bool IsPenDriveTabActive => _hardwareTab == "PenDrive";
    public bool IsUsbTabActive => _hardwareTab == "Usb";
    public string QueueTabButtonText => IsQueueTabActive ? "🖨️ Smart Office Queue (Active) ✓" : "🖨️ Smart Office Queue";
    public string PenDriveTabButtonText => IsPenDriveTabActive ? "💾 Pen Drive Broadcaster (Active) ✓" : "💾 Pen Drive Broadcaster";
    public string UsbTabButtonText => IsUsbTabActive ? "🔌 USB Tunnel (Active) ✓" : "🔌 USB Peripherals Tunnel";
    public ICommand SelectHardwareTabCommand { get; }

    // LAN Discovery for USB Hub (Auto-detect office PCs)
    private readonly HubDiscoveryHost _hubDiscoveryHost = new();
    public IReadOnlyList<DiscoveredHub> DiscoveredHubs => _discoveredHubs;
    private IReadOnlyList<DiscoveredHub> _discoveredHubs = Array.Empty<DiscoveredHub>();
    public Visibility DiscoveredHubsVisibility => Visible(_discoveredHubs.Count > 0);
    public string HubScanStatus
    {
        get => _hubScanStatus;
        set { _hubScanStatus = value; Notify(); }
    }
    private string _hubScanStatus = "🔍 Click 'Scan LAN' to auto-detect office PCs.";
    public ICommand ScanHubsCommand { get; }
    public ICommand AutoFillHostIpCommand { get; }

    // Mode D: Smart Office Queue Properties & Commands
    private readonly SmartQueueManager _queueManager;
    private readonly NetworkSpoolServer _networkSpoolServer;
    private readonly SmartQueueClientService _queueClient = new();
    public bool IsQueueServerRunning => _networkSpoolServer.IsRunning;
    public string QueueServerStatus => _networkSpoolServer.IsRunning
        ? $"Online on TCP {NetworkSpoolServer.DefaultPort} (Clients: {_queueManager.TotalConnectedClients})"
        : "Server Stopped";
    public string QueueDeviceName
    {
        get => _queueManager.DeviceName;
        set { _queueManager.DeviceName = value; Notify(); }
    }
    public DeviceCategory QueueDeviceCategory
    {
        get => _queueManager.Category;
        set { _queueManager.Category = value; Notify(); }
    }
    public IReadOnlyList<QueueItem> QueueHostJobs => _queueManager.GetAllJobs();
    public string QueueHostActiveJobText => _queueManager.ActiveJob is not null
        ? $"⚡ Active Now: {_queueManager.ActiveJob.ClientMachineName} — {_queueManager.ActiveJob.DocumentName} ({_queueManager.ActiveJob.PageCount} pgs)"
        : "Active: Idle";

    // Host: Installed Windows Printers
    public ObservableCollection<InstalledPrinterItem> AvailablePrinters { get; } = new();
    public ICommand SelectAllPrintersCommand { get; }
    public ICommand DeselectAllPrintersCommand { get; }
    public ICommand RefreshPrintersCommand { get; }

    public string QueueClientServerIp
    {
        get => _queueClientServerIp;
        set { _queueClientServerIp = value; Notify(); }
    }
    private string _queueClientServerIp = "";
    public string QueueClientDocName
    {
        get => _queueClientDocName;
        set { _queueClientDocName = value; Notify(); }
    }
    private string _queueClientDocName = "Office_Doc";
    public int QueueClientPages
    {
        get => _queueClientPages;
        set { _queueClientPages = Math.Max(1, value); Notify(); }
    }
    private int _queueClientPages = 1;
    public DeviceCategory QueueClientCategory
    {
        get => _queueClientCategory;
        set { _queueClientCategory = value; Notify(); }
    }
    private DeviceCategory _queueClientCategory = DeviceCategory.Printer;

    // Client: Shared Printers from Host
    public ObservableCollection<string> ClientAvailablePrinters { get; } = new();
    public string SelectedClientPrinter
    {
        get => _selectedClientPrinter;
        set { _selectedClientPrinter = value; Notify(); }
    }
    private string _selectedClientPrinter = "";
    public Visibility ClientPrinterSelectorVisibility => Visible(ClientAvailablePrinters.Count > 1);
    public string ClientPrinterSummaryText => ClientAvailablePrinters.Count switch
    {
        0 => "Ready (No shared printer announced)",
        1 => $"Target: {ClientAvailablePrinters[0]} (Auto-selected ✓)",
        _ => $"Choose Target Printer ({ClientAvailablePrinters.Count} available):"
    };

    public string QueueClientPositionText
    {
        get
        {
            if (_queueClientStatus is null) return "No active request";
            if (_queueClientStatus.YourPosition == 0 && !string.IsNullOrWhiteSpace(_queueClientStatus.ActiveHardwareStatus))
            {
                return $"⚡ YOUR TURN: {_queueClientStatus.ActiveHardwareStatus}";
            }
            return _queueClientStatus.FormattedPosition;
        }
    }
    public string QueueClientWaitText => _queueClientStatus?.FormattedWaitTime ?? "Ready";
    public string QueueClientMessage
    {
        get => _queueClientMessage;
        set { _queueClientMessage = value; Notify(); }
    }
    private string _queueClientMessage = "Enter server IP and submit a job.";
    public bool CanCancelClientJob => _queueClientStatus?.CanCancel ?? false;
    private QueueStatusMessage? _queueClientStatus;

    // Client View of Live Print Queue
    public IReadOnlyList<QueueItemSnapshot> QueueClientJobs => _queueClientStatus?.Queue ?? (IReadOnlyList<QueueItemSnapshot>)Array.Empty<QueueItemSnapshot>();
    public Visibility QueueClientJobsVisibility => Visible(QueueClientJobs.Count > 0);

    public ICommand StartQueueServerCommand { get; }
    public ICommand StopQueueServerCommand { get; }
    public ICommand ClearQueueFinishedCommand { get; }
    public ICommand SubmitQueueJobCommand { get; }
    public ICommand CancelClientJobCommand { get; }
    public ICommand RefreshQueueStatusCommand { get; }

    // Windows Native Ctrl+P Integration
    private readonly VirtualPrinterSpoolBridge _virtualPrinterBridge = new();
    public bool IsVirtualPrinterInstalled => VirtualPrinterSpoolBridge.CheckIfPrinterInstalled();
    public string VirtualPrinterStatusText => IsVirtualPrinterInstalled
        ? $"🟢 Windows Ctrl+P: Ready (\"{VirtualPrinterSpoolBridge.ActivePrinterName}\")"
        : "⚠️ Windows Ctrl+P: Not Configured (Click to Enable)";
    public ICommand InstallVirtualPrinterCommand { get; }

    // In-App Document Chooser
    public string SelectedDocumentPath
    {
        get => _selectedDocumentPath;
        set
        {
            _selectedDocumentPath = value;
            Notify();
            Notify(nameof(SelectedDocumentName));
            Notify(nameof(SelectedDocumentSizeText));
            Notify(nameof(HasSelectedDocument));
            Notify(nameof(SelectedDocumentBadgeVisibility));
            Notify(nameof(HasNoSelectedDocumentVisibility));
        }
    }
    private string _selectedDocumentPath = "";
    public string SelectedDocumentName => string.IsNullOrWhiteSpace(SelectedDocumentPath)
        ? ""
        : System.IO.Path.GetFileName(SelectedDocumentPath);
    public string SelectedDocumentSizeText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SelectedDocumentPath) || !System.IO.File.Exists(SelectedDocumentPath))
                return "";
            var len = new System.IO.FileInfo(SelectedDocumentPath).Length;
            return len > 1024 * 1024
                ? $"{len / (1024.0 * 1024.0):F1} MB"
                : $"{Math.Max(1, len / 1024)} KB";
        }
    }
    public bool HasSelectedDocument => !string.IsNullOrWhiteSpace(SelectedDocumentPath) && System.IO.File.Exists(SelectedDocumentPath);
    public Visibility SelectedDocumentBadgeVisibility => Visible(HasSelectedDocument);
    public Visibility HasNoSelectedDocumentVisibility => Visible(!HasSelectedDocument);
    public ICommand BrowseDocumentCommand { get; }
    public ICommand ClearSelectedDocumentCommand { get; }

    // Paper Size & Page Range Selection
    public IReadOnlyList<string> PaperSizeOptions { get; } = new[] { "A4", "Letter", "Legal", "4x6 Photo", "A5" };
    public string SelectedPaperSize
    {
        get => _selectedPaperSize;
        set
        {
            if (_selectedPaperSize != value)
            {
                _selectedPaperSize = value;
                Notify();
            }
        }
    }
    private string _selectedPaperSize = "A4";

    public string PrintPageRange
    {
        get => _printPageRange;
        set
        {
            if (_printPageRange != value)
            {
                _printPageRange = value;
                Notify();
            }
        }
    }
    private string _printPageRange = "All";

    // Mode E: Pen Drive Broadcaster Properties & Commands
    private readonly PenDriveServerService _penDriveServer = new();
    private readonly PenDriveClientService _penDriveClient = new();
    public bool IsPenDriveServerRunning => _penDriveServer.IsRunning;
    public string PenDriveServerStatus => _penDriveServer.IsRunning
        ? $"Broadcasting {_penDriveServer.DriveRoot} on TCP {_penDriveServer.Port} (PIN: {_penDriveServer.Pin})"
        : "Server Stopped";
    public IReadOnlyList<PenDriveInfo> AvailablePenDrives => _availablePenDrives;
    private IReadOnlyList<PenDriveInfo> _availablePenDrives = Array.Empty<PenDriveInfo>();
    public PenDriveInfo? SelectedPenDrive
    {
        get => _selectedPenDrive;
        set { _selectedPenDrive = value; Notify(); }
    }
    private PenDriveInfo? _selectedPenDrive;
    public string PenDrivePin
    {
        get => _penDrivePin;
        set { _penDrivePin = value; Notify(); }
    }
    private string _penDrivePin = "1234";
    public bool PenDriveReadOnly
    {
        get => _penDriveReadOnly;
        set { _penDriveReadOnly = value; Notify(); }
    }
    private bool _penDriveReadOnly;
    public IReadOnlyList<AllowedClientItem> PenDriveAllowedClients => _penDriveServer.GetAllowedClients();

    public string PenDriveClientHostIp
    {
        get => _penDriveClientHostIp;
        set { _penDriveClientHostIp = value; Notify(); }
    }
    private string _penDriveClientHostIp = "";
    public string PenDriveClientPin
    {
        get => _penDriveClientPin;
        set { _penDriveClientPin = value; Notify(); }
    }
    private string _penDriveClientPin = "1234";
    public bool IsPenDriveClientConnected => _penDriveClient.IsConnected;
    public string PenDriveClientStatusText
    {
        get => _penDriveClientStatusText;
        set { _penDriveClientStatusText = value; Notify(); }
    }
    private string _penDriveClientStatusText = "Enter Host IP and 4-digit PIN to connect.";
    public IReadOnlyList<FileEntry> PenDriveClientFiles => _penDriveClientFiles;
    private IReadOnlyList<FileEntry> _penDriveClientFiles = Array.Empty<FileEntry>();
    public string PenDriveCurrentPath => string.IsNullOrWhiteSpace(_penDriveClient.CurrentPath) ? "/" : "/" + _penDriveClient.CurrentPath;

    public ICommand RefreshPenDrivesCommand { get; }
    public ICommand StartPenDriveServerCommand { get; }
    public ICommand StopPenDriveServerCommand { get; }
    public ICommand ToggleClientAllowedCommand { get; }
    public ICommand ConnectPenDriveClientCommand { get; }
    public ICommand DisconnectPenDriveClientCommand { get; }
    public ICommand RefreshPenDriveFilesCommand { get; }
    public ICommand DownloadPenDriveFileCommand { get; }
    public ICommand PenDriveNavigateUpCommand { get; }
    public ICommand OpenPenDriveInExplorerCommand { get; }
    public ICommand OpenDownloadsFolderCommand { get; }

    // Mode C: Generic USB Peripherals Properties & Commands
    private readonly UsbShareHostService _usbShareHost = new();
    public bool IsUsbSharingActive => _usbShareHost.IsSharing;
    public string UsbSharingStatus => _usbShareHost.IsSharing
        ? $"Sharing {_usbShareHost.SharedDevices.Count} USB peripheral(s) on TCP 3240"
        : "Idle";
    public IReadOnlyList<UsbDeviceItem> AvailableUsbDevices => _availableUsbDevices;
    private IReadOnlyList<UsbDeviceItem> _availableUsbDevices = Array.Empty<UsbDeviceItem>();
    public UsbDeviceItem? SelectedUsbDevice
    {
        get => _selectedUsbDevice;
        set { _selectedUsbDevice = value; Notify(); }
    }
    private UsbDeviceItem? _selectedUsbDevice;
    public string UsbClientHostIp
    {
        get => _usbClientHostIp;
        set { _usbClientHostIp = value; Notify(); }
    }
    private string _usbClientHostIp = "";
    public string UsbClientBusId
    {
        get => _usbClientBusId;
        set { _usbClientBusId = value; Notify(); }
    }
    private string _usbClientBusId = "1-1";
    public string UsbClientStatus
    {
        get => _usbClientStatus;
        set { _usbClientStatus = value; Notify(); }
    }
    private string _usbClientStatus = "Enter Host IP and BusID to attach peripheral.";

    public ICommand RefreshUsbDevicesCommand { get; }
    public ICommand ShareUsbDeviceCommand { get; }
    public ICommand UnshareUsbDeviceCommand { get; }
    public ICommand AttachUsbDeviceCommand { get; }
    public ICommand DetachUsbDeviceCommand { get; }
    public ICommand CopyLocalTkrIdCommand { get; }
    public ICommand ConnectLabPcCommand { get; }
    public ICommand RefreshLabGridCommand { get; }
    public ICommand PromptBroadcastAnnouncementCommand { get; }
    public ICommand StartBroadcastCommand { get; }
    public ICommand StopBroadcastCommand { get; }
    public ICommand JoinBroadcastCommand { get; }
    public ICommand LeaveBroadcastCommand { get; }
    public ICommand PickAndSendFileCommand { get; }
    public ICommand OpenTransfersFolderCommand { get; }
    public ICommand OpenUpdatePageCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainViewModel()
    {
        _prefs = LabPreferences.Load();
        _localPreview = new LocalDesktopCapturePreview(Application.Current.Dispatcher);
        _localPreview.Changed += OnLocalPreviewChanged;
        _lanHost = new LanHostSession(Application.Current.Dispatcher);
        _lanHost.Changed += OnLanChanged;
        _lanViewer = new LanViewerSession(Application.Current.Dispatcher);
        _lanViewer.Changed += OnLanChanged;
        _metrics = new SessionMetricsRecorder(VersionLabel);
        _nearbyPublisher = new NearbyBluetoothPublisher(Application.Current.Dispatcher);
        _nearbyPublisher.Changed += OnNearbyChanged;
        _nearbyScanner = new NearbyBluetoothScanner(Application.Current.Dispatcher);
        _nearbyScanner.Changed += OnNearbyChanged;
        _wifiBrowser = new LanWifiBeaconBrowser(Application.Current.Dispatcher);
        _wifiBrowser.Changed += OnNearbyChanged;
        _labGridService = new LabGridService(Application.Current.Dispatcher);
        _broadcastManager = new ScreenBroadcastManager(Application.Current.Dispatcher);
        _fileTransferService = new DirectFileTransferService();
        _updateChecker = new UpdateCheckerService();
        _ = _updateChecker.CheckForUpdatesAsync();

        _labGridService.NoticeReceived += (sender, message) =>
        {
            MessageBox.Show($"[Announcement from {sender}]\n\n{message}", "TKR Desk — Lab Alert", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        _updateChecker.UpdateFound += () =>
        {
            Notify(nameof(IsUpdateAvailable));
            Notify(nameof(UpdateBannerText));
            Notify(nameof(UpdateBannerVisibility));
        };
        _broadcastManager.StateChanged += () =>
        {
            Notify(nameof(BroadcastChannels));
            Notify(nameof(BroadcastChannelsEmptyVisibility));
            Notify(nameof(BroadcastStatus));
            Notify(nameof(IsBroadcasting));
            Notify(nameof(IsWatchingBroadcast));
            Notify(nameof(BroadcastStartBtnVisibility));
            Notify(nameof(BroadcastStopBtnVisibility));
        };
        _fileTransferService.ProgressChanged += (fileName, pct) =>
        {
            Notify(nameof(FileTransferStatus));
            Notify(nameof(FileTransferProgress));
            Notify(nameof(FileTransferProgressVisibility));
        };
        _fileTransferService.FileReceived += (filePath) =>
        {
            Notify(nameof(FileTransferStatus));
            MessageBox.Show($"File received successfully!\nSaved to: {filePath}", "TKR Desk File Transfer", MessageBoxButton.OK, MessageBoxImage.Information);
        };

        if (Enum.TryParse(_prefs.QualityPreset, true, out LabQualityPreset savedQuality))
            _lanHost.QualityPreset = savedQuality;
        ApplyPreferredStreamCodecFromPrefs();
        if (Enum.TryParse(_prefs.ColorMode, true, out UiColorMode savedMode))
            _colorMode = savedMode;
        else
            _colorMode = _prefs.DarkTheme ? UiColorMode.Dark : UiColorMode.Light;
        ApplyTheme(_colorMode, save: false);

        NavigateCommand = new RelayCommand(value =>
        {
            string? page = value as string;
            if (page is not ("Overview" or "LabGrid" or "Broadcast" or "Files" or "Devices" or "History" or "Settings" or "Help")) return;
            SetRemoteFocus(false);
            _page = page;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        });
        ToggleThemeCommand = new RelayCommand(_ =>
        {
            UiColorMode next = _colorMode switch
            {
                UiColorMode.Light => UiColorMode.Dark,
                UiColorMode.Dark => UiColorMode.SciFi,
                _ => UiColorMode.Light,
            };
            ApplyTheme(next, save: true);
        });
        SetColorModeCommand = new RelayCommand(value =>
        {
            if (value is string name && Enum.TryParse(name, true, out UiColorMode mode))
                ApplyTheme(mode, save: true);
        });
        UseRecentHostCommand = new RelayCommand(value =>
        {
            if (value is string host && host.Length > 0)
            {
                RemoteAddress = host;
                Notify(nameof(RemoteAddress));
            }
        });
        ClearRecentHostsCommand = new RelayCommand(_ =>
        {
            _prefs.ClearRecentHosts();
            SavePrefs();
            Notify(nameof(RecentHosts));
            Notify(nameof(RecentHostsVisibility));
        });
        ClearHistoryCommand = new RelayCommand(_ =>
        {
            _prefs.ClearHistory();
            SavePrefs();
            NotifyHistory();
        });
        StartLocalPreviewCommand = new RelayCommand(_ => _ = StartLocalPreviewAsync(encode: false));
        StartLocalEncodeCommand = new RelayCommand(_ => _ = StartLocalPreviewAsync(encode: true));
        StopLocalPreviewCommand = new RelayCommand(_ => _localPreview.Stop());
        EnableReceivingCommand = new RelayCommand(_ => StartReceiving());
        StopReceivingCommand = new RelayCommand(_ => _lanHost.Stop());
        ConnectCommand = new RelayCommand(_ => StartConnect());
        DisconnectViewerCommand = new RelayCommand(_ => _lanViewer.Disconnect());
        RevokeControlCommand = new RelayCommand(_ => { if (CanRevokeControl) _lanHost.RevokeControl(); });
        PauseControlCommand = new RelayCommand(_ => { if (CanPauseControl) _lanHost.PauseControl(); });
        GrantControlCommand = new RelayCommand(_ => { if (CanGrantControl) _lanHost.GrantControl(); });
        SetQualityCommand = new RelayCommand(value =>
        {
            if (value is string name && Enum.TryParse(name, ignoreCase: true, out LabQualityPreset preset))
            {
                _lanHost.QualityPreset = preset;
                _prefs.QualityPreset = preset.ToString();
                SavePrefs();
                Notify(nameof(QualityPresetLabel));
                Notify(nameof(FooterText));
            }
        });
        SetStreamCodecCommand = new RelayCommand(value =>
        {
            if (value is string name && Enum.TryParse(name, ignoreCase: true, out LabStreamCodec codec))
            {
                if (codec == LabStreamCodec.H264 && !NetworkH264Bootstrap.TryEnsureLoaded(out _))
                {
                    _lanHost.StreamCodec = LabStreamCodec.Jpeg;
                    _prefs.StreamCodec = nameof(LabStreamCodec.Jpeg);
                    SavePrefs();
                    Notify(nameof(StreamCodecStatus));
                    Notify(nameof(FooterText));
                    return;
                }

                _lanHost.StreamCodec = codec;
                _prefs.StreamCodec = codec.ToString();
                SavePrefs();
                Notify(nameof(StreamCodecStatus));
                Notify(nameof(FooterText));
            }
        });
        CopyAddressCommand = new RelayCommand(_ => CopyShareText($"{PrimaryHostAddress}:{_lanHost.Port}", "Address copied"));
        CopyPinCommand = new RelayCommand(_ =>
        {
            if (!CanCopyPin) return;
            CopyShareText(_lanHost.Pin, "PIN copied");
        });
        CopyFingerprintCommand = new RelayCommand(_ =>
        {
            if (!CanCopyFingerprint) return;
            CopyShareText(_lanHost.FingerprintText, "Fingerprint copied");
        });
        CopyOfferUriCommand = new RelayCommand(_ =>
        {
            if (!CanCopyOfferUri) return;
            CopyShareText(_offerConnectUri, "Phone connect link copied");
        });
        StartNearbyScanCommand = new RelayCommand(_ =>
        {
            if (!CanStartNearbyScan) return;
            _wifiBrowser.Start();
            _nearbyScanner.Start();
            OnNearbyChanged();
        });
        StopNearbyScanCommand = new RelayCommand(_ =>
        {
            _wifiBrowser.Stop();
            _nearbyScanner.Stop();
            OnNearbyChanged();
        });
        UseNearbyPeerCommand = new RelayCommand(value =>
        {
            if (value is not NearbyPeer peer || !CanConnect) return;
            // Discovery is address-only. Never auto-fill PIN/FP or auto-connect from air.
            RemoteAddress = peer.ConnectionAddress;
            RemotePin = "";
            RemoteFingerprint = "";
            _assistBanner = "IP filled from Find nearby. Type PIN + fingerprint from the host Share panel (compare on screen), then Connect. Host must Accept.";
            _nearbyScanner.Stop();
            _wifiBrowser.Stop();
            OnNearbyChanged();
            Notify(nameof(RemotePin));
            Notify(nameof(RemoteFingerprint));
            Notify(nameof(RemoteAddress));
            Notify(nameof(BannerText));
        });
        RefreshAddressesCommand = new RelayCommand(_ =>
        {
            _lanHost.RefreshLocalAddresses();
            OnLanChanged();
        });
        OpenMetricsFolderCommand = new RelayCommand(_ =>
        {
            try
            {
                Directory.CreateDirectory(_metrics.MetricsFolder);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = _metrics.MetricsFolder,
                    UseShellExecute = true
                });
            }
            catch
            {
                /* ignore */
            }
        });
        ToggleViewerFullscreenCommand = new RelayCommand(_ =>
        {
            if (!CanToggleViewerFullscreen) return;
            SetViewerFullscreen(!_viewerFullscreen);
        });

        _queueManager = new SmartQueueManager();
        _networkSpoolServer = new NetworkSpoolServer(_queueManager);

        SelectAllPrintersCommand = new RelayCommand(_ =>
        {
            var hasConnected = AvailablePrinters.Any(p => p.IsConnected);
            foreach (var p in AvailablePrinters)
            {
                p.IsSelected = hasConnected ? p.IsConnected : true;
            }
            SyncSharedPrintersWithQueueManager();
        });

        DeselectAllPrintersCommand = new RelayCommand(_ =>
        {
            foreach (var p in AvailablePrinters)
            {
                p.IsSelected = false;
            }
            SyncSharedPrintersWithQueueManager();
        });

        RefreshPrintersCommand = new RelayCommand(_ =>
        {
            RefreshHostPrinters();
        });

        RefreshHostPrinters();
        _queueManager.QueueChanged += () =>
        {
            Application.Current?.Dispatcher?.BeginInvoke(() =>
            {
                Notify(nameof(QueueHostJobs));
                Notify(nameof(QueueHostActiveJobText));
                Notify(nameof(QueueServerStatus));
            });
        };
        _penDriveServer.ClientsChanged += () =>
        {
            Application.Current?.Dispatcher?.BeginInvoke(() =>
            {
                Notify(nameof(PenDriveAllowedClients));
                Notify(nameof(PenDriveServerStatus));
            });
        };
        _usbShareHost.StateChanged += () =>
        {
            Application.Current?.Dispatcher?.BeginInvoke(() =>
            {
                Notify(nameof(IsUsbSharingActive));
                Notify(nameof(UsbSharingStatus));
            });
        };

        SelectHardwareTabCommand = new RelayCommand(value =>
        {
            if (value is string tab)
            {
                HardwareTab = tab;
                if (_discoveredHubs.Count == 0)
                {
                    _ = Task.Run(async () =>
                    {
                        var hubs = await HubDiscoveryClient.ScanAsync();
                        Application.Current?.Dispatcher?.BeginInvoke(() =>
                        {
                            _discoveredHubs = hubs;
                            Notify(nameof(DiscoveredHubs));
                            Notify(nameof(DiscoveredHubsVisibility));
                            if (hubs.Count > 0)
                            {
                                HubScanStatus = $"Found {hubs.Count} office PC(s) on LAN.";
                                var first = hubs[0];
                                if (string.IsNullOrWhiteSpace(QueueClientServerIp)) QueueClientServerIp = first.IpAddress;
                                if (string.IsNullOrWhiteSpace(PenDriveClientHostIp)) PenDriveClientHostIp = first.IpAddress;
                                if (string.IsNullOrWhiteSpace(UsbClientHostIp)) UsbClientHostIp = first.IpAddress;
                            }
                        });
                    });
                }
            }
        });

        _hubDiscoveryHost.Start();

        ScanHubsCommand = new RelayCommand(async _ =>
        {
            HubScanStatus = "Scanning LAN for office PCs…";
            var hubs = await HubDiscoveryClient.ScanAsync();
            _discoveredHubs = hubs;
            Notify(nameof(DiscoveredHubs));
            Notify(nameof(DiscoveredHubsVisibility));

            if (hubs.Count > 0)
            {
                HubScanStatus = $"Found {hubs.Count} office PC(s) on LAN. Auto-filled!";
                var first = hubs[0];
                QueueClientServerIp = first.IpAddress;
                PenDriveClientHostIp = first.IpAddress;
                UsbClientHostIp = first.IpAddress;
            }
            else
            {
                HubScanStatus = "No office PC found on LAN. (Start server on the other PC, or enter IP manually).";
            }
        });

        AutoFillHostIpCommand = new RelayCommand(param =>
        {
            string? ip = null;
            if (param is DiscoveredHub hub)
            {
                ip = hub.IpAddress;
                HubScanStatus = $"Selected {hub.MachineName} ({hub.IpAddress})";
            }
            else if (param is string s)
            {
                ip = s;
            }
            if (!string.IsNullOrWhiteSpace(ip))
            {
                QueueClientServerIp = ip;
                PenDriveClientHostIp = ip;
                UsbClientHostIp = ip;
            }
        });

        // Mode D commands
        StartQueueServerCommand = new RelayCommand(_ =>
        {
            try
            {
                _networkSpoolServer.Start();
                Notify(nameof(IsQueueServerRunning));
                Notify(nameof(QueueServerStatus));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not start Queue Server: " + ex.Message, "TKR Desk", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        });
        StopQueueServerCommand = new RelayCommand(_ =>
        {
            _networkSpoolServer.Stop();
            Notify(nameof(IsQueueServerRunning));
            Notify(nameof(QueueServerStatus));
        });
        ClearQueueFinishedCommand = new RelayCommand(_ =>
        {
            _queueManager.ClearFinishedJobs();
            Notify(nameof(QueueHostJobs));
            Notify(nameof(QueueHostActiveJobText));
        });
        _virtualPrinterBridge.JobIntercepted += (bytes, docName) =>
        {
            Application.Current?.Dispatcher?.BeginInvoke(async () =>
            {
                var target = SelectedClientPrinter;
                if (string.IsNullOrWhiteSpace(target) && ClientAvailablePrinters.Count > 0)
                {
                    target = ClientAvailablePrinters[0];
                }
                var serverIp = !string.IsNullOrWhiteSpace(QueueClientServerIp) ? QueueClientServerIp : "127.0.0.1";
                QueueClientMessage = $"[Ctrl+P] Intercepted '{docName}' ({bytes.Length / 1024} KB). Sending to {serverIp}…";
                var res = await _queueClient.SubmitJobAsync(
                    serverIp,
                    docName,
                    1,
                    DeviceCategory.Printer,
                    target,
                    fileBytes: bytes,
                    pageRange: "All",
                    paperSize: SelectedPaperSize,
                    printOrigin: "Ctrl+P Wireless");
                if (res is not null)
                {
                    _queueClientStatus = res;
                    UpdateClientSharedPrinters(res);
                    QueueClientMessage = $"⚡ [Ctrl+P Wireless] Sent '{docName}' to {target}! Queued.";
                    Notify(nameof(QueueClientPositionText));
                    Notify(nameof(QueueClientWaitText));
                    Notify(nameof(CanCancelClientJob));
                    Notify(nameof(QueueClientJobs));
                    Notify(nameof(QueueClientJobsVisibility));
                    StartPollingQueueStatus(serverIp);
                }
            });
        };
        _virtualPrinterBridge.Start();

        InstallVirtualPrinterCommand = new RelayCommand(async _ =>
        {
            QueueClientMessage = $"Configuring Windows Ctrl+P printer '{VirtualPrinterSpoolBridge.ActivePrinterName}'…";
            var (ok, msg) = await VirtualPrinterSpoolBridge.EnsurePrinterInstalledAsync(VirtualPrinterSpoolBridge.ActivePrinterName);
            Notify(nameof(IsVirtualPrinterInstalled));
            Notify(nameof(VirtualPrinterStatusText));
            QueueClientMessage = msg;
        });

        BrowseDocumentCommand = new RelayCommand(_ =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Document or Image to Print",
                Filter = "Documents & Images (*.pdf;*.docx;*.xlsx;*.txt;*.png;*.jpg)|*.pdf;*.docx;*.xlsx;*.txt;*.png;*.jpg;*.jpeg|All Files (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                SelectedDocumentPath = dlg.FileName;
                QueueClientDocName = System.IO.Path.GetFileName(dlg.FileName);
            }
        });

        ClearSelectedDocumentCommand = new RelayCommand(_ =>
        {
            SelectedDocumentPath = "";
        });

        SubmitQueueJobCommand = new RelayCommand(async _ =>
        {
            if (string.IsNullOrWhiteSpace(QueueClientServerIp))
            {
                QueueClientMessage = "Please enter the server IP address.";
                return;
            }
            byte[]? fileBytes = null;
            var docName = QueueClientDocName;
            if (HasSelectedDocument)
            {
                try
                {
                    fileBytes = await System.IO.File.ReadAllBytesAsync(SelectedDocumentPath);
                    docName = SelectedDocumentName;
                }
                catch (Exception ex)
                {
                    QueueClientMessage = $"Error reading file: {ex.Message}";
                    return;
                }
            }
            QueueClientMessage = "Submitting job…";
            var status = await _queueClient.SubmitJobAsync(
                QueueClientServerIp,
                docName,
                QueueClientPages,
                QueueClientCategory,
                SelectedClientPrinter,
                fileBytes: fileBytes,
                pageRange: PrintPageRange,
                paperSize: SelectedPaperSize,
                printOrigin: "App Upload");
            _queueClientStatus = status;
            UpdateClientSharedPrinters(status);
            QueueClientMessage = status is not null 
                ? (fileBytes != null ? $"📁 [App Upload] '{docName}' ({fileBytes.Length / 1024} KB) submitted successfully!" : "Job submitted successfully!") 
                : "Failed to connect to queue server.";
            Notify(nameof(QueueClientPositionText));
            Notify(nameof(QueueClientWaitText));
            Notify(nameof(CanCancelClientJob));
            Notify(nameof(QueueClientJobs));
            Notify(nameof(QueueClientJobsVisibility));

            if (status?.YourJobId is not null)
            {
                StartPollingQueueStatus(QueueClientServerIp);
            }
        });
        CancelClientJobCommand = new RelayCommand(async _ =>
        {
            if (_queueClientStatus?.YourJobId is { } id)
            {
                QueueClientMessage = "Cancelling job…";
                var status = await _queueClient.CancelJobAsync(QueueClientServerIp, id);
                _queueClientStatus = status;
                UpdateClientSharedPrinters(status);
                QueueClientMessage = "Job cancelled.";
                Notify(nameof(QueueClientPositionText));
                Notify(nameof(QueueClientWaitText));
                Notify(nameof(CanCancelClientJob));
                Notify(nameof(QueueClientJobs));
                Notify(nameof(QueueClientJobsVisibility));
            }
        });
        RefreshQueueStatusCommand = new RelayCommand(async _ =>
        {
            if (string.IsNullOrWhiteSpace(QueueClientServerIp)) return;
            var status = await _queueClient.GetStatusAsync(QueueClientServerIp);
            if (status is not null)
            {
                _queueClientStatus = status;
                UpdateClientSharedPrinters(status);
                Notify(nameof(QueueClientPositionText));
                Notify(nameof(QueueClientWaitText));
                Notify(nameof(CanCancelClientJob));
                Notify(nameof(QueueClientJobs));
                Notify(nameof(QueueClientJobsVisibility));
            }
        });

        // Mode E commands
        RefreshPenDrivesCommand = new RelayCommand(_ =>
        {
            _availablePenDrives = PenDriveServerService.GetRemovableDrives();
            if (_availablePenDrives.Count > 0 && SelectedPenDrive is null)
                SelectedPenDrive = _availablePenDrives[0];
            Notify(nameof(AvailablePenDrives));
            Notify(nameof(SelectedPenDrive));
        });
        StartPenDriveServerCommand = new RelayCommand(_ =>
        {
            if (SelectedPenDrive is null)
            {
                MessageBox.Show("Please select a pen drive to share.", "TKR Desk", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                _penDriveServer.Start(SelectedPenDrive.DriveLetter, PenDrivePin, PenDriveReadOnly);
                Notify(nameof(IsPenDriveServerRunning));
                Notify(nameof(PenDriveServerStatus));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not start Pen Drive server: " + ex.Message, "TKR Desk", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        });
        StopPenDriveServerCommand = new RelayCommand(_ =>
        {
            _penDriveServer.Stop();
            Notify(nameof(IsPenDriveServerRunning));
            Notify(nameof(PenDriveServerStatus));
        });
        ToggleClientAllowedCommand = new RelayCommand(item =>
        {
            if (item is AllowedClientItem client)
            {
                _penDriveServer.SetClientAllowed(client.IpAddress, !client.IsAllowed);
                Notify(nameof(PenDriveAllowedClients));
            }
        });
        ConnectPenDriveClientCommand = new RelayCommand(async _ =>
        {
            if (string.IsNullOrWhiteSpace(PenDriveClientHostIp))
            {
                PenDriveClientStatusText = "Please enter the Host IP address.";
                return;
            }
            PenDriveClientStatusText = "Connecting…";
            var resp = await _penDriveClient.ConnectAndAuthAsync(PenDriveClientHostIp, PenDriveClientPin);
            PenDriveClientStatusText = resp.Message;
            Notify(nameof(IsPenDriveClientConnected));
            if (resp.Granted)
            {
                _penDriveClientFiles = await _penDriveClient.ListFilesAsync();
                Notify(nameof(PenDriveClientFiles));
                Notify(nameof(PenDriveCurrentPath));
            }
        });
        DisconnectPenDriveClientCommand = new RelayCommand(_ =>
        {
            _penDriveClient.Disconnect();
            _penDriveClientFiles = Array.Empty<FileEntry>();
            PenDriveClientStatusText = "Disconnected.";
            Notify(nameof(IsPenDriveClientConnected));
            Notify(nameof(PenDriveClientFiles));
            Notify(nameof(PenDriveCurrentPath));
        });
        RefreshPenDriveFilesCommand = new RelayCommand(async _ =>
        {
            if (!_penDriveClient.IsConnected) return;
            _penDriveClientFiles = await _penDriveClient.ListFilesAsync(_penDriveClient.CurrentPath);
            Notify(nameof(PenDriveClientFiles));
            Notify(nameof(PenDriveCurrentPath));
        });
        DownloadPenDriveFileCommand = new RelayCommand(async item =>
        {
            if (item is FileEntry file && !file.IsDirectory)
            {
                var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                var dest = Path.Combine(downloads, file.Name);
                PenDriveClientStatusText = $"Downloading {file.Name}…";
                var ok = await _penDriveClient.DownloadFileAsync(file.RelativePath, dest);
                PenDriveClientStatusText = ok ? $"Downloaded to Downloads\\{file.Name}!" : "Download failed.";
            }
            else if (item is FileEntry dir && dir.IsDirectory)
            {
                _penDriveClientFiles = await _penDriveClient.ListFilesAsync(dir.RelativePath);
                Notify(nameof(PenDriveClientFiles));
                Notify(nameof(PenDriveCurrentPath));
            }
        });
        PenDriveNavigateUpCommand = new RelayCommand(async _ =>
        {
            if (!_penDriveClient.IsConnected) return;
            _penDriveClient.NavigateUp();
            _penDriveClientFiles = await _penDriveClient.ListFilesAsync(_penDriveClient.CurrentPath);
            Notify(nameof(PenDriveClientFiles));
            Notify(nameof(PenDriveCurrentPath));
        });
        OpenPenDriveInExplorerCommand = new RelayCommand(_ =>
        {
            if (!_penDriveClient.OpenInWindowsExplorer())
            {
                MessageBox.Show("Direct Windows UNC share is not accessible or not created.", "TKR Desk", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        });
        OpenDownloadsFolderCommand = new RelayCommand(_ =>
        {
            try
            {
                var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                Directory.CreateDirectory(downloads);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = downloads,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open Downloads folder: " + ex.Message, "TKR Desk", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        });

        // Mode C commands
        RefreshUsbDevicesCommand = new RelayCommand(async _ =>
        {
            _availableUsbDevices = await _usbShareHost.RefreshDevicesAsync();
            if (_availableUsbDevices.Count > 0 && SelectedUsbDevice is null)
                SelectedUsbDevice = _availableUsbDevices[0];
            Notify(nameof(AvailableUsbDevices));
            Notify(nameof(SelectedUsbDevice));
        });
        ShareUsbDeviceCommand = new RelayCommand(async dev =>
        {
            var target = (dev as UsbDeviceItem) ?? SelectedUsbDevice;
            if (target is null) return;
            await _usbShareHost.ShareDeviceAsync(target);
            Notify(nameof(IsUsbSharingActive));
            Notify(nameof(UsbSharingStatus));
        });
        UnshareUsbDeviceCommand = new RelayCommand(async dev =>
        {
            var target = (dev as UsbDeviceItem) ?? SelectedUsbDevice;
            if (target is null) return;
            await _usbShareHost.UnshareDeviceAsync(target);
            Notify(nameof(IsUsbSharingActive));
            Notify(nameof(UsbSharingStatus));
        });
        AttachUsbDeviceCommand = new RelayCommand(async _ =>
        {
            if (string.IsNullOrWhiteSpace(UsbClientHostIp) || string.IsNullOrWhiteSpace(UsbClientBusId))
            {
                UsbClientStatus = "Enter Host IP and BusID.";
                return;
            }
            UsbClientStatus = "Attaching remote USB peripheral…";
            var result = await UsbipCli.AttachAsync(UsbClientHostIp, UsbClientBusId);
            UsbClientStatus = result.Ok ? "Attached successfully to local system!" : "Attach failed: " + result.Output;
        });
        DetachUsbDeviceCommand = new RelayCommand(async _ =>
        {
            if (string.IsNullOrWhiteSpace(UsbClientBusId)) return;
            UsbClientStatus = "Detaching USB peripheral…";
            var result = await UsbipCli.DetachAsync(UsbClientBusId);
            UsbClientStatus = result.Ok ? "Detached successfully." : "Detach result: " + result.Output;
        });

        // Initial scan for pen drives
        _availablePenDrives = PenDriveServerService.GetRemovableDrives();
        if (_availablePenDrives.Count > 0) SelectedPenDrive = _availablePenDrives[0];

        CopyLocalTkrIdCommand = new RelayCommand(_ =>
        {
            try
            {
                Clipboard.SetText(LocalTkrId);
                ClipboardStatus = "TKR ID copied!";
                Notify(nameof(ClipboardStatus));
                Notify(nameof(ClipboardStatusVisibility));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not copy TKR ID: " + ex.Message, "TKR Desk", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        });

        ConnectLabPcCommand = new RelayCommand(param =>
        {
            if (param is not LabGridPcItem pc) return;
            RemoteAddress = $"{pc.IPv4}:{pc.Port}";
            _page = "Overview";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
            StartConnect();
        });

        RefreshLabGridCommand = new RelayCommand(_ =>
        {
            _labGridService.RefreshFromDiscovery();
            Notify(nameof(LabGridWorkstations));
            Notify(nameof(LabGridEmptyVisibility));
        });

        PromptBroadcastAnnouncementCommand = new RelayCommand(async _ =>
        {
            if (string.IsNullOrWhiteSpace(LabNoticeText)) return;
            await _labGridService.BroadcastAnnouncementAsync(LabNoticeText);
            MessageBox.Show("Announcement broadcast sent to all lab workstations!", "TKR Desk — Lab Alert", MessageBoxButton.OK, MessageBoxImage.Information);
        });

        StartBroadcastCommand = new RelayCommand(_ =>
        {
            string title = string.IsNullOrWhiteSpace(BroadcastTitle) ? "Classroom Presentation" : BroadcastTitle;
            _broadcastManager.StartBroadcasting(title);
            Notify(nameof(IsBroadcasting));
            Notify(nameof(BroadcastStatus));
            Notify(nameof(BroadcastStartBtnVisibility));
            Notify(nameof(BroadcastStopBtnVisibility));
        });

        StopBroadcastCommand = new RelayCommand(_ =>
        {
            _broadcastManager.StopBroadcasting();
            Notify(nameof(IsBroadcasting));
            Notify(nameof(BroadcastStatus));
            Notify(nameof(BroadcastStartBtnVisibility));
            Notify(nameof(BroadcastStopBtnVisibility));
        });

        JoinBroadcastCommand = new RelayCommand(param =>
        {
            if (param is not BroadcastChannelItem channel) return;
            _broadcastManager.JoinBroadcast(channel);
            Notify(nameof(IsWatchingBroadcast));
            Notify(nameof(ViewerBroadcastStatus));
        });

        LeaveBroadcastCommand = new RelayCommand(_ =>
        {
            _broadcastManager.LeaveBroadcast();
            Notify(nameof(IsWatchingBroadcast));
            Notify(nameof(ViewerBroadcastStatus));
        });

        PickAndSendFileCommand = new RelayCommand(async _ =>
        {
            string target = FileTransferTargetAddress.Trim();
            if (string.IsNullOrWhiteSpace(target))
            {
                MessageBox.Show("Please enter the destination computer IP address or 9-digit TKR ID.", "TKR Desk File Transfer", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int port = DirectFileTransferService.DefaultTransferPort;
            if (TkrIdService.IsTkrId(target))
            {
                var resolved = TkrIdService.ResolveTkrId(target);
                if (resolved is not null)
                {
                    target = resolved.IPv4;
                }
                else
                {
                    MessageBox.Show($"TKR ID '{target}' was not found on your network. Verify the computer is online.", "TKR Desk File Transfer", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            else if (target.Contains(':', StringComparison.Ordinal))
            {
                var parts = target.Split(':', 2);
                target = parts[0];
                _ = int.TryParse(parts[1], out port);
            }

            var dialog = new OpenFileDialog
            {
                Title = "Select File to Send Directly to Remote PC",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    await _fileTransferService.SendFileAsync(target, port, dialog.FileName);
                    MessageBox.Show($"File '{Path.GetFileName(dialog.FileName)}' transferred successfully!", "TKR Desk File Transfer", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("File transfer failed: " + ex.Message, "TKR Desk File Transfer", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        });

        OpenTransfersFolderCommand = new RelayCommand(_ =>
        {
            try
            {
                Directory.CreateDirectory(_fileTransferService.TransferFolder);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = _fileTransferService.TransferFolder,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open transfers folder: " + ex.Message, "TKR Desk", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        });

        OpenUpdatePageCommand = new RelayCommand(_ =>
        {
            _updateChecker.OpenDownloadPage();
        });

        _lanHost.RefreshLocalAddresses();
    }

    public void SetViewerFullscreen(bool value)
    {
        if (_viewerFullscreen == value) return;
        if (value && !_lanViewer.IsBusy) return;
        _viewerFullscreen = value;
        Notify(nameof(IsViewerFullscreen));
        Notify(nameof(AppChromeVisibility));
        Notify(nameof(ViewerFullscreenLabel));
        Notify(nameof(ViewerSurfaceMargin));
        Notify(nameof(ViewerSurfaceRadius));
        Notify(nameof(CanToggleViewerFullscreen));
        ViewerFullscreenChanged?.Invoke();
    }

    public event Action? ViewerFullscreenChanged;

    public bool TryRemotePointer(double x, double y, double viewportWidth, double viewportHeight, bool force = false)
    {
        int frameW = _lanViewer.RemoteImage?.PixelWidth ?? _lanViewer.RemoteWidth;
        int frameH = _lanViewer.RemoteImage?.PixelHeight ?? _lanViewer.RemoteHeight;
        if (!PointerMapping.TryFromViewport(x, y, viewportWidth, viewportHeight, frameW, frameH, out var normalized))
        {
            return false;
        }

        return _lanViewer.TrySendPointerMove(normalized.X, normalized.Y, force);
    }

    public bool TryRemoteButton(PointerButton button, bool down)
        => _lanViewer.TrySendButton(button, down);

    public bool TryRemoteKey(int virtualKey, bool down)
        => _lanViewer.TrySendKey(virtualKey, down);

    public bool TryRemoteScroll(int delta)
        => _lanViewer.TrySendScroll(delta);

    public bool TryRemoteText(string text)
        => _lanViewer.TrySendText(text);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _localPreview.Changed -= OnLocalPreviewChanged;
        _lanHost.Changed -= OnLanChanged;
        _lanViewer.Changed -= OnLanChanged;
        _nearbyPublisher.Changed -= OnNearbyChanged;
        _nearbyScanner.Changed -= OnNearbyChanged;
        _wifiBrowser.Changed -= OnNearbyChanged;
        _nearbyPublisher.Dispose();
        _nearbyScanner.Dispose();
        _wifiBeacon.Dispose();
        _wifiBrowser.Dispose();
        _localPreview.Dispose();
        _metrics.Dispose();
        _lanHost.Dispose();
        _lanViewer.Dispose();
        _networkSpoolServer.Dispose();
        _queueManager.Dispose();
        _queueClient.Dispose();
        _penDriveServer.Dispose();
        _penDriveClient.Dispose();
        _usbShareHost.Dispose();
        _hubDiscoveryHost.Dispose();
        _virtualPrinterBridge.Dispose();
        _labGridService.Dispose();
        _broadcastManager.Dispose();
        _fileTransferService.Dispose();
        _updateChecker.Dispose();
    }

    private void StartReceiving()
    {
        if (!CanEnableReceiving) return;
        try
        {
            Window? window = Application.Current.MainWindow;
            IntPtr hwnd = window is null ? IntPtr.Zero : new WindowInteropHelper(window).EnsureHandle();
            if (hwnd == IntPtr.Zero)
            {
                MessageBox.Show("Window handle missing.", "TKR Desk", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _lanHost.StartListening(hwnd);
            OnLanChanged();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not start LAN receiving: " + ex.Message, "TKR Desk", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void StartConnect()
    {
        if (!CanConnect) return;
        try
        {
            string host = RemoteAddress.Trim();
            int port = ArlProtocol.DefaultPort;

            if (TkrIdService.IsTkrId(host))
            {
                var resolved = TkrIdService.ResolveTkrId(host);
                if (resolved is not null)
                {
                    host = resolved.IPv4;
                    port = resolved.Port;
                }
                else
                {
                    MessageBox.Show(
                        $"TKR ID '{host}' was not found on your local network.\n\nMake sure the remote computer is running TKR Desk with Sharing turned ON and is connected to the same Wi-Fi / LAN.",
                        "TKR Desk — Resolution",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }
            }
            else if (host.Contains(':', StringComparison.Ordinal))
            {
                string[] parts = host.Split(':', 2);
                host = parts[0];
                _ = int.TryParse(parts[1], out port);
            }

            _prefs.RememberHost(host);
            SavePrefs();
            Notify(nameof(RecentHosts));
            Notify(nameof(RecentHostsVisibility));
            _viewerPeerForHistory = host + (port == ArlProtocol.DefaultPort ? "" : ":" + port);
            _lanViewer.Connect(host, port, RemotePin, RemoteFingerprint);
            OnLanChanged();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not connect: " + ex.Message, "TKR Desk", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task StartLocalPreviewAsync(bool encode)
    {
        if (!CanStartLocalPreview) return;
        try
        {
            Window? window = Application.Current.MainWindow;
            IntPtr hwnd = window is null ? IntPtr.Zero : new WindowInteropHelper(window).EnsureHandle();
            await _localPreview.StartAsync(hwnd, encode);
        }
        catch (Exception ex)
        {
            _localPreview.Stop();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocalPreviewStatus)));
            System.Diagnostics.Debug.WriteLine("Local preview failed: " + ex);
            MessageBox.Show(
                "Local capture could not start: " + ex.Message,
                "TKR Desk",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnLocalPreviewChanged()
    {
        Notify(nameof(IsLocalPreviewRunning));
        Notify(nameof(CanStartLocalPreview));
        Notify(nameof(CanStopLocalPreview));
        Notify(nameof(LocalPreviewStatus));
        Notify(nameof(LocalPreviewImage));
        Notify(nameof(CanEnableReceiving));
        Notify(nameof(CanConnect));
        Notify(nameof(BannerText));
        Notify(nameof(FooterText));
    }

    private void OnLanChanged()
    {
        TrackHistoryTransitions();
        Notify(nameof(CanEnableReceiving));
        Notify(nameof(CanStopReceiving));
        Notify(nameof(CanConnect));
        Notify(nameof(CanDisconnectViewer));
        Notify(nameof(CanStartLocalPreview));
        Notify(nameof(HostPinText));
        Notify(nameof(HostFingerprintText));
        Notify(nameof(HasHostSecrets));
        Notify(nameof(HostSecretsVisibility));
        Notify(nameof(HostSecretsHintVisibility));
        Notify(nameof(HostListenStatus));
        Notify(nameof(HostAddressesText));
        Notify(nameof(HostPortHint));
        Notify(nameof(ThisPcAddressHint));
        Notify(nameof(PrimaryHostAddress));
        Notify(nameof(CanCopyAddress));
        Notify(nameof(CanCopyPin));
        Notify(nameof(CanCopyFingerprint));
        Notify(nameof(ViewerStatus));
        Notify(nameof(RemoteViewImage));
        Notify(nameof(BannerText));
        Notify(nameof(FooterText));
        Notify(nameof(PageTitle));
        Notify(nameof(PageSubtitle));
        Notify(nameof(ViewerSessionVisibility));
        Notify(nameof(ViewerWaitingVisibility));
        Notify(nameof(ViewerLiveVisibility));
        Notify(nameof(ViewerActionVisibility));
        Notify(nameof(HostActionVisibility));
        Notify(nameof(CanToggleViewerFullscreen));
        Notify(nameof(ViewerFullscreenLabel));
        Notify(nameof(AppChromeVisibility));
        Notify(nameof(SessionBadgeColor));
        Notify(nameof(ViewerEndLabel));
        Notify(nameof(ViewerStageLabel));
        Notify(nameof(HostHomeVisibility));
        Notify(nameof(OverviewHomeVisibility));
        Notify(nameof(SecondaryGlassVisibility));
        Notify(nameof(SecondaryPageVisibility));
        Notify(nameof(LabToolsVisibility));
        Notify(nameof(SessionLiveLabel));
        Notify(nameof(SessionLiveVisibility));
        Notify(nameof(ViewerStatsText));
        Notify(nameof(HostStatsText));
        Notify(nameof(ControlEnabled));
        Notify(nameof(CanRevokeControl));
        Notify(nameof(CanPauseControl));
        Notify(nameof(CanGrantControl));
        Notify(nameof(HostControlStatus));
        Notify(nameof(HostControlVisibility));
        Notify(nameof(InputFocusHint));
        Notify(nameof(RecentHosts));
        Notify(nameof(RecentHostsVisibility));
        Notify(nameof(ThemeStatusLabel));
        Notify(nameof(QualityPresetLabel));
        Notify(nameof(StreamCodecStatus));
        Notify(nameof(CanStartNearbyScan));
        Notify(nameof(CanStopNearbyScan));
        NotifyHistory();
        SyncSessionMetrics();
        Notify(nameof(MetricsStatusText));
        _ = SyncNearbyPublisherAsync();
    }

    private void SyncSessionMetrics()
    {
        _metrics.Sync(
            hostSharing: _lanHost.IsSharing,
            viewerConnected: _lanViewer.IsConnected,
            snapshot: () =>
            {
                if (_lanHost.IsSharing)
                {
                    return new SessionMetricsRecorder.SampleSnapshot(
                        _lanHost.SendFps,
                        _lanHost.SendKbps,
                        _lanHost.FramesSent,
                        _lanHost.FramesDropped,
                        _lanHost.BytesSent);
                }

                return new SessionMetricsRecorder.SampleSnapshot(
                    _lanViewer.ReceiveFps,
                    _lanViewer.ReceiveKbps,
                    _lanViewer.FramesReceived,
                    0,
                    _lanViewer.BytesReceived);
            });
    }

    private void OnNearbyChanged()
    {
        Notify(nameof(NearbyHostStatus));
        Notify(nameof(OfferQrImage));
        Notify(nameof(OfferQrVisibility));
        Notify(nameof(OfferConnectUri));
        Notify(nameof(CanCopyOfferUri));
        Notify(nameof(NearbyScanStatus));
        Notify(nameof(IsNearbyAdvertising));
        Notify(nameof(IsNearbyScanning));
        Notify(nameof(NearbyPeers));
        Notify(nameof(NearbyPeersVisibility));
        Notify(nameof(WifiPeers));
        Notify(nameof(WifiPeersVisibility));
        Notify(nameof(CanStartNearbyScan));
        Notify(nameof(CanStopNearbyScan));
    }

    private async Task SyncNearbyPublisherAsync()
    {
        int? ownedGeneration = null;
        try
        {
            if (!_lanHost.IsListening ||
                string.IsNullOrEmpty(_lanHost.Pin) ||
                string.IsNullOrEmpty(_lanHost.FingerprintText) ||
                PrimaryHostAddress is "No LAN address" or "")
            {
                _lastNearbyOfferKey = "";
                _nearbySyncGeneration++;
                _nearbySyncInFlightKey = null;
                ClearOfferQr();
                _nearbyPublisher.Stop();
                _wifiBeacon.Stop();
                OnNearbyChanged();
                return;
            }

            string key = PrimaryHostAddress + "|" + _lanHost.Port + "|" + _lanHost.Pin + "|" + _lanHost.FingerprintText;
            // FPS/UI churn must not restart BLE advertise — that causes "advertisement stopped" flicker.
            if (key == _lastNearbyOfferKey && _nearbyPublisher.IsAdvertising && _wifiBeacon.IsAdvertising && _offerQrImage is not null) return;
            if (_nearbySyncInFlightKey == key) return;

            _nearbySyncInFlightKey = key;
            ownedGeneration = ++_nearbySyncGeneration;
            var offer = new NearbyOffer(
                Environment.MachineName,
                PrimaryHostAddress,
                _lanHost.Port,
                _lanHost.Pin,
                _lanHost.FingerprintText);
            if (_offerQrImage is null || key != _lastNearbyOfferKey)
                UpdateOfferQr(offer);
            _wifiBeacon.Start(offer);
            await _nearbyPublisher.StartAsync(offer);
            if (ownedGeneration != _nearbySyncGeneration) return;
            _lastNearbyOfferKey = key;
            OnNearbyChanged();
        }
        catch
        {
            OnNearbyChanged();
        }
        finally
        {
            if (ownedGeneration == _nearbySyncGeneration)
                _nearbySyncInFlightKey = null;
        }
    }

    private void ClearOfferQr()
    {
        _offerQrImage = null;
        _offerConnectUri = "";
    }

    private void UpdateOfferQr(NearbyOffer offer)
    {
        _offerConnectUri = offer.ToConnectUri();
        using var generator = new QRCodeGenerator();
        using QRCodeData data = generator.CreateQrCode(_offerConnectUri, QRCodeGenerator.ECCLevel.M);
        var qr = new PngByteQRCode(data);
        byte[] png = qr.GetGraphic(4);
        using var stream = new MemoryStream(png);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        _offerQrImage = image;
    }

    private void TrackHistoryTransitions()
    {
        if (_lanViewer.IsBusy && !_wasViewerBusy)
            _viewerStartedUtc = DateTimeOffset.UtcNow;
        if (!_lanViewer.IsBusy && _wasViewerBusy)
        {
            SetViewerFullscreen(false);
            int seconds = _viewerStartedUtc is { } start
                ? (int)Math.Clamp((DateTimeOffset.UtcNow - start).TotalSeconds, 0, 86_400)
                : 0;
            string result = string.IsNullOrWhiteSpace(_lanViewer.Status) ? "Ended" : _lanViewer.Status;
            _prefs.AddHistory("Viewer", _viewerPeerForHistory, result, seconds, _lanViewer.FramesReceived);
            SavePrefs();
            _viewerStartedUtc = null;
        }

        if (_lanHost.IsSharing && !_wasHostSharing)
            _hostShareStartedUtc = DateTimeOffset.UtcNow;
        if (!_lanHost.IsSharing && _wasHostSharing)
        {
            int seconds = _hostShareStartedUtc is { } start
                ? (int)Math.Clamp((DateTimeOffset.UtcNow - start).TotalSeconds, 0, 86_400)
                : 0;
            _prefs.AddHistory("Host", "viewer", _lanHost.Status, seconds, _lanHost.FramesSent);
            SavePrefs();
            _hostShareStartedUtc = null;
        }

        _wasViewerBusy = _lanViewer.IsBusy;
        _wasHostSharing = _lanHost.IsSharing;
    }

    private void NotifyHistory()
    {
        Notify(nameof(HistoryEntries));
        Notify(nameof(HistoryEmptyVisibility));
        Notify(nameof(HistoryListVisibility));
    }

    private void ApplyTheme(UiColorMode mode, bool save)
    {
        _colorMode = mode;
        ApplyGlassPalette(mode);

        if (save)
        {
            _prefs.ColorMode = _colorMode.ToString();
            _prefs.DarkTheme = _colorMode == UiColorMode.Dark;
            _prefs.ThemePack = nameof(UiThemePack.SciFi);
            SavePrefs();
        }

        Notify(nameof(ThemeStatusLabel));
        Notify(nameof(LightModeLabel));
        Notify(nameof(DarkModeLabel));
        Notify(nameof(SciFiModeLabel));
        Notify(nameof(AppearanceCycleLabel));
        Notify(nameof(OverviewHomeVisibility));
        Notify(nameof(SecondaryGlassVisibility));
    }

    /// <summary>
    /// One glass layout. Three colour modes: Light, Dark (neutral), Sci-Fi (cyan nebula).
    /// </summary>
    private static void ApplyGlassPalette(UiColorMode mode)
    {
        switch (mode)
        {
            case UiColorMode.Light:
                SetBrush("PageBrush", "#E8F1F8");
                SetBrush("SurfaceBrush", "#B8FFFFFF");
                SetBrush("TextBrush", "#152238");
                SetBrush("MutedBrush", "#5B6B82");
                SetBrush("LineBrush", "#B7C9DA");
                SetBrush("AccentBrush", "#2563EB");
                SetBrush("AccentTextBrush", "#FFFFFF");
                SetBrush("SidebarBrush", "#F4FAFD");
                SetBrush("NavTextBrush", "#3B4D66");
                SetBrush("NavTickBrush", "#2563EB");
                SetBrush("NavHoverBrush", "#142563EB");
                SetBrush("NavActiveBrush", "#1F2563EB");
                SetBrush("BannerBrush", "#FFF1D6");
                SetBrush("BannerTextBrush", "#6B5218");
                SetBrush("ShareCardBrush", "#CCFFFFFF");
                SetBrush("ShareCardBorderBrush", "#99A8C8D8");
                SetBrush("ShareTitleBrush", "#152238");
                SetBrush("ShareMutedBrush", "#5B6B82");
                SetBrush("ShareValueBrush", "#0F1A2C");
                SetBrush("ShareAccentBrush", "#0F9F6E");
                SetBrush("TheaterBrush", "#0B1220");
                SetBrush("SecondaryButtonBrush", "#D7E8F0");
                SetBrush("SecondaryButtonTextBrush", "#152238");
                SetBrush("GlassTintBrush", "#A6FFFFFF");
                SetBrush("GlassBorderBrush", "#99FFFFFF");
                SetBrush("GlassFieldBrush", "#66FFFFFF");
                SetBrush("GlassWellBrush", "#59FFFFFF");
                SetBrush("StageWashBrush", "#5538BDF8");
                SetBrush("StageVignetteBrush", "#140B2838");
                SetDouble("StageGlowOpacity", 0.28);
                SetDouble("StageGridOpacity", 0.22);
                SetDouble("StageHorizonOpacity", 0.18);
                break;

            case UiColorMode.Dark:
                // Neutral dark glass — slate, blue accent (not cyan sci-fi).
                SetBrush("PageBrush", "#101826");
                SetBrush("SurfaceBrush", "#4D182338");
                SetBrush("TextBrush", "#E6EEF8");
                SetBrush("MutedBrush", "#9AA9BF");
                SetBrush("LineBrush", "#3D2C3B55");
                SetBrush("AccentBrush", "#3B82F6");
                SetBrush("AccentTextBrush", "#FFFFFF");
                SetBrush("SidebarBrush", "#CC0B1422");
                SetBrush("NavTextBrush", "#B7C6DB");
                SetBrush("NavTickBrush", "#3B82F6");
                SetBrush("NavHoverBrush", "#223B82F6");
                SetBrush("NavActiveBrush", "#333B82F6");
                SetBrush("BannerBrush", "#663A3018");
                SetBrush("BannerTextBrush", "#F3D9A0");
                SetBrush("ShareCardBrush", "#4D121D30");
                SetBrush("ShareCardBorderBrush", "#592C3B55");
                SetBrush("ShareTitleBrush", "#E6EEF8");
                SetBrush("ShareMutedBrush", "#8FA0B8");
                SetBrush("ShareValueBrush", "#FFFFFF");
                SetBrush("ShareAccentBrush", "#34D399");
                SetBrush("TheaterBrush", "#0B1220");
                SetBrush("SecondaryButtonBrush", "#3324344C");
                SetBrush("SecondaryButtonTextBrush", "#E6EEF8");
                SetBrush("GlassTintBrush", "#28FFFFFF");
                SetBrush("GlassBorderBrush", "#5599AABB");
                SetBrush("GlassFieldBrush", "#3B0B1220");
                SetBrush("GlassWellBrush", "#330B1422");
                SetBrush("StageWashBrush", "#332563EB");
                SetBrush("StageVignetteBrush", "#99000000");
                SetDouble("StageGlowOpacity", 0.22);
                SetDouble("StageGridOpacity", 0.18);
                SetDouble("StageHorizonOpacity", 0.12);
                break;

            default:
                // Sci-Fi: bright ice / holographic tech — not another dark theme.
                SetBrush("PageBrush", "#D9F4FA");
                SetBrush("SurfaceBrush", "#B8FFFFFF");
                SetBrush("TextBrush", "#083344");
                SetBrush("MutedBrush", "#3F6B78");
                SetBrush("LineBrush", "#8ECAD8");
                SetBrush("AccentBrush", "#06B6D4");
                SetBrush("AccentTextBrush", "#FFFFFF");
                SetBrush("SidebarBrush", "#E8FBFF");
                SetBrush("NavTextBrush", "#0E4A5A");
                SetBrush("NavTickBrush", "#06B6D4");
                SetBrush("NavHoverBrush", "#2206B6D4");
                SetBrush("NavActiveBrush", "#3306B6D4");
                SetBrush("BannerBrush", "#CFFAFE");
                SetBrush("BannerTextBrush", "#155E75");
                SetBrush("ShareCardBrush", "#CCFFFFFF");
                SetBrush("ShareCardBorderBrush", "#88A5F3FC");
                SetBrush("ShareTitleBrush", "#083344");
                SetBrush("ShareMutedBrush", "#3F6B78");
                SetBrush("ShareValueBrush", "#0B2830");
                SetBrush("ShareAccentBrush", "#D97706");
                SetBrush("TheaterBrush", "#0B1220");
                SetBrush("SecondaryButtonBrush", "#C5EBF3");
                SetBrush("SecondaryButtonTextBrush", "#0B2830");
                SetBrush("GlassTintBrush", "#99FFFFFF");
                SetBrush("GlassBorderBrush", "#AA67E8F9");
                SetBrush("GlassFieldBrush", "#A6FFFFFF");
                SetBrush("GlassWellBrush", "#80FFFFFF");
                SetBrush("StageWashBrush", "#6622D3EE");
                SetBrush("StageVignetteBrush", "#18089AAB");
                SetDouble("StageGlowOpacity", 0.45);
                SetDouble("StageGridOpacity", 0.35);
                SetDouble("StageHorizonOpacity", 0.28);
                break;
        }
    }

    private void CopyShareText(string value, string okMessage)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "No LAN address" or "—")
        {
            ClipboardStatus = "Nothing to copy yet";
            Notify(nameof(ClipboardStatus));
            Notify(nameof(ClipboardStatusVisibility));
            return;
        }

        try
        {
            Clipboard.SetText(value.Trim());
            ClipboardStatus = okMessage;
        }
        catch (Exception ex)
        {
            ClipboardStatus = "Copy failed: " + ex.Message;
        }

        Notify(nameof(ClipboardStatus));
        Notify(nameof(ClipboardStatusVisibility));
    }

    private bool IsLikelySamePcLabViewer()
    {
        string host = RemoteAddress.Trim();
        if (host.Length == 0) return false;
        if (host is "127.0.0.1" or "::1" or "localhost") return true;
        if (string.Equals(host, PrimaryHostAddress, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (string part in _lanHost.LocalAddressesText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(host, part, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private bool IsLikelySamePcLabHost()
    {
        // Viewer hello uses Environment.MachineName; same PC ⇒ "with THISPC" in host status.
        string marker = " with " + Environment.MachineName;
        return _lanHost.Status.Contains(marker, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshHostPrinters()
    {
        AvailablePrinters.Clear();
        var printers = LocalPrinterService.GetInstalledPrinters();
        foreach (var p in printers)
        {
            p.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(InstalledPrinterItem.IsSelected))
                {
                    SyncSharedPrintersWithQueueManager();
                }
            };
            AvailablePrinters.Add(p);
        }
        SyncSharedPrintersWithQueueManager();
    }

    private void SyncSharedPrintersWithQueueManager()
    {
        var selected = AvailablePrinters.Where(p => p.IsSelected).Select(p => p.Name).ToList();
        _queueManager.SetSharedPrinters(selected);
    }

    private void UpdateClientSharedPrinters(QueueStatusMessage? status)
    {
        if (status is null) return;
        var list = status.SharedPrinters ?? new List<string>();

        if (!ClientAvailablePrinters.SequenceEqual(list))
        {
            ClientAvailablePrinters.Clear();
            foreach (var p in list)
            {
                ClientAvailablePrinters.Add(p);
            }
        }

        // Client Auto-Select:
        // 1. If only 1 printer shared -> auto-select it immediately.
        // 2. If multiple shared -> keep current if valid, else pick first.
        // 3. If none -> clear.
        if (ClientAvailablePrinters.Count == 1)
        {
            SelectedClientPrinter = ClientAvailablePrinters[0];
        }
        else if (ClientAvailablePrinters.Count > 1)
        {
            if (string.IsNullOrWhiteSpace(SelectedClientPrinter) || !ClientAvailablePrinters.Contains(SelectedClientPrinter))
            {
                SelectedClientPrinter = ClientAvailablePrinters[0];
            }
        }
        else
        {
            SelectedClientPrinter = "";
        }

        if (!string.IsNullOrWhiteSpace(SelectedClientPrinter))
        {
            var cleanName = SelectedClientPrinter.Replace(" Printer", "").Trim();
            VirtualPrinterSpoolBridge.ActivePrinterName = $"{cleanName} (TKRDesk Wireless)";
        }
        else
        {
            VirtualPrinterSpoolBridge.ActivePrinterName = VirtualPrinterSpoolBridge.DefaultPrinterName;
        }

        Notify(nameof(ClientPrinterSelectorVisibility));
        Notify(nameof(ClientPrinterSummaryText));
        Notify(nameof(VirtualPrinterStatusText));
        Notify(nameof(IsVirtualPrinterInstalled));
    }

    private void StartPollingQueueStatus(string targetIp)
    {
        _ = Task.Run(async () =>
        {
            for (int poll = 0; poll < 60; poll++)
            {
                await Task.Delay(1000);
                if (string.IsNullOrWhiteSpace(targetIp)) break;
                var s = await _queueClient.GetStatusAsync(targetIp);
                if (s is null) break;

                Application.Current?.Dispatcher?.BeginInvoke(() =>
                {
                    _queueClientStatus = s;
                    UpdateClientSharedPrinters(s);
                    if (s.YourPosition == 0)
                    {
                        QueueClientMessage = !string.IsNullOrWhiteSpace(s.ActiveHardwareStatus)
                            ? $"⚡ {s.ActiveHardwareStatus}"
                            : "⚡ Job is being processed on the hardware…";
                    }
                    else if (s.YourPosition > 0)
                    {
                        QueueClientMessage = $"In line: Position #{s.YourPosition}";
                    }
                    else
                    {
                        QueueClientMessage = "✅ Job completed successfully on hardware!";
                    }
                    Notify(nameof(QueueClientPositionText));
                    Notify(nameof(QueueClientWaitText));
                    Notify(nameof(CanCancelClientJob));
                    Notify(nameof(QueueClientJobs));
                    Notify(nameof(QueueClientJobsVisibility));
                });

                if (s.YourPosition < 0) break;
            }
        });
    }

    private void ApplyPreferredStreamCodecFromPrefs()
    {
        LabStreamCodec preferred = LabStreamCodec.Jpeg;
        if (Enum.TryParse(_prefs.StreamCodec, true, out LabStreamCodec saved))
            preferred = saved;

        if (preferred == LabStreamCodec.H264 && NetworkH264Bootstrap.TryEnsureLoaded(out _))
            _lanHost.StreamCodec = LabStreamCodec.H264;
        else if (preferred == LabStreamCodec.H264)
            _lanHost.StreamCodec = LabStreamCodec.Jpeg;
        else
            _lanHost.StreamCodec = LabStreamCodec.Jpeg;
    }

    private void SavePrefs()
    {
        try { _prefs.Save(); }
        catch { /* local prefs must never break the lab UI */ }
    }

    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    private static void SetBrush(string key, string color)
        => Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    private static void SetDouble(string key, double value)
        => Application.Current.Resources[key] = value;
    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class RelayCommand(Action<object?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute(parameter);
}
