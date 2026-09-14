using SoulScreen.AirPlay.FairPlay;
using SoulScreen.AirPlay.Pairing;
using SoulScreen.AirPlay.Plist;
using SoulScreen.AirPlay.Streams;
using SoulScreen.Core.Buffers;
using SoulScreen.Core.Logging;
using SoulScreen.Core.Media;
using SoulScreen.Core.Sources;

namespace SoulScreen.AirPlay.Rtsp;

/// <summary>
/// Implements the AirPlay control protocol on top of <see cref="RtspServer"/>.
/// <para>
/// A mirroring session runs roughly: GET /info, POST /pair-setup, two POSTs to
/// /pair-verify, two POSTs to /fp-setup, a SETUP carrying the wrapped stream key, a second
/// SETUP asking for stream ports, RECORD, then media flows until TEARDOWN.
/// </para>
/// </summary>
public sealed class AirPlayRequestHandler(AirPlayOptions options, DeviceIdentity identity) : IRtspRequestHandler
{
    /// <summary>Stream type for mirrored video.</summary>
    private const int StreamTypeMirrorVideo = 110;

    /// <summary>Stream type for real-time audio over RTP, which is what mirroring sends.</summary>
    private const int StreamTypeRealtimeAudio = 96;

    /// <summary>Stream type for AirPlay 2's buffered audio, used for media playback.</summary>
    private const int StreamTypeBufferedAudio = 103;

    /// <summary>Qualifier iOS sends to ask for the _airplay._tcp TXT record over HTTP.</summary>
    private const string TxtAirPlayKey = "txtAirPlay";

    /// <summary>Qualifier for the _raop._tcp TXT record.</summary>
    private const string TxtRaopKey = "txtRAOP";

    private readonly ILogger _log = Log.For("airplay");
    private CancellationToken _shutdownToken = CancellationToken.None;

    /// <summary>
    /// The advertised services, needed because /info can be asked for their raw TXT records.
    /// Set by the receiver once the advertisement has been built.
    /// </summary>
    public Discovery.ServiceProfile? AirPlayService { get; set; }

    public Discovery.ServiceProfile? RaopService { get; set; }

    public event EventHandler<AirPlaySession>? SessionStarted;
    public event EventHandler<AirPlaySession>? SessionEnded;
    public event EventHandler<SourceDeviceInfo>? DeviceIdentified;
    public event EventHandler<VideoFormat>? VideoFormatChanged;
    public event EventHandler<MediaSample>? VideoSampleReady;
    public event EventHandler<AudioFormat>? AudioFormatChanged;
    public event EventHandler<MediaSample>? AudioSampleReady;

    /// <summary>The session currently streaming, if any.</summary>
    public AirPlaySession? ActiveSession { get; private set; }

    public void AttachShutdownToken(CancellationToken token) => _shutdownToken = token;

    /// <summary>
    /// Drops the session that is currently mirroring, if there is one. Returns false when
    /// nothing was streaming. The connection's own cleanup raises <see cref="SessionEnded"/>.
    /// </summary>
    public bool DisconnectActiveSession()
    {
        var session = ActiveSession;
        if (session is null) return false;
        _log.Info($"disconnecting {session.Device?.Name ?? session.RemoteEndPoint.ToString()} at the receiver's request");
        session.Disconnect();
        return true;
    }

