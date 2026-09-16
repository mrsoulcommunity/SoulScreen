using SoulScreen.App.Logic;

namespace SoulScreen.Tests;

public class OcrIndexTests
{
    [Fact]
    public void EmptyOrNullTextNeverMatches()
    {
        Assert.False(OcrIndex.Matches(null, "hello"));
        Assert.False(OcrIndex.Matches("", "hello"));
    }

    [Fact]
    public void AnEmptyQueryMatchesAnyNonEmptyText()
    {
        Assert.True(OcrIndex.Matches("some recognised text", ""));
        Assert.True(OcrIndex.Matches("some recognised text", "   "));
    }

    [Fact]
    public void EveryTermMustAppearSomewhereInTheText()
    {
        var text = "Wi-Fi password: Summer2026!";
        Assert.True(OcrIndex.Matches(text, "password"));
        Assert.True(OcrIndex.Matches(text, "wi-fi Summer2026"));
        Assert.False(OcrIndex.Matches(text, "password ethernet"));
    }

    [Fact]
    public void MatchingIgnoresCase()
    {
        Assert.True(OcrIndex.Matches("ERROR CODE 500", "error code"));
    }

    [Fact]
    public void IsFreshComparesTheExactLastWriteTime()
    {
        var writtenUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var entry = new OcrCacheEntry("a.png", writtenUtc, "text");
        Assert.True(OcrIndex.IsFresh(entry, writtenUtc));
        Assert.False(OcrIndex.IsFresh(entry, writtenUtc.AddSeconds(1)));
    }

    [Fact]
    public void PruneMissingDropsEntriesForFilesThatAreGone()
    {
        var entries = new[]
        {
            new OcrCacheEntry("a.png", DateTime.UtcNow, "a"),
            new OcrCacheEntry("b.png", DateTime.UtcNow, "b"),
            new OcrCacheEntry("c.png", DateTime.UtcNow, "c"),
        };
        var existing = new HashSet<string> { "a.png", "c.png" };

        var kept = OcrIndex.PruneMissing(entries, existing);
        Assert.Equal(2, kept.Count);
        Assert.Contains(kept, e => e.Path == "a.png");
        Assert.Contains(kept, e => e.Path == "c.png");
    }

    [Fact]
    public void NotYetIndexedSkipsFreshEntriesAndIncludesEverythingElse()
    {
        var writtenUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var cache = new Dictionary<string, OcrCacheEntry>
        {
            ["fresh.png"] = new OcrCacheEntry("fresh.png", writtenUtc, "fresh text"),
            ["stale.png"] = new OcrCacheEntry("stale.png", writtenUtc, "old text"),
        };

        var pending = OcrIndex.NotYetIndexed(
            ["fresh.png", "stale.png", "new.png"],
            cache,
            path => path switch
            {
                "fresh.png" => writtenUtc,
                "stale.png" => writtenUtc.AddMinutes(5), // changed on disk since it was cached
                _ => DateTime.UtcNow,
            });

        Assert.DoesNotContain("fresh.png", pending);
        Assert.Contains("stale.png", pending);
        Assert.Contains("new.png", pending);
    }
}
