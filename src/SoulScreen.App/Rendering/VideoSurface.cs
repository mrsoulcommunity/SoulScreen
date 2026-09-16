using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SoulScreen.Core.Logging;
using SoulScreen.Media;

namespace SoulScreen.App.Rendering;

/// <summary>
/// Displays decoded frames.
/// <para>
/// The picture reaches the screen through a <see cref="WriteableBitmap"/>, written between
/// <see cref="WriteableBitmap.TryLock(Duration)"/> and <see cref="WriteableBitmap.Unlock"/>.
/// That pair is not ceremony: WPF composites on a thread of its own, and the lock is how it
/// says whether it has finished reading the pixels. Writing a picture into memory the
/// compositor is reading - which is what backing the image with a plain shared section does -
/// puts the top of one frame on screen with the bottom of another. At sixty frames a second
/// it happens most frames, and it looks like the picture is being corrupted in transit.
/// </para>
/// <para>
/// The bitmap is created once per geometry and assigned to <see cref="Image.Source"/> once.
/// Every frame after that is a write and an <see cref="WriteableBitmap.AddDirtyRect"/> on the
/// same bitmap, which is what keeps WPF from rebuilding its render-side texture each frame.
/// </para>
/// <para>
/// When each picture is shown is <see cref="FramePacer"/>'s decision, not this class's.
/// </para>
/// </summary>
public sealed class VideoSurface : Image, IDisposable
{
    /// <summary>
    /// How long a composition pass will wait for the compositor to release the back buffer.
    /// Short on purpose: a frame skipped here costs one picture, whereas blocking the UI
    /// thread costs the whole pass and every input event queued behind it.
    /// </summary>
    private static readonly Duration LockTimeout = new(TimeSpan.FromMilliseconds(3));

    private readonly ILogger _log = Log.For("surface");
    private readonly FramePacer _pacer = new();

    private WriteableBitmap? _bitmap;
    private Int32Rect _dirtyRect;

    private bool _renderingHooked;
    private volatile bool _frozen;
    private long _presentedFrames;
    private long _latencySumMicroseconds;
    private long _latencySamples;

    /// <summary>Composition passes seen, for the achieved refresh rate.</summary>
    private long _compositionPasses;

    private readonly Stopwatch _rateClock = Stopwatch.StartNew();
    private long _presentedAtLastSample;
    private long _passesAtLastSample;
    private double _presentedPerSecond;
    private double _compositionPerSecond;

    /// <summary>Measured gap between composition passes, in stopwatch ticks.</summary>
    private long _passIntervalTicks;
    private long _lastPassTicks;

    public VideoSurface()
    {
        Stretch = Stretch.Uniform;
        // Linear, not HighQuality. WPF's high-quality mode is the Fant filter, which is
        // resampled in software and costs milliseconds per frame at this size; bilinear runs
        // on the GPU and is indistinguishable on moving video.
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);

