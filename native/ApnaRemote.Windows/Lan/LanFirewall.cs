namespace ApnaRemote.Windows.Lan;

/// <summary>
/// Informational only. Starting the app/listener must never mutate Windows Firewall.
/// The user may separately choose the documented host firewall helper.
/// </summary>
internal static class LanFirewall
{
    public static string ManualSetupHint(int port)
        => $"Firewall not checked or changed. If another PC cannot connect, review the host setup guide for TCP {port}.";
}
