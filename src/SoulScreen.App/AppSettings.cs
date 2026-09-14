using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SoulScreen.AirPlay;
using SoulScreen.Core.Logging;

namespace SoulScreen.App;

/// <summary>How the window's colours are chosen.</summary>
public enum AppTheme
{
    /// <summary>Follow the Windows "choose your mode" setting, live.</summary>
    System,
    Dark,
    Light,
}

/// <summary>How the phone's picture is fitted into the window.</summary>
public enum VideoFit
{
    /// <summary>Whole picture visible, letterboxed as needed.</summary>
    Fit,
    /// <summary>Fills the window, cropping whatever does not fit.</summary>
    Fill,
    /// <summary>Fills the window, ignoring the aspect ratio.</summary>
    Stretch,
    /// <summary>One phone pixel per screen pixel, however the window is sized.</summary>
    Actual,
}

/// <summary>
/// The trade between smoothness and delay. Each level sets how much decoded picture and
/// sound is held back before it is shown, which is what hides Wi-Fi's unevenness.
/// </summary>
public enum LatencyMode
{
    /// <summary>Longest cushion. Nothing short of a real outage is seen.</summary>
    Smooth,
    /// <summary>The default: a tenth of a second, enough for an ordinary home network.</summary>
    Balanced,
    /// <summary>Shortest cushion. Best on a wired-quality link; jitter shows otherwise.</summary>
    Responsive,
}

/// <summary>A phone that has mirrored to this PC before.</summary>
public sealed class RecentDevice
{
    public string Name { get; set; } = string.Empty;
    public string? Model { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public int SessionCount { get; set; }
    /// <summary>Total time spent mirroring, across every session.</summary>
    public double TotalSeconds { get; set; }
}

/// <summary>
/// User preferences, persisted next to the pairing identity in %LOCALAPPDATA%\SoulScreen.
/// </summary>
public sealed class AppSettings
{
    private static readonly ILogger Log_ = Log.For("settings");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Most phones remembered in the history list.</summary>
    public const int MaxRecentDevices = 8;

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoulScreen");

    private static string FilePath => Path.Combine(Directory, "settings.json");

    // ------------------------------------------------------------------ receiver

    /// <summary>Name the receiver advertises, which is what appears on the phone.</summary>
    public string DeviceName { get; set; } = Environment.MachineName;

    /// <summary>Control-channel port. 7000 is what Apple receivers use.</summary>
    public ushort Port { get; set; } = 7000;

    /// <summary>Accept the phone's audio as well as its screen.</summary>
    public bool EnableAudio { get; set; } = true;

    /// <summary>Start advertising as soon as the app opens.</summary>
    public bool StartReceiverOnLaunch { get; set; } = true;

    // Advertised-resolution bounds. Shared by Normalise() and by the settings form's
    // validation, so a value the form rejects is one a hand-edited file could not have
    // kept either.
    public const int MinDisplayWidth = 640;
    public const int MaxDisplayWidth = 3840;
    public const int MinDisplayHeight = 480;
    public const int MaxDisplayHeight = 2160;
    public const int MinRefreshRate = 24;
    public const int MaxRefreshRate = 120;

    /// <summary>Resolution advertised to the phone, which is what it encodes at.</summary>
    public int DisplayWidth { get; set; } = 1920;

    public int DisplayHeight { get; set; } = 1080;

    public int DisplayRefreshRate { get; set; } = 60;

    /// <summary>Log every RTSP request and response. Noisy, but the first thing to turn on
    /// when a phone will not connect.</summary>
    public bool TraceProtocol { get; set; }

    // -------------------------------------------------------------------- picture

    public AppTheme Theme { get; set; } = AppTheme.System;

    public VideoFit VideoFit { get; set; } = VideoFit.Fit;

    /// <summary>Clockwise rotation applied to the picture: 0, 90, 180 or 270.</summary>
    public int Rotation { get; set; }

