using SoulScreen.AirPlay.Rtsp;
using SoulScreen.Core.Logging;
using SoulScreen.Core.Media;
using SoulScreen.Core.Sources;

namespace SoulScreen.AirPlay;

/// <summary>
/// One sender's mirroring session, surfaced as its own <see cref="IMirrorSource"/>, so a
/// multi-device host can put each session on its own tile.
/// <para>
/// The adapter is deliberately thin: it forwards the per-session events
/// <see cref="AirPlayRequestHandler"/> raises with a session attached, and reads its state
/// straight off the <see cref="AirPlaySession"/>. The session's own lifetime - pairing,
/// FairPlay, the media channels, the socket - is unchanged; disposing this adapter does not
/// end the phone's session, it only detaches the observer.
/// </para>
/// </summary>
public sealed class AirPlaySessionSource : IMirrorSource
{
    private readonly ILogger _log = Log.For("session-source");

    /// <summary>The control-connection session this adapter mirrors.</summary>
    public AirPlaySession Session { get; }

    /// <summary>The handler to consult for a round-trip figure, or null when the host did
    /// not supply one. The timing channel itself lives on the session.</summary>
    private readonly Func<AirPlaySession, double?>? _roundTripMillisecondsOf;

    internal AirPlaySessionSource(AirPlaySession session, Func<AirPlaySession, double?>? roundTripMillisecondsOf)
    {
        Session = session;
        _roundTripMillisecondsOf = roundTripMillisecondsOf;
    }

    /// <summary>Stable per-connection id, which is what a tile keeps between layout passes.</summary>
    public string Id => $"airplay:{Session.RemoteEndPoint}";

    public string DisplayName => Session.Device?.Name ?? "AirPlay session";

    public MirrorSourceState State => Session.IsStreaming
        ? MirrorSourceState.Streaming
        : Session.Device is not null ? MirrorSourceState.Connecting : MirrorSourceState.Ready;

    public SourceDeviceInfo? Device => Session.Device;

    /// <summary>When this sender's control connection opened - the session's clock.</summary>
    public DateTime StartedAtUtc => Session.StartedAtUtc;

    /// <summary>Most recent measured round-trip to this sender, in milliseconds, or null
    /// before its timing channel has synchronised. Each session measures its own.</summary>
    public double? RoundTripMilliseconds => _roundTripMillisecondsOf?.Invoke(Session);

    public event EventHandler<MirrorSourceStateChangedEventArgs>? StateChanged;
    public event EventHandler<VideoFormat>? VideoFormatChanged;
    public event EventHandler<MediaSample>? VideoSampleReady;
    public event EventHandler<AudioFormat>? AudioFormatChanged;
    public event EventHandler<MediaSample>? AudioSampleReady;

    /// <summary>Wires the per-session events of <paramref name="handler"/> to this adapter.
    /// Called by the session source provider, which is the only place that knows both ends.</summary>
    internal void Wire(AirPlayRequestHandler handler)
    {
        handler.SessionVideoFormatChanged += OnSessionVideoFormatChanged;
        handler.SessionVideoSampleReady += OnSessionVideoSampleReady;
        handler.SessionAudioFormatChanged += OnSessionAudioFormatChanged;
        handler.SessionAudioSampleReady += OnSessionAudioSampleReady;
    }

    /// <summary>Undoes <see cref="Wire"/>. Safe to call more than once, and always called
    /// before the adapter is dropped, so a finished session cannot raise into it.</summary>
    internal void Unwire(AirPlayRequestHandler handler)
    {
        handler.SessionVideoFormatChanged -= OnSessionVideoFormatChanged;
        handler.SessionVideoSampleReady -= OnSessionVideoSampleReady;
        handler.SessionAudioFormatChanged -= OnSessionAudioFormatChanged;
        handler.SessionAudioSampleReady -= OnSessionAudioSampleReady;
    }

    /// <summary>Says the state out loud from the session's own fields, raising <see
    /// cref="StateChanged"/> when it has moved on. An AirPlaySession has no state event of
    /// its own, so the host polls this from its half-second UI tick; last-reported state is
    /// remembered so the event only fires on a real change.</summary>
    private MirrorSourceState _reportedState = MirrorSourceState.Stopped;

    public MirrorSourceStateChangedEventArgs Describe()
    {
        var state = State;
        if (state == _reportedState) return new MirrorSourceStateChangedEventArgs(state, Device);

        _reportedState = state;
        var args = new MirrorSourceStateChangedEventArgs(state, Device);
        StateChanged?.Invoke(this, args);
        return args;
    }

    private void OnSessionVideoFormatChanged(object? sender, (AirPlaySession Session, VideoFormat Format) e)
    {
        if (!ReferenceEquals(e.Session, Session)) return;
        VideoFormatChanged?.Invoke(this, e.Format);
    }

    private void OnSessionVideoSampleReady(object? sender, (AirPlaySession Session, MediaSample Sample) e)
    {
        if (!ReferenceEquals(e.Session, Session)) return;
        VideoSampleReady?.Invoke(this, e.Sample);
    }

    private void OnSessionAudioFormatChanged(object? sender, (AirPlaySession Session, AudioFormat Format) e)
    {
        if (!ReferenceEquals(e.Session, Session)) return;
        AudioFormatChanged?.Invoke(this, e.Format);
    }

    private void OnSessionAudioSampleReady(object? sender, (AirPlaySession Session, MediaSample Sample) e)
    {
        if (!ReferenceEquals(e.Session, Session)) return;
        AudioSampleReady?.Invoke(this, e.Sample);
    }

    /// <summary>A session is started and stopped by its control connection, not by the
    /// observer; this adapter has nothing of its own to start.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>Ends this sender's session from this side - the same Disconnect the
    /// single-session path offers. The adapter stays usable; only the phone's mirroring
    /// stops.</summary>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _log.Info($"ending the session from {Session.RemoteEndPoint} at the host's request");
        Session.Disconnect();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
