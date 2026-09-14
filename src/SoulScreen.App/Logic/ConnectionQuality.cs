namespace SoulScreen.App.Logic;

/// <summary>How well a session is getting through, as the toolbar's three bars show it.</summary>
public enum ConnectionQualityLevel
{
    /// <summary>Not enough of the session seen yet to say.</summary>
    Unknown,
    /// <summary>The picture itself is being interrupted.</summary>
    Poor,
    /// <summary>Something was lost recently, but the session is holding up.</summary>
    Fair,
    /// <summary>Nothing lost on the way.</summary>
    Good,
}

/// <summary>
/// Judges a session's connection from what it has lost recently, not from how fast it is going.
/// <para>
/// Frame rate says little on its own: iOS sends fewer frames when the screen is still, so a
/// phone showing a static page at eight frames a second is perfectly healthy. Losses are what a
/// person actually sees and hears - a resynchronisation freezes the picture until the next
/// keyframe, and a lost audio packet is a click or a gap - so they are what is counted.
/// </para>
/// <para>
/// The meter is fed the running totals once a tick and looks back over a window of seconds. A
/// tick in which a total rose counts as one event however much it rose by, since one Wi-Fi pause
/// can cost sixty frames at once and is still one pause.
/// </para>
/// </summary>
public sealed class ConnectionQualityMeter
{
    /// <summary>How far back the judgement looks.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    /// <summary>How much of a session has to be seen before anything is said about it.</summary>
    public static readonly TimeSpan Warmup = TimeSpan.FromSeconds(2);

    private readonly Queue<(TimeSpan At, bool VideoLoss, bool AudioLoss)> _events = new();
    private TimeSpan? _startedAt;
    private long _lastVideoLost = -1;
    private long _lastAudioLost = -1;

    /// <summary>The level as of the last sample.</summary>
    public ConnectionQualityLevel Level { get; private set; } = ConnectionQualityLevel.Unknown;

    /// <summary>Picture interruptions within the window.</summary>
    public int VideoEvents { get; private set; }

    /// <summary>Audio gaps within the window.</summary>
    public int AudioEvents { get; private set; }

    /// <summary>Forgets the session measured so far; the next sample starts a new one.</summary>
    public void Reset()
    {
        _events.Clear();
        _startedAt = null;
        _lastVideoLost = -1;
        _lastAudioLost = -1;
        VideoEvents = 0;
        AudioEvents = 0;
        Level = ConnectionQualityLevel.Unknown;
    }

    /// <param name="now">Any monotonic clock.</param>
    /// <param name="videoLost">Running total of frames lost before they could be shown: dropped
    /// by a decoder that fell behind, or skipped while waiting for a keyframe. Frames the display
    /// had no time for are not network losses and do not belong here.</param>
    /// <param name="audioLost">Running total of audio packets that went missing or had to be skipped.</param>
    public ConnectionQualityLevel Sample(TimeSpan now, long videoLost, long audioLost)
    {
        _startedAt ??= now;

        // A total that went down belongs to a new pipeline; it is a fresh baseline, not a loss.
        var videoLoss = _lastVideoLost >= 0 && videoLost > _lastVideoLost;
        var audioLoss = _lastAudioLost >= 0 && audioLost > _lastAudioLost;
        _lastVideoLost = videoLost;
        _lastAudioLost = audioLost;

        if (videoLoss || audioLoss) _events.Enqueue((now, videoLoss, audioLoss));
        while (_events.Count > 0 && now - _events.Peek().At > Window) _events.Dequeue();

        VideoEvents = _events.Count(e => e.VideoLoss);
        AudioEvents = _events.Count(e => e.AudioLoss);

        Level = now - _startedAt.Value < Warmup ? ConnectionQualityLevel.Unknown : Judge(VideoEvents, AudioEvents);
        return Level;
    }

    /// <summary>Two freezes in ten seconds, or sound breaking up every couple of seconds, is a
    /// poor connection; any freeze at all, or a few gaps, is a fair one.</summary>
    public static ConnectionQualityLevel Judge(int videoEvents, int audioEvents)
    {
        if (videoEvents >= 2 || audioEvents >= 5) return ConnectionQualityLevel.Poor;
        if (videoEvents == 1 || audioEvents >= 2) return ConnectionQualityLevel.Fair;
        return ConnectionQualityLevel.Good;
    }

    /// <summary>The words behind the bars, for the tooltip.</summary>
    public static string Describe(ConnectionQualityLevel level) => level switch
    {
        ConnectionQualityLevel.Good => "Connection: excellent. Nothing has been lost on the way.",
        ConnectionQualityLevel.Fair => "Connection: fair. A little sound or picture went missing in the last few seconds.",
        ConnectionQualityLevel.Poor => "Connection: poor. The picture keeps being interrupted - moving closer to the router, or using a 5 GHz network, helps most.",
        _ => "Connection: measuring…",
    };
}
