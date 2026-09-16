namespace SoulScreen.App.Logic;

/// <summary>One screenshot's OCR result, cached to disk so the same file is never read by
/// the OCR engine twice.</summary>
/// <param name="Path">Absolute path of the screenshot the text came from.</param>
/// <param name="WrittenUtc">The file's last-write time when it was OCR'd - if the file on
/// disk has since changed, the entry is stale and must be redone.</param>
/// <param name="Text">The recognised text, verbatim. Empty for a screenshot with no text
/// SoulScreen could read, which is itself worth remembering so it is not retried forever.</param>
public sealed record OcrCacheEntry(string Path, DateTime WrittenUtc, string Text);

/// <summary>
/// Turns a folder of screenshots into something searchable by the words on screen, not just
/// the file name - "find the screenshot with the error code" or "find the one with the
/// Wi-Fi password on it" - without a server, an index database, or leaving the machine.
/// <para>
/// Deliberately free of WPF and of the OCR engine itself (see the WinRT-backed extractor in
/// <c>SoulScreen.App</c>'s own namespace): what belongs here is the parts that can be
/// tested without a bitmap, a window, or a language pack - matching, and deciding which
/// cached entries are still good.
/// </para>
/// </summary>
public static class OcrIndex
{
    /// <summary>
    /// True when every word typed appears somewhere in <paramref name="text"/>, the same
    /// all-terms-must-match rule <see cref="CaptureFilter.Matches"/> applies to file names,
    /// so one search box narrows by name and by on-screen text at once rather than needing
    /// two different query languages.
    /// </summary>
    public static bool Matches(string? text, string query)
    {
        if (string.IsNullOrEmpty(text)) return false;

        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return true;

        foreach (var term in terms)
            if (!text.Contains(term, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>
    /// True when a cached entry can be trusted for a file with the given last-write time -
    /// false for a file that has changed since it was OCR'd (overwritten with the same
    /// name) or one whose entry has since been superseded some other way.
    /// </summary>
    public static bool IsFresh(OcrCacheEntry entry, DateTime currentWrittenUtc) =>
        entry.WrittenUtc == currentWrittenUtc;

    /// <summary>
    /// The cache with entries for files that no longer exist removed, so deleting
    /// screenshots keeps the cache file from growing without bound.
    /// </summary>
    public static List<OcrCacheEntry> PruneMissing(IEnumerable<OcrCacheEntry> entries, ISet<string> existingPaths) =>
        entries.Where(entry => existingPaths.Contains(entry.Path)).ToList();

    /// <summary>
    /// Screenshots in <paramref name="paths"/> that either have no cache entry yet, or
    /// whose entry no longer matches the file's current last-write time - what the OCR pass
    /// still has work to do on.
    /// </summary>
    public static List<string> NotYetIndexed(
        IEnumerable<string> paths, IReadOnlyDictionary<string, OcrCacheEntry> cache, Func<string, DateTime> writtenUtc)
    {
        var pending = new List<string>();
        foreach (var path in paths)
        {
            if (cache.TryGetValue(path, out var entry) && IsFresh(entry, writtenUtc(path))) continue;
            pending.Add(path);
        }
        return pending;
    }
}
