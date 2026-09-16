namespace SoulScreen.App.Logic;

/// <summary>Where a watermark sits on the picture.</summary>
public enum WatermarkCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Center,
}

/// <summary>
/// Works out the pixel rectangle a watermark image is drawn into, given the canvas size and
/// the watermark's own settings. Pure arithmetic - no WPF, no file I/O - so it can be tested
/// without a bitmap, a window or a font.
/// <para>
/// The watermark's width is a fraction of the canvas' shortest side, not a fixed pixel size:
/// a screenshot and a 4K recording of the same session should carry the same-looking mark,
/// proportionally, rather than a logo sized for one looking tiny or huge on the other.
/// </para>
/// </summary>
public static class WatermarkPlacement
{
    /// <summary>Smallest fraction of the canvas' shortest side the watermark may be scaled
    /// to. Below this a mark becomes illegible rather than merely subtle.</summary>
    public const double MinScale = 0.02;

    /// <summary>Largest fraction allowed, so a mistyped setting cannot cover the whole
    /// picture.</summary>
    public const double MaxScale = 0.60;

    public const double DefaultScale = 0.14;
    public const double DefaultOpacity = 0.65;
    public const double DefaultMarginFraction = 0.03;

    /// <summary>
    /// The rectangle, in canvas pixels, the watermark image should be drawn into so it keeps
    /// its own aspect ratio, sized to a fraction of the canvas' shortest side, and sits in
    /// the given corner with a margin measured the same way.
    /// </summary>
    /// <param name="canvasWidth">Width of the picture the mark is drawn onto, in pixels.</param>
    /// <param name="canvasHeight">Height of the picture the mark is drawn onto, in pixels.</param>
    /// <param name="markWidth">Width of the watermark source image, in pixels.</param>
    /// <param name="markHeight">Height of the watermark source image, in pixels.</param>
    /// <param name="scale">Fraction of the canvas' shortest side the mark's longest side
    /// should span. Clamped to MinScale..MaxScale.</param>
    /// <param name="corner">Which corner (or the centre) the mark anchors to.</param>
    /// <param name="marginFraction">Gap from the edges, as a fraction of the canvas' shortest
    /// side. Ignored for WatermarkCorner.Center.</param>
    /// <returns>A rectangle that always lies fully inside the canvas, even for a mark or a
    /// margin large enough that a naive placement would run off the edge.</returns>
    public static (double X, double Y, double Width, double Height) Place(
        double canvasWidth, double canvasHeight,
        double markWidth, double markHeight,
        double scale, WatermarkCorner corner, double marginFraction)
    {
        if (canvasWidth <= 0 || canvasHeight <= 0 || markWidth <= 0 || markHeight <= 0)
            return (0, 0, 0, 0);

        scale = Math.Clamp(scale, MinScale, MaxScale);
        marginFraction = Math.Clamp(marginFraction, 0, 0.5);

        var shortestSide = Math.Min(canvasWidth, canvasHeight);
        var targetLongestSide = shortestSide * scale;
        var markLongestSide = Math.Max(markWidth, markHeight);
        var markScale = targetLongestSide / markLongestSide;

        var width = markWidth * markScale;
        var height = markHeight * markScale;
        var margin = shortestSide * marginFraction;

        // Clamp first, so a mark or margin big enough to overflow the canvas is pulled back
        // to fully on-screen rather than drawn partly off it.
        width = Math.Min(width, canvasWidth);
        height = Math.Min(height, canvasHeight);
        margin = Math.Min(margin, Math.Min(canvasWidth - width, canvasHeight - height) / 2);
        margin = Math.Max(margin, 0);

        var (x, y) = corner switch
        {
            WatermarkCorner.TopLeft => (margin, margin),
            WatermarkCorner.TopRight => (canvasWidth - width - margin, margin),
            WatermarkCorner.BottomLeft => (margin, canvasHeight - height - margin),
            WatermarkCorner.BottomRight => (canvasWidth - width - margin, canvasHeight - height - margin),
            _ => ((canvasWidth - width) / 2, (canvasHeight - height) / 2),
        };

        return (x, y, width, height);
    }
}
