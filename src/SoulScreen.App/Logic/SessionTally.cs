namespace SoulScreen.App.Logic;

/// <summary>
/// Counts what a session has produced so far: screenshots taken and recordings made.
/// <para>
/// The counters are bumped by the code paths that create the files, and are read once, when the
/// session ends, to build the <see cref="SessionSummary"/>. They are cleared in the same places
/// the session's own state is, so they can never describe the session before.
/// </para>
/// </summary>
public sealed class SessionTally
{
    public int Screenshots { get; private set; }
    public int Recordings { get; private set; }

    /// <summary>The size on disk of the recordings made this session.</summary>
    public long RecordedBytes { get; private set; }

    public void AddScreenshot() => Screenshots++;

    /// <param name="bytes">The size of the file that finished, or zero when it was moved
    /// or deleted before its size could be read.</param>
    public void AddRecording(long bytes)
    {
        Recordings++;
        RecordedBytes += Math.Max(bytes, 0);
    }

    public void Clear()
    {
        Screenshots = 0;
        Recordings = 0;
        RecordedBytes = 0;
    }
}
