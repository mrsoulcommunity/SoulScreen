using SoulScreen.AirPlay.Discovery;
using SoulScreen.AirPlay.FairPlay;
using SoulScreen.AirPlay.Pairing;
using SoulScreen.AirPlay.Rtsp;
using SoulScreen.Core.Logging;
using SoulScreen.Core.Media;
using SoulScreen.Core.Sources;

namespace SoulScreen.AirPlay;

/// <summary>
/// A wireless AirPlay mirroring receiver: advertises itself over mDNS, serves the RTSP
/// control channel, and surfaces whatever an iPhone sends as codec-agnostic media samples.
/// </summary>
public sealed class AirPlayReceiver : IMirrorSource
{
    private readonly ILogger _log = Log.For("receiver");
    private readonly AirPlayOptions _options;
    private readonly DeviceIdentity _identity;
    private readonly AirPlayRequestHandler _handler;
    private readonly RtspServer _rtsp;
    private readonly MulticastDnsResponder _responder;

    private CancellationTokenSource? _cts;
    private MirrorSourceState _state = MirrorSourceState.Stopped;

    public AirPlayReceiver(AirPlayOptions? options = null)
    {
        _options = options ?? new AirPlayOptions();
        _identity = DeviceIdentity.LoadOrCreate(_options.StateDirectory);

        _handler = new AirPlayRequestHandler(_options, _identity);
        _rtsp = new RtspServer(_handler, $"AirTunes/{_options.SourceVersion}") { Trace = _options.TraceProtocol };
        _responder = new MulticastDnsResponder { HostName = _options.DeviceName };

        _handler.DeviceIdentified += (_, device) => SetState(MirrorSourceState.Connecting, device);
        _handler.SessionStarted += (_, session) => SetState(MirrorSourceState.Streaming, session.Device);
        _handler.SessionEnded += (_, _) =>
        {
            Device = null;
            SetState(MirrorSourceState.Ready);
        };
        _handler.VideoFormatChanged += (_, format) => VideoFormatChanged?.Invoke(this, format);
        _handler.VideoSampleReady += (_, sample) => VideoSampleReady?.Invoke(this, sample);
        _handler.AudioFormatChanged += (_, format) => AudioFormatChanged?.Invoke(this, format);
        _handler.AudioSampleReady += (_, sample) => AudioSampleReady?.Invoke(this, sample);
    }

    public string Id => "airplay";

    public string DisplayName => "AirPlay (wireless)";

    public MirrorSourceState State => _state;

    public SourceDeviceInfo? Device { get; private set; }

    public AirPlayOptions Options => _options;

    public DeviceIdentity Identity => _identity;

    /// <summary>Name the receiver is advertising, which is what shows up on the phone.</summary>
    public string AdvertisedName => _options.DeviceName;

    public event EventHandler<MirrorSourceStateChangedEventArgs>? StateChanged;
    public event EventHandler<VideoFormat>? VideoFormatChanged;
    public event EventHandler<MediaSample>? VideoSampleReady;
    public event EventHandler<AudioFormat>? AudioFormatChanged;
    public event EventHandler<MediaSample>? AudioSampleReady;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_cts is not null) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _handler.AttachShutdownToken(_cts.Token);

        try
        {
            await _rtsp.StartAsync(_options.Port, _cts.Token).ConfigureAwait(false);

            var (airplay, raop) = AirPlayAdvertisement.Build(_options, _identity);

            // The control channel serves the same TXT records over /info, so it has to see
            // the very profiles being advertised rather than build its own copy.
            _handler.AirPlayService = airplay;
            _handler.RaopService = raop;

            _responder.ClearServices();
            _responder.HostName = _options.DeviceName;
            _responder.Advertise(airplay);
            _responder.Advertise(raop);
            await _responder.StartAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            SetState(MirrorSourceState.Faulted, message: ex.Message);
            throw;
        }

        if (!NativeFairPlay.IsAvailable)
        {
            // Not fatal here: the receiver still appears on the phone, and failing at
            // /fp-setup with a clear log beats refusing to start at all.
            _log.Warn($"FairPlay helper missing - mirroring will fail at the handshake. {NativeFairPlay.UnavailableReason}");
        }

        SetState(MirrorSourceState.Ready);
        _log.Info($"receiver \"{_options.DeviceName}\" ready on port {_rtsp.Port}");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return;

        await cts.CancelAsync().ConfigureAwait(false);
        await _responder.StopAsync().ConfigureAwait(false);
        await _rtsp.StopAsync().ConfigureAwait(false);
        cts.Dispose();

        Device = null;
        SetState(MirrorSourceState.Stopped);
    }

    /// <summary>Re-publishes the advertisement, which is how a renamed receiver or a
    /// changed network shows up again without a restart.</summary>
    public void Reannounce() => _responder.Announce();

    private void SetState(MirrorSourceState state, SourceDeviceInfo? device = null, string? message = null)
    {
        if (device is not null) Device = device;
        if (_state == state && device is null && message is null) return;

        _state = state;
        StateChanged?.Invoke(this, new MirrorSourceStateChangedEventArgs(state, Device, message));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _rtsp.DisposeAsync().ConfigureAwait(false);
        await _responder.DisposeAsync().ConfigureAwait(false);
    }
}
