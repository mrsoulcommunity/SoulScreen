using SoulScreen.Core.Media;

namespace SoulScreen.Android;

/// <summary>One thing recovered from the raw stream: either a codec configuration change
/// or a video access unit ready to hand to the render pipeline.</summary>
public abstract record AndroidH264Unit;

public sealed record AndroidFormatUnit(VideoFormat Format) : AndroidH264Unit;

public sealed record AndroidSampleUnit(byte[] Payload, bool IsKeyFrame) : AndroidH264Unit;

/// <summary>
/// Turns the raw Annex-B byte stream <c>adb exec-out screenrecord --output-format=h264 -</c>
/// writes to stdout into the same shape the AirPlay transport already delivers: a
/// <see cref="VideoFormat"/> event when SPS/PPS are seen or change, and one sample per
/// slice NAL.
/// <para>
/// screenrecord's output has no framing beyond Annex-B start codes and no separate control
/// channel for the codec configuration the way AirPlay's RTSP does, so both have to be
/// recovered from the same byte stream here. Bytes arrive in arbitrary pipe-sized chunks
/// that share no boundary with NAL units, so a NAL is only known to be complete once the
/// next start code is seen; the tail of every chunk is held back until then.
/// </para>
/// </summary>
public sealed class AndroidH264Assembler
{
    /// <summary>A NAL that has not closed after this many bytes is not a NAL - the stream
    /// desynchronised (adb killed mid-write, a corrupt read). Drop it and resync on the
    /// next start code rather than growing without bound.</summary>
    private const int MaxPendingNalBytes = 16 * 1024 * 1024;

    private readonly PendingBuffer _pending = new();

    /// <summary>
    /// True when byte 0 of <see cref="_pending"/> is the continuation of a NAL whose start
    /// code closed a previous <see cref="Feed"/> call - as opposed to unclassified bytes
    /// that have not yet been seen to follow any start code at all.
    /// <para>
    /// <see cref="ScanStartCodes"/> has no memory between calls - each call sees only the
    /// bytes <see cref="_pending"/> currently holds - so without this flag a NAL whose
    /// opening start code was found in one <see cref="Feed"/> call but whose closing start
    /// code only arrives in the next would look, to that next call, like it starts at
    /// position 0 with nothing open, and would be silently discarded as if it were
    /// unclassified prefix rather than reported as a closed NAL.
    /// </para>
    /// </summary>
    private bool _pendingStartsInsideOpenNal;

    private byte[]? _sps;
    private byte[]? _pps;
    private byte[]? _announcedParameterSets;

    /// <summary>
    /// Feeds the next chunk read from the process's stdout. Returns whatever became
    /// available as a result, in order - typically nothing, sometimes one sample, and once
    /// near the start of the stream a format followed by a sample.
    /// </summary>
    public List<AndroidH264Unit> Feed(ReadOnlySpan<byte> chunk)
    {
        _pending.Append(chunk);

        var (closedRanges, openStart) = ScanStartCodes(_pending.Span, _pendingStartsInsideOpenNal);
        var results = new List<AndroidH264Unit>(closedRanges.Count);

        foreach (var (start, end) in closedRanges)
        {
            if (end > start) HandleNal(_pending.Span[start..end], results);
        }

        if (openStart >= 0 && _pending.Length - openStart > MaxPendingNalBytes)
        {
            _pending.Clear();
            _pendingStartsInsideOpenNal = false;
        }
        else
        {
            // Nothing found yet: keep only the last couple of bytes, in case they are the
            // first bytes of a start code that straddles this chunk and the next.
            var discardTo = openStart >= 0 ? openStart : Math.Max(0, _pending.Length - 3);
            _pending.DiscardFront(discardTo);
            _pendingStartsInsideOpenNal = openStart >= 0;
        }

        return results;
    }

