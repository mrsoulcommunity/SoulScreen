namespace SoulScreen.App.Logic;

/// <summary>What came of checking an in/out selection against the clip it is cut from.</summary>
public readonly record struct ClipRangeResult(bool IsValid, string? Error, TimeSpan Start, TimeSpan Duration)
{
    public static ClipRangeResult Invalid(string error) => new(false, error, default, default);
}

/// <summary>
/// Validates a precise in/out selection before it is handed to <c>ClipExporter</c>.
/// <para>
/// The exporter itself clamps whatever it is given - a clip too short becomes the shortest
/// one it writes, one too long becomes the longest - which is right for a moment picked by a
/// single length slider, where there is nothing to be wrong about. An in/out pair is
/// different: "in" after "out", or either one outside the recording, is a mistake the user
/// made rather than a length to be rounded off, and deserves to be said so before an export
/// runs rather than silently cut down to something else.
/// </para>
/// Deliberately free of WPF and of the exporter itself, so the rules can be tested alone.
/// </summary>
public static class ClipRange
{
    public static ClipRangeResult Validate(
        TimeSpan start, TimeSpan end, TimeSpan? totalDuration, TimeSpan minimumLength, TimeSpan maximumLength)
    {
        if (start < TimeSpan.Zero) return ClipRangeResult.Invalid("The in point cannot be before the start of the recording.");
        if (totalDuration is { } total && start > total)
            return ClipRangeResult.Invalid("The in point is past the end of the recording.");

        if (end <= start) return ClipRangeResult.Invalid("The out point must be after the in point.");
        if (totalDuration is { } total2 && end > total2)
            return ClipRangeResult.Invalid("The out point is past the end of the recording.");

        var duration = end - start;
        if (duration < minimumLength)
            return ClipRangeResult.Invalid($"A clip must be at least {minimumLength.TotalSeconds:0.#} s long.");
        if (duration > maximumLength)
            return ClipRangeResult.Invalid($"A clip can be at most {maximumLength.TotalSeconds:0.#} s long.");

        return new ClipRangeResult(true, null, start, duration);
    }

    /// <summary>
    /// A sensible starting in/out pair for the panel opening at <paramref name="playerPosition"/>:
    /// in is where the player is, out is <paramref name="defaultLength"/> later, pulled back so
    /// neither point runs past a known total duration.
    /// </summary>
    public static (TimeSpan In, TimeSpan Out) DefaultRange(
        TimeSpan playerPosition, TimeSpan? totalDuration, TimeSpan defaultLength)
    {
        var start = playerPosition < TimeSpan.Zero ? TimeSpan.Zero : playerPosition;
        if (totalDuration is not { } total) return (start, start + defaultLength);

        if (start > total) start = total;
        var end = start + defaultLength > total ? total : start + defaultLength;
        if (end - start < TimeSpan.Zero) end = start;
        return (start, end);
    }
}
