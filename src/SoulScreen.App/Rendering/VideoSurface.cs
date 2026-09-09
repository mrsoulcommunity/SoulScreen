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
/// The decode thread writes each picture straight into shared memory the compositor reads
/// from, and the UI thread does nothing per frame but swap a reference and invalidate. That
/// is one copy from the decoder to the screen instead of the three a WriteableBitmap costs,
/// and the UI thread never holds anything the decoder is waiting on: the one lock they share
/// is taken by the UI thread only when the picture geometry changes.
/// </para>
/// <para>
/// Presentation is paced by the compositor rather than by arrival: whatever picture is
/// newest when a frame is composed is the one shown. A phone sending faster than the display
/// refreshes simply has its extra frames superseded, which is what keeps the image current
/// instead of progressively late.
/// </para>
/// </summary>
public sealed class VideoSurface : Image, IDisposable
{
    /// <summary>
    /// Composition passes a retired ring is kept mapped for. Three is comfortably more than
    /// the one frame WPF's render thread can still be holding.
    /// </summary>
    private const int RetireDelayFrames = 3;

    private readonly ILogger _log = Log.For("surface");
    private readonly object _ringLock = new();

    /// <summary>Rings waiting out their delay before the memory is unmapped. UI thread only.</summary>
    private readonly List<(FrameBufferRing Ring, int FramesRemaining)> _retired = [];

    private FrameBufferRing? _ring;

    /// <summary>Geometry requested by the decoder but not yet allocated on the UI thread.</summary>
    private (int Width, int Height)? _pendingGeometry;

    private bool _renderingHooked;
    private long _presentedFrames;
    private long _skippedPresents;
    private long _latencySumMicroseconds;
    private long _latencySamples;

    public VideoSurface()
    {
        Stretch = Stretch.Uniform;
        // Linear, not HighQuality. WPF's high-quality mode is the Fant filter, which is
        // resampled in software and costs milliseconds per frame at this size; bilinear runs
        // on the GPU and is indistinguishable on moving video.
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
        RenderOptions.SetCachingHint(this, CachingHint.Unspecified);
        SnapsToDevicePixels = true;

        Loaded += (_, _) => HookRendering();
        Unloaded += (_, _) => UnhookRendering();
    }

    /// <summary>Pictures actually drawn, at most one per composition pass.</summary>
    public long PresentedFrameCount => Interlocked.Read(ref _presentedFrames);

    /// <summary>
    /// Pictures superseded before they could be drawn, because another arrived within the
    /// same composition pass. Expected when the phone outruns the display.
    /// </summary>
    public long SkippedPresentCount => Interlocked.Read(ref _skippedPresents);

    /// <summary>Size of the picture currently displayed, or empty before the first frame.</summary>
    public Size VideoSize { get; private set; }

    /// <summary>
    /// Mean time between a picture leaving the decoder and reaching the screen, over the
    /// samples taken so far. Only the render half of the path; it does not include the
    /// network or the decode itself.
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
    /// Accepts a decoded frame from any thread. Returns as soon as the pixels are copied;
    /// the caller may recycle the frame immediately.
    /// </summary>
    public void Present(DecodedVideoFrame frame)
    {
        // The lock spans the copy, not just the field read. Unmapping a section while this
        // thread is writing into it would fault, and a rotation does exactly that. It is
        // uncontended on every ordinary frame: the only other taker is a ring swap.
        lock (_ringLock)
        {
            var ring = _ring;
            if (ring is null || ring.Width != frame.Width || ring.Height != frame.Height)
            {
                // Allocating the images has to happen on the dispatcher that owns them, so
                // note the geometry and drop this picture; the next lands in the new ring.
                RequestGeometryLocked(frame.Width, frame.Height);
                return;
            }

            var buffer = frame.Buffer;
            var byteCount = ring.ByteCount;
            if (buffer.Length < byteCount) return;

            // A memcpy into the shared section: the single copy between decoder and screen.
            System.Runtime.InteropServices.Marshal.Copy(buffer, 0, ring.BeginWrite(), byteCount);

            // A picture still queued when the next is published was never shown.
            if (ring.HasPending) Interlocked.Increment(ref _skippedPresents);
            ring.PublishFrom(frame.DecodedAtUtc);
        }
    }

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
        Source = null;
        VideoSize = default;

