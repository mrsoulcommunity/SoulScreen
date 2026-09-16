using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SoulScreen.AirPlay;
using SoulScreen.App.Logic;
using SoulScreen.Core.Logging;

namespace SoulScreen.App;

/// <summary>The file format screenshots are saved in.</summary>
public enum ScreenshotFormat
{
    /// <summary>Lossless, and exactly what was on screen.</summary>
    Png,
    /// <summary>A fraction of the size, for sharing.</summary>
    Jpeg,
}

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
/// Where the floating picture controls (record, screenshot, sound, pause) live. Each mode
/// answers a different way of working: <see cref="Floating"/> for the default over-picture
/// experience, <see cref="Corner"/> for an out-of-the-way parked bar, <see cref="Free"/> for
/// the bar the user has dragged to where they want it, and <see cref="Docked"/> for a bar
/// that lives with the rest of the window's chrome and is never hidden.
/// </summary>
public enum ControlBarPlacement
{
    /// <summary>Floats centred over the foot of the picture, fading away until the pointer moves.</summary>
    Floating,
    /// <summary>Parks in one of the picture's corners (chosen by <see cref="ControlBarCorner"/>),
    /// still fading when the pointer is still.</summary>
    Corner,
    /// <summary>Stays wherever the user dragged it, snapping to the nearest corner at rest so
    /// it lines up with the picture's edge.</summary>
    Free,
    /// <summary>Sits in the top caption strip beside the window controls, never fading. The
    /// picture fills the area underneath.</summary>
    Docked,
}

/// <summary>One of the four picture corners, or the centred position over its foot.</summary>
public enum ControlBarCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    /// <summary>The original placement: centred over the foot of the picture.</summary>
    BottomCentre,
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

    // ---------------------------------------------------- per-device profile

    /// <summary>This phone's own accent colour, independent of every other phone's.</summary>
    public AccentColor Accent { get; set; } = AccentColor.Blue;

    /// <summary>This phone's own picture mapping: fit, fill, stretch or actual size.</summary>
    public VideoFit VideoFit { get; set; } = VideoFit.Fit;

    /// <summary>This phone's own rotation: 0, 90, 180 or 270.</summary>
    public int Rotation { get; set; }

    public bool MirrorHorizontally { get; set; }

    /// <summary>Start recording as soon as this phone connects, regardless of the
    /// general "record on connect" setting.</summary>
    public bool AutoRecord { get; set; }
}

/// <summary>The first day of the week, used by any calendar widget.</summary>
public enum FirstDayOfWeek
{
    Saturday,
    Sunday,
    Monday,
}

/// <summary>
/// Locale-aware timestamp formatting. Lives in settings so the gallery, summary and log
/// all switch at once.
/// </summary>
public sealed class TimestampSettings
{
    /// <summary>
    /// When non-null, overrides the locale-based default. <c>null</c> means "follow the
    /// system culture" (Shamsi when the UI culture is Persian).
    /// </summary>
    public bool? UseShamsi { get; set; }

    /// <summary>When Shamsi is on, also show the Gregorian in parentheses.</summary>
    public bool ShowGregorianAlongside { get; set; }

    public FirstDayOfWeek FirstDay { get; set; } = FirstDayOfWeek.Saturday;
}

/// <summary>
/// A custom logo or text laid over every screenshot, for demos and branded hand-offs. Off
/// by default - nothing about an existing capture changes until a mark is chosen and turned
/// on.
/// <para>
/// Screenshots only: recordings are written by remuxing the phone's H.264 directly with no
/// decode step (see <c>SessionRecorder</c>), which is what keeps recording both lossless and
/// nearly free of CPU cost. Baking a mark into every frame would mean fully transcoding
/// every recording, trading that away for a feature a still image already delivers.
/// </para>
/// <para>
/// The image is read from disk each time a screenshot is composed rather than cached in
/// settings, so replacing the file (a new logo, a fixed typo) takes effect on the very next
/// screenshot with no restart.
/// </para>
/// </summary>
public sealed class WatermarkSettings
{
    /// <summary>Off by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>Path to a PNG or JPEG to draw over every capture. Null/missing/unreadable
    /// is treated the same as disabled for that capture - a screenshot must never fail to
    /// save just because the watermark file was moved.</summary>
    public string? ImagePath { get; set; }

