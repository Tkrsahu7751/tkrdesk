namespace ApnaRemote.Core;

public static class PointerMapping
{
    public static void Validate(DisplayBounds display)
    {
        if (display.Width is < 1 or > 32768 || display.Height is < 1 or > 32768 ||
            (long)display.Left + display.Width - 1 is < int.MinValue or > int.MaxValue ||
            (long)display.Top + display.Height - 1 is < int.MinValue or > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(display));
    }

    public static PixelPoint ToPhysical(double x, double y, DisplayBounds display)
    {
        Validate(display);
        if (!double.IsFinite(x) || !double.IsFinite(y) || x is < 0 or > 1 || y is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(x), "Coordinates must be finite and normalized.");
        return new(display.Left + (int)Math.Round(x * (display.Width - 1)),
            display.Top + (int)Math.Round(y * (display.Height - 1)));
    }

    /// <summary>Ignores pointer positions in letterboxing instead of clicking a display edge.</summary>
    public static bool TryFromViewport(double x, double y, double viewportWidth,
        double viewportHeight, int frameWidth, int frameHeight, out (double X, double Y) normalized)
    {
        normalized = default;
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(viewportWidth) ||
            !double.IsFinite(viewportHeight) || viewportWidth <= 0 || viewportHeight <= 0 ||
            frameWidth is < 1 or > 32768 || frameHeight is < 1 or > 32768) return false;
        double scale = Math.Min(viewportWidth / frameWidth, viewportHeight / frameHeight);
        double width = frameWidth * scale, height = frameHeight * scale;
        double left = (viewportWidth - width) / 2, top = (viewportHeight - height) / 2;
        if (x < left || y < top || x > left + width || y > top + height) return false;
        normalized = ((x - left) / width, (y - top) / height);
        return true;
    }
}
