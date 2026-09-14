namespace SoulScreen.App.Logic;

/// <summary>
/// The axis-aligned scale and offset that carries one rectangle onto another.
/// <para>
/// Drawings made over the picture are kept in the picture's place: when the window is resized
/// the picture moves and grows, and the strokes are carried from where the picture was to where
/// it now is. The same mapping takes them from the screen onto a screenshot's pixels.
/// </para>
/// Deliberately free of WPF so it can be tested on its own.
/// </summary>
internal readonly record struct RectMapping(double ScaleX, double ScaleY, double OffsetX, double OffsetY)
{
    /// <summary>The mapping from <paramref name="from"/> to <paramref name="to"/>, or null when
    /// either has no area - there is nothing sensible to scale a stroke by.</summary>
    public static RectMapping? Between(Bounds from, Bounds to)
    {
        if (!IsUsable(from) || !IsUsable(to)) return null;
        var scaleX = to.Width / from.Width;
        var scaleY = to.Height / from.Height;
        return new RectMapping(scaleX, scaleY, to.Left - from.Left * scaleX, to.Top - from.Top * scaleY);
    }

    public (double X, double Y) Apply(double x, double y) => (x * ScaleX + OffsetX, y * ScaleY + OffsetY);

    /// <summary>True when applying it would move nothing by as much as a hundredth of a pixel.</summary>
    public bool IsIdentity =>
        Math.Abs(ScaleX - 1) < 1e-6 && Math.Abs(ScaleY - 1) < 1e-6 && Math.Abs(OffsetX) < 0.01 && Math.Abs(OffsetY) < 0.01;

    private static bool IsUsable(Bounds bounds) =>
        bounds.Width > 0 && bounds.Height > 0
        && double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top)
        && double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height);
}
