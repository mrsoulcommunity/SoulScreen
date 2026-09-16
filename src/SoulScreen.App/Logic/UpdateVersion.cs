namespace SoulScreen.App.Logic;

/// <summary>
/// A released version, boiled down to the three numbers that decide whether one build is
/// newer than another. GitHub tags spell it "v1.2.0"; the assembly spells it "1.2.0.0"; a
/// release note might add "-beta.1" or "+abcdef" on the end. All three collapse to the same
/// (Major, Minor, Patch), which is the only thing an update check actually needs.
/// </summary>
public readonly record struct UpdateVersion(int Major, int Minor, int Patch) : IComparable<UpdateVersion>
{
    /// <summary>
    /// Parses a tag or version string such as "v1.2.0", "1.2", or "1.2.0-beta.1". Anything
    /// that does not start with a number, once a leading "v" is stripped, fails to parse
    /// rather than guessing - a release named after a codename should never compare as newer
    /// than every real version because it happened to parse as 0.0.0.
    /// </summary>
    public static bool TryParse(string? text, out UpdateVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();
        if (trimmed.Length > 0 && (trimmed[0] == 'v' || trimmed[0] == 'V')) trimmed = trimmed[1..];

        // A pre-release or build suffix does not change which of two versions is newer for
        // this app's purposes, so only the numeric prefix counts.
        var suffix = trimmed.IndexOfAny(['-', '+']);
        if (suffix >= 0) trimmed = trimmed[..suffix];

        if (trimmed.Length == 0) return false;
        var parts = trimmed.Split('.');

        if (!int.TryParse(parts[0], out var major) || major < 0) return false;
        var minor = parts.Length > 1 && int.TryParse(parts[1], out var m) && m >= 0 ? m : 0;
        var patch = parts.Length > 2 && int.TryParse(parts[2], out var p) && p >= 0 ? p : 0;

        version = new UpdateVersion(major, minor, patch);
        return true;
    }

    /// <summary>
    /// From a .NET assembly <see cref="Version"/>, whose Build field is what this app's own
    /// Version property calls the patch number (see Directory.Build.props / the app csproj).
    /// An unset field reads back as -1, which is clamped to 0.
    /// </summary>
    public static UpdateVersion FromAssemblyVersion(Version? version) => version is null
        ? default
        : new UpdateVersion(Math.Max(version.Major, 0), Math.Max(version.Minor, 0), Math.Max(version.Build, 0));

    public int CompareTo(UpdateVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0) return major;
        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public static bool operator <(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) >= 0;

    public bool IsNewerThan(UpdateVersion other) => this > other;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}
