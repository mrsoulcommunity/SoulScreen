using System.Buffers.Binary;

namespace SoulScreen.Core.Media;

/// <summary>H.264 helpers shared by the AirPlay and USB transports: both deliver
/// length-prefixed NAL units plus an avcC configuration record.</summary>
public static class H264
{
    public const int NalTypeSlice = 1;
    public const int NalTypeIdr = 5;
    public const int NalTypeSei = 6;
    public const int NalTypeSps = 7;
    public const int NalTypePps = 8;

    private static ReadOnlySpan<byte> StartCode => [0x00, 0x00, 0x00, 0x01];

    /// <summary>
    /// Rewrites a buffer of 4-byte-length-prefixed NAL units into Annex-B in place by
    /// overwriting each length field with a start code. Both forms are the same size, so
    /// no copy is needed.
    /// </summary>
    /// <returns>True when the whole buffer parsed cleanly.</returns>
    public static bool ConvertLengthPrefixedToAnnexB(Span<byte> buffer, out bool containsKeyFrame)
    {
        containsKeyFrame = false;
        var offset = 0;

        while (offset + 4 <= buffer.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(offset, 4));
            if (length == 0 || offset + 4 + length > (uint)buffer.Length)
                return false;

            StartCode.CopyTo(buffer.Slice(offset, 4));

            var nalType = buffer[offset + 4] & 0x1f;
            if (nalType is NalTypeIdr or NalTypeSps) containsKeyFrame = true;

            offset += 4 + (int)length;
        }

        return offset == buffer.Length;
    }

    public const int NalTypeAccessUnitDelimiter = 9;

    /// <summary>Wraps one NAL unit payload in a start code.</summary>
    public static byte[] ToAnnexB(params ReadOnlyMemory<byte>[] nalUnits)
    {
        var total = nalUnits.Sum(n => n.Length + 4);
        var output = new byte[total];
        var offset = 0;
        foreach (var nal in nalUnits)
        {
            StartCode.CopyTo(output.AsSpan(offset));
            nal.Span.CopyTo(output.AsSpan(offset + 4));
            offset += 4 + nal.Length;
        }
        return output;
    }

    /// <summary>
    /// Splits an Annex-B buffer into its NAL units, accepting both three- and four-byte
    /// start codes.
    /// </summary>
    /// <returns>Ranges into <paramref name="annexB"/>, one per NAL unit payload.</returns>
    public static List<Range> SplitAnnexB(ReadOnlySpan<byte> annexB)
    {
        var units = new List<Range>();
        var start = -1;

        for (var i = 0; i + 2 < annexB.Length; i++)
        {
            if (annexB[i] != 0 || annexB[i + 1] != 0) continue;

            int payloadStart;
            if (annexB[i + 2] == 1) payloadStart = i + 3;
            else if (i + 3 < annexB.Length && annexB[i + 2] == 0 && annexB[i + 3] == 1) payloadStart = i + 4;
            else continue;

            if (start >= 0) units.Add(new Range(start, i));
            start = payloadStart;
            i = payloadStart - 1;
        }

        if (start >= 0 && start < annexB.Length) units.Add(new Range(start, annexB.Length));
        return units;
    }

    /// <summary>
    /// Rewrites Annex-B into the four-byte-length-prefixed form MP4 stores, which is the
    /// inverse of what the AirPlay transport does on the way in.
    /// </summary>
    /// <param name="annexB">Source access unit.</param>
    /// <param name="destination">Buffer to write into; never needs more room than the source.</param>
    /// <param name="dropParameterSets">
    /// Skip SPS, PPS and access unit delimiters. MP4 carries the parameter sets in the
    /// track's avcC record instead, and repeating them in every sample is redundant.
    /// </param>
    /// <returns>Bytes written, or -1 when the destination is too small.</returns>
    public static int ConvertAnnexBToLengthPrefixed(
        ReadOnlySpan<byte> annexB,
        Span<byte> destination,
        bool dropParameterSets = true)
    {
        var written = 0;

        foreach (var range in SplitAnnexB(annexB))
        {
            var (offset, length) = range.GetOffsetAndLength(annexB.Length);
            if (length == 0) continue;

            var nalType = annexB[offset] & 0x1f;
            if (dropParameterSets && nalType is NalTypeSps or NalTypePps or NalTypeAccessUnitDelimiter)
                continue;

            if (written + 4 + length > destination.Length) return -1;

            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
                destination.Slice(written, 4), (uint)length);
            annexB.Slice(offset, length).CopyTo(destination[(written + 4)..]);
            written += 4 + length;
        }

        return written;
    }
}

