using SoulScreen.Core.Media;

namespace SoulScreen.Core.Sources;

/// <summary>
/// A transport that can deliver an iOS device's screen to this machine.
/// Implemented once for AirPlay (wireless) and once for the iOS USB/QuickTime link,
/// so the UI and render pipeline stay transport-agnostic.
/// </summary>
public interface IMirrorSource : IAsyncDisposable
{
    /// <summary>Short id used in settings and logs, e.g. "airplay" or "usb".</summary>
    string Id { get; }

    /// <summary>Human readable name for the UI.</summary>
    string DisplayName { get; }

    MirrorSourceState State { get; }

    /// <summary>The device currently attached, if any.</summary>
    SourceDeviceInfo? Device { get; }

    event EventHandler<MirrorSourceStateChangedEventArgs>? StateChanged;

    /// <summary>Raised when the codec configuration is known or changes (e.g. rotation).</summary>
    event EventHandler<VideoFormat>? VideoFormatChanged;

    /// <summary>
    /// Raised for every encoded video frame.
    /// <para>
    /// The sample belongs to the source and is recycled as soon as the handler returns, so
    /// a handler that wants to keep the bytes must copy them. Handlers run on the source's
    /// receive thread and should hand off rather than do real work inline.
    /// </para>
    /// </summary>
    event EventHandler<MediaSample>? VideoSampleReady;

    event EventHandler<AudioFormat>? AudioFormatChanged;

    /// <summary>Raised for every encoded audio packet. Same ownership rule as
    /// <see cref="VideoSampleReady"/>: valid only for the duration of the call.</summary>
    event EventHandler<MediaSample>? AudioSampleReady;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

public sealed class MirrorSourceStateChangedEventArgs(
    MirrorSourceState state,
    SourceDeviceInfo? device = null,
    string? message = null) : EventArgs
{
    public MirrorSourceState State { get; } = state;
    public SourceDeviceInfo? Device { get; } = device;
    /// <summary>Detail for the UI - the error text when <see cref="State"/> is Faulted.</summary>
    public string? Message { get; } = message;
}
