using System.Buffers.Binary;

namespace SoulScreen.Media;

/// <summary>
/// Small edits to interleaved 16-bit little-endian PCM that change its length or its level
/// without leaving a step in the waveform - the tools <see cref="AudioPipeline"/> keeps the
/// sound in time with.
/// </summary>
internal static class PcmSplice
{
    /// <summary>
    /// Removes <paramref name="frames"/> frames, spread evenly through the audio. Each one is
    /// folded into the frame before it rather than simply cut out, so the waveform bends at
    /// the splice instead of jumping.
    /// </summary>
    /// <returns>The new length in bytes.</returns>
    public static int Shrink(byte[] pcm, int length, int channels, int frames)
    {
        var align = channels * sizeof(short);
        var total = length / align;
        frames = Math.Min(frames, total / 2);
        if (frames <= 0) return length;

        var write = 0;
        var removed = 0;
        var nextRemoval = SplicePoint(total, frames, 0);

        for (var read = 0; read < total; read++)
        {
            if (removed < frames && read == nextRemoval)
            {
                for (var channel = 0; channel < channels; channel++)
                {
                    var kept = (write - 1) * align + channel * sizeof(short);
                    var dropped = read * align + channel * sizeof(short);
                    WriteSample(pcm, kept, (ReadSample(pcm, kept) + ReadSample(pcm, dropped)) / 2);
                }

                removed++;
                if (removed < frames) nextRemoval = SplicePoint(total, frames, removed);
                continue;
            }

            if (write != read) Buffer.BlockCopy(pcm, read * align, pcm, write * align, align);
            write++;
        }

        return write * align;
    }

    /// <summary>
    /// Inserts <paramref name="frames"/> frames, spread evenly through the audio, each one
    /// halfway between the two it is placed between. <paramref name="pcm"/> must have room
    /// for them after <paramref name="length"/>.
    /// </summary>
    /// <returns>The new length in bytes.</returns>
    public static int Stretch(byte[] pcm, int length, int channels, int frames)
    {
        var align = channels * sizeof(short);
        var total = length / align;
        frames = Math.Min(frames, total / 2);
        if (frames <= 0) return length;
        if (pcm.Length < (total + frames) * align)
            throw new ArgumentException("The buffer has no room for the inserted frames.", nameof(pcm));

        // Walked from the end, so that every frame has moved before anything is written over
        // where it was.
        var write = total + frames - 1;
        var remaining = frames;
        var nextInsertion = SplicePoint(total, frames, remaining - 1);

        for (var read = total - 1; read >= 0 && write > read; read--)
        {
            Buffer.BlockCopy(pcm, read * align, pcm, write * align, align);
            write--;

            if (remaining == 0 || read != nextInsertion) continue;

            // Between frame read - 1 and frame read, neither of which has been overwritten.
            for (var channel = 0; channel < channels; channel++)
            {
                var before = (read - 1) * align + channel * sizeof(short);
                var after = read * align + channel * sizeof(short);
                WriteSample(pcm, write * align + channel * sizeof(short),
                    (ReadSample(pcm, before) + ReadSample(pcm, after)) / 2);
            }

            write--;
            remaining--;
            if (remaining > 0) nextInsertion = SplicePoint(total, frames, remaining - 1);
        }

        return (total + frames) * align;
    }

    /// <summary>Ramps the first <paramref name="frames"/> frames up from silence.</summary>
    public static void FadeIn(Span<byte> pcm, int channels, int frames)
    {
        var align = channels * sizeof(short);
        frames = Math.Min(frames, pcm.Length / align);

        for (var frame = 0; frame < frames; frame++)
            Scale(pcm.Slice(frame * align, align), frame, frames);
    }

    /// <summary>Ramps the last <paramref name="frames"/> frames down to silence.</summary>
    public static void FadeOut(Span<byte> pcm, int channels, int frames)
    {
        var align = channels * sizeof(short);
        var total = pcm.Length / align;
        frames = Math.Min(frames, total);

        for (var frame = 0; frame < frames; frame++)
            Scale(pcm.Slice((total - frames + frame) * align, align), frames - 1 - frame, frames);
    }

    /// <summary>
    /// Where the <paramref name="index"/>th of <paramref name="count"/> splices falls in
    /// <paramref name="total"/> frames: evenly spaced, never at the first frame, and always
    /// after the one before it while <paramref name="count"/> is at most half of
    /// <paramref name="total"/>.
    /// </summary>
    private static int SplicePoint(int total, int count, int index) => (int)((index + 1L) * total / (count + 1));

    private static void Scale(Span<byte> frame, int numerator, int denominator)
    {
        for (var offset = 0; offset + 1 < frame.Length; offset += sizeof(short))
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(frame[offset..]);
            BinaryPrimitives.WriteInt16LittleEndian(frame[offset..], (short)(sample * numerator / denominator));
        }
    }

    private static int ReadSample(byte[] pcm, int offset) => BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(offset));

    private static void WriteSample(byte[] pcm, int offset, int value)
        => BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(offset), (short)value);
}
