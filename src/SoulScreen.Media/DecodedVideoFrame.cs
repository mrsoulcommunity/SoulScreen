using System;
using System.Buffers;
using System.Diagnostics;

namespace SoulScreen.Media;

/// <summary>
/// One decoded picture in BGRA8888, the layout WPF and Direct2D both take without further
/// conversion.
/// <para>
/// The pixel buffer is rented from a pool, so a consumer must dispose the frame once it has
/// uploaded or copied the pixels. At 1080p60 that is roughly half a gigabyte per second of
/// allocation avoided.
/// </para>
/// </summary>
public sealed class DecodedVideoFrame : IDisposable
{
    private byte[]? _pixels;

    private DecodedVideoFrame(byte[] pixels, int width, int height, int stride, long timestampUs)
    {
        _pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
        TimestampUs = timestampUs;
        // A Stopwatch reading, so the latency measure on the render hot path is a
        // tick subtraction instead of a DateTime.UtcNow call every frame.
        DecodedAtTicks = Stopwatch.GetTimestamp();
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Bytes per row, which may exceed <c>Width * 4</c> for alignment.</summary>
    public int Stride { get; }

    /// <summary>Presentation timestamp in microseconds on the sender's clock.</summary>
    public long TimestampUs { get; }

    /// <summary>When the frame finished decoding, as wall-clock time. Kept for logs and
    /// diagnostics; the render path measures latency from <see cref="DecodedAtTicks"/>,
    /// which is a far cheaper clock to read on every frame.</summary>
    public DateTime DecodedAtUtc { get; } = DateTime.UtcNow;

    /// <summary>The matching <see cref="Stopwatch.GetTimestamp"/> reading. Used by the
    /// presentation hot path to compute end-to-end latency without paying for a
    /// <see cref="DateTime.UtcNow"/> syscall on every composition pass.</summary>
    public long DecodedAtTicks { get; }

    public ReadOnlySpan<byte> Pixels => _pixels is null
        ? throw new ObjectDisposedException(nameof(DecodedVideoFrame))
        : _pixels.AsSpan(0, Stride * Height);

    /// <summary>The backing array, for APIs that need one rather than a span.</summary>
    public byte[] Buffer => _pixels ?? throw new ObjectDisposedException(nameof(DecodedVideoFrame));

    internal static DecodedVideoFrame Rent(int width, int height, int stride, long timestampUs)
    {
        var pixels = ArrayPool<byte>.Shared.Rent(stride * height);
        return new DecodedVideoFrame(pixels, width, height, stride, timestampUs);
    }

    public void Dispose()
    {
        var pixels = Interlocked.Exchange(ref _pixels, null);
        if (pixels is not null) ArrayPool<byte>.Shared.Return(pixels);
    }

    public override string ToString() => $"{Width}x{Height} @ {TimestampUs / 1000.0:0.#} ms";
}
