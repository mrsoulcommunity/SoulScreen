using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace SoulScreen.App;

/// <summary>
/// Reads the text out of a screenshot with Windows' own OCR engine (<see cref="OcrEngine"/>,
/// part of the OS since Windows 10 - no model to download, no network call, nothing sent
/// anywhere). Everything WinRT-specific lives here so the rest of the app - the index, the
/// search box, the gallery - only ever sees plain strings.
/// </summary>
internal static class ScreenshotOcr
{
    /// <summary>
    /// Null the first time this is asked for on a machine with no recognition language
    /// installed at all - checked once, since which languages are installed does not change
    /// while SoulScreen is running.
    /// </summary>
    private static readonly Lazy<OcrEngine?> Engine = new(OcrEngine.TryCreateFromUserProfileLanguages);

    /// <summary>True when Windows has at least one OCR-capable language installed.</summary>
    public static bool IsAvailable => Engine.Value is not null;

    /// <summary>
    /// The text Windows can read out of the screenshot at <paramref name="path"/>, top to
    /// bottom as recognised lines joined with newlines, or "" if the engine found nothing -
    /// which is not an error: a screenshot of an all-picture app has no text to find.
    /// </summary>
    /// <exception cref="InvalidOperationException">No OCR language is installed.</exception>
    /// <exception cref="IOException">The file could not be read as an image.</exception>
    public static async Task<string> RecognizeAsync(string path, CancellationToken cancellationToken = default)
    {
        var engine = Engine.Value ?? throw new InvalidOperationException(
            "No OCR language is installed. Add one under Windows Settings > Time & Language > Language & region.");

        // Decoded off whatever thread calls this - the caller runs it on a background
        // thread, since decoding and recognising both cost real time for a large screenshot.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];

        cancellationToken.ThrowIfCancellationRequested();

        // OCR wants Bgra8: whatever the file's own format is, converted once here rather
        // than asking every caller to know that.
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);

        cancellationToken.ThrowIfCancellationRequested();

        using var softwareBitmap = SoftwareBitmap.CreateCopyFromBuffer(
            pixels.AsBuffer(), BitmapPixelFormat.Bgra8, converted.PixelWidth, converted.PixelHeight,
            BitmapAlphaMode.Premultiplied);

        var result = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken);
        return result.Text;
    }
}
