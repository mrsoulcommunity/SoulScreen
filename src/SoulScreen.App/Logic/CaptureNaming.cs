using System.Globalization;
using System.IO;

namespace SoulScreen.App.Logic;

/// <summary>What a file in the capture folder is.</summary>
public enum CaptureKind
{
    Screenshot,
    Recording,
}

/// <summary>
/// Names capture files and recognises them again.
/// <para>
/// Names carry the local time to the second, formatted with the invariant culture: under a
/// culture with its own calendar the same instant would otherwise be spelled differently
/// from one machine to the next, and the files would stop sorting by name. Two captures in
/// the same second used to share a name, and the second silently overwrote the first; a
/// numbered suffix now keeps both.
/// </para>
/// Deliberately free of WPF so it can be tested on its own.
/// </summary>
internal static class CaptureNaming
{
    public const string Prefix = "SoulScreen-";

    /// <summary>A path in <paramref name="directory"/> that does not exist yet.</summary>
    /// <param name="extension">With its dot, e.g. ".png".</param>
    /// <param name="exists">Checks a path; File.Exists unless a test supplies its own.</param>
    public static string NewPath(string directory, DateTime localTime, string extension, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var stem = Prefix + localTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        var path = Path.Combine(directory, stem + extension);
        for (var suffix = 2; exists(path); suffix++)
            path = Path.Combine(directory, $"{stem}-{suffix.ToString(CultureInfo.InvariantCulture)}{extension}");
        return path;
    }

    /// <summary>What a file is, judged by its name, or null for anything SoulScreen did not make.</summary>
    public static CaptureKind? KindOf(string path)
    {
        var name = Path.GetFileName(path);
        if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;

        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" => CaptureKind.Screenshot,
            ".mp4" => CaptureKind.Recording,
            _ => null,
        };
    }

    /// <summary>"940 KB", "12.4 MB": one decimal only where it says something.</summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        < 0 => "-",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0 / 1024.0:0.#} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} GB"),
    };
}
