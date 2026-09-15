using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace SoulScreen.App;

/// <summary>
/// What Windows currently offers as displays, in the coordinate space window Left and Top
/// use. Everything OS-specific about enumerating monitors lives here; the choice and
/// clamping live in <see cref="Logic.DisplayLayout"/>, which is tested.
/// </summary>
internal static class DisplayService
{
    public static IReadOnlyList<Logic.DisplayChoice> ListChoices(Window window)
    {
        var choices = new List<Logic.DisplayChoice>();
        try
        {
            var dpi = VisualTreeHelper.GetDpi(window);
            foreach (var screen in Forms.Screen.AllScreens)
            {
                var area = screen.WorkingArea;
                choices.Add(new Logic.DisplayChoice(
                    choices.Count,
                    string.IsNullOrWhiteSpace(screen.DeviceName) ? null : screen.DeviceName,
                    screen.Primary,
                    new Logic.RectBounds(area.Left / dpi.DpiScaleX, area.Top / dpi.DpiScaleY,
                        area.Width / dpi.DpiScaleX, area.Height / dpi.DpiScaleY)));
            }
        }
        catch (Exception)
        {
            // A monitor list that cannot be read must never stop the window from being placed.
            return [];
        }
        return choices;
    }

    /// <summary>The union of every display's work area, for clamping.</summary>
    public static Logic.RectBounds VirtualDesktop(Window window)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(window);
            double left = double.MaxValue, top = double.MaxValue;
            double right = double.MinValue, bottom = double.MinValue;
            foreach (var screen in Forms.Screen.AllScreens)
            {
                var area = screen.WorkingArea;
                left = Math.Min(left, area.Left / dpi.DpiScaleX);
                top = Math.Min(top, area.Top / dpi.DpiScaleY);
                right = Math.Max(right, area.Right / dpi.DpiScaleX);
                bottom = Math.Max(bottom, area.Bottom / dpi.DpiScaleY);
            }
            if (left >= right || top >= bottom) return default;
            return new Logic.RectBounds(left, top, right - left, bottom - top);
        }
        catch (Exception)
        {
            return default;
        }
    }

    /// <summary>The window's bounds in the same space <see cref="ListChoices"/> uses.</summary>
    public static Logic.RectBounds BoundsOf(Window window) =>
        new(window.Left, window.Top, window.ActualWidth, window.ActualHeight);

    /// <summary>Moves the window onto <paramref name="display"/>, centred and clamped.</summary>
    public static void MoveTo(Window window, Logic.DisplayChoice display)
    {
        var (left, top) = Logic.DisplayLayout.PlacementFor(display, BoundsOf(window));
        window.Left = left;
        window.Top = top;
    }

    /// <summary>
    /// Watches for displays being plugged or unplugged, which changes both the choice list
    /// and whether the chosen one still exists.
    /// </summary>
    public sealed class DisplayWatcher : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern IntPtr RegisterDeviceNotification(IntPtr recipient, IntPtr notificationFilter, uint flags);

        [DllImport("user32.dll")]
        private static extern bool UnregisterDeviceNotification(IntPtr handle);

        private const uint DeviceNotifyWindowHandle = 0;
        private static readonly IntPtr DeviceNotifyAll = new(0);

        private const int WmDisplayChange = 0x007E;
        private const int WmDeviceChange = 0x0219;

        private readonly Action _changed;
        private HwndSource? _source;
        private IntPtr _notificationHandle;

        public DisplayWatcher(Window window, Action changed)
        {
            _changed = changed;
            if (PresentationSource.FromVisual(window) is HwndSource source)
            {
                _source = source;
                source.AddHook(Hook);
                Register(window);
            }
            else
            {
                window.SourceInitialized += (_, _) =>
                {
                    if (PresentationSource.FromVisual(window) is HwndSource lateSource)
                    {
                        _source = lateSource;
                        lateSource.AddHook(Hook);
                        Register(window);
                    }
                };
            }
        }

        private void Register(Window window)
        {
            try
            {
                var handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero) return;
                var filter = Marshal.AllocHGlobal(Marshal.SizeOf<DevBroadcastDeviceInterface>());
                Marshal.StructureToPtr(new DevBroadcastDeviceInterface(), filter, false);
                _notificationHandle = RegisterDeviceNotification(handle, filter, DeviceNotifyWindowHandle);
                Marshal.FreeHGlobal(filter);
            }
            catch (Exception)
            {
                // Watching is a nicety; the choice is re-read whenever settings open anyway.
            }
        }

        private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // WM_DISPLAYCHANGE covers resolution changes; WM_DEVICECHANGE with a display
            // device covers dock and monitor arrivals. Either way the list is stale.
            if ((msg == WmDisplayChange) || (msg == WmDeviceChange && wParam.ToInt64() == 7))
                _changed();
            return IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DevBroadcastDeviceInterface
        {
            public int Size = Marshal.SizeOf<DevBroadcastDeviceInterface>();
            public int DeviceType = 5; // DBT_DEVTYP_DEVICEINTERFACE
            public int Reserved;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] ClassGuid = [0xCA, 0x69, 0x76, 0x4B, 0xD9, 0x54, 0xC0, 0x40, 0xA5, 0x54, 0x61, 0xA7, 0x29, 0x0B, 0x3E, 0x5B];

            public DevBroadcastDeviceInterface()
            {
            }
        }

        public void Dispose()
        {
            if (_notificationHandle != IntPtr.Zero)
            {
                try { UnregisterDeviceNotification(_notificationHandle); }
                catch { /* shutting down */ }
                _notificationHandle = IntPtr.Zero;
            }
            _source?.RemoveHook(Hook);
        }
    }
}
