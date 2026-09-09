using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SoulScreen.App.Rendering;

/// <summary>
/// A small ring of shared-memory images that WPF can draw straight out of.
/// <para>
/// The usual way to show video in WPF - <see cref="WriteableBitmap.WritePixels(Int32Rect, Array, int, int)"/> -
/// copies the picture into WPF's own back buffer on the UI thread, on top of whatever copy
/// got it there. Backing the image with a file mapping instead lets the decode thread write
/// the pixels once, directly into the memory the compositor reads; presenting is then just
/// an <see cref="InteropBitmap.Invalidate"/> and a reference swap.
/// </para>
/// <para>
/// Three buffers rather than two: the compositor may still be reading the picture it was
/// handed while the next one is being written and a third is queued, and a writer that
/// caught up with the reader would tear.
/// </para>
/// </summary>
internal sealed class FrameBufferRing : IDisposable
{
    private const int BufferCount = 3;

    private const uint PageReadWrite = 0x04;
    private const uint FileMapAllAccess = 0xF001F;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    private readonly Buffer[] _buffers = new Buffer[BufferCount];
    private readonly object _stateLock = new();

    /// <summary>Buffer the writer is filling.</summary>
    private int _writeIndex;

    /// <summary>Buffer finished but not yet shown, or -1.</summary>
    private int _pendingIndex = -1;

    /// <summary>Buffer the compositor is currently reading, or -1.</summary>
    private int _displayIndex = -1;

    /// <summary>When the pending picture left the decoder, for the latency figure.</summary>
    private DateTime _pendingDecodedAtUtc;

    private FrameBufferRing(int width, int height, int stride)
    {
        Width = width;
        Height = height;
        Stride = stride;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }

    public int ByteCount => Stride * Height;

    /// <summary>
    /// Allocates a ring for one picture geometry. Must be called on the UI thread: the
    /// images it creates belong to that dispatcher.
    /// </summary>
    public static FrameBufferRing Create(int width, int height)
    {
        // Four bytes per pixel, already a multiple of four, so no row padding is needed.
        var stride = width * 4;
        var ring = new FrameBufferRing(width, height, stride);

        try
        {
            for (var i = 0; i < BufferCount; i++)
                ring._buffers[i] = Buffer.Allocate(width, height, stride);
            return ring;
        }
        catch
        {
            ring.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Address of the buffer to write the next picture into. Safe to use from any thread;
    /// valid until the matching <see cref="PublishFrom"/>.
    /// </summary>
    public IntPtr BeginWrite()
    {
        int index;
        lock (_stateLock) index = _writeIndex;
        return _buffers[index].View;
    }

    /// <summary>True when a written picture is still waiting to be shown.</summary>
    public bool HasPending
    {
        get { lock (_stateLock) return _pendingIndex >= 0; }
    }

    /// <summary>
    /// Marks the buffer just written as the one to show next, remembering when it left the
    /// decoder.
    /// </summary>
    public void PublishFrom(DateTime decodedAtUtc)
    {
        lock (_stateLock)
        {
            _pendingIndex = _writeIndex;
            _pendingDecodedAtUtc = decodedAtUtc;
            _writeIndex = NextFree();
        }
    }

    /// <summary>
    /// Takes the pending picture for display, or null when nothing new has arrived. Must be
    /// called on the UI thread.
    /// </summary>
    public InteropBitmap? TakeForDisplay(out DateTime decodedAtUtc)
    {
        int index;
        lock (_stateLock)
        {
            decodedAtUtc = _pendingDecodedAtUtc;
            if (_pendingIndex < 0) return null;
            index = _pendingIndex;
            _pendingIndex = -1;
            _displayIndex = index;
        }

        var bitmap = _buffers[index].Bitmap;
        // The pixels behind the image changed without WPF knowing; this is what tells it.
        bitmap.Invalidate();
        return bitmap;
    }

    /// <summary>Picks a buffer that is neither queued for display nor being displayed.</summary>
    private int NextFree()
    {
        for (var offset = 1; offset <= BufferCount; offset++)
        {
            var candidate = (_writeIndex + offset) % BufferCount;
            if (candidate != _pendingIndex && candidate != _displayIndex) return candidate;
        }
        // Unreachable with three buffers and at most two reserved, but never return one in use.
        return _writeIndex;
    }

    /// <summary>Copies whatever is on screen into a standalone, frozen image.</summary>
    public BitmapSource? Snapshot()
    {
        int index;
        lock (_stateLock) index = _displayIndex;
        if (index < 0) return null;

        var copy = new WriteableBitmap(Width, Height, 96, 96, PixelFormats.Bgr32, null);
        copy.WritePixels(new Int32Rect(0, 0, Width, Height), _buffers[index].View, ByteCount, Stride);
        copy.Freeze();
        return copy;
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            _pendingIndex = -1;
            _displayIndex = -1;
        }

        for (var i = 0; i < _buffers.Length; i++)
        {
            _buffers[i].Dispose();
            _buffers[i] = default;
        }
    }

    /// <summary>One shared section and the image WPF reads it through.</summary>
    private struct Buffer : IDisposable
    {
        public IntPtr Section;
        public IntPtr View;
        public InteropBitmap Bitmap;

        public static Buffer Allocate(int width, int height, int stride)
        {
            var byteCount = (uint)(stride * height);

            var section = CreateFileMapping(InvalidHandleValue, IntPtr.Zero, PageReadWrite, 0, byteCount, null);
            if (section == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

            var view = MapViewOfFile(section, FileMapAllAccess, 0, 0, byteCount);
            if (view == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                CloseHandle(section);
                throw new Win32Exception(error);
            }

            // Bgr32 rather than Bgra32: the decoder fills alpha with 255 and asking the
            // compositor to blend every pixel against it costs real time for no effect.
            var bitmap = (InteropBitmap)Imaging.CreateBitmapSourceFromMemorySection(
                section, width, height, PixelFormats.Bgr32, stride, 0);

            return new Buffer { Section = section, View = view, Bitmap = bitmap };
        }

        public void Dispose()
        {
            if (View != IntPtr.Zero) UnmapViewOfFile(View);
            if (Section != IntPtr.Zero) CloseHandle(Section);
            View = IntPtr.Zero;
            Section = IntPtr.Zero;
            Bitmap = null!;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileMapping(IntPtr file, IntPtr attributes, uint protect,
        uint maximumSizeHigh, uint maximumSizeLow, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr mapping, uint desiredAccess,
        uint offsetHigh, uint offsetLow, uint bytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnmapViewOfFile(IntPtr address);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
