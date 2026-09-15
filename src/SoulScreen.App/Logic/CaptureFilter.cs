using System.IO;

namespace SoulScreen.App.Logic;

/// <summary>How the captures gallery orders itself.</summary>
public enum CaptureSortOrder
{
    /// <summary>Most recently written first - the gallery's default.</summary>
    NewestFirst,
    /// <summary>Oldest first, for finding where a session began.</summary>
    OldestFirst,
    /// <summary>Biggest files first, for finding what is filling the drive.</summary>
    LargestFirst,
}

/// <summary>
/// Search and sort for the captures gallery.
/// <para>
/// Deliberately free of WPF so the filtering can be tested on its own, like the rest of
/// the app's Logic.
/// </para>
/// </summary>
internal static class CaptureFilter
{
    /// <summary>
    /// True when <paramref name="fileName"/> answers to what was typed. Every word typed
    /// has to appear in the name, so "rec 5" narrows rather than widens; matching ignores
    /// case, and there is no pattern language to learn.
    /// <para>
    /// The kind of capture counts as part of its name: "rec" finds recordings and "shot"
    /// finds screenshots even though neither word appears in the file name itself, which
    /// is how people actually look for them.
    /// </para>
    /// </summary>
    public static bool Matches(string fileName, string query)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return true;

        var kindWords = KindWords(fileName);
        var kindText = string.Join(" ", kindWords);
        foreach (var term in terms)
        {
            if (fileName.Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
            // "rec" answers through "recording": typed short forms count as the word.
            if (kindText.Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
            return false;
        }
        return true;
    }

    /// <summary>What the capture is, in the words a user might type for it - derived from
    /// the extension, since that is what distinguishes a recording from a screenshot.</summary>
    private static string[] KindWords(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.ToLowerInvariant() switch
        {
            ".mp4" or ".mov" or ".mkv" => ["recording", "video", "movie"],
            ".png" or ".jpg" or ".jpeg" => ["screenshot", "shot", "picture", "image"],
            _ => [],
        };
    }

    /// <summary>Sorts <paramref name="captures"/> in place into the given order.</summary>
    public static void Sort<T>(List<T> captures, Func<T, DateTime> modified, Func<T, long> size, CaptureSortOrder order)
    {
        switch (order)
        {
            case CaptureSortOrder.OldestFirst:
                captures.Sort((a, b) => modified(a).CompareTo(modified(b)));
                break;
            case CaptureSortOrder.LargestFirst:
                captures.Sort((a, b) =>
                {
                    var bySize = size(b).CompareTo(size(a));
                    // Same size, oldest first: a stable, sensible tiebreak.
                    return bySize != 0 ? bySize : modified(a).CompareTo(modified(b));
                });
                break;
            default:
                captures.Sort((a, b) => modified(b).CompareTo(modified(a)));
                break;
        }
    }

    /// <summary>"14 items · 1.8 GB", "1 item · 940 KB", or "empty" for none.</summary>
    public static string DescribeCount(int count, long totalBytes)
    {
        if (count <= 0) return "empty";
        return count == 1
            ? $"1 item · {CaptureNaming.FormatSize(totalBytes)}"
            : $"{count} items · {CaptureNaming.FormatSize(totalBytes)}";
    }
}
