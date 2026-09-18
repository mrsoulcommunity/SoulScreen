using SoulScreen.App.Logic;

namespace SoulScreen.Tests;

public class CaptureFavoritesTests
{
    [Fact]
    public void AddStarsAPathThatWasNotStarred()
    {
        var favorites = new List<string>();
        Assert.True(CaptureFavorites.Add(favorites, "C:\\c\\a.png"));
        Assert.Single(favorites);
    }

    [Fact]
    public void AddingTwiceIsANoOp()
    {
        var favorites = new List<string> { "C:\\c\\a.png" };
        Assert.False(CaptureFavorites.Add(favorites, "C:\\c\\a.png"));
        Assert.Single(favorites);
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        var favorites = new List<string> { "C:\\Captures\\A.PNG" };
        Assert.True(CaptureFavorites.Contains(favorites, "c:\\captures\\a.png"));
    }

    [Fact]
    public void RemoveDropsAStarredPath()
    {
        var favorites = new List<string> { "C:\\c\\a.png", "C:\\c\\b.png" };
        Assert.True(CaptureFavorites.Remove(favorites, "C:\\c\\a.png"));
        Assert.Single(favorites);
        Assert.False(CaptureFavorites.Remove(favorites, "C:\\c\\a.png"));
    }

    [Fact]
    public void ToggleFlipsBackAndForth()
    {
        var favorites = new List<string>();
        Assert.True(CaptureFavorites.Toggle(favorites, "C:\\c\\a.png"));
        Assert.Single(favorites);
        Assert.False(CaptureFavorites.Toggle(favorites, "C:\\c\\a.png"));
        Assert.Empty(favorites);
    }

    [Fact]
    public void ToggleOfABlankPathStarsNothing()
    {
        var favorites = new List<string>();
        // Not a capture, so there is no star to report as set; the caller must not be told the
        // tile it belongs to is now a favorite.
        Assert.False(CaptureFavorites.Toggle(favorites, string.Empty));
        Assert.False(CaptureFavorites.Toggle(favorites, "   "));
        Assert.Empty(favorites);
    }

    [Fact]
    public void SanitiseDropsBlankAndDuplicateEntries()
    {
        var cleaned = CaptureFavorites.Sanitise(["C:\\c\\a.png", "", "  ", "C:\\c\\A.PNG", "C:\\c\\b.png"]);
        Assert.Equal(2, cleaned.Count);
        Assert.Contains("C:\\c\\a.png", cleaned);
        Assert.Contains("C:\\c\\b.png", cleaned);
    }

    [Fact]
    public void SanitiseOfNullIsEmpty()
    {
        Assert.Empty(CaptureFavorites.Sanitise(null));
    }

    [Fact]
    public void PruneMissingKeepsOnlyExistingFiles()
    {
        var favorites = new[] { "a.png", "b.png", "c.png" };
        var kept = CaptureFavorites.PruneMissing(favorites, path => path != "b.png");
        Assert.Equal(["a.png", "c.png"], kept);
    }
}
