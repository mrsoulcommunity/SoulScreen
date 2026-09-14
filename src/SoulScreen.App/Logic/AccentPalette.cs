using System.Globalization;

namespace SoulScreen.App.Logic;

/// <summary>The accent colour: Apple's system colours, in the order System Settings lists them.</summary>
public enum AccentColor
{
    Blue,
    Purple,
    Pink,
    Red,
    Orange,
    Yellow,
    Green,
    Graphite,
}

/// <summary>An opaque colour, kept apart from WPF's so the palette maths can be tested alone.</summary>
internal readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb Parse(string hex)
    {
        var value = hex.TrimStart('#');
        if (value.Length != 6) throw new FormatException($"'{hex}' is not a #RRGGBB colour.");
        return new Rgb(
            byte.Parse(value[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(value[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(value[4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    public static readonly Rgb White = new(255, 255, 255);
    public static readonly Rgb Black = new(0, 0, 0);

    /// <summary>This colour moved <paramref name="amount"/> of the way towards <paramref name="other"/>.</summary>
    public Rgb Mix(Rgb other, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return new Rgb(Lerp(R, other.R), Lerp(G, other.G), Lerp(B, other.B));
        byte Lerp(byte from, byte to) => (byte)Math.Round(from + (to - from) * amount);
    }

    /// <summary>WCAG relative luminance, 0 for black to 1 for white.</summary>
    public double Luminance
    {
        get
        {
            return 0.2126 * Channel(R) + 0.7152 * Channel(G) + 0.0722 * Channel(B);
            static double Channel(byte c)
            {
                var s = c / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
        }
    }
}

/// <summary>Every shade the theme derives from one accent.</summary>
internal readonly record struct AccentShades(Rgb Accent, Rgb Hover, Rgb Pressed, Rgb Muted, byte TintAlpha, Rgb OnAccent);

/// <summary>
/// Derives the accent shades for a theme. Each accent has a dark and a light value, as Apple's
/// system colours do; hover lifts it towards white, pressed sinks it, and the text drawn on it
/// turns dark where the colour is too light for white to read - yellow, in practice.
/// </summary>
internal static class AccentPalette
{
    private static readonly Rgb DarkText = Rgb.Parse("#1D1D1F");

    public static AccentShades For(AccentColor accent, bool dark)
    {
        // Blue is the palette the app shipped with, and keeps its hand-tuned shades exactly.
        if (accent == AccentColor.Blue)
        {
            return dark
                ? new AccentShades(Rgb.Parse("#0A84FF"), Rgb.Parse("#3395FF"), Rgb.Parse("#0071E3"), Rgb.Parse("#1F4D80"), 0x33, Rgb.White)
                : new AccentShades(Rgb.Parse("#007AFF"), Rgb.Parse("#2B8FFF"), Rgb.Parse("#0064D6"), Rgb.Parse("#B9D6FF"), 0x26, Rgb.White);
        }

        var baseColour = Rgb.Parse(BaseHex(accent, dark));
        return new AccentShades(
            baseColour,
            baseColour.Mix(Rgb.White, 0.17),
            baseColour.Mix(Rgb.Black, dark ? 0.11 : 0.16),
            dark ? baseColour.Mix(Rgb.Black, 0.5) : baseColour.Mix(Rgb.White, 0.73),
            (byte)(dark ? 0x33 : 0x26),
            baseColour.Luminance > 0.55 ? DarkText : Rgb.White);
    }

    private static string BaseHex(AccentColor accent, bool dark) => accent switch
    {
        AccentColor.Purple => dark ? "#BF5AF2" : "#AF52DE",
        AccentColor.Pink => dark ? "#FF375F" : "#FF2D55",
        AccentColor.Red => dark ? "#FF453A" : "#FF3B30",
        AccentColor.Orange => dark ? "#FF9F0A" : "#FF9500",
        // A shade deeper than Apple's in the light theme: the accent is also link text, and
        // #FFCC00 on white can barely be read.
        AccentColor.Yellow => dark ? "#FFD60A" : "#E0A800",
        AccentColor.Green => dark ? "#30D158" : "#34C759",
        AccentColor.Graphite => dark ? "#98989D" : "#8E8E93",
        _ => dark ? "#0A84FF" : "#007AFF",
    };

    /// <summary>The colour a swatch is drawn in: the accent as the current theme shows it.</summary>
    public static Rgb Swatch(AccentColor accent, bool dark) => For(accent, dark).Accent;
}
