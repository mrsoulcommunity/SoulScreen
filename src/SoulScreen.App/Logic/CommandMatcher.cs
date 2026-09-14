namespace SoulScreen.App.Logic;

/// <summary>
/// Ranks command titles against what has been typed into the command palette.
/// <para>
/// Every word typed has to match somewhere, so "rec stop" narrows rather than widens. A word
/// scores best as the start of the title, then as the start of one of its words, then
/// anywhere inside it, and last as letters in order ("fscr" for "fullscreen") - the order in
/// which people expect a launcher to guess. Keywords are searched too, a little below the
/// title, so "pip" finds the mini player without cluttering what the palette shows.
/// </para>
/// Deliberately free of WPF so the ranking can be tested on its own.
/// </summary>
internal static class CommandMatcher
{
    private const int KeywordPenalty = 150;

    /// <summary>
    /// How well <paramref name="title"/> (or its <paramref name="keywords"/>) matches
    /// <paramref name="query"/>: higher is better, and null means it does not match at all.
    /// An empty query matches everything equally.
    /// </summary>
    public static int? Score(string query, string title, string? keywords = null)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return 0;

        var total = 0;
        foreach (var term in terms)
        {
            var best = ScoreTerm(term, title);
            if (keywords is not null && ScoreTerm(term, keywords) is { } keywordScore)
            {
                keywordScore -= KeywordPenalty;
                if (best is null || keywordScore > best) best = keywordScore;
            }

            if (best is null) return null;
            total += best.Value;
        }

        // Shorter titles win ties: "Mute" before "Mute the phone's audio in recordings".
        return total - Math.Min(title.Length, 60);
    }

    private static int? ScoreTerm(string term, string text)
    {
        if (text.Length == 0) return null;

        if (text.Equals(term, StringComparison.OrdinalIgnoreCase)) return 1000;
        if (text.StartsWith(term, StringComparison.OrdinalIgnoreCase)) return 800;

        var wordIndex = 0;
        for (var i = 1; i < text.Length; i++)
        {
            if (!IsWordStart(text, i)) continue;
            wordIndex++;
            if (string.Compare(text, i, term, 0, term.Length, StringComparison.OrdinalIgnoreCase) == 0
                && i + term.Length <= text.Length)
            {
                return 600 - Math.Min(wordIndex, 20) * 10;
            }
        }

        if (text.Contains(term, StringComparison.OrdinalIgnoreCase)) return 400;

        return ScoreSubsequence(term, text);
    }

    /// <summary>Letters of the term in order, with a bonus for each that lands on a word start
    /// and a cost for the gaps between them.</summary>
    private static int? ScoreSubsequence(string term, string text)
    {
        var score = 200;
        var position = 0;
        var previous = -1;

        foreach (var wanted in term)
        {
            var found = -1;
            for (var i = position; i < text.Length; i++)
            {
                if (char.ToLowerInvariant(text[i]) != char.ToLowerInvariant(wanted)) continue;
                found = i;
                break;
            }

            if (found < 0) return null;
            if (IsWordStart(text, found)) score += 15;
            if (previous >= 0) score -= Math.Min(found - previous - 1, 10) * 4;
            previous = found;
            position = found + 1;
        }

        return Math.Max(score, 1);
    }

    private static bool IsWordStart(string text, int index) =>
        index == 0 || (!char.IsLetterOrDigit(text[index - 1]) && char.IsLetterOrDigit(text[index]));
}
