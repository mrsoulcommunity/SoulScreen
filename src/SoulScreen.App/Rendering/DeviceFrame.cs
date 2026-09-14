using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Controls;

namespace SoulScreen.App.Rendering;

/// <summary>
/// Puts a screenshot inside a drawn iPhone: a dark bezel with the screen's own rounded corners,
/// a soft shadow, and a transparent background - the picture a slide, a document or an App Store
/// page wants, rather than a bare rectangle of screen.
/// <para>
/// Everything is proportional to the screen's shorter side, so a frame around a 1080-pixel-wide
/// capture looks the same as one around a 720-pixel one, and the screen itself is drawn at its
/// own resolution: framing never scales the phone's pixels.
/// </para>
/// </summary>
public static class DeviceFrame
{
    /// <summary>Bezel width as a share of the screen's shorter side.</summary>
    public const double BezelRatio = 0.045;

    /// <summary>Room left around the device for its shadow, as a share of the shorter side.</summary>
    public const double MarginRatio = 0.08;

    /// <summary>
    /// The screen's corner radius as a share of its shorter side. A phone's screen corners are
    /// about a ninth of its width; anything squarer - an iPad, or a picture letterboxed into a
    /// landscape stream - gets a gentler curve, as the window's rounded picture does.
    /// </summary>
    public static double CornerRatio(double width, double height)
    {
        var aspect = width / height;
        var phoneShaped = aspect < 0.62 || aspect > 1 / 0.62;
        return phoneShaped ? 0.11 : 0.035;
    }

    /// <summary>The size of the framed image for a screen of the given size, in pixels.</summary>
    public static (int Width, int Height) OutputSize(int screenWidth, int screenHeight)
    {
        var shortSide = Math.Min(screenWidth, screenHeight);
        var bezel = Math.Round(shortSide * BezelRatio);
        var margin = Math.Round(shortSide * MarginRatio);
        return ((int)(screenWidth + 2 * (bezel + margin)), (int)(screenHeight + 2 * (bezel + margin)));
    }

    /// <summary>Renders <paramref name="screen"/> inside the frame. Must run on a thread that can
    /// build WPF visuals (an STA thread); the result is frozen and may be used anywhere.</summary>
    public static BitmapSource Compose(BitmapSource screen)
    {
        ArgumentNullException.ThrowIfNull(screen);

        var width = screen.PixelWidth;
        var height = screen.PixelHeight;
        if (width <= 0 || height <= 0) throw new ArgumentException("The screenshot is empty.", nameof(screen));

        var shortSide = Math.Min(width, height);
        var bezel = Math.Round(shortSide * BezelRatio);
        var margin = Math.Round(shortSide * MarginRatio);
        var screenRadius = shortSide * CornerRatio(width, height);
        var outerRadius = screenRadius + bezel;
        var (outputWidth, outputHeight) = OutputSize(width, height);

        // Laid out in pixels: the tree is rendered at 96 dpi, where one unit is one pixel, so the
        // phone's pixels land one-for-one on the output's.
        var device = new Grid
        {
            Width = width + 2 * bezel,
            Height = height + 2 * bezel,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // The body: near-black, with a hairline of lighter metal at its edge and a shadow beneath.
        device.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(outerRadius),
            Background = Frozen(Color.FromRgb(0x0B, 0x0B, 0x0C)),
            BorderBrush = Frozen(Color.FromRgb(0x3A, 0x3A, 0x3C)),
            BorderThickness = new Thickness(Math.Max(1, shortSide / 360.0)),
            Effect = new DropShadowEffect
            {
                BlurRadius = margin * 0.9,
                ShadowDepth = margin * 0.18,
                Direction = 270,
                Opacity = 0.35,
                Color = Colors.Black,
                RenderingBias = RenderingBias.Quality,
            },
        });

        var picture = new Image
        {
            Source = screen,
            Width = width,
            Height = height,
            Stretch = Stretch.Fill,
            Margin = new Thickness(bezel),
            Clip = new RectangleGeometry(new Rect(0, 0, width, height), screenRadius, screenRadius),
        };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.NearestNeighbor);
        device.Children.Add(picture);

        var root = new Grid { Width = outputWidth, Height = outputHeight, Background = Brushes.Transparent };
        root.Children.Add(device);
        root.Measure(new Size(outputWidth, outputHeight));
        root.Arrange(new Rect(0, 0, outputWidth, outputHeight));
        root.UpdateLayout();

        var target = new RenderTargetBitmap(outputWidth, outputHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(root);
        target.Freeze();
        return target;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
