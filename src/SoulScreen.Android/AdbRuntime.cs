namespace SoulScreen.Android;

/// <summary>
/// Locates the <c>adb</c> (Android Debug Bridge) executable.
/// <para>
/// adb is not bundled with the source: <c>tools/fetch-adb.ps1</c> downloads Google's
/// platform-tools into <c>native/adb</c>. This probes that location, then PATH, then the
/// default Android Studio SDK install, so a machine that already has the SDK needs nothing
/// extra.
/// </para>
/// </summary>
public static class AdbRuntime
{
    private static readonly object InitLock = new();
    private static bool _initialised;
    private static string? _path;
    private static string? _unavailableReason;

    public static bool IsAvailable
    {
        get { EnsureInitialised(); return _path is not null; }
    }

    /// <summary>Full path to adb.exe, once found.</summary>
    public static string? ExecutablePath
    {
        get { EnsureInitialised(); return _path; }
    }

    public static string? UnavailableReason
    {
        get { EnsureInitialised(); return _unavailableReason; }
    }

    private static void EnsureInitialised()
    {
        if (_initialised) return;
        lock (InitLock)
        {
            if (_initialised) return;
            _initialised = true;
            _path = FindAdb();
            if (_path is null)
            {
                _unavailableReason =
                    "adb.exe was not found. Run 'pwsh tools/fetch-adb.ps1' to download Android " +
                    "platform-tools, or install it yourself and add it to PATH.";
            }
        }
    }

    private static string? FindAdb()
    {
        foreach (var candidate in CandidateDirectories())
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            var path = Path.Combine(candidate, "adb.exe");
            if (File.Exists(path)) return path;
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathVariable.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            string path;
            try { path = Path.Combine(directory, "adb.exe"); }
            catch (ArgumentException) { continue; }
            if (File.Exists(path)) return path;
        }

        // Android Studio's default SDK location - present on any machine that has ever
        // opened Android Studio, whether or not it is on PATH.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var sdkDefault = Path.Combine(localAppData, "Android", "Sdk", "platform-tools", "adb.exe");
        return File.Exists(sdkDefault) ? sdkDefault : null;
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        var appDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(appDirectory, "adb");
        yield return Path.Combine(appDirectory, "native", "adb");

        // Walk up out of bin/Debug/net8.0 to the repository's native/adb, for a run
        // straight out of the build without a publish step.
        var probe = new DirectoryInfo(appDirectory);
        for (var depth = 0; depth < 6 && probe is not null; depth++, probe = probe.Parent)
            yield return Path.Combine(probe.FullName, "native", "adb");
    }
}
