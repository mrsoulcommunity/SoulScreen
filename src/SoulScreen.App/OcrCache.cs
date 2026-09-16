using System.IO;
using System.Text.Json;
using SoulScreen.App.Logic;
using SoulScreen.Core.Logging;

namespace SoulScreen.App;

/// <summary>
/// Persists <see cref="OcrCacheEntry"/> rows to a small JSON file beside settings.json, so a
/// screenshot is only ever OCR'd once, not again on every gallery open or app restart.
/// </summary>
internal sealed class OcrCache
{
    private static readonly ILogger Log_ = Log.For("ocr");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private string FilePath => Path.Combine(AppSettings.Directory, "ocr-index.json");

    private readonly Dictionary<string, OcrCacheEntry> _entries;

    private OcrCache(Dictionary<string, OcrCacheEntry> entries) => _entries = entries;

    public static OcrCache Load()
    {
        var path = Path.Combine(AppSettings.Directory, "ocr-index.json");
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<List<OcrCacheEntry>>(File.ReadAllText(path), SerializerOptions);
                if (loaded is not null)
                    return new OcrCache(loaded.Where(e => e is not null)
                        .ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex)
        {
            // A corrupt or foreign-format cache is not worth blocking search over - it is
            // rebuilt from scratch, at the cost of re-reading screenshots once.
            Log_.Warn($"could not read {path}; starting a fresh OCR index", ex);
        }

        return new OcrCache(new Dictionary<string, OcrCacheEntry>(StringComparer.OrdinalIgnoreCase));
    }

    public IReadOnlyDictionary<string, OcrCacheEntry> Entries => _entries;

    public void Set(string path, DateTime writtenUtc, string text) =>
        _entries[path] = new OcrCacheEntry(path, writtenUtc, text);

    /// <summary>Drops entries for files that no longer exist, then writes the cache back.
    /// Failure to save is logged, not thrown - a search that ran fine must not fail just
    /// because writing its cache afterwards did not.</summary>
    public void PruneAndSave(ISet<string> existingPaths)
    {
        var kept = OcrIndex.PruneMissing(_entries.Values, existingPaths);
        _entries.Clear();
        foreach (var entry in kept) _entries[entry.Path] = entry;
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Directory);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_entries.Values.ToList(), SerializerOptions));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log_.Warn($"could not save {FilePath}", ex);
        }
    }
}
