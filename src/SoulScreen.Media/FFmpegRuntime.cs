using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using SoulScreen.Core.Logging;

namespace SoulScreen.Media;

/// <summary>
/// Thrown when the FFmpeg shared libraries are missing or unusable.
/// </summary>
public sealed class FFmpegUnavailableException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);

/// <summary>
/// Locates and initialises the FFmpeg shared libraries.
/// <para>
/// FFmpeg is not bundled with the source: <c>tools/fetch-ffmpeg.ps1</c> downloads an LGPL
/// build into <c>native/ffmpeg</c>. This probes the usual places, reports a clear failure
/// if it cannot find them, and routes FFmpeg's own logging into SoulScreen's log.
/// </para>
/// </summary>
public static class FFmpegRuntime
{
    private static readonly ILogger Log_ = Log.For("ffmpeg");
    private static readonly object InitLock = new();

    private static bool? _available;
    private static string? _unavailableReason;

    // Held for the lifetime of the process: FFmpeg keeps the pointer and will call back
    // from decoder threads, so letting the GC collect the delegate crashes the process.
    private static av_log_set_callback_callback? _logCallback;

    public static bool IsAvailable
    {
        get
        {
            EnsureInitialised();
            return _available!.Value;
        }
    }

    public static string? UnavailableReason
    {
        get
        {
            EnsureInitialised();
            return _unavailableReason;
        }
    }

    /// <summary>Directory the libraries were loaded from, once initialisation succeeded.</summary>
    public static string? LibraryDirectory { get; private set; }

    /// <summary>Version banner, e.g. "libavcodec 62.11.100".</summary>
    public static string? Version { get; private set; }

    /// <summary>How much FFmpeg detail reaches the log. Raise it when a stream will not decode.</summary>
    public static bool VerboseLogging { get; set; }

    public static void ThrowIfUnavailable()
    {
        if (!IsAvailable)
            throw new FFmpegUnavailableException(
                $"FFmpeg is not available. {_unavailableReason} " +
                "Run 'pwsh tools/fetch-ffmpeg.ps1' to download it, then restart SoulScreen.");
    }

    private static void EnsureInitialised()
    {
        if (_available.HasValue) return;
        lock (InitLock)
        {
            if (_available.HasValue) return;

            var directory = FindLibraryDirectory();
            if (directory is null)
            {
                _available = false;
                _unavailableReason = "The FFmpeg shared libraries (avcodec, avutil, swscale, swresample) were not found.";
                Log_.Warn(_unavailableReason);
                return;
            }

            try
            {
                ffmpeg.RootPath = directory;
                DynamicallyLoadedBindings.Initialize();

                var version = ffmpeg.avcodec_version();
                LibraryDirectory = directory;
                Version = $"libavcodec {version >> 16}.{(version >> 8) & 0xff}.{version & 0xff}";

                InstallLogCallback();

                _available = true;
                Log_.Info($"{Version} loaded from {directory}");
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                _available = false;
                _unavailableReason = ex switch
                {
                    BadImageFormatException => $"The FFmpeg libraries in {directory} are the wrong architecture; SoulScreen needs x64.",
                    EntryPointNotFoundException => $"The FFmpeg libraries in {directory} are a different major version than SoulScreen expects.",
                    _ => $"The FFmpeg libraries in {directory} could not be loaded.",
                };
                Log_.Error(_unavailableReason, ex);
            }
        }
    }

    private static string? FindLibraryDirectory()
    {
        foreach (var candidate in CandidateDirectories())
        {
            if (string.IsNullOrEmpty(candidate) || !Directory.Exists(candidate)) continue;
            // avcodec is the one library nothing else can substitute for.
            if (Directory.EnumerateFiles(candidate, "avcodec-*.dll").Any()) return candidate;
        }
        return null;
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        var appDirectory = AppContext.BaseDirectory;
        yield return appDirectory;
        yield return Path.Combine(appDirectory, "ffmpeg");
        yield return Path.Combine(appDirectory, "native", "ffmpeg");

        // Walk up out of bin/Debug/net8.0 to the repository's native/ffmpeg.
        var probe = new DirectoryInfo(appDirectory);
        for (var depth = 0; depth < 6 && probe is not null; depth++, probe = probe.Parent)
            yield return Path.Combine(probe.FullName, "native", "ffmpeg");
    }

    /// <summary>Routes FFmpeg's internal logging into SoulScreen's, so a decode failure
    /// explains itself instead of vanishing into stderr.</summary>
    private static unsafe void InstallLogCallback()
    {
        ffmpeg.av_log_set_level(VerboseLogging ? ffmpeg.AV_LOG_VERBOSE : ffmpeg.AV_LOG_WARNING);

        _logCallback = (p0, level, format, vl) =>
        {
            if (level > ffmpeg.av_log_get_level()) return;

            const int bufferSize = 1024;
            var buffer = stackalloc byte[bufferSize];
            var printPrefix = 1;
            ffmpeg.av_log_format_line(p0, level, format, vl, buffer, bufferSize, &printPrefix);
            var message = Marshal.PtrToStringAnsi((IntPtr)buffer)?.TrimEnd();
            if (string.IsNullOrEmpty(message)) return;

            if (level <= ffmpeg.AV_LOG_ERROR) Log_.Error(message);
            else if (level <= ffmpeg.AV_LOG_WARNING) Log_.Warn(message);
            else Log_.Debug(message);
        };

        ffmpeg.av_log_set_callback(_logCallback);
    }

    /// <summary>Turns an FFmpeg negative error code into something readable.</summary>
    public static unsafe string DescribeError(int error)
    {
        const int bufferSize = 256;
        var buffer = stackalloc byte[bufferSize];
        ffmpeg.av_strerror(error, buffer, bufferSize);
        return $"{Marshal.PtrToStringAnsi((IntPtr)buffer)} ({error})";
    }
}
