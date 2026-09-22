namespace ApnaRemote.Viewer;

public partial class MainPage : ContentPage
{
    private ViewerPageModel? _model;

    public MainPage()
    {
        InitializeComponent();
        BindingContextChanged += OnBindingContextChanged;
    }

    private void OnBindingContextChanged(object? sender, EventArgs e)
    {
        if (_model is not null)
            FrameSurface.Pointer -= OnFramePointer;
        _model = BindingContext as ViewerPageModel;
        if (_model is not null)
            FrameSurface.Pointer += OnFramePointer;
    }

    private void OnFramePointer(object? sender, RemotePointerEventArgs e)
        => _model?.HandlePointer(e);

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        if (Handler is null && BindingContext is ViewerPageModel model)
        {
            FrameSurface.Pointer -= OnFramePointer;
            model.Dispose();
        }
    }
}
