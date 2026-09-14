namespace SoulScreen.App.Logic;

/// <summary>A rectangle in device-independent pixels, kept apart from WPF's so it can be tested alone.</summary>
internal readonly record struct Bounds(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

/// <summary>
/// Where the mini player goes and how big it is.
/// <para>
/// It takes the picture's shape, so there are no bars. Its size is remembered as the length
/// of its longer side - that survives the phone turning, where a width and height would not -
/// and its place as the corner it was left in. Anything remembered that no longer fits the
/// screen it is opened on is pulled back inside rather than trusted.
/// </para>
/// </summary>
internal static class MiniPlayerGeometry
{
    /// <summary>Gap kept between the player and the edges of the work area.</summary>
    public const double Margin = 20;

    /// <summary>Shortest side the player may have: enough for its controls.</summary>
    public const double MinimumShortSide = 150;

    /// <param name="aspect">Picture width over height.</param>
    /// <param name="workArea">The work area of the screen the window is on.</param>
    /// <param name="rememberedLongSide">Longer side from last time, or null for the default.</param>
    /// <param name="rememberedLeft">Left edge from last time, or null to go bottom-right.</param>
    /// <param name="rememberedTop">Top edge from last time.</param>
    public static Bounds Place(double aspect, Bounds workArea, double? rememberedLongSide, double? rememberedLeft, double? rememberedTop)
    {
        if (!(aspect > 0) || double.IsInfinity(aspect)) aspect = 9.0 / 16.0;

        var portrait = aspect < 1;
        var defaultLongSide = portrait ? workArea.Height * 0.42 : workArea.Width * 0.3;
        var longSide = rememberedLongSide is > 0 and < 100_000 ? rememberedLongSide.Value : defaultLongSide;

        var (width, height) = portrait ? (longSide * aspect, longSide) : (longSide, longSide / aspect);

        // Never smaller than the controls need...
        var shortSide = Math.Min(width, height);
        if (shortSide < MinimumShortSide)
        {
            var grow = MinimumShortSide / shortSide;
            width *= grow;
            height *= grow;
        }

        // ...and never larger than the screen, less its margins.
        var fit = Math.Min(
            Math.Max(workArea.Width - 2 * Margin, 1) / width,
            Math.Max(workArea.Height - 2 * Margin, 1) / height);
        if (fit < 1)
        {
            width *= fit;
            height *= fit;
        }

        var (left, top) = rememberedLeft is { } l && rememberedTop is { } t
            ? (l, t)
            : (workArea.Right - Margin - width, workArea.Bottom - Margin - height);

        left = Clamp(left, workArea.Left, workArea.Right - width);
        top = Clamp(top, workArea.Top, workArea.Bottom - height);
        return new Bounds(left, top, width, height);
    }

    /// <summary>How close to an edge a dropped player has to be to be pulled onto it.</summary>
    public const double SnapDistance = 56;

    /// <summary>
    /// Where a player let go of at <paramref name="window"/> settles, as picture in picture does
    /// on a Mac: near an edge, it lines up with that edge's margin; partly off the screen, it is
    /// brought back on; anywhere else it stays exactly where it was put.
    /// </summary>
    public static Bounds Snap(Bounds window, Bounds workArea)
    {
        var left = SnapAxis(window.Left, window.Width, workArea.Left, workArea.Right);
        var top = SnapAxis(window.Top, window.Height, workArea.Top, workArea.Bottom);
        return window with { Left = left, Top = top };
    }

    private static double SnapAxis(double start, double length, double areaStart, double areaEnd)
    {
        var near = areaStart + Margin;
        var far = areaEnd - Margin - length;

        // Too big to sit between the margins: keep it on the screen, and nothing more.
        if (far < near) return Clamp(start, areaStart, areaEnd - length);

        if (start <= near + SnapDistance) return near;
        if (start >= far - SnapDistance) return far;
        return start;
    }

    /// <summary>The length to remember for a player of this size.</summary>
    public static double LongSide(double width, double height) => Math.Max(width, height);

    /// <summary>Math.Clamp that tolerates max below min, which a player wider than a tiny
    /// screen produces; the lower edge wins.</summary>
    private static double Clamp(double value, double min, double max) =>
        max < min ? min : Math.Clamp(value, min, max);
}
