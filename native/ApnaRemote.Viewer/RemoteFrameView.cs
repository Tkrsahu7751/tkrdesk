using ApnaRemote.Core;

namespace ApnaRemote.Viewer;

public enum RemotePointerKind
{
    Move,
    LeftDown,
    LeftUp,
    RightClick,
    Scroll,
}

public sealed class RemotePointerEventArgs : EventArgs
{
    public required RemotePointerKind Kind { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double ViewportWidth { get; init; }
    public double ViewportHeight { get; init; }
    public int ScrollDelta { get; init; }
}

/// <summary>
/// JPEG frame surface. Touch is mapped by the page using letterbox-aware PointerMapping.
/// </summary>
public sealed class RemoteFrameView : View
{
    public static readonly BindableProperty FrameJpegProperty = BindableProperty.Create(
        nameof(FrameJpeg), typeof(byte[]), typeof(RemoteFrameView), null);

    public static readonly BindableProperty InputEnabledProperty = BindableProperty.Create(
        nameof(InputEnabled), typeof(bool), typeof(RemoteFrameView), false);

    public static readonly BindableProperty FrameWidthProperty = BindableProperty.Create(
        nameof(FrameWidth), typeof(int), typeof(RemoteFrameView), 0);

    public static readonly BindableProperty FrameHeightProperty = BindableProperty.Create(
        nameof(FrameHeight), typeof(int), typeof(RemoteFrameView), 0);

    public byte[]? FrameJpeg
    {
        get => (byte[]?)GetValue(FrameJpegProperty);
        set => SetValue(FrameJpegProperty, value);
    }

    public bool InputEnabled
    {
        get => (bool)GetValue(InputEnabledProperty);
        set => SetValue(InputEnabledProperty, value);
    }

    public int FrameWidth
    {
        get => (int)GetValue(FrameWidthProperty);
        set => SetValue(FrameWidthProperty, value);
    }

    public int FrameHeight
    {
        get => (int)GetValue(FrameHeightProperty);
        set => SetValue(FrameHeightProperty, value);
    }

    public event EventHandler<RemotePointerEventArgs>? Pointer;

    internal void RaisePointer(RemotePointerEventArgs args) => Pointer?.Invoke(this, args);
}
