using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace ApnaRemote.Windows.Controls;

/// <summary>
/// Frosted glass panel for the Sci-Fi theme pack.
/// WPF has no backdrop blur, so the panel samples the cinematic stage with a live
/// VisualBrush, blurs that sample, then tints it. The sample follows the panel so the
/// stage lights line up with what sits behind the glass.
/// </summary>
public sealed class GlassPanel : ContentControl
{
    /// <summary>Extra sampled ring; a Gaussian blur fades toward untouched edges.</summary>
    private const double Bleed = 40;

    public static readonly DependencyProperty BackdropProperty = DependencyProperty.Register(
        nameof(Backdrop), typeof(FrameworkElement), typeof(GlassPanel),
        new PropertyMetadata(null, OnBackdropChanged));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(GlassPanel),
        new PropertyMetadata(new CornerRadius(18)));

    public static readonly DependencyProperty BlurRadiusProperty = DependencyProperty.Register(
        nameof(BlurRadius), typeof(double), typeof(GlassPanel),
        new PropertyMetadata(30.0, OnBlurRadiusChanged));

    private Rectangle? _frost;
    private FrameworkElement? _clipHost;
    private VisualBrush? _frostBrush;
    private BlurEffect? _blur;
    private Rect _sampled = Rect.Empty;
    private Size _clipped = Size.Empty;

    public GlassPanel()
    {
        LayoutUpdated += OnLayoutUpdated;
    }

    public FrameworkElement? Backdrop
    {
        get => (FrameworkElement?)GetValue(BackdropProperty);
        set => SetValue(BackdropProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public double BlurRadius
    {
        get => (double)GetValue(BlurRadiusProperty);
        set => SetValue(BlurRadiusProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _frost = GetTemplateChild("PART_Frost") as Rectangle;
        _clipHost = GetTemplateChild("PART_ClipHost") as FrameworkElement;
        _blur = new BlurEffect
        {
            Radius = BlurRadius,
            KernelType = KernelType.Gaussian,
            RenderingBias = RenderingBias.Performance,
        };
        if (_frost is not null) _frost.Effect = _blur;
        _sampled = Rect.Empty;
        _clipped = Size.Empty;
        AttachBackdropBrush();
        Refresh();
    }

    private static void OnBackdropChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (GlassPanel)d;
        panel._sampled = Rect.Empty;
        panel.AttachBackdropBrush();
        panel.Refresh();
    }

    private static void OnBlurRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (GlassPanel)d;
        if (panel._blur is not null) panel._blur.Radius = (double)e.NewValue;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => Refresh();

    private void AttachBackdropBrush()
    {
        if (_frost is null) return;

        if (Backdrop is null)
        {
            _frostBrush = null;
            _frost.Fill = Brushes.Transparent;
            return;
        }

        _frostBrush = new VisualBrush(Backdrop)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill,
        };
        _frost.Fill = _frostBrush;
    }

    private void Refresh()
    {
        UpdateClip();
        UpdateSample();
    }

    private void UpdateClip()
    {
        if (_clipHost is null) return;
        double width = _clipHost.ActualWidth;
        double height = _clipHost.ActualHeight;
        if (width <= 0 || height <= 0) return;

        var size = new Size(width, height);
        if (size == _clipped) return;
        _clipped = size;

        double radius = CornerRadius.TopLeft;
        _clipHost.Clip = new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);
    }

    private void UpdateSample()
    {
        if (_frostBrush is null || Backdrop is null) return;
        if (!IsVisible || !Backdrop.IsVisible) return;
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        Rect sample;
        try
        {
            Point origin = TransformToVisual(Backdrop).Transform(new Point(0, 0));
            sample = new Rect(
                origin.X - Bleed,
                origin.Y - Bleed,
                ActualWidth + (Bleed * 2),
                ActualHeight + (Bleed * 2));
        }
        catch (InvalidOperationException)
        {
            // Panel and stage are not connected yet; the next layout pass retries.
            return;
        }

        if (sample == _sampled) return;
        _sampled = sample;
        _frostBrush.Viewbox = sample;
    }
}
