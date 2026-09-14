using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using SoulScreen.Core.Logging;

namespace SoulScreen.App;

/// <summary>What a system-wide shortcut does.</summary>
internal enum HotkeyAction
{
    Screenshot,
    Record,
    MiniPlayer,
    ShowWindow,
}

/// <summary>
/// Shortcuts that reach SoulScreen while another application has the keyboard - a screenshot
/// of the phone without leaving the document being written.
/// <para>
/// Every one is Ctrl+Alt+Shift and a letter. Ctrl+Alt alone is AltGr on many keyboard layouts,
/// where Ctrl+Alt+S types a letter, and taking it would stop people typing; the Windows key
/// combinations are Windows' own. A shortcut another program already holds simply fails to
/// register, and the settings say which.
/// </para>
/// </summary>
internal sealed class GlobalHotkeys : IDisposable
{
    private static readonly ILogger Log_ = Log.For("hotkeys");

    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;

    /// <summary>Ids are offset so they cannot collide with anything else registered on the window.</summary>
    private const int IdBase = 0x5300;

    public const string ModifierText = "Ctrl+Alt+Shift";

    public static readonly (HotkeyAction Action, Key Key, string Description)[] Bindings =
    [
        (HotkeyAction.Screenshot, Key.S, "Save a screenshot"),
        (HotkeyAction.Record, Key.R, "Start or stop recording"),
        (HotkeyAction.MiniPlayer, Key.M, "Mini player"),
        (HotkeyAction.ShowWindow, Key.O, "Bring SoulScreen to the front"),
    ];

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    private readonly Window _window;
    private readonly HashSet<int> _registered = [];
    private HwndSource? _source;

    public GlobalHotkeys(Window window) => _window = window;

    /// <summary>Raised on the UI thread when a registered shortcut is pressed.</summary>
    public event Action<HotkeyAction>? Pressed;

    public bool IsRegistered => _registered.Count > 0;

    public static string Gesture(Key key) => $"{ModifierText}+{key}";

    /// <summary>Registers every shortcut, replacing any registered before. Returns the ones
    /// another application already holds.</summary>
    public IReadOnlyList<HotkeyAction> Register()
    {
        Unregister();

        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero) return Bindings.Select(b => b.Action).ToList();

        if (_source is null)
        {
            _source = HwndSource.FromHwnd(handle);
            _source?.AddHook(Hook);
        }

        var failed = new List<HotkeyAction>();
        foreach (var (action, key, _) in Bindings)
        {
            var id = IdBase + (int)action;
            var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (RegisterHotKey(handle, id, ModControl | ModAlt | ModShift | ModNoRepeat, virtualKey))
            {
                _registered.Add(id);
            }
            else
            {
                failed.Add(action);
                Log_.Info($"{Gesture(key)} is held by another application (error {Marshal.GetLastWin32Error()})");
            }
        }

        return failed;
    }

    public void Unregister()
    {
        if (_registered.Count == 0) return;
        var handle = new WindowInteropHelper(_window).Handle;
        foreach (var id in _registered)
            if (handle != IntPtr.Zero) UnregisterHotKey(handle, id);
        _registered.Clear();
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmHotkey) return IntPtr.Zero;
        var id = wParam.ToInt32();
        if (!_registered.Contains(id)) return IntPtr.Zero;

        handled = true;
        Pressed?.Invoke((HotkeyAction)(id - IdBase));
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source?.RemoveHook(Hook);
        _source = null;
    }
}