    public async Task<RtspResponse> HandleAsync(RtspRequest request, RtspConnectionContext context, CancellationToken cancellationToken)
    {
        var session = GetOrCreateSession(context);
        var path = request.Path;

        // Some senders still probe the legacy AirPort Express RSA challenge. Answering it
        // needs Apple's leaked private key, which SoulScreen does not carry; modern
        // senders proceed without a response.
        if (request.Headers.ContainsKey("Apple-Challenge"))
            _log.Debug("sender sent Apple-Challenge; continuing without an Apple-Response");

        return (request.Method, path) switch
        {
            ("OPTIONS", _) => HandleOptions(),
            ("GET", "/info") => HandleInfo(request),
            ("POST", "/info") => HandleInfo(request),
            ("POST", "/pair-setup") => HandlePairSetup(),
            ("POST", "/pair-verify") => HandlePairVerify(request, session),
            ("POST", "/fp-setup") => HandleFairPlaySetup(request, session),
            ("POST", "/pair-pin-start") => RtspResponse.Ok(),
            ("POST", "/feedback") => RtspResponse.Ok(),
            ("POST", "/audioMode") => RtspResponse.Ok(),
            ("GET", "/stream.xml") => HandleInfo(request),
            ("SETUP", _) => HandleSetup(request, session, cancellationToken),
            ("RECORD", _) => HandleRecord(session),
            ("SET_PARAMETER", _) => HandleSetParameter(request),
            ("GET_PARAMETER", _) => HandleGetParameter(request),
            ("FLUSH", _) => RtspResponse.Ok(),
            ("TEARDOWN", _) => await HandleTeardownAsync(request, session).ConfigureAwait(false),
            _ => HandleUnknown(request),
        };
    }

    private AirPlaySession GetOrCreateSession(RtspConnectionContext context)
    {
        if (context.Session is AirPlaySession existing) return existing;

        var session = new AirPlaySession(identity, context.RemoteEndPoint);
        session.AttachDisconnect(context.RequestClose);
        context.Session = session;
        return session;
    }

    // ------------------------------------------------------------------ methods

    private static RtspResponse HandleOptions()
    {
        var response = RtspResponse.Ok();
        response.Headers["Public"] =
            "ANNOUNCE, SETUP, RECORD, PAUSE, FLUSH, TEARDOWN, OPTIONS, GET_PARAMETER, SET_PARAMETER, POST, GET, PUT";
        return response;
    }

    private RtspResponse HandleUnknown(RtspRequest request)
    {
        _log.Warn($"unhandled request {request.Method} {request.Path}" +
                  (request.Body.Length > 0 ? $"\n{Hex.Dump(request.Body, 128)}" : ""));
        return RtspResponse.Ok();
    }

    /// <summary>
    /// Describes the receiver. The sender reads this before it commits to mirroring, and
    /// the "displays" entry in particular decides what resolution it will encode at.
    /// <para>
    /// There are two shapes of answer. iOS opens every session by asking for a specific
    /// qualifier - "txtAirPlay" or "txtRAOP" - and for those it wants nothing but that
    /// service's raw DNS-SD TXT record back. Answering the full device dictionary instead
    /// looks like a malformed reply: the phone closes the connection and never proceeds to
    /// pairing, which presents as a receiver that appears in Control Center and then does
    /// nothing when tapped. Only a request with no qualifier gets the full description.
    /// </para>
    /// </summary>
    private RtspResponse HandleInfo(RtspRequest request)
    {
        if (TryReadQualifier(request) is { } qualifier)
        {
            var response = new PlistDictionary();

            if (qualifier.Equals(TxtAirPlayKey, StringComparison.OrdinalIgnoreCase) && AirPlayService is not null)
                response[TxtAirPlayKey] = AirPlayService.EncodeTxtRecordData();
            else if (qualifier.Equals(TxtRaopKey, StringComparison.OrdinalIgnoreCase) && RaopService is not null)
                response[TxtRaopKey] = RaopService.EncodeTxtRecordData();
            else
                _log.Warn($"/info asked for an unknown qualifier '{qualifier}'");

            _log.Debug($"/info qualifier '{qualifier}' answered with {response.Count} entr(ies)");
            return RtspResponse.BinaryPlistBody(response);
        }

        var info = new PlistDictionary
        {
            ["deviceID"] = identity.DeviceId,
            ["macAddress"] = identity.DeviceId,
            ["pk"] = identity.Ed25519PublicKey,
            ["features"] = (long)options.Features,
            ["name"] = options.DeviceName,
            ["model"] = options.Model,
            ["pi"] = identity.PublicIdentifier.ToString(),
            ["protovers"] = "1.1",
            ["sourceVersion"] = options.SourceVersion,
            ["vv"] = 2,
            // 68 = receiver available and audio-capable, matching what an Apple TV reports.
            ["statusFlags"] = 68,
            ["keepAliveLowPower"] = 1,
            ["keepAliveSendStatsAsBody"] = true,
            ["initialVolume"] = 0.0,
        };

        var display = new PlistDictionary
        {
            ["uuid"] = identity.PublicIdentifier.ToString(),
            ["widthPhysical"] = 0,
            ["heightPhysical"] = 0,
            ["width"] = options.DisplayWidth,
            ["height"] = options.DisplayHeight,
            ["widthPixels"] = options.DisplayWidth,
            ["heightPixels"] = options.DisplayHeight,
            ["rotation"] = false,
            // Expressed as a frame duration in seconds, not a frequency.
            ["refreshRate"] = 1.0 / Math.Max(options.DisplayRefreshRate, 1),
            ["maxFPS"] = options.DisplayRefreshRate,
            ["overscanned"] = false,
            ["features"] = 14,
        };
        info["displays"] = new PlistArray { display };

        if (options.EnableAudio)
        {
            info["audioFormats"] = new PlistArray
            {
                AudioCapability(100),
                AudioCapability(101),
            };
            info["audioLatencies"] = new PlistArray
            {
                AudioLatency(100),
                AudioLatency(101),
            };
        }

        return RtspResponse.BinaryPlistBody(info);

        // 0x3fffffc advertises every PCM/ALAC/AAC variant a receiver can be asked for.
        static PlistDictionary AudioCapability(int type) => new()
        {
            ["type"] = type,
            ["audioInputFormats"] = 0x3fffffc,
            ["audioOutputFormats"] = 0x3fffffc,
        };

        static PlistDictionary AudioLatency(int type) => new()
        {
            ["type"] = type,
            ["audioType"] = "default",
            ["inputLatencyMicros"] = 0,
            ["outputLatencyMicros"] = 0,
        };
    }

