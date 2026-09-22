using System.Windows;
using System.Windows.Input;
using ApnaRemote.Core;

namespace ApnaRemote.Windows;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private bool _releasingCapture;
    private WindowState _restoreState = WindowState.Normal;
    private WindowStyle _restoreStyle = WindowStyle.SingleBorderWindow;
    private ResizeMode _restoreResize = ResizeMode.CanResize;
    private double _restoreLeft;
    private double _restoreTop;
    private double _restoreWidth;
    private double _restoreHeight;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Deactivated += (_, _) => ReleaseInput();
        Closed += (_, _) => _viewModel.Dispose();
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ControlEnabled) && !_viewModel.ControlEnabled)
                ReleaseInput();
        };
        _viewModel.ViewerFullscreenChanged += ApplyViewerFullscreen;
    }

    private void ApplyViewerFullscreen()
    {
        if (_viewModel.IsViewerFullscreen)
        {
            _restoreState = WindowState;
            _restoreStyle = WindowStyle;
            _restoreResize = ResizeMode;
            _restoreLeft = Left;
            _restoreTop = Top;
            _restoreWidth = Width;
            _restoreHeight = Height;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            MinWidth = 320;
            MinHeight = 240;
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowStyle = _restoreStyle;
            ResizeMode = _restoreResize;
            MinWidth = 640;
            MinHeight = 420;
            if (!double.IsNaN(_restoreLeft)) Left = _restoreLeft;
            if (!double.IsNaN(_restoreTop)) Top = _restoreTop;
            if (!double.IsNaN(_restoreWidth) && _restoreWidth > 0) Width = _restoreWidth;
            if (!double.IsNaN(_restoreHeight) && _restoreHeight > 0) Height = _restoreHeight;
            WindowState = _restoreState;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11 && _viewModel.CanToggleViewerFullscreen)
        {
            _viewModel.SetViewerFullscreen(!_viewModel.IsViewerFullscreen);
            e.Handled = true;
        }
    }

    private void ReleaseInput()
    {
        _viewModel.SetRemoteFocus(false);
        ReleaseCapture();
        if (RemoteSurface.IsKeyboardFocusWithin) Keyboard.ClearFocus();
    }
    private void ReleaseCapture()
    {
        if (!RemoteSurface.IsMouseCaptured) return;
        _releasingCapture = true;
        try { RemoteSurface.ReleaseMouseCapture(); }
        finally { _releasingCapture = false; }
    }
    private void RemoteSurface_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => _viewModel.SetRemoteFocus(true);
    private void RemoteSurface_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => ReleaseInput();
    private void RemoteSurface_LostMouseCapture(object sender, MouseEventArgs e) { if (!_releasingCapture) ReleaseInput(); }

    private bool PointAt(FrameworkElement surface, MouseEventArgs e, bool force)
    {
        Point p = e.GetPosition(surface);
        return _viewModel.TryRemotePointer(p.X, p.Y, surface.ActualWidth, surface.ActualHeight, force);
    }
    private void RemoteSurface_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement surface && _viewModel.ControlEnabled && surface.IsKeyboardFocusWithin)
            _ = PointAt(surface, e, false);
    }
    private void RemoteSurface_MouseLeave(object sender, MouseEventArgs e)
    {
        // End a drag at the view boundary instead of leaving a button held outside the app.
        ReleaseInput();
    }
    private void RemoteSurface_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement surface || !_viewModel.ControlEnabled || !PointAt(surface, e, true)) return;
        if (MapButton(e.ChangedButton) is not { } button) return;
        surface.Focus();
        if (!surface.CaptureMouse()) return;
        if (!_viewModel.TryRemoteButton(button, true)) ReleaseCapture();
        e.Handled = true;
    }
    private void RemoteSurface_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.ControlEnabled) return;
        if (MapButton(e.ChangedButton) is { } button) _viewModel.TryRemoteButton(button, false);
        if (e.LeftButton == MouseButtonState.Released && e.RightButton == MouseButtonState.Released && e.MiddleButton == MouseButtonState.Released)
            ReleaseCapture();
        e.Handled = true;
    }
    private void RemoteSurface_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is FrameworkElement surface && _viewModel.ControlEnabled && surface.IsKeyboardFocusWithin && PointAt(surface, e, true))
        { _viewModel.TryRemoteScroll(e.Delta); e.Handled = true; }
    }
    private void RemoteSurface_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_viewModel.IsViewerFullscreen)
            {
                _viewModel.SetViewerFullscreen(false);
                e.Handled = true;
                return;
            }
            ReleaseInput(); Keyboard.ClearFocus(); e.Handled = true; return;
        }
        if (e.Key == Key.F11 && _viewModel.CanToggleViewerFullscreen)
        {
            _viewModel.SetViewerFullscreen(!_viewModel.IsViewerFullscreen);
            e.Handled = true;
            return;
        }
        if (!_viewModel.ControlEnabled || !RemoteSurface.IsKeyboardFocusWithin) return;
        int vk = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);
        if (vk != 0 && _viewModel.TryRemoteKey(vk, true)) e.Handled = true;
    }
    private void RemoteSurface_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_viewModel.ControlEnabled || !RemoteSurface.IsKeyboardFocusWithin) return;
        int vk = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);
        if (vk != 0 && _viewModel.TryRemoteKey(vk, false)) e.Handled = true;
    }
    private void RemoteSurface_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // Physical virtual-key events use the HOST layout. Do not inject a second Unicode copy.
        // IME composition needs its own future explicit text mode.
        if (_viewModel.ControlEnabled) e.Handled = true;
    }
    private static PointerButton? MapButton(MouseButton button) => button switch
    { MouseButton.Left => PointerButton.Left, MouseButton.Right => PointerButton.Right, MouseButton.Middle => PointerButton.Middle, _ => null };
}
