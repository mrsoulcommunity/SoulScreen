using System.Net;
using System.Security.Cryptography;
using SoulScreen.AirPlay.FairPlay;
using SoulScreen.AirPlay.Pairing;
using SoulScreen.AirPlay.Streams;
using SoulScreen.Core.Logging;
using SoulScreen.Core.Sources;

namespace SoulScreen.AirPlay;

/// <summary>
/// Everything one sender's control connection accumulates: the pairing handshake, the
/// FairPlay exchange, the unwrapped stream key and the media channels that key opens.
/// <para>
/// A session is created lazily on the first request of a connection and torn down when the
/// socket closes, so a phone that disconnects abruptly cannot leave streams behind.
/// </para>
/// </summary>
public sealed class AirPlaySession : IAsyncDisposable
{
    private readonly ILogger _log = Log.For("session");

    public AirPlaySession(DeviceIdentity identity, IPEndPoint remoteEndPoint)
    {
        Identity = identity;
        RemoteEndPoint = remoteEndPoint;
        PairVerify = new PairVerifySession(identity);
    }

    public DeviceIdentity Identity { get; }

    public IPEndPoint RemoteEndPoint { get; }

    public PairVerifySession PairVerify { get; }

    /// <summary>Created on the first /fp-setup request; absent until then.</summary>
    public FairPlaySession? FairPlay { get; private set; }

    /// <summary>The 16-byte AES key SETUP delivered, unwrapped by FairPlay.</summary>
    public byte[]? StreamKey { get; private set; }

    /// <summary>The AES IV SETUP delivered, used for the audio channel.</summary>
    public byte[]? StreamIv { get; private set; }

    public MirrorVideoStream? Video { get; private set; }

    public AudioStream? Audio { get; private set; }

    public TimingChannel? Timing { get; private set; }

    /// <summary>Device details from SETUP, once the sender has introduced itself.</summary>
    public SourceDeviceInfo? Device { get; private set; }

    /// <summary>True once video frames are actually arriving.</summary>
    public bool IsStreaming => Video is not null;

    public FairPlaySession EnsureFairPlay() => FairPlay ??= new FairPlaySession();

    public void SetDevice(string? name, string? model)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        Device = new SourceDeviceInfo(name, model, RemoteEndPoint.Address.ToString());
        _log.Info($"sender identified as {Device}");
    }

    /// <summary>Unwraps the "ekey" from SETUP and remembers the IV that came with it.</summary>
    public void SetEncryptionKey(byte[] encryptedKey, byte[]? iv)
    {
        var fairPlay = FairPlay ?? throw new InvalidOperationException(
            "SETUP delivered a key before /fp-setup ran; the sender skipped the FairPlay handshake.");

        StreamKey = fairPlay.DecryptKey(encryptedKey);
        StreamIv = iv;
        _log.Debug("stream key installed");
    }

    public TimingChannel StartTiming(int senderTimingPort, CancellationToken cancellationToken)
    {
        Timing ??= new TimingChannel();
        Timing.Start(RemoteEndPoint.Address, senderTimingPort, cancellationToken);
        return Timing;
    }

    /// <summary>
    /// Opens the mirroring video channel. Requires both a completed pair-verify (for the
    /// ECDH secret) and a FairPlay-unwrapped key, because the stream key is derived from
    /// the two together.
    /// </summary>
    public MirrorVideoStream StartVideo(ulong streamConnectionId, string? dumpDirectory, CancellationToken cancellationToken)
    {
        if (StreamKey is null)
            throw new InvalidOperationException("Cannot start mirroring: SETUP never delivered a stream key.");
        if (PairVerify.SharedSecret is null)
            throw new InvalidOperationException(
                "Cannot start mirroring: pair-verify did not complete, so the stream key cannot be derived.");

        Video = new MirrorVideoStream(StreamKey, PairVerify.SharedSecret, streamConnectionId, dumpDirectory);
        Video.Start(cancellationToken);
        return Video;
    }

    public AudioStream StartAudio(Core.Media.AudioFormat format, string? dumpDirectory, CancellationToken cancellationToken)
    {
        if (StreamKey is null || StreamIv is null)
            throw new InvalidOperationException("Cannot start audio: SETUP never delivered a key and IV.");
        if (PairVerify.SharedSecret is null)
            throw new InvalidOperationException(
                "Cannot start audio: pair-verify did not complete, so the stream key cannot be derived.");

        // The same session binding the video uses. Audio takes the result directly as its
        // AES-CBC key, where video derives a CTR key and IV from it.
        var audioKey = Crypto.AirPlayKeys.SessionKey(StreamKey, PairVerify.SharedSecret);
        try
        {
            Audio = new AudioStream(audioKey, StreamIv, format, dumpDirectory);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(audioKey);
        }

        Audio.Start(cancellationToken);
        return Audio;
    }

    /// <summary>Closes the media channels but keeps the pairing state, which is what a
    /// TEARDOWN naming specific streams asks for.</summary>
    public async Task StopStreamsAsync()
    {
        var video = Video;
        Video = null;
        if (video is not null) await video.DisposeAsync().ConfigureAwait(false);

        var audio = Audio;
        Audio = null;
        if (audio is not null) await audio.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopStreamsAsync().ConfigureAwait(false);

        var timing = Timing;
        Timing = null;
        if (timing is not null) await timing.DisposeAsync().ConfigureAwait(false);

        FairPlay?.Dispose();
        PairVerify.Dispose();

        if (StreamKey is not null) CryptographicOperations.ZeroMemory(StreamKey);
        if (StreamIv is not null) CryptographicOperations.ZeroMemory(StreamIv);
        StreamKey = null;
        StreamIv = null;
    }
}
