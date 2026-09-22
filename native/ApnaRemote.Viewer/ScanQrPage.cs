namespace ApnaRemote.Viewer;

public sealed class ScanQrPage : ContentPage
{
    private readonly TaskCompletionSource<string?> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ZXing.Net.Maui.Controls.CameraBarcodeReaderView _camera;
    private bool _done;

    public Task<string?> Result => _tcs.Task;

    public ScanQrPage()
    {
        Title = "Scan host QR";
        BackgroundColor = Color.FromArgb("#070E18");

        _camera = new ZXing.Net.Maui.Controls.CameraBarcodeReaderView
        {
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
            IsDetecting = true,
        };
        _camera.Options = new ZXing.Net.Maui.BarcodeReaderOptions
        {
            Formats = ZXing.Net.Maui.BarcodeFormat.QrCode,
            AutoRotate = true,
            Multiple = false,
            TryHarder = true,
        };
        _camera.BarcodesDetected += OnBarcodesDetected;

        var cancel = new Button
        {
            Text = "Cancel",
            BackgroundColor = Color.FromArgb("#1E3A5F"),
            TextColor = Colors.White,
            CornerRadius = 12,
            HeightRequest = 48,
            Margin = new Thickness(16),
        };
        cancel.Clicked += async (_, _) => await FinishAsync(null);

        var hint = new Label
        {
            Text = "Point at the Phone QR on the PC",
            TextColor = Color.FromArgb("#CAD7EB"),
            FontSize = 14,
            HorizontalTextAlignment = TextAlignment.Center,
            Margin = new Thickness(16, 12),
        };

        var grid = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto),
            },
        };
        grid.Add(hint, 0, 0);
        grid.Add(_camera, 0, 1);
        grid.Add(cancel, 0, 2);
        Content = grid;
    }

    private void OnBarcodesDetected(object? sender, ZXing.Net.Maui.BarcodeDetectionEventArgs e)
    {
        if (_done) return;
        string? value = e.Results?.FirstOrDefault()?.Value;
        if (string.IsNullOrWhiteSpace(value)) return;
        MainThread.BeginInvokeOnMainThread(async () => await FinishAsync(value));
    }

    private async Task FinishAsync(string? value)
    {
        if (_done) return;
        _done = true;
        _camera.IsDetecting = false;
        _camera.BarcodesDetected -= OnBarcodesDetected;
        _tcs.TrySetResult(value);
        if (Navigation.NavigationStack.Contains(this))
            await Navigation.PopAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _camera.IsDetecting = false;
        if (!_done)
        {
            _done = true;
            _tcs.TrySetResult(null);
        }
    }
}