    /// <summary>
    /// Reads the single qualifier string from an /info request body, or null when the
    /// request carries no property list - which is how iOS asks for the full description.
    /// </summary>
    private static string? TryReadQualifier(RtspRequest request)
    {
        if (request.Body.Length == 0) return null;
        if (request.BodyAsPlist() is not { } body) return null;
        if (body.GetArray("qualifier") is not { Count: > 0 } qualifier) return null;
        return qualifier[0] is PlistString name ? name.Value : null;
    }

    /// <summary>Legacy pair-setup: the sender just wants our long-term public key.</summary>
    private RtspResponse HandlePairSetup()
    {
        _log.Debug("pair-setup: returning the receiver public key");
        return RtspResponse.Binary(identity.Ed25519PublicKey);
    }

    private RtspResponse HandlePairVerify(RtspRequest request, AirPlaySession session)
    {
        try
        {
            var response = session.PairVerify.Handle(request.Body);
            return RtspResponse.Binary(response);
        }
        catch (InvalidDataException ex)
        {
            _log.Warn($"pair-verify failed: {ex.Message}");
            return RtspResponse.Forbidden();
        }
    }

    private RtspResponse HandleFairPlaySetup(RtspRequest request, AirPlaySession session)
    {
        try
        {
            var fairPlay = session.EnsureFairPlay();
            return RtspResponse.Binary(fairPlay.HandleRequest(request.Body));
        }
        catch (FairPlayUnavailableException ex)
        {
            _log.Error(ex.Message);
            return RtspResponse.Forbidden();
        }
        catch (InvalidDataException ex)
        {
            _log.Warn($"fp-setup failed: {ex.Message}\n{Hex.Dump(request.Body, 64)}");
            return RtspResponse.Forbidden();
        }
    }

