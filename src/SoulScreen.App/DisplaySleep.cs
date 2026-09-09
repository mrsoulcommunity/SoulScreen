using System.Runtime.InteropServices;

namespace SoulScreen.App;

/// <summary>
/// Keeps the screen on while a session is mirroring.
/// <para>
/// Windows counts idleness by input, not by what is on screen, so watching a mirrored phone
/// would let the monitor blank and eventually lock the machine part-way through. Video
/// players make the same request for the same reason.
/// </para>
/// <para>
/// The state is per-thread, so every call has to come from the same thread - the UI thread -
/// and it lapses on its own if the process dies.
/// </para>
/// </summary>
internal static class DisplaySleep
{
    /// <summary>Makes the request persist rather than resetting the timer once.</summary>
    private const uint Continuous = 0x80000000;

    private const uint SystemRequired = 0x00000001;
    private const uint DisplayRequired = 0x00000002;

    private static bool _held;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    /// <summary>Asks Windows to keep the display and the machine awake.</summary>
    public static void Hold()
    {
        if (_held) return;
        _held = SetThreadExecutionState(Continuous | SystemRequired | DisplayRequired) != 0;
    }

    /// <summary>Lets the normal idle timers resume.</summary>
    public static void Release()
    {
        if (!_held) return;
        SetThreadExecutionState(Continuous);
        _held = false;
    }
}
