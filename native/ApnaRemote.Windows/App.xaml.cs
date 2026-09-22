using System.Windows;
using ApnaRemote.Windows.Capture;
using ApnaRemote.Windows.Lan;

namespace ApnaRemote.Windows;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Any(static a => string.Equals(a, "--discovery-lifecycle-selftest", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = Dispatcher.InvokeAsync(async () => Shutdown(await DiscoveryLifecycleSelfTest.RunAsync(Dispatcher)));
            return;
        }

        if (e.Args.Any(static a => string.Equals(a, "--network-h264-selftest", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(NetworkH264SelfTest.Run());
            return;
        }

        if (e.Args.Any(static a => string.Equals(a, "--viewer-lifecycle-selftest", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = Dispatcher.InvokeAsync(async () => Shutdown(await ViewerLifecycleSelfTest.RunAsync(Dispatcher)));
            return;
        }

        if (e.Args.Any(static a => string.Equals(a, "--host-lifecycle-selftest", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(HostLifecycleSelfTest.Run());
            return;
        }

        if (e.Args.Any(static a => string.Equals(a, "--lan-lab-selftest", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code = LanLabSelfTest.Run();
            Shutdown(code);
            return;
        }

        if (e.Args.Any(static a => string.Equals(a, "--encode-probe", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code = EncodeProbe.Run();
            Shutdown(code);
            return;
        }

        if (e.Args.Any(static a => string.Equals(a, "--encode-lab-check", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code = LocalH264EncodeLabCheck.Run();
            Shutdown(code);
            return;
        }

        base.OnStartup(e);
        _ = NetworkH264Bootstrap.TryEnsureLoaded(out _);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
