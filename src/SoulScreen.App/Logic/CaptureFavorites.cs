namespace SoulScreen.App.Logic;

/// <summary>
/// Which captures are starred to keep.
/// <para>
/// A favorite is exempt from the storage budget's pruning (<see cref="CaptureBudget"/>) and
/// searchable by the word "favorite" or "star" in the gallery, the same way a capture's kind
/// answers to "shot" or "rec" in <see cref="CaptureFilter"/>. Paths are matched
/// case-insensitively, since Windows paths are, and a rename or move silently drops a
/// capture from the list rather than leaving a stale entry pointing at nothing - the list is
/// pruned against what is actually on disk each time the gallery refreshes.
/// </para>
/// Deliberately free of WPF so it can be tested on its own.
/// </summary>
internal static class CaptureFavorites
{
    public static bool Contains(IReadOnlyCollection<string> favorites, string path) =>
        favorites.Contains(path, StringComparer.OrdinalIgnoreCase);

    /// <summary>Stars a capture. Returns false if it was starred already.</summary>
    public static bool Add(List<string> favorites, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Contains(favorites, path)) return false;
        favorites.Add(path);
        return true;
    }

    /// <summary>Unstars a capture. Returns false if it was not starred.</summary>
    public static bool Remove(List<string> favorites, string path) =>
        favorites.RemoveAll(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)) > 0;

    /// <summary>Stars a capture if it is not starred, unstars it if it is. Returns the new state.
    /// A blank path is not a capture, so it reports "not starred" rather than claiming a star
    /// it cannot record - the caller shows that state to the user.</summary>
    public static bool Toggle(List<string> favorites, string path) =>
        !string.IsNullOrWhiteSpace(path) && (Add(favorites, path) || !Remove(favorites, path));

    /// <summary>Drops blank and repeated entries a hand-edited settings file could hold.</summary>
    public static List<string> Sanitise(List<string>? favorites)
    {
        var clean = new List<string>();
        if (favorites is null) return clean;
        foreach (var path in favorites)
            Add(clean, path);
        return clean;
    }

    /// <summary>Favorites whose file no longer exists at the given path, dropped so the list
    /// cannot grow forever with entries for captures that were deleted outside this list -
    /// moved to the Recycle Bin, or removed by hand from Explorer.</summary>
    public static List<string> PruneMissing(IReadOnlyCollection<string> favorites, Func<string, bool> exists) =>
        favorites.Where(exists).ToList();
}
