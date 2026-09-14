using NAudio.CoreAudioApi;
using SoulScreen.Core.Logging;

namespace SoulScreen.Media;

/// <summary>One playback device the phone's audio can be sent to.</summary>
/// <param name="Id">Stable endpoint id, what settings store. Null for the system default.</param>
/// <param name="Name">What the user sees, e.g. "Speakers (Realtek Audio)".</param>
public readonly record struct AudioOutputDevice(string? Id, string Name)
{
    /// <summary>Whatever Windows currently routes sound to; follows the system setting.</summary>
    public static AudioOutputDevice SystemDefault { get; } = new(null, "System default");

    public bool IsDefault => Id is null;
}

/// <summary>Enumerates and resolves playback devices.</summary>
public static class AudioDevices
{
    private static readonly ILogger Log_ = Log.For("audio-dev");

    /// <summary>Every active playback device, with the system default first.</summary>
    public static IReadOnlyList<AudioOutputDevice> ListOutputs()
    {
        var devices = new List<AudioOutputDevice> { AudioOutputDevice.SystemDefault };
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    devices.Add(new AudioOutputDevice(device.ID, device.FriendlyName));
                }
            }
        }
        catch (Exception ex)
        {
            // No audio service at all - a bare server, or a session with no sound devices.
            Log_.Debug($"could not enumerate playback devices: {ex.Message}");
        }
        return devices;
    }

    /// <summary>
    /// Looks a stored id up again. Returns null when the id is null, or the device has since
    /// been unplugged - in which case the caller should fall back to the default rather than
    /// fail, since a missing headset is not a reason for silence.
    /// </summary>
    internal static MMDevice? TryOpen(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(id);
            if (device.State == DeviceState.Active) return device;
            device.Dispose();
            Log_.Warn("the chosen playback device is not active; using the system default");
            return null;
        }
        catch (Exception ex)
        {
            Log_.Warn($"the chosen playback device could not be opened; using the system default: {ex.Message}");
            return null;
        }
    }
}