    /// <summary>Flip the picture left to right, for a phone pointed at a mirror or a camera.</summary>
    public bool MirrorHorizontally { get; set; }

    /// <summary>Resize the window to the phone's shape when a session starts.</summary>
    public bool FitWindowToVideo { get; set; } = true;

    /// <summary>Hold the window at the picture's aspect ratio while it is being resized.</summary>
    public bool LockAspectRatio { get; set; } = true;

    public LatencyMode Latency { get; set; } = LatencyMode.Balanced;

    /// <summary>Keep the display awake while a phone is mirroring.</summary>
    public bool KeepDisplayAwake { get; set; } = true;

    /// <summary>Show the statistics overlay over the picture.</summary>
    public bool ShowStats { get; set; }

    // ---------------------------------------------------------------------- audio

    /// <summary>Whether the phone's audio starts muted.</summary>
    public bool Muted { get; set; }

    /// <summary>Playback level for the phone's audio, 0 to 1. Applies to the mirrored
    /// stream only - the PC's own mixing levels are the system's business.</summary>
    public double Volume { get; set; } = 1.0;

    /// <summary>Endpoint id of the playback device, or null for the system default.</summary>
    public string? AudioOutputDeviceId { get; set; }

    // -------------------------------------------------------------------- capture

    /// <summary>Where screenshots and recordings go. Defaults to Videos\SoulScreen for
    /// recordings and Pictures\SoulScreen for screenshots when left empty.</summary>
    public string CaptureDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SoulScreen");

    /// <summary>Put the phone's audio into recordings.</summary>
    public bool RecordAudio { get; set; } = true;

    /// <summary>Also put each screenshot on the clipboard.</summary>
    public bool CopyScreenshotToClipboard { get; set; }

    // --------------------------------------------------------------------- window

    /// <summary>Keep the window above other applications.</summary>
    public bool AlwaysOnTop { get; set; }

    /// <summary>Minimising sends the window to the notification area instead of the taskbar.</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>Closing hides the window to the notification area; the receiver keeps running.</summary>
    public bool CloseToTray { get; set; }

    /// <summary>Show a notification when a phone connects or disconnects while the window is hidden.</summary>
    public bool TrayNotifications { get; set; } = true;

    /// <summary>Start SoulScreen when signing in to Windows, minimised.</summary>
    public bool LaunchAtStartup { get; set; }

    /// <summary>Last window placement, restored on the next launch.</summary>
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    // -------------------------------------------------------------------- history

    public List<RecentDevice> RecentDevices { get; set; } = [];

