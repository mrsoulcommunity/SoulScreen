namespace SoulScreen.Android;

/// <summary>
/// Locates the scrcpy server jar this build was written against.
/// <para>
/// Not bundled with the source: <c>tools/fetch-scrcpy-server.ps1</c> downloads Genymobile's
/// signed release asset, checksum-verified, into <c>native/scrcpy</c>. Without it,
/// <see cref="AndroidMirrorSource"/> falls back to driving "screenrecord" directly - video
/// only, and no live-rotation handling.
/// </para>
/// </summary>
public static class ScrcpyServerRuntime
{
    /// <summary>
    /// The scrcpy protocol version <see cref="ScrcpySession"/> is written against. scrcpy
    /// refuses to run when the version it is passed does not match the server jar's own
    /// build exactly, so this and the pinned download in fetch-scrcpy-server.ps1 must move
    /// together.
    /// </summary>
    public const string ServerVersion = "4.1";

    /// <summary>Where the jar is pushed to on the phone. Writable by the shell user without
    /// needing root, and not on any path Android itself scans for apps.</summary>
    public const string RemoteJarPath = "/data/local/tmp/scrcpy-server.jar";

    private static readonly object InitLock = new();
    private static bool _initialised;
    private static string? _localPath;

    public static bool IsAvailable
    {
        get { EnsureInitialised(); return _localPath is not null; }
    }

    public static string? LocalPath
    {
        get { EnsureInitialised(); return _localPath; }
    }

    public static string? UnavailableReason { get; private set; }

    private static void EnsureInitialised()
    {
        if (_initialised) return;
        lock (InitLock)
        {
            if (_initialised) return;
            _initialised = true;
            _localPath = FindServerJar();
            if (_localPath is null)
            {
                UnavailableReason =
                    "scrcpy-server.jar was not found. Run 'pwsh tools/fetch-scrcpy-server.ps1' for audio and " +
                    "rotation-aware Android mirroring; without it, SoulScreen falls back to screenrecord " +
                    "(video only, fixed orientation).";
            }
        }
    }

    private static string? FindServerJar()
    {
        foreach (var candidate in CandidateDirectories())
        {
            var path = Path.Combine(candidate, "scrcpy-server.jar");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        var appDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(appDirectory, "scrcpy");
        yield return Path.Combine(appDirectory, "native", "scrcpy");

        var probe = new DirectoryInfo(appDirectory);
        for (var depth = 0; depth < 6 && probe is not null; depth++, probe = probe.Parent)
            yield return Path.Combine(probe.FullName, "native", "scrcpy");
    }
}
