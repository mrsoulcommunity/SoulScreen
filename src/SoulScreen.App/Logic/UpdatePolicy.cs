namespace SoulScreen.App.Logic;

/// <summary>
/// The purely logical half of the update check: when a background check is due, whether the
/// latest release counts as "available" given what has already been skipped, and which asset
/// in a release is the one this PC should download. None of this touches the network or the
/// file system, so it is exercised directly by the test suite; <see cref="UpdateService"/>
/// (in the App project proper) is the half that actually reaches GitHub.
/// </summary>
public static class UpdatePolicy
{
    /// <summary>How often a silent background check is allowed to run. A user who restarts
    /// SoulScreen ten times a day should not spend ten requests on GitHub's API for it.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    /// <summary>True once <paramref name="interval"/> has passed since the last check, or
    /// there has never been one.</summary>
    public static bool IsCheckDue(DateTime? lastCheckUtc, DateTime nowUtc, TimeSpan interval) =>
        lastCheckUtc is null || nowUtc - lastCheckUtc.Value >= interval;

    /// <summary>
    /// An update is worth telling the user about when the release is strictly newer than what
    /// is running and its tag is not the one "Skip this version" was pressed for. The skip is
    /// compared as text against the tag rather than the parsed version, so skipping "v1.2.0"
    /// does not also silence a differently-tagged build that happens to parse the same way.
    /// </summary>
    public static bool IsUpdateAvailable(UpdateVersion current, UpdateVersion latest, string latestTag, string? skippedTag) =>
        latest.IsNewerThan(current)
        && !string.Equals(latestTag?.Trim(), skippedTag?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The release asset to download: a <c>.zip</c> naming "win-x64" or "win64", which is how
    /// <c>tools/publish.ps1</c>'s companion packaging step and every SoulScreen release so far
    /// names the self-contained build. Falls back to the only zip present when there is
    /// exactly one, so a release that dropped the platform suffix from its one asset still
    /// resolves rather than silently offering nothing.
    /// </summary>
    public static string? SelectWindowsAssetName(IEnumerable<string> assetNames)
    {
        var names = assetNames.Where(n => n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();

        var platformMatch = names.FirstOrDefault(n =>
            n.Contains("win-x64", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("win64", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("windows", StringComparison.OrdinalIgnoreCase));
        if (platformMatch is not null) return platformMatch;

        return names.Count == 1 ? names[0] : null;
    }

    /// <summary>Splits a GitHub asset digest such as "sha256:abcd…" into its algorithm and
    /// hex payload, or returns null when the string is not in that shape.</summary>
    public static (string Algorithm, string Hex)? ParseDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        var separator = digest.IndexOf(':');
        if (separator <= 0 || separator == digest.Length - 1) return null;
        return (digest[..separator].Trim().ToLowerInvariant(), digest[(separator + 1)..].Trim());
    }
}