    public WatermarkCorner Corner { get; set; } = WatermarkCorner.BottomRight;

    /// <summary>Fraction of the canvas' shortest side the mark's longest side spans.</summary>
    public double Scale { get; set; } = WatermarkPlacement.DefaultScale;

    /// <summary>0 (invisible) to 1 (opaque).</summary>
    public double Opacity { get; set; } = WatermarkPlacement.DefaultOpacity;

    /// <summary>Gap from the edges, as a fraction of the canvas' shortest side. Ignored for
    /// <see cref="WatermarkCorner.Center"/>.</summary>
    public double MarginFraction { get; set; } = WatermarkPlacement.DefaultMarginFraction;
}

/// <summary>
/// The PIN lock: guards SoulScreen from being opened by someone else with physical access to
/// an unlocked PC, independent of Windows' own lock screen.
/// <para>
/// Only a salted PBKDF2 hash of the PIN is kept - see <see cref="Logic.AppLock"/> - and that
/// hash is DPAPI-protected to the current Windows user on top, so copying settings.json to
/// another machine or another account carries nothing worth cracking.
/// </para>
/// </summary>
public sealed class LockSettings
{
    /// <summary>Off by default: a PIN nobody asked for is a lockout waiting to happen.</summary>
    public bool Enabled { get; set; }

    /// <summary>DPAPI-protected (current user), then base64: the salt PBKDF2 used.</summary>
    public string? ProtectedSaltBase64 { get; set; }

    /// <summary>DPAPI-protected (current user), then base64: the PBKDF2 hash itself.</summary>
    public string? ProtectedHashBase64 { get; set; }

    public int Iterations { get; set; } = Logic.AppLock.DefaultIterations;

    /// <summary>Lock again after this many minutes of no input while the window has focus and
    /// nothing is mirroring. Zero means "only when minimised to the tray or on request".</summary>
    public int AutoLockAfterMinutesIdle { get; set; }

    /// <summary>Lock whenever the window goes to the notification area.</summary>
    public bool LockOnMinimizeToTray { get; set; } = true;

    /// <summary>Lock as soon as SoulScreen starts, before anything - including a mirrored
    /// phone from a previous session's demo - can be seen.</summary>
    public bool LockOnLaunch { get; set; }

    // ---- backoff state, persisted so restarting the app cannot be used to reset it ----

    /// <summary>Wrong PINs entered in a row since the last correct one.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>When the most recent wrong PIN was entered, for <see cref="Logic.LockoutPolicy"/>.</summary>
    public DateTime? LastFailureUtc { get; set; }
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

