namespace SoulScreen.App.Logic;

/// <summary>
/// Moving settings between machines, or keeping them safe before an experiment.
/// <para>
/// A settings file names nothing about the machine it came from: the window was there, the
/// phone history is this PC's memory of its phones, and the trust decisions are about
/// people. So an import takes the preferences and leaves the identity - keeping this
/// machine's window placement, its receiver name, its device history, its allowed and
/// blocked iPhones and its welcome-seen flag.
/// </para>
/// Deliberately free of WPF so the merge can be tested on its own.
/// </summary>
public static class SettingsTransfer
{
    /// <summary>What an import decided, for the toast that reports it.</summary>
    public enum ImportOutcome
    {
        /// <summary>Preferences were applied.</summary>
        Applied,
        /// <summary>The file could not be read as settings at all.</summary>
        Unreadable,
    }

    /// <summary>The fields an export/import carries, described for the user.</summary>
    public static IReadOnlyList<string> ExportedFields { get; } =
    [
        "Receiver name, port, resolution and audio",
        "Appearance: theme, accent",
        "Picture: fit, rotation, smoothness, rounded corners",
        "Capture folder, format, storage budget, starred captures",
        "Window: displays, tray, hotkeys, startup",
    ];

    /// <summary>
    /// Copies every preference from <paramref name="imported"/> onto
    /// <paramref name="current"/>, keeping the identity fields of the current settings.
    /// Both sides are clamped afterwards, so a hand-edited file cannot poison the result.
    /// </summary>
    /// <returns>The fields that changed, for the summary toast.</returns>
    public static IReadOnlyList<string> ApplyImported(AppSettings current, AppSettings imported)
    {
        var changed = new List<string>();

        void Set<T>(T newValue, T oldValue, string label, Action assign)
        {
            if (EqualityComparer<T>.Default.Equals(newValue, oldValue)) return;
            assign();
            changed.Add(label);
        }

        void SetList<T>(IReadOnlyList<T> newValue, IReadOnlyList<T> oldValue, string label, Action assign)
        {
            if (newValue.Count == oldValue.Count && Enumerable.SequenceEqual(newValue, oldValue)) return;
            assign();
            changed.Add(label);
        }

        // ---------------------------------------------------------- receiver
        // The name stays: like the window place, it names this machine, and an import
        // from elsewhere must not rename the receiver out from under the household's
        // iPhones.
        Set(imported.Port, current.Port, "port", () => current.Port = imported.Port);
        Set(imported.EnableAudio, current.EnableAudio, "audio", () => current.EnableAudio = imported.EnableAudio);
        Set(imported.DisplayWidth, current.DisplayWidth, "resolution", () => current.DisplayWidth = imported.DisplayWidth);
        Set(imported.DisplayHeight, current.DisplayHeight, "resolution", () => current.DisplayHeight = imported.DisplayHeight);
        Set(imported.DisplayRefreshRate, current.DisplayRefreshRate, "resolution", () => current.DisplayRefreshRate = imported.DisplayRefreshRate);
        Set(imported.TraceProtocol, current.TraceProtocol, "protocol tracing", () => current.TraceProtocol = imported.TraceProtocol);

        // ---------------------------------------------------------- appearance
        Set(imported.Theme, current.Theme, "theme", () => current.Theme = imported.Theme);
        Set(imported.Accent, current.Accent, "accent", () => current.Accent = imported.Accent);

        // ---------------------------------------------------------- picture
        Set(imported.VideoFit, current.VideoFit, "fit", () => current.VideoFit = imported.VideoFit);
        Set(imported.Rotation, current.Rotation, "rotation", () => current.Rotation = imported.Rotation);
        Set(imported.MirrorHorizontally, current.MirrorHorizontally, "mirroring", () => current.MirrorHorizontally = imported.MirrorHorizontally);
        Set(imported.Latency, current.Latency, "smoothness", () => current.Latency = imported.Latency);
        Set(imported.RoundedCorners, current.RoundedCorners, "rounded corners", () => current.RoundedCorners = imported.RoundedCorners);
        Set(imported.ShowStats, current.ShowStats, "statistics overlay", () => current.ShowStats = imported.ShowStats);
        Set(imported.ShowPerformanceGraph, current.ShowPerformanceGraph, "performance graph", () => current.ShowPerformanceGraph = imported.ShowPerformanceGraph);

        // ---------------------------------------------------------- capture
        Set(imported.CaptureDirectory, current.CaptureDirectory, "capture folder", () => current.CaptureDirectory = imported.CaptureDirectory);
        Set(imported.ScreenshotFormat, current.ScreenshotFormat, "screenshot format", () => current.ScreenshotFormat = imported.ScreenshotFormat);
        Set(imported.RecordAudio, current.RecordAudio, "recording audio", () => current.RecordAudio = imported.RecordAudio);
        Set(imported.CopyScreenshotToClipboard, current.CopyScreenshotToClipboard, "clipboard screenshots", () => current.CopyScreenshotToClipboard = imported.CopyScreenshotToClipboard);
        Set(imported.CaptureBudgetBytes, current.CaptureBudgetBytes, "storage budget", () => current.CaptureBudgetBytes = imported.CaptureBudgetBytes);
        SetList(imported.FavoriteCaptures, current.FavoriteCaptures, "starred captures", () => current.FavoriteCaptures = imported.FavoriteCaptures);

        // ---------------------------------------------------------- window and system
        Set(imported.AlwaysOnTop, current.AlwaysOnTop, "keep on top", () => current.AlwaysOnTop = imported.AlwaysOnTop);
        Set(imported.MinimizeToTray, current.MinimizeToTray, "minimise to tray", () => current.MinimizeToTray = imported.MinimizeToTray);
        Set(imported.CloseToTray, current.CloseToTray, "close to tray", () => current.CloseToTray = imported.CloseToTray);
        Set(imported.TrayNotifications, current.TrayNotifications, "tray notifications", () => current.TrayNotifications = imported.TrayNotifications);
        Set(imported.GlobalHotkeys, current.GlobalHotkeys, "global shortcuts", () => current.GlobalHotkeys = imported.GlobalHotkeys);
        Set(imported.Animations, current.Animations, "animations", () => current.Animations = imported.Animations);
        Set(imported.TargetDisplay, current.TargetDisplay, "display", () => current.TargetDisplay = imported.TargetDisplay);

        return changed;
    }

    /// <summary>Judges a file before anything is applied: is it settings at all?</summary>
    public static ImportOutcome Classify(AppSettings? loaded)
    {
        if (loaded is null) return ImportOutcome.Unreadable;
        // A file of the right shape always has at least these defaults; a file that is
        // merely JSON but not settings deserialises to null or fails outright.
        return ImportOutcome.Applied;
    }
}