        Loaded += (_, _) => HookRendering();
        Unloaded += (_, _) => UnhookRendering();
    }

    /// <summary>Pictures drawn, at most one per composition pass.</summary>
    public long PresentedFrameCount => Interlocked.Read(ref _presentedFrames);

    /// <summary>
    /// Pictures discarded without being shown. Expected in small numbers whenever the phone
    /// outruns the display; a rising count means it is outrunning it consistently.
    /// </summary>
    public long SupersededFrameCount => _pacer.DroppedFrameCount;

    /// <summary>Size of the picture currently displayed, or empty before the first frame.</summary>
    public Size VideoSize { get; private set; }

    /// <summary>Pictures reaching the screen per second - what motion actually looks like.</summary>
    public double PresentedPerSecond => _presentedPerSecond;

    /// <summary>
    /// Composition passes per second, which is the display's refresh rate as WPF sees it.
    /// A source rate that is not a whole fraction of this judders no matter how even the
    /// frames arrive, because each one has to be held for a varying number of refreshes.
    /// </summary>
    public double CompositionPerSecond => _compositionPerSecond;

    /// <summary>
    /// Holds the picture on screen while the stream carries on underneath: frames keep being
    /// taken from the cushion on their schedule and are simply not drawn, so un-pausing lands
    /// on the live picture rather than on a backlog, and the cushion never overfills.
    /// </summary>
    public bool IsFrozen
    {
        get => _frozen;
        set => _frozen = value;
    }

    /// <summary>Pictures waiting in the pacing cushion.</summary>
    public int BufferedFrameCount => _pacer.Depth;

    /// <summary>How long a picture is held before it is shown, which is what hides Wi-Fi's
    /// unevenness. Safe to change mid-session.</summary>
    public TimeSpan PresentationDelay
    {
        get => _pacer.TargetDelay;
        set => _pacer.TargetDelay = value;
    }

    /// <summary>Source rate as the pacer measures it, which is the rate pictures are released at.</summary>
    public double PacedSourceRate => _pacer.SourceRate;

    /// <summary>
    /// Mean time from a picture leaving the decoder to reaching the screen, which includes
    /// the delay the pacing cushion deliberately adds. The render half of the path only; it
    /// does not include the network or the decode itself.
    /// </summary>
    public double AveragePresentLatencyMilliseconds
    {
        get
        {
            var samples = Interlocked.Read(ref _latencySamples);
            return samples == 0 ? 0 : Interlocked.Read(ref _latencySumMicroseconds) / (double)samples / 1000.0;
        }
    }

    /// <summary>Raised on the UI thread when a picture of a new size is first shown.</summary>
    public event EventHandler<Size>? VideoSizeChanged;

    /// <summary>
    /// Accepts a decoded frame from any thread and takes ownership of it. Returns as soon as
    /// the frame is queued - nothing is copied here, so the decode thread is never made to
    /// wait on the display.
    /// </summary>
    public void Present(DecodedVideoFrame frame) => _pacer.Enqueue(frame, Stopwatch.GetTimestamp());

    /// <summary>Clears the surface, e.g. when a session ends. Safe from any thread.</summary>
    public void Clear()
    {
        if (Dispatcher.CheckAccess()) ClearCore();
        // BeginInvoke rather than Invoke: this can be reached while the window is closing,
        // and a blocking call into a dispatcher that is shutting down deadlocks.
        else Dispatcher.BeginInvoke(ClearCore);
    }

    private void ClearCore()
    {
        _pacer.Reset();
        _frozen = false;
        Source = null;
        _bitmap = null;
        VideoSize = default;
    }

    /// <summary>
    /// Builds the bitmap for one picture geometry. UI thread only - which is where it is
    /// wanted anyway, since the frame that needs it is being presented on that thread.
    /// </summary>
    private WriteableBitmap? Allocate(int width, int height)
    {
        WriteableBitmap created;
        try
        {
            // Bgr32 rather than Bgra32: the decoder fills alpha with 255, and asking the
            // compositor to blend every pixel against it costs real time for no effect.
            created = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
        }
        catch (Exception ex)
        {
            _log.Error($"could not allocate a {width}x{height} frame buffer", ex);
            return null;
        }

        _bitmap = created;
        _dirtyRect = new Int32Rect(0, 0, width, height);

        // Assigned once for this geometry. Every subsequent frame is a write and a dirty
        // rect on the same bitmap.
        Source = created;
        VideoSize = new Size(width, height);
        _log.Info($"frame buffer allocated for {width}x{height}");
        VideoSizeChanged?.Invoke(this, VideoSize);
        return created;
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
        var now = Stopwatch.GetTimestamp();
        MeasurePass(now);

        var frame = _pacer.TryDequeue(now, _passIntervalTicks);
        if (frame is null) return;

        using (frame)
        {
            if (_frozen) return;

            var bitmap = _bitmap;
            if (bitmap is null || bitmap.PixelWidth != frame.Width || bitmap.PixelHeight != frame.Height)
                bitmap = Allocate(frame.Width, frame.Height);
            if (bitmap is null) return;

            // Not Lock: the compositor is still reading, and waiting on it here would stall
            // the UI thread. Leaving this frame unshown is the cheaper of the two, and the
            // next one is along in a few milliseconds.
            if (!bitmap.TryLock(LockTimeout)) return;

            try
            {
                CopyInto(bitmap, frame);
                bitmap.AddDirtyRect(_dirtyRect);
            }
            finally
            {
                bitmap.Unlock();
            }

            Interlocked.Increment(ref _presentedFrames);

            // Tick subtraction against the frame's decode time, rather than a
            // DateTime.UtcNow call every composition pass: at 60-144 Hz this is the
            // hottest path in the app, and the wall-clock syscall is orders of
            // magnitude slower than reading a Stopwatch counter.
            var latencyTicks = now - frame.DecodedAtTicks;
            // Anything over a second is a stalled decode, a paused machine, or a
            // measurement glitch - never a real frame latency, and not worth skewing
            // the average with.
            if (latencyTicks > 0 && latencyTicks < Stopwatch.Frequency)
            {
                Interlocked.Add(ref _latencySumMicroseconds, latencyTicks * 1_000_000L / Stopwatch.Frequency);
                Interlocked.Increment(ref _latencySamples);
            }
        }
    }

    /// <summary>
    /// Copies one picture into the locked back buffer. Both sides are almost always four
    /// bytes per pixel with no row padding, which makes this a single block move; the
    /// row-by-row path is there for the case where WPF pads its stride.
    /// </summary>
    private static void CopyInto(WriteableBitmap bitmap, DecodedVideoFrame frame)
    {
        var destinationStride = bitmap.BackBufferStride;
        var destination = bitmap.BackBuffer;
        var source = frame.Buffer;

        if (frame.Stride == destinationStride)
        {
            Marshal.Copy(source, 0, destination, destinationStride * frame.Height);
            return;
        }

        var rowBytes = Math.Min(frame.Stride, destinationStride);
        for (var y = 0; y < frame.Height; y++)
            Marshal.Copy(source, y * frame.Stride, destination + y * destinationStride, rowBytes);
    }

    private void MeasurePass(long now)
    {
        Interlocked.Increment(ref _compositionPasses);

        if (_lastPassTicks != 0)
        {
            var delta = now - _lastPassTicks;
            // Anything from about five to five hundred passes a second; outside that the
            // window was hidden or the machine stalled, and it says nothing about refresh.
            if (delta > Stopwatch.Frequency / 500 && delta < Stopwatch.Frequency / 5)
                _passIntervalTicks = _passIntervalTicks == 0
                    ? delta
                    : _passIntervalTicks + (delta - _passIntervalTicks) / 8;
        }

        _lastPassTicks = now;
        UpdateRates();
    }

    private void UpdateRates()
    {
        var elapsed = _rateClock.Elapsed;
        if (elapsed.TotalSeconds < 1) return;

        var presented = Interlocked.Read(ref _presentedFrames);
        var passes = Interlocked.Read(ref _compositionPasses);

        _presentedPerSecond = (presented - _presentedAtLastSample) / elapsed.TotalSeconds;
        _compositionPerSecond = (passes - _passesAtLastSample) / elapsed.TotalSeconds;

        _presentedAtLastSample = presented;
        _passesAtLastSample = passes;
        _rateClock.Restart();
    }

    /// <summary>Takes a snapshot of what is on screen right now, or null before the first
    /// frame. UI thread only.</summary>
    public BitmapSource? Snapshot()
    {
        var bitmap = _bitmap;
        if (bitmap is null) return null;

        var copy = new WriteableBitmap(bitmap);
        copy.Freeze();
        return copy;
    }

    /// <summary>Forgets the accumulated statistics, so a new session starts from zero.</summary>
    public void ResetStatistics()
    {
        Interlocked.Exchange(ref _presentedFrames, 0);
        Interlocked.Exchange(ref _latencySumMicroseconds, 0);
        Interlocked.Exchange(ref _latencySamples, 0);
        Interlocked.Exchange(ref _compositionPasses, 0);
        _pacer.ResetStatistics();
        _presentedAtLastSample = 0;
        _passesAtLastSample = 0;
        _rateClock.Restart();
    }

    public void Dispose()
    {
        UnhookRendering();
        ClearCore();
        _pacer.Dispose();
    }
}