        lock (_ringLock)
        {
            Retire(_ring);
            _ring = null;
            _pendingGeometry = null;
        }
    }

    /// <summary>Records a geometry to allocate. Caller must hold <see cref="_ringLock"/>.</summary>
    private void RequestGeometryLocked(int width, int height)
    {
        if (_pendingGeometry is { } pending && pending.Width == width && pending.Height == height) return;
        _pendingGeometry = (width, height);
        Dispatcher.BeginInvoke(() => AllocateRing(width, height));
    }

    private void AllocateRing(int width, int height)
    {
        FrameBufferRing created;
        try
        {
            created = FrameBufferRing.Create(width, height);
        }
        catch (Exception ex)
        {
            _log.Error($"could not allocate {width}x{height} frame buffers", ex);
            lock (_ringLock) _pendingGeometry = null;
            return;
        }

        lock (_ringLock)
        {
            Retire(_ring);
            _ring = created;
            _pendingGeometry = null;
        }

        Source = null;
        VideoSize = new Size(width, height);
        _log.Info($"frame buffers allocated for {width}x{height}");
        VideoSizeChanged?.Invoke(this, VideoSize);
    }

    /// <summary>
    /// Queues a ring for disposal a few composition passes from now.
    /// <para>
    /// Unmapping immediately is not safe even once no thread of ours is writing: WPF's own
    /// render thread reads the section asynchronously, and may still be drawing the picture
    /// it was handed. Holding the memory for a few frames costs a few megabytes briefly and
    /// removes the race entirely.
    /// </para>
    /// </summary>
    private void Retire(FrameBufferRing? ring)
    {
        if (ring is null) return;
        _retired.Add((ring, RetireDelayFrames));
    }

    /// <summary>Ages the retirement list by one composition pass, freeing anything due.</summary>
    private void DrainRetired()
    {
        if (_retired.Count == 0) return;

        for (var i = _retired.Count - 1; i >= 0; i--)
        {
            var (ring, remaining) = _retired[i];
            if (remaining > 1)
            {
                _retired[i] = (ring, remaining - 1);
                continue;
            }

            _retired.RemoveAt(i);
            ring.Dispose();
        }
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
        DrainRetired();

        // No lock: _ring is only ever assigned on this thread.
        var ring = _ring;
        if (ring is null) return;

        var taken = ring.TakeForDisplay(out var decodedAtUtc);
        if (taken is null) return;

        // Assigning the same reference again is a no-op, which is exactly right: Invalidate
        // has already told WPF the pixels moved.
        if (!ReferenceEquals(Source, taken)) Source = taken;

        Interlocked.Increment(ref _presentedFrames);

        var latency = (DateTime.UtcNow - decodedAtUtc).TotalMicroseconds;
        if (latency is > 0 and < 1_000_000)
        {
            Interlocked.Add(ref _latencySumMicroseconds, (long)latency);
            Interlocked.Increment(ref _latencySamples);
        }
    }

    /// <summary>Takes a snapshot of what is on screen right now, or null before the first frame.</summary>
    public BitmapSource? Snapshot()
    {
        lock (_ringLock) return _ring?.Snapshot();
    }

    /// <summary>Forgets the accumulated statistics, so a new session starts from zero.</summary>
    public void ResetStatistics()
    {
        Interlocked.Exchange(ref _presentedFrames, 0);
        Interlocked.Exchange(ref _skippedPresents, 0);
        Interlocked.Exchange(ref _latencySumMicroseconds, 0);
        Interlocked.Exchange(ref _latencySamples, 0);
    }

    public void Dispose()
    {
        UnhookRendering();
        ClearCore();

        // Nothing will pump the retirement list once rendering is unhooked, and the window
        // is going away, so release the mappings now.
        foreach (var (ring, _) in _retired) ring.Dispose();
        _retired.Clear();
    }
}