    // ------------------------------------------------------------------ lifecycle

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), SerializerOptions);
                if (loaded is not null)
                {
                    loaded.Normalise();
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            // Corrupt settings are not worth blocking startup over.
            Log_.Warn($"could not read {FilePath}; falling back to defaults", ex);
        }

        var defaults = new AppSettings();
        defaults.Normalise();
        // Written straight away so the file exists to be found and hand-edited, rather than
        // appearing only after the first change made through the UI.
        defaults.Save();
        return defaults;
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            // Written beside the file and swapped in, so a crash mid-write cannot leave a
            // half-written settings file to be read as corrupt on the next launch.
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, SerializerOptions));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log_.Warn($"could not write {FilePath}", ex);
        }
    }

    /// <summary>Returns a fresh set of defaults, keeping only what identifies this PC.</summary>
    public static AppSettings Defaults() => new();

    /// <summary>Clamps anything a hand-edited file could have made nonsensical.</summary>
    public void Normalise()
    {
        if (string.IsNullOrWhiteSpace(DeviceName)) DeviceName = Environment.MachineName;
        if (Port == 0) Port = 7000;
        // A hand-edited file can hold anything; NaN fails the range check on both ends,
        // which is why the clamp is written as two guards rather than Math.Clamp.
        if (Volume is < 0.0 or > 1.0 or double.NaN) Volume = 1.0;
        DisplayWidth = Math.Clamp(DisplayWidth, MinDisplayWidth, MaxDisplayWidth);
        DisplayHeight = Math.Clamp(DisplayHeight, MinDisplayHeight, MaxDisplayHeight);
        DisplayRefreshRate = Math.Clamp(DisplayRefreshRate, MinRefreshRate, MaxRefreshRate);
        Rotation = NormaliseRotation(Rotation);
        if (!Enum.IsDefined(Theme)) Theme = AppTheme.System;
        if (!Enum.IsDefined(VideoFit)) VideoFit = VideoFit.Fit;
        if (!Enum.IsDefined(Latency)) Latency = LatencyMode.Balanced;
        if (string.IsNullOrWhiteSpace(CaptureDirectory))
            CaptureDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SoulScreen");
        if (string.IsNullOrWhiteSpace(AudioOutputDeviceId)) AudioOutputDeviceId = null;

        if (WindowWidth is not (> 0 and < 20000) || WindowHeight is not (> 0 and < 20000))
        {
            WindowWidth = null;
            WindowHeight = null;
        }
        if (WindowLeft is not (> -20000 and < 20000) || WindowTop is not (> -20000 and < 20000))
        {
            WindowLeft = null;
            WindowTop = null;
        }

        RecentDevices ??= [];
        RecentDevices.RemoveAll(d => d is null || string.IsNullOrWhiteSpace(d.Name));
        if (RecentDevices.Count > MaxRecentDevices)
            RecentDevices = RecentDevices.OrderByDescending(d => d.LastSeenUtc).Take(MaxRecentDevices).ToList();
    }

    /// <summary>Folds any angle onto one of the four the picture can be shown at.</summary>
    public static int NormaliseRotation(int degrees)
    {
        var folded = ((degrees % 360) + 360) % 360;
        return (folded / 90) * 90;
    }

    /// <summary>The delay the picture is held for at each latency setting.</summary>
    public static TimeSpan PresentationDelayFor(LatencyMode mode) => mode switch
    {
        LatencyMode.Smooth => TimeSpan.FromMilliseconds(160),
        LatencyMode.Responsive => TimeSpan.FromMilliseconds(50),
        _ => TimeSpan.FromMilliseconds(100),
    };

    /// <summary>
    /// The audio margin that keeps sound level with a picture held for
    /// <paramref name="presentationDelay"/>. The sound card's own buffer makes up the rest.
    /// </summary>
    public static TimeSpan AudioReserveFor(TimeSpan presentationDelay) =>
        presentationDelay - TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Records a session against the phone that ran it: bumps its count and time, and keeps
    /// the list short and most-recent first.
    /// </summary>
    public void RememberDevice(string name, string? model, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        var existing = RecentDevices.FirstOrDefault(d =>
            string.Equals(d.Name, name, StringComparison.Ordinal)
            && string.Equals(d.Model, model, StringComparison.Ordinal));

        if (existing is null)
        {
            existing = new RecentDevice { Name = name, Model = model };
            RecentDevices.Add(existing);
        }

        existing.LastSeenUtc = DateTime.UtcNow;
        existing.SessionCount++;
        existing.TotalSeconds += Math.Max(duration.TotalSeconds, 0);

        RecentDevices = RecentDevices
            .OrderByDescending(d => d.LastSeenUtc)
            .Take(MaxRecentDevices)
            .ToList();
    }

    public AirPlayOptions ToAirPlayOptions() => new()
    {
        DeviceName = DeviceName,
        Port = Port,
        EnableAudio = EnableAudio,
        Features = EnableAudio
            ? AirPlayFeaturePresets.MirroringWithAudio
            : AirPlayFeaturePresets.MirroringVideoOnly,
        DisplayWidth = DisplayWidth,
        DisplayHeight = DisplayHeight,
        DisplayRefreshRate = DisplayRefreshRate,
        TraceProtocol = TraceProtocol,
        StateDirectory = Directory,
    };
}
