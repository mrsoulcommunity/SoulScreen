namespace SoulScreen.App.Logic;

/// <summary>
/// Keeps the capture folder under a size the user has chosen.
/// <para>
/// A mirroring session can write hundreds of megabytes in an hour, and a folder nobody
/// prunes eventually fills the drive. The plan is computed from a listing of the folder:
/// oldest captures first, never touching a recording still being written. The actual
/// deleting happens in the app, which knows how to put files in the Recycle Bin.
/// </para>
/// Deliberately free of WPF so the planning can be tested on its own.
/// </summary>
internal static class CaptureBudget
{
    /// <summary>One capture in the folder, as the planner needs to see it.</summary>
    /// <param name="Path">Full path.</param>
    /// <param name="SizeBytes">Length on disk.</param>
    /// <param name="ModifiedUtc">When it was last written.</param>
    /// <param name="IsBeingRecorded">True for the recording in progress, which is never a
    /// candidate: deleting the file being written ends the recording as a playable file.</param>
    /// <param name="IsFavorite">True for a capture starred to keep, which the budget must
    /// never remove - starring something is how a user says "not this one" to the very
    /// pruning this budget exists to do.</param>
    public sealed record Candidate(string Path, long SizeBytes, DateTime ModifiedUtc, bool IsBeingRecorded, bool IsFavorite = false);

    /// <summary>The outcome of planning: what to remove, and what to say about it.</summary>
    public sealed record Plan(IReadOnlyList<string> Remove, long FreedBytes);

    /// <summary>
    /// Picks the oldest captures to remove until the folder fits in
    /// <paramref name="budgetBytes"/>. The recording in progress is never picked, and a
    /// single capture larger than the whole budget is left alone - pruning it would not
    /// bring the folder under budget anyway.
    /// </summary>
    /// <param name="captures">Everything in the folder.</param>
    /// <param name="budgetBytes">The most the folder may hold; zero or less means no budget.</param>
    public static Plan PlanRemoval(IReadOnlyList<Candidate> captures, long budgetBytes)
    {
        if (budgetBytes <= 0 || captures.Count == 0) return new Plan([], 0);

        var total = captures.Sum(c => c.SizeBytes);
        if (total <= budgetBytes) return new Plan([], 0);

        var removable = captures
            .Where(c => !c.IsBeingRecorded && !c.IsFavorite)
            .OrderBy(c => c.ModifiedUtc)
            .ToList();
        if (removable.Count == 0) return new Plan([], 0);

        // If even without the active recording the folder cannot fit, take everything
        // removable and stop there.
        var toRemove = new List<string>();
        long freed = 0;
        var remaining = total;
        foreach (var candidate in removable)
        {
            if (remaining <= budgetBytes) break;
            toRemove.Add(candidate.Path);
            freed += candidate.SizeBytes;
            remaining -= candidate.SizeBytes;
        }

        return new Plan(toRemove, freed);
    }

    /// <summary>"Freed 1.2 GB", for the toast that says what the budget did.</summary>
    public static string DescribeFreed(long bytes) => $"{CaptureNaming.FormatSize(bytes)} freed";
}
