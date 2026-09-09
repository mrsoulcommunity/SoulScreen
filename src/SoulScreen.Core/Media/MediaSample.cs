using System.Buffers;

namespace SoulScreen.Core.Media;

/// <summary>
/// A pooled chunk of encoded media handed from a source to the render pipeline.
/// The payload lives in an <see cref="ArrayPool{T}"/> buffer, so a consumer must
/// dispose the sample once it has copied or submitted the bytes.
/// </summary>
public sealed class MediaSample : IDisposable
{
    private byte[]? _buffer;

    private MediaSample(byte[] buffer, int length, long timestampUs, bool isKeyFrame)
    {
        _buffer = buffer;
        Length = length;
        TimestampUs = timestampUs;
        IsKeyFrame = isKeyFrame;
    }

    /// <summary>Encoded payload. Only the first <see cref="Length"/> bytes are meaningful.</summary>
    public ReadOnlySpan<byte> Span => _buffer is null
        ? throw new ObjectDisposedException(nameof(MediaSample))
        : _buffer.AsSpan(0, Length);

    public ReadOnlyMemory<byte> Memory => _buffer is null
        ? throw new ObjectDisposedException(nameof(MediaSample))
        : _buffer.AsMemory(0, Length);

    public int Length { get; }

    /// <summary>Presentation timestamp in microseconds on the source's own clock.</summary>
    public long TimestampUs { get; }

    /// <summary>True when this video sample can be decoded without any preceding frame.</summary>
    public bool IsKeyFrame { get; }

    /// <summary>Copies <paramref name="payload"/> into a pooled buffer.</summary>
    public static MediaSample Copy(ReadOnlySpan<byte> payload, long timestampUs, bool isKeyFrame = false)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(payload.Length, 1));
        payload.CopyTo(buffer);
        return new MediaSample(buffer, payload.Length, timestampUs, isKeyFrame);
    }

    /// <summary>
    /// Wraps a buffer that was already rented from <see cref="ArrayPool{T}.Shared"/>.
    /// Ownership transfers to the sample - the caller must not touch the array afterwards.
    /// </summary>
    public static MediaSample AdoptPooled(byte[] pooledBuffer, int length, long timestampUs, bool isKeyFrame = false)
        => new(pooledBuffer, length, timestampUs, isKeyFrame);

    public byte[] ToArray() => Span.ToArray();

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
            ArrayPool<byte>.Shared.Return(buffer);
    }
}
