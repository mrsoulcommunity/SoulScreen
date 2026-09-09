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
/// The picture reaches the screen through a shared memory section WPF draws straight out of.
/// The image source is assigned once per geometry and never replaced: swapping it per frame
/// makes WPF rebuild its render-side resource each time, which is plainly visible as flicker.
/// </para>
/// <para>
/// The decode thread hands pictures over through a three-slot buffer, and the UI thread
/// copies the newest into the section during the render pass that will show it. Writing the
/// section only on the UI thread, immediately before composition, is what keeps a half-drawn
/// frame from ever reaching the screen.
/// </para>
/// <para>
/// Presentation is paced by the compositor rather than by arrival: whatever picture is in
/// the buffer when a frame is composed is the one shown. A phone sending faster than the
/// display refreshes simply has its extra frames overwritten, which is what keeps the image
/// current instead of progressively late.
/// </para>
/// </summary>
public sealed class VideoSurface : Image, IDisposable
{
    /// <summary>
    /// Composition passes a replaced buffer is kept mapped for. WPF's render thread reads a
    /// section asynchronously and can still be drawing one we have stopped using, so a
    /// rotation frees the old memory a few frames late rather than immediately.
    /// </summary>
    private const int RetireDelayFrames = 4;

    private readonly ILogger _log = Log.For("surface");

    /// <summary>
    /// Guards which buffer and slots are current. Held only long enough to read or swap
    /// those references - never across a copy - so the decode thread is never made to wait
    /// on the UI thread.
    /// </summary>
    private readonly object _bufferLock = new();

    /// <summary>Buffers waiting out their delay before release. UI thread only.</summary>
    private readonly List<(SharedFrameBuffer Buffer, int FramesRemaining)> _retired = [];

    private SharedFrameBuffer? _buffer;

    /// <summary>Hand-off between the decode thread and the UI thread.</summary>
    private FrameSlots? _slots;

    /// <summary>Geometry the decoder asked for but the UI thread has not allocated yet.</summary>
    private (int Width, int Height)? _pendingGeometry;

    /// <summary>When the picture waiting in the slots left the decoder.</summary>
    private DateTime _frameDecodedAtUtc;

    private bool _renderingHooked;
    private long _presentedFrames;
    private long _supersededFrames;
    private long _latencySumMicroseconds;
    private long _latencySamples;

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
    /// Pictures overwritten before they could be drawn, because another arrived within the
    /// same composition pass. Expected whenever the phone outruns the display.
    /// </summary>
    public long SupersededFrameCount => Interlocked.Read(ref _supersededFrames);

    /// <summary>Size of the picture currently displayed, or empty before the first frame.</summary>
    public Size VideoSize { get; private set; }

    /// <summary>
    /// Mean time from a picture leaving the decoder to reaching the screen. The render half
    /// of the path only; it does not include the network or the decode itself.
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
        FrameSlots? slots;
        lock (_bufferLock)
        {
            var buffer = _buffer;
            if (buffer is null || buffer.Width != frame.Width || buffer.Height != frame.Height)
            {
                // The image belongs to the dispatcher, so allocation has to happen there.
                // This picture is dropped; the next one lands in the new buffer.
                RequestGeometryLocked(frame.Width, frame.Height);
                return;
            }

            slots = _slots;
            _frameDecodedAtUtc = frame.DecodedAtUtc;
        }

        if (slots is null) return;

        // Outside the lifetime lock: the slots outlive a geometry change independently, and
        // this copy is the longest thing on this path.
        var destination = slots.BeginWrite();
        var source = frame.Buffer;
        if (source.Length < slots.ByteCount) return;
        Array.Copy(source, destination, slots.ByteCount);

        if (slots.Publish()) Interlocked.Increment(ref _supersededFrames);
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

        lock (_bufferLock)
        {
            Retire(_buffer);
            _buffer = null;
            _slots = null;
            _pendingGeometry = null;
        }
    }

    /// <summary>Notes a geometry to allocate. Caller must hold <see cref="_bufferLock"/>.</summary>
    private void RequestGeometryLocked(int width, int height)
    {
        if (_pendingGeometry is { } pending && pending.Width == width && pending.Height == height) return;
        _pendingGeometry = (width, height);
        Dispatcher.BeginInvoke(() => Allocate(width, height));
    }

    private void Allocate(int width, int height)
    {
        SharedFrameBuffer created;
        try
        {
            created = SharedFrameBuffer.Create(width, height);
        }
        catch (Exception ex)
        {
            _log.Error($"could not allocate a {width}x{height} frame buffer", ex);
            lock (_bufferLock) _pendingGeometry = null;
            return;
        }

        lock (_bufferLock)
        {
            Retire(_buffer);
            _buffer = created;
            _slots = new FrameSlots(created.ByteCount);
            _pendingGeometry = null;
        }

        // Assigned once for this geometry. Every subsequent frame is an Invalidate on the
        // same image, which is what keeps WPF from rebuilding its texture each frame.
        Source = created.Bitmap;
        VideoSize = new Size(width, height);
        _log.Info($"frame buffer allocated for {width}x{height}");
        VideoSizeChanged?.Invoke(this, VideoSize);
    }

    private void Retire(SharedFrameBuffer? buffer)
    {
        if (buffer is null) return;
        _retired.Add((buffer, RetireDelayFrames));
    }

    /// <summary>Ages the retirement list by one composition pass, releasing anything due.</summary>
    private void DrainRetired()
    {
        for (var i = _retired.Count - 1; i >= 0; i--)
        {
            var (buffer, remaining) = _retired[i];
            if (remaining > 1)
            {
                _retired[i] = (buffer, remaining - 1);
                continue;
            }

            _retired.RemoveAt(i);
            buffer.Dispose();
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
        if (_retired.Count > 0) DrainRetired();

        // Only this thread assigns these, so no lock is needed to read them.
        var buffer = _buffer;
        var slots = _slots;
        if (buffer is null || slots is null || !slots.HasReady) return;

        var picture = slots.BeginRead();
        if (picture is null) return;

        DateTime decodedAtUtc;
        try
        {
            lock (_bufferLock) decodedAtUtc = _frameDecodedAtUtc;
            buffer.Write(picture);
        }
        finally
        {
            slots.EndRead();
        }

        // The pixels behind the image changed without WPF knowing; this is what tells it.
        buffer.Bitmap.Invalidate();
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
        lock (_bufferLock) return _buffer?.Snapshot();
    }

    /// <summary>Forgets the accumulated statistics, so a new session starts from zero.</summary>
    public void ResetStatistics()
    {
        Interlocked.Exchange(ref _presentedFrames, 0);
        Interlocked.Exchange(ref _supersededFrames, 0);
        Interlocked.Exchange(ref _latencySumMicroseconds, 0);
        Interlocked.Exchange(ref _latencySamples, 0);
    }

    public void Dispose()
    {
        UnhookRendering();
        ClearCore();

        // Nothing pumps the retirement list once rendering is unhooked and the window is
        // going away, so release the mappings now.
        foreach (var (buffer, _) in _retired) buffer.Dispose();
        _retired.Clear();
    }
}
