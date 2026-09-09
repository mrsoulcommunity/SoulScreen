using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SoulScreen.App.Rendering;

/// <summary>
/// One picture-sized block of shared memory that WPF draws straight out of.
/// <para>
/// The usual way to show video in WPF - <c>WriteableBitmap.WritePixels</c> - copies the
/// picture into WPF's own back buffer on the UI thread, on top of whatever copy got it
/// there. Backing the image with a file mapping instead lets the decode thread write the
/// pixels once, directly into the memory the compositor reads; presenting is then nothing
/// but an <see cref="InteropBitmap.Invalidate"/>.
/// </para>
/// <para>
/// Deliberately a single buffer. Rotating through several and swapping
/// <c>Image.Source</c> between them makes WPF rebuild its render-side resource for a
/// different bitmap on every frame, which is plainly visible as flicker. One buffer means
/// the source is set once and never changes. The cost is that a write can in principle
/// overlap the compositor's read - a copy takes a fraction of a millisecond out of each
/// sixteen, so it rarely happens, and a momentary seam is a far smaller artefact than
/// flicker.
/// </para>
/// </summary>
internal sealed class SharedFrameBuffer : IDisposable
{
    private const uint PageReadWrite = 0x04;
    private const uint FileMapAllAccess = 0xF001F;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    private IntPtr _section;
    private IntPtr _view;

    private SharedFrameBuffer(int width, int height, int stride, IntPtr section, IntPtr view, InteropBitmap bitmap)
    {
        Width = width;
        Height = height;
        Stride = stride;
        _section = section;
        _view = view;
        Bitmap = bitmap;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }

    public int ByteCount => Stride * Height;

    /// <summary>The image to hand to WPF. Set it as a source once; it never needs replacing.</summary>
    public InteropBitmap Bitmap { get; }

    /// <summary>Where to write pixels. Valid until this buffer is disposed.</summary>
    public IntPtr View => _view;

    /// <summary>
    /// Allocates a buffer for one picture geometry. Must be called on the UI thread: the
    /// image it creates belongs to that dispatcher.
    /// </summary>
    public static SharedFrameBuffer Create(int width, int height)
    {
        // Four bytes per pixel is already a multiple of four, so rows need no padding.
        var stride = width * 4;
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

        // Bgr32 rather than Bgra32: the decoder fills alpha with 255, and asking the
        // compositor to blend every pixel against it costs real time for no effect.
        var bitmap = (InteropBitmap)Imaging.CreateBitmapSourceFromMemorySection(
            section, width, height, PixelFormats.Bgr32, stride, 0);

        return new SharedFrameBuffer(width, height, stride, section, view, bitmap);
    }

    /// <summary>Copies a picture in. Safe from any thread while the buffer is alive.</summary>
    public void Write(byte[] pixels)
    {
        var view = _view;
        if (view == IntPtr.Zero || pixels.Length < ByteCount) return;
        Marshal.Copy(pixels, 0, view, ByteCount);
    }

    /// <summary>Copies the current contents into a standalone, frozen image.</summary>
    public BitmapSource Snapshot()
    {
        var copy = new WriteableBitmap(Width, Height, 96, 96, PixelFormats.Bgr32, null);
        copy.WritePixels(new System.Windows.Int32Rect(0, 0, Width, Height), _view, ByteCount, Stride);
        copy.Freeze();
        return copy;
    }

    public void Dispose()
    {
        var view = Interlocked.Exchange(ref _view, IntPtr.Zero);
        if (view != IntPtr.Zero) UnmapViewOfFile(view);

        var section = Interlocked.Exchange(ref _section, IntPtr.Zero);
        if (section != IntPtr.Zero) CloseHandle(section);
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
