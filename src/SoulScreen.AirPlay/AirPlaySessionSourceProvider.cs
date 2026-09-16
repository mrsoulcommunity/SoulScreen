using SoulScreen.AirPlay.Rtsp;
using SoulScreen.Core.Logging;
using SoulScreen.Core.Media;
using SoulScreen.Core.Sources;

namespace SoulScreen.AirPlay;

/// <summary>
/// Turns each live <see cref="AirPlaySession"/> the request handler reports into an
/// <see cref="AirPlaySessionSource"/>, and keeps the two lists in step, so a host can put
/// every concurrent sender on its own tile without touching the single-session path.
/// <para>
/// Sessions arrive and leave on socket threads while the host reads the list on the UI
/// thread, so every mutation is locked. A source is created the moment its session appears
/// and unwired exactly once when the session's connection closes - whether that close was a
/// TEARDOWN, an abrupt drop, or the receiver shutting down.
/// </para>
/// </summary>
public sealed class AirPlaySessionSourceProvider : IDisposable
{
    private readonly ILogger _log = Log.For("sessions");
    private readonly AirPlayRequestHandler _handler;
    private readonly Dictionary<AirPlaySession, AirPlaySessionSource> _sources = [];
    private readonly object _gate = new();

    /// <summary>Whether the handler's per-session events have already been subscribed.
    /// Subscribing twice would deliver every sample twice.</summary>
    private bool _handlerWired;

    /// <summary>The host caps the grid, so the provider caps the sources: a session beyond
    /// the cap still mirrors - the phone knows no different - but it is not offered a tile.</summary>
    private int _maximumSources = 4;

    /// <summary>Highest number of concurrent sessions offered to the host, 1 to 8.</summary>
    public int MaximumSources
    {
        get => _maximumSources;
        set
        {
            if (value < 1) value = 1;
            if (value > 8) value = 8;
            _maximumSources = value;
        }
    }

    public AirPlaySessionSourceProvider(AirPlayRequestHandler handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));

        lock (_gate)
        {
            if (_handlerWired) return;
            _handler.SessionVideoFormatChanged += OnSessionVideoFormatChanged;
            _handler.SessionVideoSampleReady += OnSessionVideoSampleReady;
            _handler.SessionAudioFormatChanged += OnSessionAudioFormatChanged;
            _handler.SessionAudioSampleReady += OnSessionAudioSampleReady;
            _handlerWired = true;
        }
    }

    /// <summary>Every live session source, oldest sender first. The list is a snapshot;
    /// callers see a coherent set however the sockets move underneath.</summary>
    public IReadOnlyList<AirPlaySessionSource> Sources
    {
        get
        {
            lock (_gate)
            {
                var list = new List<AirPlaySessionSource>(_sources.Count);
                foreach (var pair in _sources) list.Add(pair.Value);
                list.Sort(static (a, b) => a.Session.StartedAtUtc.CompareTo(b.Session.StartedAtUtc));
                return list;
            }
        }
    }

    /// <summary>How many sessions are live right now, offered or not.</summary>
    public int Count
    {
        get { lock (_gate) return _sources.Count; }
    }

    /// <summary>The source for one session, or null when the session has none - beyond the
    /// cap, or already gone.</summary>
    public AirPlaySessionSource? SourceOf(AirPlaySession session)
    {
        lock (_gate) return _sources.TryGetValue(session, out var source) ? source : null;
    }

    /// <summary>Snapshots the handler's session list and reconciles: creating a source for
    /// each session that has none, unwiring each whose session is gone. The receiver calls
    /// this when a session starts or ends; a host may also call it from a periodic tick to
    /// catch sessions whose life never touched either event.</summary>
    public void Reconcile()
    {
        var live = _handler.Sessions;

        lock (_gate)
        {
            foreach (var session in live)
            {
                if (_sources.ContainsKey(session)) continue;
                if (_sources.Count >= _maximumSources) continue;

                var source = new AirPlaySessionSource(session, RoundTripMillisecondsOf);
                source.Wire(_handler);
                _sources[session] = source;
                _log.Info($"source added for {session.Device?.ToString() ?? session.RemoteEndPoint.ToString()}"
                          + $" ({_sources.Count} of {_maximumSources})");
            }

            // A session the handler no longer lists is gone, however its socket closed.
            List<AirPlaySessionSource> dead = [];
            foreach (var pair in _sources)
                if (!live.Contains(pair.Key))
                {
                    pair.Value.Unwire(_handler);
                    dead.Add(pair.Value);
                }

            foreach (var source in dead) _sources.Remove(source.Session);
            if (dead.Count > 0) _log.Info($"{dead.Count} source(s) removed; {_sources.Count} remain");
        }
    }

    /// <summary>The handler's per-session round-trip lookup, resolved lazily so this class
    /// does not reach into the handler's session bookkeeping at construction.</summary>
    private double? RoundTripMillisecondsOf(AirPlaySession session)
    {
        var timing = session.Timing;
        return timing is { IsSynchronised: true } ? timing.RoundTripMicroseconds / 1000.0 : null;
    }

    // The provider unwires its own subscriptions once; the per-source subscriptions are
    // unwired by Reconcile as each source goes. The sample events fire on every frame, so
    // their handlers only reconcile when the session is genuinely unknown to the provider -
    // a full reconcile on a hot path would allocate and sort sixty times a second.

    private void OnSessionVideoFormatChanged(object? sender, (AirPlaySession Session, VideoFormat Format) e) => Reconcile();

    private void OnSessionVideoSampleReady(object? sender, (AirPlaySession Session, MediaSample Sample) e)
    {
        if (NeedsNoReconcile(e.Session)) return;
        Reconcile();
    }

    private void OnSessionAudioFormatChanged(object? sender, (AirPlaySession Session, AudioFormat Format) e) => Reconcile();

    private void OnSessionAudioSampleReady(object? sender, (AirPlaySession Session, MediaSample Sample) e)
    {
        if (NeedsNoReconcile(e.Session)) return;
        Reconcile();
    }

    /// <summary>Whether a per-sample event needs no reconcile at all: the session already
    /// has a source, or the grid is full and no source could be made for it. The check runs
    /// under the same gate as Reconcile itself, so its answer cannot go stale in between.</summary>
    private bool NeedsNoReconcile(AirPlaySession session)
    {
        lock (_gate)
        {
            return _sources.ContainsKey(session) || _sources.Count >= _maximumSources;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_handlerWired)
            {
                _handler.SessionVideoFormatChanged -= OnSessionVideoFormatChanged;
                _handler.SessionVideoSampleReady -= OnSessionVideoSampleReady;
                _handler.SessionAudioFormatChanged -= OnSessionAudioFormatChanged;
                _handler.SessionAudioSampleReady -= OnSessionAudioSampleReady;
                _handlerWired = false;
            }

            foreach (var pair in _sources) pair.Value.Unwire(_handler);
            _sources.Clear();
        }
    }
}
