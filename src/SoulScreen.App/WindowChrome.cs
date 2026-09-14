using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Shell = System.Windows.Shell;

namespace SoulScreen.App;

/// <summary>
/// Window frame handling.
/// <para>
/// The app draws its own caption through <see cref="Shell.WindowChrome"/> so the toolbar
/// can be the title bar. DWM is also told which theme the window is in, so the thin frame
/// it still paints - and the caption it paints while the window is being dragged between
/// monitors - matches.
/// </para>
/// </summary>
internal static class WindowFrame
{
    /// <summary>Attribute id in Windows 10 20H1 and later.</summary>
    private const int UseImmersiveDarkMode = 20;

    /// <summary>The id the same attribute had in Windows 10 1809 and 1903.</summary>
    private const int UseImmersiveDarkModeBefore20H1 = 19;

    /// <summary>Windows 11: which of the rounded-corner treatments the window gets.</summary>
    private const int WindowCornerPreference = 33;

    private const int CornerPreferenceRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Height of the client-area strip that behaves as the caption: draggable, double-click
    /// to maximise, right-click for the system menu. Matches the app's toolbar.
    /// </summary>
    public const double CaptionHeight = 48;

    /// <summary>Grab area for resizing, in device-independent pixels.</summary>
    private const double ResizeBorder = 6;

    /// <summary>Tells DWM which theme the frame should match. Harmless where unsupported.</summary>
    public static void SetDarkFrame(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var enabled = dark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, UseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
    }

    /// <summary>Asks Windows 11 for rounded corners, which a custom-chromed window otherwise loses.</summary>
    public static void RoundCorners(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var preference = CornerPreferenceRound;
        DwmSetWindowAttribute(handle, WindowCornerPreference, ref preference, sizeof(int));
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

/// <summary>
/// Holds a window at a fixed aspect ratio while the user drags its edges, the way a video
/// player does. Works on the WM_SIZING message, so the window never briefly shows the wrong
/// shape and then snaps back: the size Windows is about to apply is corrected before it is.
/// </summary>
internal sealed class WindowAspectLock : IDisposable
{
    private const int WmSizing = 0x0214;

    private const int SizeLeft = 1, SizeRight = 2, SizeTop = 3, SizeTopLeft = 4,
        SizeTopRight = 5, SizeBottom = 6, SizeBottomLeft = 7, SizeBottomRight = 8;

    private readonly Window _window;
    private HwndSource? _source;

    /// <summary>Width over height of the content the window should be shaped to; 0 disables.</summary>
    public double Aspect { get; set; }

    /// <summary>
    /// Client-area pixels, in DIPs, that are not the picture: toolbar and status bar
    /// heights, and any horizontal chrome. Supplied by the window each time, because the
    /// chrome comes and goes with fullscreen.
    /// </summary>
    public Func<Size>? ChromeSize { get; set; }

    public bool Enabled { get; set; } = true;

    public WindowAspectLock(Window window)
    {
        _window = window;
        if (PresentationSource.FromVisual(window) is HwndSource source) Attach(source);
        else window.SourceInitialized += (_, _) => Attach((HwndSource)PresentationSource.FromVisual(window)!);
    }

    private void Attach(HwndSource source)
    {
        _source = source;
        source.AddHook(Hook);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmSizing || !Enabled || Aspect <= 0) return IntPtr.Zero;
        if (_window.WindowState != WindowState.Normal) return IntPtr.Zero;

        var rect = Marshal.PtrToStructure<Rect>(lParam);
        var edge = wParam.ToInt32();

        // Everything below is in physical pixels, which is what the rect holds.
        var dpi = VisualTreeHelper.GetDpi(_window);
        var chrome = ChromeSize?.Invoke() ?? default;
        var frame = FramePixels(hwnd, rect);
        var extraWidth = frame.Width + chrome.Width * dpi.DpiScaleX;
        var extraHeight = frame.Height + chrome.Height * dpi.DpiScaleY;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var contentWidth = Math.Max(width - extraWidth, 1);
        var contentHeight = Math.Max(height - extraHeight, 1);

        var minWidth = _window.MinWidth * dpi.DpiScaleX;
        var minHeight = _window.MinHeight * dpi.DpiScaleY;

        // Side edges set the width and derive the height; top and bottom the reverse.
        // Corners follow whichever dimension the drag changed more.
        var widthDrives = edge switch
        {
            SizeLeft or SizeRight => true,
            SizeTop or SizeBottom => false,
            _ => Math.Abs(contentWidth / Aspect - contentHeight) >= Math.Abs(contentHeight * Aspect - contentWidth),
        };

        if (widthDrives)
        {
            contentHeight = contentWidth / Aspect;
            if (contentHeight + extraHeight < minHeight)
            {
                contentHeight = minHeight - extraHeight;
                contentWidth = contentHeight * Aspect;
            }
        }
        else
        {
            contentWidth = contentHeight * Aspect;
            if (contentWidth + extraWidth < minWidth)
            {
                contentWidth = minWidth - extraWidth;
                contentHeight = contentWidth / Aspect;
            }
        }

        var newWidth = (int)Math.Round(contentWidth + extraWidth);
        var newHeight = (int)Math.Round(contentHeight + extraHeight);

        // Keep the edge the user is not holding where it is.
        switch (edge)
        {
            case SizeLeft or SizeTopLeft or SizeBottomLeft:
                rect.Left = rect.Right - newWidth;
                break;
            default:
                rect.Right = rect.Left + newWidth;
                break;
        }

        switch (edge)
        {
            case SizeTop or SizeTopLeft or SizeTopRight:
                rect.Top = rect.Bottom - newHeight;
                break;
            default:
                rect.Bottom = rect.Top + newHeight;
                break;
        }

        Marshal.StructureToPtr(rect, lParam, fDeleteOld: false);
        handled = true;
        return new IntPtr(1);
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    /// <summary>Pixels of window frame around the client area, on each axis.</summary>
    private static Size FramePixels(IntPtr hwnd, Rect proposed)
    {
        if (!GetWindowRect(hwnd, out var window) || !GetClientRect(hwnd, out var client))
            return default;
        var frameWidth = (window.Right - window.Left) - (client.Right - client.Left);
        var frameHeight = (window.Bottom - window.Top) - (client.Bottom - client.Top);
        return new Size(Math.Max(frameWidth, 0), Math.Max(frameHeight, 0));
    }

    public void Dispose()
    {
        _source?.RemoveHook(Hook);
        _source = null;
    }
}
