using System.Windows;
using ApnaRemote.Windows.Input;

namespace ApnaRemote.Windows;

internal sealed record HostConsent(CaptureMonitor? Monitor, bool Control, bool EndsListening = false)
{
    public static HostConsent StopReceiving { get; } = new(null, false, EndsListening: true);
}

public partial class HostConsentWindow : Window
{
    internal HostConsent? Consent { get; private set; }
    internal bool StopReceivingRequested { get; private set; }

    internal HostConsentWindow(string peerName, string address)
    {
        InitializeComponent();
        PeerText.Text = $"Claimed PC: {peerName}\nSource address: {address}";
        MonitorList.ItemsSource = CaptureDisplayBounds.Enumerate();
        // Deliberate selection is required; do not select a different screen automatically.
        MonitorList.SelectedIndex = -1;
    }

    private void ModeChanged(object sender, RoutedEventArgs e)
    {
        if (AllowControl is null || MonitorList is null) return;
        bool window = WindowOnly.IsChecked == true;
        AllowControl.IsEnabled = !window;
        if (window) AllowControl.IsChecked = false;
        MonitorList.IsEnabled = !window;
    }

    private void ShareClicked(object sender, RoutedEventArgs e)
    {
        bool picker = WindowOnly.IsChecked == true;
        if (!picker && MonitorList.SelectedItem is not CaptureMonitor)
        {
            MessageBox.Show(this, "Choose the exact monitor to share, or use the view-only picker.", "Select screen");
            return;
        }

        Consent = new(picker ? null : (CaptureMonitor)MonitorList.SelectedItem, !picker && AllowControl.IsChecked == true);
        DialogResult = true;
    }

    private void StopReceivingClicked(object sender, RoutedEventArgs e)
    {
        StopReceivingRequested = true;
        Consent = HostConsent.StopReceiving;
        DialogResult = false;
        Close();
    }
}
