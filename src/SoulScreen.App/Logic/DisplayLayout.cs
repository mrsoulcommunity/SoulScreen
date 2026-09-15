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
    public bool IsEmpty => !double.IsFinite(Left) || !double.IsFinite(Top)
        || !double.IsFinite(Width) || !double.IsFinite(Height) || Width <= 0 || Height <= 0;
}

/// <summary>Testable display selection and safe window placement helpers.</summary>
public static class DisplayLayout
{
    public const string CurrentDisplay = "current";
    public const string PrimaryDisplay = "primary";

    public static DisplayChoice? Resolve(string? choice, IReadOnlyList<DisplayChoice> displays, RectBounds windowBounds)
    {
        if (displays.Count == 0 || windowBounds.IsEmpty) return null;
        if (string.IsNullOrEmpty(choice) || choice == CurrentDisplay) return null;

        if (choice == PrimaryDisplay)
            return displays.FirstOrDefault(d => !d.WorkArea.IsEmpty && d.IsPrimary)
                ?? displays.FirstOrDefault(d => !d.WorkArea.IsEmpty);

        if (int.TryParse(choice, System.Globalization.CultureInfo.InvariantCulture, out var index)
            && index >= 0 && index < displays.Count)
        {
            var chosen = displays[index];
            return chosen.WorkArea.IsEmpty || IsOnDisplay(windowBounds, chosen.WorkArea) ? null : chosen;
        }

        return null;
    }

    public static bool IsOnDisplay(RectBounds window, RectBounds display)
    {
        if (window.IsEmpty || display.IsEmpty) return false;
        var centreX = window.Left + window.Width / 2;
        var centreY = window.Top + window.Height / 2;
        return centreX >= display.Left && centreX < display.Right
            && centreY >= display.Top && centreY < display.Bottom;
    }

    public static (double Left, double Top) PlacementFor(DisplayChoice display, RectBounds window)
    {
        if (display.WorkArea.IsEmpty || window.IsEmpty) return (window.Left, window.Top);
        var left = display.WorkArea.Left + Math.Max((display.WorkArea.Width - window.Width) / 2, 0);
        var top = display.WorkArea.Top + Math.Max((display.WorkArea.Height - window.Height) / 2, 0);
        if (left + window.Width > display.WorkArea.Right) left = Math.Max(display.WorkArea.Right - window.Width, display.WorkArea.Left);
        if (top + window.Height > display.WorkArea.Bottom) top = Math.Max(display.WorkArea.Bottom - window.Height, display.WorkArea.Top);
        return (left, top);
    }

    public static (double Left, double Top) ClampToDisplays(
        double left, double top, double width, double height,
        RectBounds virtualDesktop, double minVisible = 120)
    {
        if (virtualDesktop.IsEmpty || !double.IsFinite(left) || !double.IsFinite(top)
            || !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            return (left, top);

        minVisible = Math.Clamp(double.IsFinite(minVisible) ? minVisible : 120, 40, Math.Max(width, 40));

        // Keep the window's title bar reachable. A window larger than the virtual desktop
        // needs a negative origin so its far edge can still land on a real display. A normal
        // window, on the other hand, should never preserve a large left/top overhang from a
        // monitor that was just unplugged.
        var x = width > virtualDesktop.Width
            ? Math.Clamp(left, virtualDesktop.Right - width, virtualDesktop.Left)
            : left < virtualDesktop.Left
                ? virtualDesktop.Left
                : left > virtualDesktop.Right - minVisible
                    ? Math.Max(virtualDesktop.Right - width, virtualDesktop.Left)
                    : left;
        var y = height > virtualDesktop.Height
            ? virtualDesktop.Bottom - height
            : top < virtualDesktop.Top
                ? virtualDesktop.Top
                : top > virtualDesktop.Bottom - minVisible
                    ? Math.Max(virtualDesktop.Bottom - height, virtualDesktop.Top)
                    : top;
        return (x, y);
    }
}
