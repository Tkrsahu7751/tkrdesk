#if ANDROID
using Android.Graphics;
using Android.Views;
using Android.Widget;
using Microsoft.Maui.Handlers;
using View = Android.Views.View;

namespace ApnaRemote.Viewer;

public sealed class RemoteFrameViewHandler : ViewHandler<RemoteFrameView, ImageView>
{
    private Bitmap? _current;
    private float _downX, _downY;
    private long _downTick;
    private bool _moved;
    private bool _longFired;
    private float _lastScrollY;
    private int _activePointers;
    private CancellationTokenSource? _longCts;

    public static IPropertyMapper<RemoteFrameView, RemoteFrameViewHandler> Mapper =
        new PropertyMapper<RemoteFrameView, RemoteFrameViewHandler>(ViewMapper)
        {
            [nameof(RemoteFrameView.FrameJpeg)] = MapFrameJpeg,
        };

    public RemoteFrameViewHandler() : base(Mapper) { }

    protected override ImageView CreatePlatformView()
    {
        var view = new ImageView(Context);
        view.SetScaleType(ImageView.ScaleType.FitCenter);
        view.SetAdjustViewBounds(true);
        view.Clickable = true;
        view.LongClickable = true;
        view.Touch += OnTouch;
        return view;
    }

    protected override void DisconnectHandler(ImageView platformView)
    {
        platformView.Touch -= OnTouch;
        CancelLongPress();
        platformView.SetImageDrawable(null);
        _current?.Recycle();
        _current = null;
        base.DisconnectHandler(platformView);
    }

    private void OnTouch(object? sender, View.TouchEventArgs e)
    {
        if (VirtualView is null || e.Event is null)
        {
            e.Handled = false;
            return;
        }

        if (!VirtualView.InputEnabled)
        {
            e.Handled = false;
            return;
        }

        MotionEvent ev = e.Event;
        float vw = PlatformView.Width;
        float vh = PlatformView.Height;
        if (vw <= 0 || vh <= 0)
        {
            e.Handled = true;
            return;
        }

        switch (ev.ActionMasked)
        {
            case MotionEventActions.Down:
                _activePointers = 1;
                _downX = ev.GetX();
                _downY = ev.GetY();
                _downTick = Environment.TickCount64;
                _moved = false;
                _longFired = false;
                _lastScrollY = _downY;
                Raise(RemotePointerKind.Move, _downX, _downY, vw, vh);
                ArmLongPress(vw, vh);
                e.Handled = true;
                break;

            case MotionEventActions.PointerDown:
                _activePointers = ev.PointerCount;
                CancelLongPress();
                _lastScrollY = ev.GetY();
                e.Handled = true;
                break;

            case MotionEventActions.Move:
                if (ev.PointerCount >= 2)
                {
                    CancelLongPress();
                    float y = ev.GetY();
                    float dy = y - _lastScrollY;
                    if (Math.Abs(dy) >= 8)
                    {
                        int delta = (int)Math.Clamp(-dy * 3, -1200, 1200);
                        Raise(RemotePointerKind.Scroll, ev.GetX(), y, vw, vh, delta);
                        _lastScrollY = y;
                    }
                }
                else
                {
                    float x = ev.GetX(), y = ev.GetY();
                    if (Math.Abs(x - _downX) > 12 || Math.Abs(y - _downY) > 12)
                    {
                        _moved = true;
                        CancelLongPress();
                    }
                    Raise(RemotePointerKind.Move, x, y, vw, vh);
                }
                e.Handled = true;
                break;

            case MotionEventActions.Up:
            case MotionEventActions.Cancel:
                CancelLongPress();
                if (!_longFired && !_moved && ev.ActionMasked == MotionEventActions.Up && _activePointers <= 1)
                {
                    Raise(RemotePointerKind.LeftDown, _downX, _downY, vw, vh);
                    Raise(RemotePointerKind.LeftUp, _downX, _downY, vw, vh);
                }
                else if (_moved && ev.ActionMasked == MotionEventActions.Up)
                {
                    // drag end: ensure no stuck button (tap path already up)
                }
                _activePointers = 0;
                e.Handled = true;
                break;

            case MotionEventActions.PointerUp:
                _activePointers = Math.Max(0, ev.PointerCount - 1);
                e.Handled = true;
                break;

            default:
                e.Handled = true;
                break;
        }
    }

    private void ArmLongPress(float vw, float vh)
    {
        CancelLongPress();
        _longCts = new CancellationTokenSource();
        var cts = _longCts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(480, cts.Token).ConfigureAwait(false);
                if (cts.IsCancellationRequested || _moved || _longFired) return;
                _longFired = true;
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (VirtualView is null || !VirtualView.InputEnabled) return;
                    Raise(RemotePointerKind.RightClick, _downX, _downY, vw, vh);
                });
            }
            catch (OperationCanceledException) { }
        });
    }

    private void CancelLongPress()
    {
        try { _longCts?.Cancel(); } catch { /* ignore */ }
        _longCts?.Dispose();
        _longCts = null;
    }

    private void Raise(RemotePointerKind kind, float x, float y, float vw, float vh, int scroll = 0)
        => VirtualView?.RaisePointer(new RemotePointerEventArgs
        {
            Kind = kind,
            X = x,
            Y = y,
            ViewportWidth = vw,
            ViewportHeight = vh,
            ScrollDelta = scroll,
        });

    private static void MapFrameJpeg(RemoteFrameViewHandler handler, RemoteFrameView view)
    {
        byte[]? jpeg = view.FrameJpeg;
        if (jpeg is null || jpeg.Length == 0)
        {
            handler.PlatformView.SetImageDrawable(null);
            handler._current?.Recycle();
            handler._current = null;
            return;
        }

        Bitmap? decoded = BitmapFactory.DecodeByteArray(jpeg, 0, jpeg.Length);
        if (decoded is null) return;
        Bitmap? previous = handler._current;
        handler._current = decoded;
        handler.PlatformView.SetImageBitmap(decoded);
        previous?.Recycle();
    }
}
#endif
