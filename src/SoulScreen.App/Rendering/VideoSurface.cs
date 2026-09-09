using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SoulScreen.Media;

namespace SoulScreen.App.Rendering;

/// <summary>
/// Displays decoded frames.
/// <para>
/// Frames arrive on the decode thread but WPF bitmaps may only be touched on the UI
/// thread, so a frame is copied into a staging buffer as it arrives and uploaded during
/// the next composition pass. That decouples the decoder from the display: if the phone
/// sends 60 fps to a 60 Hz screen each frame is shown once, and if it sends more, the
/// extra frames are simply superseded rather than queued behind the compositor.
/// </para>
/// </summary>
public sealed class VideoSurface : Image, IDisposable
{
    private readonly object _stagingLock = new();

    private byte[]? _staging;
    private int _stagingWidth;
    private int _stagingHeight;
    private int _stagingStride;
    private bool _stagingDirty;

    private WriteableBitmap? _bitmap;
    private bool _renderingHooked;
    private long _presentedFrames;

    public VideoSurface()
    {
        Stretch = Stretch.Uniform;
        // A phone screen is almost always being scaled down into this window, where the
        // higher-quality filter keeps small text on the phone legible instead of aliasing it.
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Loaded += (_, _) => HookRendering();
        Unloaded += (_, _) => UnhookRendering();
    }

    /// <summary>Frames actually drawn, which is at most one per composition pass.</summary>
    public long PresentedFrameCount => Interlocked.Read(ref _presentedFrames);

    /// <summary>Size of the picture currently displayed, or empty before the first frame.</summary>
    public Size VideoSize { get; private set; }

    /// <summary>Raised on the UI thread the first time a frame of a new size is shown.</summary>
    public event EventHandler<Size>? VideoSizeChanged;

    /// <summary>
    /// Accepts a decoded frame from any thread. Returns immediately; the frame may be
    /// recycled by the caller as soon as this returns.
    /// </summary>
    public void Present(DecodedVideoFrame frame)
    {
        lock (_stagingLock)
        {
            var required = frame.Stride * frame.Height;
            if (_staging is null || _staging.Length < required)
                _staging = new byte[required];

            frame.Pixels.CopyTo(_staging);
            _stagingWidth = frame.Width;
            _stagingHeight = frame.Height;
            _stagingStride = frame.Stride;
            _stagingDirty = true;
        }
    }

    /// <summary>Clears the surface, e.g. when a session ends. Safe from any thread.</summary>
    public void Clear()
    {
        lock (_stagingLock)
        {
            _stagingDirty = false;
            _stagingWidth = 0;
            _stagingHeight = 0;
        }

        if (Dispatcher.CheckAccess())
        {
            ClearCore();
        }
        else
        {
            // BeginInvoke rather than Invoke: this can be reached while the window is
            // closing, and a blocking call into a dispatcher that is shutting down deadlocks.
            Dispatcher.BeginInvoke(ClearCore);
        }
    }

    private void ClearCore()
    {
        Source = null;
        _bitmap = null;
        VideoSize = default;
    }

    private void HookRendering()
    {
        if (_renderingHooked) return;
        CompositionTarget.Rendering += OnRendering;
        _renderingHooked = true;
    }

    private void UnhookRendering()
    {
        if (!_renderingHooked) return;
        CompositionTarget.Rendering -= OnRendering;
        _renderingHooked = false;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        int width, height, stride;
        byte[] pixels;

        lock (_stagingLock)
        {
            if (!_stagingDirty || _staging is null) return;
            _stagingDirty = false;
            width = _stagingWidth;
            height = _stagingHeight;
            stride = _stagingStride;
            pixels = _staging;

            // The upload happens inside the lock so the decode thread cannot overwrite the
            // buffer halfway through. The copy is a straight memcpy of a few megabytes at
            // most, which is far cheaper than the double-buffering it would take to avoid.
            if (width <= 0 || height <= 0) return;
            EnsureBitmap(width, height);
            _bitmap!.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
        }

        Interlocked.Increment(ref _presentedFrames);
    }

    private void EnsureBitmap(int width, int height)
    {
        if (_bitmap is not null && _bitmap.PixelWidth == width && _bitmap.PixelHeight == height) return;

        _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        Source = _bitmap;
        VideoSize = new Size(width, height);
        VideoSizeChanged?.Invoke(this, VideoSize);
    }

    /// <summary>Takes a snapshot of what is on screen right now, or null before the first frame.</summary>
    public BitmapSource? Snapshot()
    {
        if (_bitmap is null) return null;
        var copy = new WriteableBitmap(_bitmap);
        copy.Freeze();
        return copy;
    }

    public void Dispose() => UnhookRendering();
}
