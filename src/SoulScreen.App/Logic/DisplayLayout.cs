namespace SoulScreen.App.Logic;

/// <summary>One display the window could be moved to.</summary>
/// <param name="Index">Zero-based index in the monitor list.</param>
/// <param name="DeviceName">The monitor's own name, where Windows provides one.</param>
/// <param name="IsPrimary">True for the monitor Windows calls its main one.</param>
/// <param name="WorkArea">The area of the monitor not covered by the taskbar, in the same
/// coordinate space window Left/Top use.</param>
public sealed record DisplayChoice(int Index, string? DeviceName, bool IsPrimary, RectBounds WorkArea);

/// <summary>A rectangle in screen coordinates, WPF-free so it can be tested.</summary>
public readonly record struct RectBounds(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// Choosing and clamping which display the window sits on.
/// <para>
/// Moving a window to a chosen display is easy; doing it without stranding it - half on a
/// monitor that has since been unplugged, or centred on a display Windows then removed -
/// is the part worth testing. Deliberately free of WPF so it can be tested on its own.
/// </para>
/// </summary>
public static class DisplayLayout
{
    /// <summary>The persisted choice: current display, or the primary one.</summary>
    public const string CurrentDisplay = "current";
    /// <summary>The persisted choice meaning the primary display.</summary>
    public const string PrimaryDisplay = "primary";

    /// <summary>
    /// The display to move to for <paramref name="choice"/>, or null to leave the window
    /// where it is. The current display is matched by the monitor holding the largest share
    /// of the window - the one it is on, in every case that matters. A choice naming a
    /// display that no longer exists falls back to leaving the window alone rather than
    /// throwing it somewhere unexpected.
    /// </summary>
    public static DisplayChoice? Resolve(
        string? choice, IReadOnlyList<DisplayChoice> displays,
        RectBounds windowBounds)
    {
        if (displays.Count == 0) return null;
        if (string.IsNullOrEmpty(choice) || choice == CurrentDisplay) return null;

        if (choice == PrimaryDisplay)
            return displays.FirstOrDefault(d => d.IsPrimary) ?? displays[0];

        if (int.TryParse(choice, System.Globalization.CultureInfo.InvariantCulture, out var index))
        {
            if (index < 0 || index >= displays.Count) return null;
            var chosen = displays[index];
            if (IsOnDisplay(windowBounds, chosen.WorkArea)) return null;
            return chosen;
        }

        return null;
    }

    /// <summary>True when the window's centre sits on this display.</summary>
    public static bool IsOnDisplay(RectBounds window, RectBounds display)
    {
        if (display.IsEmpty) return false;
        var centreX = window.Left + window.Width / 2;
        var centreY = window.Top + window.Height / 2;
        return centreX >= display.Left && centreX < display.Right
            && centreY >= display.Top && centreY < display.Bottom;
    }

    /// <summary>
    /// Where the window goes: centred on the display, then pulled back inside it if the
    /// window is larger than the display allows.
    /// </summary>
    public static (double Left, double Top) PlacementFor(DisplayChoice display, RectBounds window)
    {
        var left = display.WorkArea.Left + Math.Max((display.WorkArea.Width - window.Width) / 2, 0);
        var top = display.WorkArea.Top + Math.Max((display.WorkArea.Height - window.Height) / 2, 0);

        // A window bigger than the work area - fullscreen on a small monitor, or a portrait
        // window on a landscape one - still starts inside it.
        if (left + window.Width > display.WorkArea.Right) left = Math.Max(display.WorkArea.Right - window.Width, display.WorkArea.Left);
        if (top + window.Height > display.WorkArea.Bottom) top = Math.Max(display.WorkArea.Bottom - window.Height, display.WorkArea.Top);

        return (left, top);
    }

    /// <summary>Clamps any position so the window's title bar stays reachable: at least
    /// <paramref name="minVisible"/> pixels of it must land on some display. Given the
    /// union of every display's work area, this cannot leave a window in the void between
    /// or beyond monitors.</summary>
    public static (double Left, double Top) ClampToDisplays(
        double left, double top, double width, double height,
        RectBounds virtualDesktop, double minVisible = 120)
    {
        minVisible = Math.Clamp(minVisible, 40, width > 0 ? width : 40);
        if (virtualDesktop.IsEmpty)
            return (left, top);

        var x = left;
        var y = top;
        // An edge beyond the desktop comes back to that edge; a far-side overhang pulls
        // fully inside, so the title bar is reachable again. A window already touching
        // every edge is left where its owner put it.
        if (x < virtualDesktop.Left) x = virtualDesktop.Left;
        if (x > virtualDesktop.Right - minVisible) x = Math.Max(virtualDesktop.Right - width, virtualDesktop.Left);
        if (y > virtualDesktop.Bottom - minVisible) y = Math.Max(virtualDesktop.Bottom - height, virtualDesktop.Top);
        if (y < virtualDesktop.Top) y = virtualDesktop.Top;
        return (x, y);
    }
}