/// <summary>
/// An AVCDecoderConfigurationRecord ("avcC"), the codec setup blob iOS sends before the
/// first frame and again after every rotation.
/// </summary>
public sealed class AvcDecoderConfiguration
{
    public required byte ProfileIndication { get; init; }
    public required byte ProfileCompatibility { get; init; }
    public required byte LevelIndication { get; init; }

    /// <summary>Bytes used for each NAL length prefix in the elementary stream; always 4 for AirPlay.</summary>
    public required int NalLengthSize { get; init; }

    public required IReadOnlyList<byte[]> SequenceParameterSets { get; init; }
    public required IReadOnlyList<byte[]> PictureParameterSets { get; init; }

    /// <summary>SPS and PPS concatenated in Annex-B form, ready to prefix a keyframe.</summary>
    public byte[] ToAnnexB() => H264.ToAnnexB(
        [.. SequenceParameterSets.Concat(PictureParameterSets).Select(s => (ReadOnlyMemory<byte>)s)]);

    /// <summary>
    /// Re-encodes this configuration as an avcC record, the form an MP4 track stores it in.
    /// </summary>
    public byte[] ToAvcC()
    {
        var record = new List<byte>(64)
        {
            1,                                  // configurationVersion
            ProfileIndication,
            ProfileCompatibility,
            LevelIndication,
            (byte)(0xFC | (NalLengthSize - 1)), // 6 reserved bits set, then lengthSizeMinusOne
            (byte)(0xE0 | SequenceParameterSets.Count),
        };

        foreach (var sps in SequenceParameterSets) AppendParameterSet(record, sps);
        record.Add((byte)PictureParameterSets.Count);
        foreach (var pps in PictureParameterSets) AppendParameterSet(record, pps);

        return [.. record];

        static void AppendParameterSet(List<byte> into, byte[] parameterSet)
        {
            into.Add((byte)(parameterSet.Length >> 8));
            into.Add((byte)parameterSet.Length);
            into.AddRange(parameterSet);
        }
    }

    /// <summary>
    /// Rebuilds a configuration from parameter sets already in Annex-B form, which is how
    /// they travel once the transport has converted them.
    /// </summary>
    public static AvcDecoderConfiguration? FromAnnexB(ReadOnlySpan<byte> parameterSets)
    {
        var spsList = new List<byte[]>();
        var ppsList = new List<byte[]>();

        foreach (var range in H264.SplitAnnexB(parameterSets))
        {
            var (offset, length) = range.GetOffsetAndLength(parameterSets.Length);
            if (length == 0) continue;

            var nal = parameterSets.Slice(offset, length).ToArray();
            switch (nal[0] & 0x1f)
            {
                case H264.NalTypeSps: spsList.Add(nal); break;
                case H264.NalTypePps: ppsList.Add(nal); break;
            }
        }

        // A configuration without an SPS describes nothing; the profile bytes come from it.
        if (spsList.Count == 0 || spsList[0].Length < 4) return null;

        return new AvcDecoderConfiguration
        {
            ProfileIndication = spsList[0][1],
            ProfileCompatibility = spsList[0][2],
            LevelIndication = spsList[0][3],
            NalLengthSize = 4,
            SequenceParameterSets = spsList,
            PictureParameterSets = ppsList,
        };
    }