    private void HandleNal(ReadOnlySpan<byte> nal, List<AndroidH264Unit> results)
    {
        if (nal.Length == 0) return;
        var nalType = nal[0] & 0x1f;

        switch (nalType)
        {
            case H264.NalTypeSps:
                _sps = nal.ToArray();
                TryEmitFormat(results);
                break;

            case H264.NalTypePps:
                _pps = nal.ToArray();
                TryEmitFormat(results);
                break;

            case H264.NalTypeSlice:
            case H264.NalTypeIdr:
                results.Add(new AndroidSampleUnit(H264.ToAnnexB(nal.ToArray()), nalType == H264.NalTypeIdr));
                break;

            default:
                // AUD, SEI, filler and the like carry nothing the decoder needs from us.
                break;
        }
    }

    private void TryEmitFormat(List<AndroidH264Unit> results)
    {
        if (_sps is null || _pps is null) return;

        var parameterSets = H264.ToAnnexB(_sps, _pps);

        // The encoder does not re-announce SPS/PPS unprompted, but guard anyway: a decoder
        // resynchronising for no reason is a visible stall.
        if (_announcedParameterSets is not null && _announcedParameterSets.AsSpan().SequenceEqual(parameterSets))
            return;

        var configuration = AvcDecoderConfiguration.FromAnnexB(parameterSets);
        if (configuration is null) return;

        _announcedParameterSets = parameterSets;
        configuration.TryGetDimensions(out var width, out var height);
        results.Add(new AndroidFormatUnit(new VideoFormat(VideoCodec.H264, width, height, parameterSets, 0)));
    }

    /// <summary>
    /// Finds every start code in <paramref name="data"/> and returns the byte ranges of the
    /// NAL units that closed (a start code was found on both sides) plus the offset the
    /// still-open trailing NAL starts at, or -1 if no start code has been seen at all.
    /// Mirrors <see cref="H264.SplitAnnexB"/> exactly, except the final unit is left open
    /// rather than assumed complete, which is the one thing that differs about parsing a
    /// stream instead of a buffer that is already whole.
    /// </summary>
    /// <param name="startsInsideOpenNal">True when position 0 is itself the continuation of
    /// a NAL left open by the previous call, so the first start code found closes it rather
    /// than being treated as the first one ever seen.</param>
    private static (List<(int Start, int End)> Closed, int OpenStart) ScanStartCodes(
        ReadOnlySpan<byte> data, bool startsInsideOpenNal)
    {
        var closed = new List<(int, int)>();
        var start = startsInsideOpenNal ? 0 : -1;
        var i = 0;

        while (i + 2 < data.Length)
        {
            if (data[i] != 0 || data[i + 1] != 0) { i++; continue; }

            int payloadStart;
            if (data[i + 2] == 1) payloadStart = i + 3;
            else if (i + 3 < data.Length && data[i + 2] == 0 && data[i + 3] == 1) payloadStart = i + 4;
            else { i++; continue; }

            if (start >= 0) closed.Add((start, i));
            start = payloadStart;
            i = payloadStart;
        }

        return (closed, start);
    }

    /// <summary>Growable byte buffer that only ever needs its front trimmed off, which a
    /// plain array does more cheaply than a List&lt;byte&gt; that has no span-based
    /// AddRange.</summary>
    private sealed class PendingBuffer
    {
        private byte[] _data = new byte[16 * 1024];
        private int _length;

        public int Length => _length;

        public ReadOnlySpan<byte> Span => _data.AsSpan(0, _length);

        public void Append(ReadOnlySpan<byte> chunk)
        {
            EnsureCapacity(_length + chunk.Length);
            chunk.CopyTo(_data.AsSpan(_length));
            _length += chunk.Length;
        }

        public void DiscardFront(int count)
        {
            if (count <= 0) return;
            if (count >= _length) { _length = 0; return; }
            Array.Copy(_data, count, _data, 0, _length - count);
            _length -= count;
        }

        public void Clear() => _length = 0;

        private void EnsureCapacity(int needed)
        {
            if (needed <= _data.Length) return;
            var newSize = _data.Length * 2;
            while (newSize < needed) newSize *= 2;
            Array.Resize(ref _data, newSize);
        }
    }
}