    /// <summary>
    /// SETUP arrives twice. The first carries the session description and the wrapped
    /// stream key; the second asks for ports for each media stream.
    /// </summary>
    private RtspResponse HandleSetup(RtspRequest request, AirPlaySession session, CancellationToken cancellationToken)
    {
        var body = request.BodyAsPlist();
        if (body is null)
        {
            _log.Warn($"SETUP body was not a property list\n{Hex.Dump(request.Body, 128)}");
            return RtspResponse.BadRequest();
        }

        var result = new PlistDictionary();

        if (body.GetData("ekey") is { } encryptedKey)
        {
            session.SetDevice(body.GetString("name"), body.GetString("model"));
            if (session.Device is { } device) DeviceIdentified?.Invoke(this, device);

            try
            {
                session.SetEncryptionKey(encryptedKey, body.GetData("eiv"));
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
            {
                _log.Error($"could not unwrap the stream key: {ex.Message}");
                return RtspResponse.Forbidden();
            }

            var timingPort = (int)(body.GetInteger("timingPort") ?? 0);
            var timing = session.StartTiming(timingPort, Linked(cancellationToken));

            result["timingPort"] = timing.Port;
            // Mirroring does not use the event channel, and reporting 0 keeps the sender
            // from opening a connection we would only have to drain.
            result["eventPort"] = 0;
            return RtspResponse.BinaryPlistBody(result);
        }

        if (body.GetArray("streams") is { } streams)
        {
            var responses = new PlistArray();
            foreach (var entry in streams.OfType<PlistDictionary>())
            {
                var descriptor = SetupStream(entry, session, cancellationToken);
                if (descriptor is not null) responses.Add(descriptor);
            }

            if (responses.Count == 0) return RtspResponse.BadRequest();

            result["streams"] = responses;
            return RtspResponse.BinaryPlistBody(result);
        }

        _log.Warn($"SETUP had neither a key nor streams: {body}");
        return RtspResponse.BinaryPlistBody(result);
    }

    private PlistDictionary? SetupStream(PlistDictionary stream, AirPlaySession session, CancellationToken cancellationToken)
    {
        var type = (int)(stream.GetInteger("type") ?? -1);

        switch (type)
        {
            case StreamTypeMirrorVideo:
            {
                var connectionId = unchecked((ulong)(stream.GetInteger("streamConnectionID") ?? 0));
                _log.Info($"starting mirroring, streamConnectionID={connectionId}");

                MirrorVideoStream video;
                try
                {
                    video = session.StartVideo(connectionId, options.DumpDirectory, Linked(cancellationToken));
                }
                catch (InvalidOperationException ex)
                {
                    _log.Error($"could not start mirroring: {ex.Message}");
                    return null;
                }

                video.FormatChanged += (_, format) => VideoFormatChanged?.Invoke(this, format);
                video.SampleReady += (_, sample) => VideoSampleReady?.Invoke(this, sample);
                video.Ended += (_, _) => _log.Info("mirroring data channel ended");

                ActiveSession = session;
                SessionStarted?.Invoke(this, session);

                return new PlistDictionary { ["type"] = StreamTypeMirrorVideo, ["dataPort"] = video.Port };
            }

            case StreamTypeBufferedAudio:
            case StreamTypeRealtimeAudio:
            {
                if (!options.EnableAudio)
                {
                    _log.Info("audio stream requested but audio is disabled; declining");
                    return null;
                }

                var format = ReadAudioFormat(stream);
                AudioStream audio;
                try
                {
                    audio = session.StartAudio(format, options.DumpDirectory, Linked(cancellationToken));
                }
                catch (InvalidOperationException ex)
                {
                    _log.Error($"could not start audio: {ex.Message}");
                    return null;
                }

                audio.SampleReady += (_, sample) => AudioSampleReady?.Invoke(this, sample);
                AudioFormatChanged?.Invoke(this, format);

                return new PlistDictionary
                {
                    ["type"] = type,
                    ["dataPort"] = audio.DataPort,
                    ["controlPort"] = audio.ControlPort,
                    ["audioBufferSize"] = 8 * 1024 * 1024,
                };
            }

            default:
                _log.Warn($"declining unsupported stream type {type}: {stream}");
                return null;
        }
    }

    /// <summary>Maps the sender's compression-type field onto a codec we can name.</summary>
    private static AudioFormat ReadAudioFormat(PlistDictionary stream)
    {
        var compressionType = (int)(stream.GetInteger("ct") ?? 0);
        var codec = compressionType switch
        {
            1 => AudioCodec.Pcm16,
            2 => AudioCodec.Alac,
            4 => AudioCodec.AacLc,
            8 => AudioCodec.AacEld,
            _ => AudioCodec.AacEld, // what mirroring uses when it does not say
        };

        // Every AirPlay audio format standardised so far runs at 44.1 kHz stereo.
        var framesPerPacket = (int)(stream.GetInteger("spf") ?? (codec == AudioCodec.AacEld ? 480 : 1024));
        return new AudioFormat(codec, 44100, 2, framesPerPacket, []);
    }

    private RtspResponse HandleRecord(AirPlaySession session)
    {
        _log.Info("RECORD: session is live");
        var response = RtspResponse.Ok();
        response.Headers["Audio-Latency"] = "11025";
        return response;
    }

    private RtspResponse HandleSetParameter(RtspRequest request)
    {
        // Volume and playback progress arrive here as text/parameters; both are advisory
        // for a mirroring receiver.
        if (request.ContentType?.StartsWith("text/parameters", StringComparison.OrdinalIgnoreCase) == true)
        {
            var text = System.Text.Encoding.UTF8.GetString(request.Body).Trim();
            if (text.Length > 0) _log.Debug($"SET_PARAMETER {text.ReplaceLineEndings(" ")}");
        }
        return RtspResponse.Ok();
    }

    private static RtspResponse HandleGetParameter(RtspRequest request)
    {
        var query = System.Text.Encoding.UTF8.GetString(request.Body);
        return query.Contains("volume", StringComparison.OrdinalIgnoreCase)
            ? RtspResponse.Text("volume: 0.000000\r\n")
            : RtspResponse.Ok();
    }

    private async Task<RtspResponse> HandleTeardownAsync(RtspRequest request, AirPlaySession session)
    {
        var body = request.BodyAsPlist();
        var streams = body?.GetArray("streams");

        if (streams is null)
        {
            _log.Info("TEARDOWN: closing the whole session");
            await session.StopStreamsAsync().ConfigureAwait(false);
            if (ReferenceEquals(ActiveSession, session))
            {
                ActiveSession = null;
                SessionEnded?.Invoke(this, session);
            }
        }
        else
        {
            // A partial teardown names the streams to drop, and only those are closed; the
            // control connection stays up. iOS drops its audio stream on its own part way
            // through a session and sets up a new one later - and closing everything here
            // took the picture down with it every time it did.
            foreach (var entry in streams.OfType<PlistDictionary>())
            {
                var type = (int)(entry.GetInteger("type") ?? -1);
                _log.Info($"TEARDOWN: closing stream type {type}");

                switch (type)
                {
                    case StreamTypeMirrorVideo:
                        await session.StopVideoAsync().ConfigureAwait(false);
                        break;
                    case StreamTypeRealtimeAudio:
                    case StreamTypeBufferedAudio:
                        await session.StopAudioAsync().ConfigureAwait(false);
                        break;
                    default:
                        _log.Warn($"TEARDOWN named stream type {type}, which this receiver does not open");
                        break;
                }
            }
        }

        return RtspResponse.Ok();
    }

    public void OnConnectionClosed(RtspConnectionContext context)
    {
        if (context.Session is not AirPlaySession session) return;
        context.Session = null;

        var wasActive = ReferenceEquals(ActiveSession, session);
        if (wasActive) ActiveSession = null;

        // Disposal touches sockets and native handles, so keep it off the connection thread.
        _ = Task.Run(async () =>
        {
            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _log.Warn("session teardown failed", ex); }
            if (wasActive) SessionEnded?.Invoke(this, session);
        });
    }

    /// <summary>Ties a request-scoped token to the receiver's lifetime so a stream outlives
    /// the request that created it but not the receiver.</summary>
    private CancellationToken Linked(CancellationToken requestToken)
        => _shutdownToken.CanBeCanceled ? _shutdownToken : requestToken;
}