    /// <summary>
    /// Where settings, the pairing identity and the log live. SOULSCREEN_HOME moves all of it
    /// elsewhere - a second, separate copy for trying a build out beside the one in daily use,
    /// or a portable copy on a removable drive.
    /// </summary>
    public static string Directory { get; } =
        Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } home
            ? Path.GetFullPath(home)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoulScreen");

    /// <summary>The environment variable that relocates <see cref="Directory"/>.</summary>
    public const string HomeVariable = "SOULSCREEN_HOME";

    /// <summary>True when <see cref="Directory"/> was moved by <see cref="HomeVariable"/>.</summary>
    public static bool HasCustomHome => Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 };

    private static string FilePath => Path.Combine(Directory, "settings.json");

    // ------------------------------------------------------------------ receiver

    /// <summary>Name the receiver advertises, which is what appears on the phone.</summary>
    public string DeviceName { get; set; } = Environment.MachineName;

    /// <summary>Control-channel port. 7000 is what Apple receivers use.</summary>
    public ushort Port { get; set; } = 7000;

    /// <summary>Accept the phone's audio as well as its screen.</summary>
    public bool EnableAudio { get; set; } = true;

    /// <summary>
    /// Mirror more than one iPhone at once into a grid of tiles. Off keeps today's
    /// single-session behaviour exactly as it is - one phone takes the whole window, and a
    /// second connecting takes the receiver over, as it always has.
    /// </summary>
    public bool EnableMultiDevice { get; set; }

    /// <summary>Highest number of phones mirrored at once in multi-device mode, 2 to 4.
    /// A session beyond the cap still mirrors - the phone knows no different - but it is
    /// not offered a tile until one frees up.</summary>
    public int MaxMirroredTiles { get; set; } = 4;

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

    /// <summary>The colour of selection, switches and the primary buttons.</summary>
    public AccentColor Accent { get; set; } = AccentColor.Blue;

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

    /// <summary>Draw the small performance graph under the statistics overlay, so a spike
    /// is seen as a shape rather than worked out from numbers.</summary>
    public bool ShowPerformanceGraph { get; set; }

    /// <summary>Round the picture's corners in the window, as the phone's own screen is.</summary>
    public bool RoundedCorners { get; set; } = true;

    // --------------------------------------------------------------------- markup

    public const int MarkupColorCount = 6;
    public const int MarkupSizeCount = 3;

    /// <summary>The ink last chosen for drawing over the picture.</summary>
    public int MarkupColorIndex { get; set; }

    /// <summary>The stroke width last chosen: fine, medium or bold.</summary>
    public int MarkupSizeIndex { get; set; } = 1;

    // -------------------------------------------------------------------- privacy

    /// <summary>Hold a phone's picture and sound back until it is allowed to mirror.</summary>
    public bool AskBeforeMirroring { get; set; }

    /// <summary>Phones allowed to mirror without being asked about.</summary>
    public List<DeviceKey> AllowedDevices { get; set; } = [];

    /// <summary>Phones turned away whenever they try to mirror.</summary>
    public List<DeviceKey> BlockedDevices { get; set; } = [];

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

    public ScreenshotFormat ScreenshotFormat { get; set; } = ScreenshotFormat.Png;

    /// <summary>Most the capture folder may hold, in bytes. Zero means no budget, which is
    /// the default: the folder grows until the user prunes it.</summary>
    public long CaptureBudgetBytes { get; set; }

    // ------------------------------------------------------------- on connecting

    /// <summary>Bring the window out of the notification area or the taskbar when a phone
    /// starts mirroring: a mirror nobody can see is not much use.</summary>
    public bool BringToFrontOnConnect { get; set; } = true;

    /// <summary>Go fullscreen when a phone starts mirroring.</summary>
    public bool FullscreenOnConnect { get; set; }

    /// <summary>Start recording when a phone starts mirroring. Not for the demo.</summary>
    public bool RecordOnConnect { get; set; }

    /// <summary>Leave fullscreen when mirroring ends, rather than filling the screen with
    /// the idle panel.</summary>
    public bool LeaveFullscreenOnDisconnect { get; set; } = true;

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

    /// <summary>Screenshot, record, mini player and show-window shortcuts that work while
    /// another application has the keyboard. Off by default: they take keys from every app.</summary>
    public bool GlobalHotkeys { get; set; }

    /// <summary>Set once the welcome screen has been seen, so it is shown only the first time.</summary>
    public bool HasSeenWelcome { get; set; }

    /// <summary>The app's own answer to Windows' animation setting.</summary>
    public MotionPreference Animations { get; set; } = MotionPreference.FollowWindows;

    /// <summary>Which display the window sits on: "current", "primary", or the index of
    /// one, as text. Checked when a session starts and when displays change.</summary>
    public string TargetDisplay { get; set; } = DisplayLayout.CurrentDisplay;

    /// <summary>Last window placement, restored on the next launch.</summary>
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    /// <summary>The mini player's longer side and its place, from the last time it was used.</summary>
    public double? MiniPlayerLongSide { get; set; }
    public double? MiniPlayerLeft { get; set; }
    public double? MiniPlayerTop { get; set; }

    // -------------------------------------------------------- picture controls

    /// <summary>
    /// Where the floating picture controls sit. <see cref="ControlBarPlacement.Floating"/> keeps
    /// the original behaviour - the bar in the lower centre of the picture, fading away until the
    /// pointer moves. <see cref="ControlBarPlacement.Corner"/> parks it in one of the four
    /// corners (chosen by <see cref="ControlBarCorner"/>), still auto-hiding. <see cref="ControlBarPlacement.Free"/>
    /// remembers the dragged-to position and snaps to whichever corner it ends nearest, so a
    /// bar pulled into the corner is also a parked one. <see cref="ControlBarPlacement.Docked"/>
    /// tucks the bar into the top caption strip next to the window controls, where it sits
    /// beside the rest of the chrome and never fades out.
    /// </summary>
    public ControlBarPlacement ControlBarPlacement { get; set; } = ControlBarPlacement.Floating;

    /// <summary>Which corner the floating picture controls dock to, in <see cref="ControlBarPlacement.Corner"/>
    /// and as the snap target in <see cref="ControlBarPlacement.Free"/>.</summary>
    public ControlBarCorner ControlBarCorner { get; set; } = ControlBarCorner.BottomCentre;

    /// <summary>The bar's last dragged position in <see cref="ControlBarPlacement.Free"/>, as
    /// fractions of the picture's width and height (0..1). Saved so the bar returns to the same
    /// spot after a relaunch.</summary>
    public double? ControlBarFreeX { get; set; }
    public double? ControlBarFreeY { get; set; }

    /// <summary>When true, the floating bar shows only icons (no shortcut labels, tighter
    /// spacing) for users who already know the shortcuts and want the bar to take less room.</summary>
    public bool CompactControlBar { get; set; }

    /// <summary>True the very first time the floating bar is shown after install - the
    /// drag-me hint pulses briefly, then this flips off so it never appears again.</summary>
    public bool HasSeenBarHint { get; set; }

    // -------------------------------------------------------------------- history

    public List<RecentDevice> RecentDevices { get; set; } = [];

    // ----------------------------------------------------------------- timestamps

    /// <summary>
    /// How timestamps appear across the app: gallery subtitles, session summary, activity
    /// log, and capture filenames. Persisted as a single object so future fields can be
    /// added without breaking existing settings files.
    /// </summary>
    public TimestampSettings Timestamps { get; set; } = new();

    // -------------------------------------------------------- recurring recording

    /// <summary>"Record every weekday at 09:00" rules, checked once a minute while the app
    /// runs. Empty by default - nothing records on its own until one is added.</summary>
    public List<RecurringRecordingRule> RecurringRecordings { get; set; } = [];

    // ------------------------------------------------------------------------ lock

    /// <summary>The PIN lock. Off by default.</summary>
    public LockSettings Lock { get; set; } = new();

    // ------------------------------------------------------------------- watermark

    /// <summary>The custom logo/branding overlay. Off by default.</summary>
    public WatermarkSettings Watermark { get; set; } = new();

    // ------------------------------------------------------------------------ ocr

    /// <summary>
    /// Search screenshots by the text on screen, not just the file name. Off by default:
    /// running Windows' OCR over a folder of screenshots costs real time on first use, and
    /// nobody who never needs it should pay that just because the app was updated.
    /// </summary>
    public bool EnableOcrSearch { get; set; }

    // ------------------------------------------------------------------- updates

    /// <summary>Looks for a newer release on GitHub when SoulScreen starts, and again every
    /// few hours while it runs. The check itself is silent; nothing downloads or installs
    /// without "Install and restart" in Settings.</summary>
    public bool CheckForUpdatesAutomatically { get; set; } = true;

    /// <summary>The release tag "Skip this version" was pressed for, so it is not raised
    /// again. Compared as text, not as a parsed version.</summary>
    public string? SkippedUpdateVersion { get; set; }

    /// <summary>When the last quiet background check ran, so the interval between checks is
    /// honoured across restarts rather than resetting every time SoulScreen opens.</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }

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
        if (!Enum.IsDefined(Accent)) Accent = AccentColor.Blue;
        if (!Enum.IsDefined(ScreenshotFormat)) ScreenshotFormat = ScreenshotFormat.Png;
        if (!Enum.IsDefined(Animations)) Animations = MotionPreference.FollowWindows;
        if (!Enum.IsDefined(ControlBarPlacement)) ControlBarPlacement = ControlBarPlacement.Floating;
        if (!Enum.IsDefined(ControlBarCorner)) ControlBarCorner = ControlBarCorner.BottomCentre;

        // The tile budget is a hand-editable number; anything the grid cannot lay out reads
        // as the four-tile maximum rather than zero phones mirrored.
        if (MaxMirroredTiles is < 1 or > 4) MaxMirroredTiles = 4;

        // The free-position fractions are 0..1 against the picture's edges; anything else was
        // typed by hand into a settings file, or corrupted on the way in, and would place the
        // bar off-screen.
        if (ControlBarFreeX is < 0.0 or > 1.0 or double.NaN) ControlBarFreeX = null;
        if (ControlBarFreeY is < 0.0 or > 1.0 or double.NaN) ControlBarFreeY = null;

        // A hand-edited budget can hold anything; negative and absurdly small values mean
        // "off" rather than "prune every capture the moment it is taken".
        if (CaptureBudgetBytes < 256L * 1024 * 1024) CaptureBudgetBytes = 0;

        // The display choice is "current", "primary" or a whole number a monitor answers to.
        if (TargetDisplay != DisplayLayout.CurrentDisplay
            && TargetDisplay != DisplayLayout.PrimaryDisplay
            && (!int.TryParse(TargetDisplay, System.Globalization.CultureInfo.InvariantCulture, out var displayIndex) || displayIndex < 0))
        {
            TargetDisplay = DisplayLayout.CurrentDisplay;
        }

        if (MiniPlayerLongSide is not (> 0 and < 20000)) MiniPlayerLongSide = null;
        if (MiniPlayerLeft is not (> -20000 and < 20000) || MiniPlayerTop is not (> -20000 and < 20000))
        {
            MiniPlayerLeft = null;
            MiniPlayerTop = null;
        }
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

        MarkupColorIndex = MarkupColorIndex is >= 0 and < MarkupColorCount ? MarkupColorIndex : 0;
        MarkupSizeIndex = MarkupSizeIndex is >= 0 and < MarkupSizeCount ? MarkupSizeIndex : 1;
        AllowedDevices = DeviceTrust.Sanitise(AllowedDevices);
        BlockedDevices = DeviceTrust.Sanitise(BlockedDevices);
        // A phone on both lists is blocked; being allowed as well only confuses the settings.
        AllowedDevices.RemoveAll(key => DeviceTrust.Contains(BlockedDevices, key.Name, key.Model));

        RecentDevices ??= [];
        RecentDevices.RemoveAll(d => d is null || string.IsNullOrWhiteSpace(d.Name));
        if (RecentDevices.Count > MaxRecentDevices)
            RecentDevices = RecentDevices.OrderByDescending(d => d.LastSeenUtc).Take(MaxRecentDevices).ToList();

        // Timestamps is a complex object; null-safe + clamp the enum.
        Timestamps ??= new TimestampSettings();
        if (!Enum.IsDefined(Timestamps.FirstDay)) Timestamps.FirstDay = FirstDayOfWeek.Saturday;

        // A hand-edited rule can hold a stray flag combination or a negative duration; the
        // schedule already treats None as "never fires", so nothing further is dropped here -
        // only the shapes that would otherwise crash the picker or the day-of-week arithmetic.
        RecurringRecordings ??= [];
        RecurringRecordings.RemoveAll(rule => rule is null);
        foreach (var rule in RecurringRecordings)
        {
            if (string.IsNullOrWhiteSpace(rule.Id)) rule.Id = Guid.NewGuid().ToString("N");
            rule.Days &= RecordingDays.All;
            if (rule.DurationMinutes < 0) rule.DurationMinutes = 0;
        }

        // A lock with no hash cannot be enabled - a hand-edited "Enabled: true" with nothing
        // else would otherwise lock the app out permanently with no PIN that could open it.
        Lock ??= new LockSettings();
        if (string.IsNullOrEmpty(Lock.ProtectedSaltBase64) || string.IsNullOrEmpty(Lock.ProtectedHashBase64))
            Lock.Enabled = false;
        if (Lock.Iterations <= 0) Lock.Iterations = Logic.AppLock.DefaultIterations;
        if (Lock.AutoLockAfterMinutesIdle < 0) Lock.AutoLockAfterMinutesIdle = 0;
        if (Lock.ConsecutiveFailures < 0) Lock.ConsecutiveFailures = 0;

        // A watermark that cannot be drawn (bad enum, out-of-range slider dragged in a hand-
        // edited file) should still degrade to something sane rather than throw mid-capture.
        Watermark ??= new WatermarkSettings();
        if (!Enum.IsDefined(Watermark.Corner)) Watermark.Corner = WatermarkCorner.BottomRight;
        Watermark.Scale = Math.Clamp(Watermark.Scale, WatermarkPlacement.MinScale, WatermarkPlacement.MaxScale);
        Watermark.Opacity = Math.Clamp(Watermark.Opacity, 0, 1);
        Watermark.MarginFraction = Math.Clamp(Watermark.MarginFraction, 0, 0.5);
        if (Watermark.Enabled && string.IsNullOrWhiteSpace(Watermark.ImagePath)) Watermark.Enabled = false;
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