    public static AvcDecoderConfiguration Parse(ReadOnlySpan<byte> record)
    {
        if (record.Length < 7)
            throw new InvalidDataException($"avcC record is only {record.Length} bytes.");
        if (record[0] != 1)
            throw new InvalidDataException($"Unsupported avcC configuration version {record[0]}.");

        var offset = 5;
        var spsCount = record[offset++] & 0x1f;
        var spsList = new List<byte[]>(spsCount);
        for (var i = 0; i < spsCount; i++)
            spsList.Add(ReadParameterSet(record, ref offset, "SPS"));

        if (offset >= record.Length)
            throw new InvalidDataException("avcC record ended before the PPS count.");

        var ppsCount = record[offset++];
        var ppsList = new List<byte[]>(ppsCount);
        for (var i = 0; i < ppsCount; i++)
            ppsList.Add(ReadParameterSet(record, ref offset, "PPS"));

        return new AvcDecoderConfiguration
        {
            ProfileIndication = record[1],
            ProfileCompatibility = record[2],
            LevelIndication = record[3],
            NalLengthSize = (record[4] & 0x03) + 1,
            SequenceParameterSets = spsList,
            PictureParameterSets = ppsList,
        };
    }

    private static byte[] ReadParameterSet(ReadOnlySpan<byte> record, ref int offset, string what)
    {
        if (offset + 2 > record.Length)
            throw new InvalidDataException($"avcC record ended inside a {what} length.");
        var length = BinaryPrimitives.ReadUInt16BigEndian(record.Slice(offset, 2));
        offset += 2;
        if (offset + length > record.Length)
            throw new InvalidDataException($"avcC {what} claims {length} bytes but only {record.Length - offset} remain.");
        var data = record.Slice(offset, length).ToArray();
        offset += length;
        return data;
    }

    /// <summary>Reads the coded picture size out of the first SPS. Returns false when the
    /// SPS uses a feature this minimal parser does not cover.</summary>
    public bool TryGetDimensions(out int width, out int height)
    {
        width = height = 0;
        var sps = SequenceParameterSets.FirstOrDefault();
        return sps is not null && SpsParser.TryGetDimensions(sps, out width, out height);
    }

    public override string ToString() =>
        $"avcC profile={ProfileIndication} level={LevelIndication} sps={SequenceParameterSets.Count} pps={PictureParameterSets.Count}";
}

