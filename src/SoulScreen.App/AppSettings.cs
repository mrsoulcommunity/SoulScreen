using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SoulScreen.AirPlay;
using SoulScreen.Core.Logging;

namespace SoulScreen.App;

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
    };

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoulScreen");

    private static string FilePath => Path.Combine(Directory, "settings.json");

    /// <summary>Name the receiver advertises, which is what appears on the phone.</summary>
    public string DeviceName { get; set; } = Environment.MachineName;

    /// <summary>Control-channel port. 7000 is what Apple receivers use.</summary>
    public ushort Port { get; set; } = 7000;

    /// <summary>Accept the phone's audio as well as its screen.</summary>
    public bool EnableAudio { get; set; } = true;

    /// <summary>Keep the window above other applications.</summary>
    public bool AlwaysOnTop { get; set; }

    /// <summary>Start advertising as soon as the app opens.</summary>
    public bool StartReceiverOnLaunch { get; set; } = true;

    /// <summary>Resolution advertised to the phone, which is what it encodes at.</summary>
    public int DisplayWidth { get; set; } = 1920;

    public int DisplayHeight { get; set; } = 1080;

    public int DisplayRefreshRate { get; set; } = 60;

    /// <summary>Where screenshots go. Defaults to Pictures\SoulScreen.</summary>
    public string CaptureDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SoulScreen");

    /// <summary>Log every RTSP request and response. Noisy, but the first thing to turn on
    /// when a phone will not connect.</summary>
    public bool TraceProtocol { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
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
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SerializerOptions));
        }
        catch (Exception ex)
        {
            Log_.Warn($"could not write {FilePath}", ex);
        }
    }

    /// <summary>Clamps anything a hand-edited file could have made nonsensical.</summary>
    private void Normalise()
    {
        if (string.IsNullOrWhiteSpace(DeviceName)) DeviceName = Environment.MachineName;
        if (Port == 0) Port = 7000;
        DisplayWidth = Math.Clamp(DisplayWidth, 640, 3840);
        DisplayHeight = Math.Clamp(DisplayHeight, 480, 2160);
        DisplayRefreshRate = Math.Clamp(DisplayRefreshRate, 24, 120);
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
