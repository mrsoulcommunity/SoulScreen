using FFmpeg.AutoGen;

namespace SoulScreen.Media;

/// <summary>
/// The colour handling every conversion of a decoded picture with swscale depends on: which
/// pixel format a frame really is, which matrix and range it was encoded with, and how to
/// tell the converter about both.
/// <para>
/// Getting either wrong is immediately visible - the wrong matrix tints the picture, the
/// wrong range crushes blacks and blows out highlights - and iOS reports both
/// inconsistently, so the decoder and the clip exporter normalise a frame the same way
/// through here rather than each with their own copy of the rules.
/// </para>
/// </summary>
internal static unsafe class ColourSpace
{
    /// <summary>
    /// swscale's algorithm flags. The binding exposes the colour-space constants but not
    /// these, so they are repeated from libswscale/swscale.h.
    /// </summary>
    public const int SwsPoint = 0x10;

    /// <summary>swscale's neutral contrast and saturation, both 1.0 in 16.16 fixed point.</summary>
    public const int NeutralGain = 1 << 16;

    /// <summary>
    /// Maps the deprecated YUVJ formats onto their plain equivalents, reporting the full
    /// range they imply. Passing YUVJ straight to swscale works but logs a deprecation
    /// warning on every stream and leaves the range for the caller to set anyway.
    /// </summary>
    public static (AVPixelFormat Format, bool FullRange) Normalise(AVPixelFormat format) => format switch
    {
        AVPixelFormat.AV_PIX_FMT_YUVJ420P => (AVPixelFormat.AV_PIX_FMT_YUV420P, true),
        AVPixelFormat.AV_PIX_FMT_YUVJ422P => (AVPixelFormat.AV_PIX_FMT_YUV422P, true),
        AVPixelFormat.AV_PIX_FMT_YUVJ444P => (AVPixelFormat.AV_PIX_FMT_YUV444P, true),
        AVPixelFormat.AV_PIX_FMT_YUVJ440P => (AVPixelFormat.AV_PIX_FMT_YUV440P, true),
        _ => (format, false),
    };

    /// <summary>
    /// Whether a frame carries full-range luma. iOS mirrors with full-range luma, which
    /// libavcodec reports either as a YUVJ pixel format or through <c>color_range</c>; both
    /// spellings have to be honoured.
    /// </summary>
    public static bool IsFullRange(AVFrame* frame, bool formatImpliesFullRange) =>
        formatImpliesFullRange || frame->color_range == AVColorRange.AVCOL_RANGE_JPEG;

    /// <summary>
    /// The matrix a frame was encoded with. Unspecified is the common case from a phone.
    /// Height is the usual tiebreak: standard-definition content is BT.601, everything
    /// larger is BT.709.
    /// </summary>
    public static AVColorSpace Of(AVFrame* frame) =>
        frame->colorspace != AVColorSpace.AVCOL_SPC_UNSPECIFIED
            ? frame->colorspace
            : frame->height > 576 ? AVColorSpace.AVCOL_SPC_BT709 : AVColorSpace.AVCOL_SPC_SMPTE170M;

    /// <summary>
    /// Tells swscale which matrix and range the source uses. Every destination SoulScreen
    /// converts to is full range, so that side is fixed.
    /// </summary>
    /// <returns>swscale's result: negative when the pixel format accepts no overrides.</returns>
    public static int Apply(SwsContext* scaler, bool fullRange, AVColorSpace colorspace)
    {
        var coefficients = ffmpeg.sws_getCoefficients(ToSwsColorspace(colorspace));
        if (coefficients is null) return -1;

        var table = new int_array4();
        for (uint i = 0; i < 4; i++) table[i] = coefficients[i];

        return ffmpeg.sws_setColorspaceDetails(scaler, table, srcRange: fullRange ? 1 : 0, table, dstRange: 1,
            brightness: 0, contrast: NeutralGain, saturation: NeutralGain);
    }

    private static int ToSwsColorspace(AVColorSpace colorspace) => colorspace switch
    {
        AVColorSpace.AVCOL_SPC_BT709 => ffmpeg.SWS_CS_ITU709,
        AVColorSpace.AVCOL_SPC_FCC => ffmpeg.SWS_CS_FCC,
        AVColorSpace.AVCOL_SPC_BT470BG => ffmpeg.SWS_CS_ITU601,
        AVColorSpace.AVCOL_SPC_SMPTE170M => ffmpeg.SWS_CS_SMPTE170M,
        AVColorSpace.AVCOL_SPC_SMPTE240M => ffmpeg.SWS_CS_SMPTE240M,
        AVColorSpace.AVCOL_SPC_BT2020_NCL or AVColorSpace.AVCOL_SPC_BT2020_CL => ffmpeg.SWS_CS_BT2020,
        _ => ffmpeg.SWS_CS_ITU709,
    };

    public static string Describe(AVColorSpace colorspace) => colorspace switch
    {
        AVColorSpace.AVCOL_SPC_BT709 => "BT.709",
        AVColorSpace.AVCOL_SPC_BT470BG or AVColorSpace.AVCOL_SPC_SMPTE170M => "BT.601",
        AVColorSpace.AVCOL_SPC_BT2020_NCL or AVColorSpace.AVCOL_SPC_BT2020_CL => "BT.2020",
        _ => colorspace.ToString(),
    };
}
