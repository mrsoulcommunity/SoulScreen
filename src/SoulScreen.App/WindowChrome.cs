using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Shell = System.Windows.Shell;

namespace SoulScreen.App;

/// <summary>
/// Window frame handling.
/// <para>
/// The app is fully dark, so a system caption would sit on top of it as a bright white
/// bar. Asking DWM for a dark caption is tried first because it keeps the native title bar
/// with all its behaviour; where DWM ignores the request - which it does on some Windows
/// builds even while reporting success - the app falls back to drawing its own caption
/// through <see cref="Shell.WindowChrome"/>.
/// </para>
/// </summary>
internal static class WindowFrame
{
    /// <summary>Attribute id in Windows 10 20H1 and later.</summary>
    private const int UseImmersiveDarkMode = 20;

    /// <summary>The id the same attribute had in Windows 10 1809 and 1903.</summary>
    private const int UseImmersiveDarkModeBefore20H1 = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Height of the client-area strip that behaves as the caption: draggable, double-click
    /// to maximise, right-click for the system menu. Matches the app's toolbar.
    /// </summary>
    public const double CaptionHeight = 46;

    /// <summary>Grab area for resizing, in device-independent pixels.</summary>
    private const double ResizeBorder = 6;

    /// <summary>Requests a dark caption. Harmless where it is not supported.</summary>
    public static void TryDarkTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var enabled = 1;
        if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, UseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
    }

    /// <summary>
    /// Replaces the system caption with the app's own toolbar. Snapping, resizing, Aero
    /// Snap and the system menu all keep working because the frame is still a normal
    /// window - only the caption is drawn by us.
    /// </summary>
    public static void UseCustomCaption(Window window)
    {
        Shell.WindowChrome.SetWindowChrome(window, new Shell.WindowChrome
        {
            CaptionHeight = CaptionHeight,
            ResizeBorderThickness = new Thickness(ResizeBorder),
            // Zero glass and no corner radius keep the frame flush with our own background.
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
    }

    public static void RemoveCustomCaption(Window window) =>
        Shell.WindowChrome.SetWindowChrome(window, null);

    /// <summary>
    /// A maximised window with a custom chrome extends its resize border past the screen
    /// edge, so the content needs padding back by exactly that much.
    /// </summary>
    public static Thickness MaximisedPadding(Window window) =>
        window.WindowState == WindowState.Maximized
            ? new Thickness(ResizeBorder + 1)
            : new Thickness(0);
}