/// <summary>
/// Just enough SPS parsing to recover the picture dimensions, which the UI needs in order
/// to size the window before a single frame has been decoded.
/// </summary>
internal static class SpsParser
{
    public static bool TryGetDimensions(ReadOnlySpan<byte> sps, out int width, out int height)
    {
        width = height = 0;
        try
        {
            // Drop the NAL header byte, then undo emulation prevention.
            var payload = sps.Length > 1 && (sps[0] & 0x1f) == H264.NalTypeSps ? sps[1..] : sps;
            var rbsp = RemoveEmulationPrevention(payload);
            var reader = new BitReader(rbsp);

            var profileIdc = reader.ReadBits(8);
            reader.ReadBits(8);  // constraint flags + reserved
            reader.ReadBits(8);  // level_idc
            reader.ReadUnsignedExpGolomb(); // seq_parameter_set_id

            var chromaFormatIdc = 1;
            if (profileIdc is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
            {
                chromaFormatIdc = (int)reader.ReadUnsignedExpGolomb();
                if (chromaFormatIdc == 3) reader.ReadBit(); // separate_colour_plane_flag
                reader.ReadUnsignedExpGolomb(); // bit_depth_luma_minus8
                reader.ReadUnsignedExpGolomb(); // bit_depth_chroma_minus8
                reader.ReadBit();               // qpprime_y_zero_transform_bypass_flag
                if (reader.ReadBit())           // seq_scaling_matrix_present_flag
                    SkipScalingMatrix(ref reader, chromaFormatIdc == 3 ? 12 : 8);
            }

            reader.ReadUnsignedExpGolomb(); // log2_max_frame_num_minus4
            var picOrderCntType = reader.ReadUnsignedExpGolomb();
            if (picOrderCntType == 0)
            {
                reader.ReadUnsignedExpGolomb(); // log2_max_pic_order_cnt_lsb_minus4
            }
            else if (picOrderCntType == 1)
            {
                reader.ReadBit();               // delta_pic_order_always_zero_flag
                reader.ReadSignedExpGolomb();   // offset_for_non_ref_pic
                reader.ReadSignedExpGolomb();   // offset_for_top_to_bottom_field
                var cycleLength = reader.ReadUnsignedExpGolomb();
                for (var i = 0UL; i < cycleLength; i++) reader.ReadSignedExpGolomb();
            }

            reader.ReadUnsignedExpGolomb(); // max_num_ref_frames
            reader.ReadBit();               // gaps_in_frame_num_value_allowed_flag

            var widthInMbs = (int)reader.ReadUnsignedExpGolomb() + 1;
            var heightInMapUnits = (int)reader.ReadUnsignedExpGolomb() + 1;
            var frameMbsOnly = reader.ReadBit();
            if (!frameMbsOnly) reader.ReadBit(); // mb_adaptive_frame_field_flag
            reader.ReadBit();                    // direct_8x8_inference_flag

            int cropLeft = 0, cropRight = 0, cropTop = 0, cropBottom = 0;
            if (reader.ReadBit()) // frame_cropping_flag
            {
                cropLeft = (int)reader.ReadUnsignedExpGolomb();
                cropRight = (int)reader.ReadUnsignedExpGolomb();
                cropTop = (int)reader.ReadUnsignedExpGolomb();
                cropBottom = (int)reader.ReadUnsignedExpGolomb();
            }

            // Crop offsets are in chroma samples, so they scale with the subsampling.
            var subWidth = chromaFormatIdc is 1 or 2 ? 2 : 1;
            var subHeight = chromaFormatIdc == 1 ? 2 : 1;
            var frameHeightMultiplier = frameMbsOnly ? 1 : 2;

            width = widthInMbs * 16 - subWidth * (cropLeft + cropRight);
            height = heightInMapUnits * 16 * frameHeightMultiplier
                     - subHeight * frameHeightMultiplier * (cropTop + cropBottom);

            return width > 0 && height > 0;
        }
        catch (Exception)
        {
            // A dimension we cannot parse is cosmetic; the decoder will still figure it out.
            return false;
        }
    }

    private static void SkipScalingMatrix(ref BitReader reader, int listCount)
    {
        for (var i = 0; i < listCount; i++)
        {
            if (!reader.ReadBit()) continue;
            var size = i < 6 ? 16 : 64;
            var lastScale = 8;
            var nextScale = 8;
            for (var j = 0; j < size; j++)
            {
                if (nextScale != 0)
                {
                    var delta = reader.ReadSignedExpGolomb();
                    nextScale = (lastScale + (int)delta + 256) % 256;
                }
                lastScale = nextScale == 0 ? lastScale : nextScale;
            }
        }
    }

    /// <summary>Strips the 0x03 bytes inserted to stop 00 00 00/01/02/03 appearing in the payload.</summary>
    private static byte[] RemoveEmulationPrevention(ReadOnlySpan<byte> data)
    {
        var output = new byte[data.Length];
        var written = 0;
        var zeroes = 0;
        foreach (var b in data)
        {
            if (zeroes >= 2 && b == 0x03)
            {
                zeroes = 0;
                continue;
            }
            zeroes = b == 0 ? zeroes + 1 : 0;
            output[written++] = b;
        }
        return output[..written];
    }

    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _bitPosition;

        public bool ReadBit()
        {
            var byteIndex = _bitPosition >> 3;
            if (byteIndex >= _data.Length) throw new EndOfStreamException("Ran off the end of the SPS.");
            var bit = (_data[byteIndex] >> (7 - (_bitPosition & 7))) & 1;
            _bitPosition++;
            return bit == 1;
        }

        public uint ReadBits(int count)
        {
            uint value = 0;
            for (var i = 0; i < count; i++) value = (value << 1) | (ReadBit() ? 1u : 0u);
            return value;
        }

        /// <summary>Unsigned Exp-Golomb, the variable-length coding H.264 uses throughout.</summary>
        public ulong ReadUnsignedExpGolomb()
        {
            var leadingZeroes = 0;
            while (!ReadBit())
            {
                if (++leadingZeroes > 32) throw new InvalidDataException("Exp-Golomb code is too long.");
            }
            if (leadingZeroes == 0) return 0;
            return (1UL << leadingZeroes) - 1 + ReadBits(leadingZeroes);
        }

        public long ReadSignedExpGolomb()
        {
            var value = ReadUnsignedExpGolomb();
            // Zig-zag: 0, 1, -1, 2, -2, ...
            return (value & 1) == 1 ? (long)((value + 1) / 2) : -(long)(value / 2);
        }
    }
}
