using System.Diagnostics;

namespace SoulScreen.Android;

public enum AdbDeviceState
{
    /// <summary>Authorized and ready - what "adb devices" calls "device".</summary>
    Online,
    /// <summary>Plugged in, but the phone has not yet had "Allow USB debugging" tapped.</summary>
    Unauthorized,
    /// <summary>Enumerated but not responding, usually mid-reconnect.</summary>
    Offline,
    Other,
}

/// <param name="Serial">adb's identifier - a USB serial, or "ip:port" for wireless debugging.</param>
/// <param name="Model">Reported by "-l", e.g. "Pixel_7_Pro"; SoulScreen shows it with spaces.</param>
public readonly record struct AdbDevice(string Serial, AdbDeviceState State, string? Model)
{
    public string DisplayModel => Model?.Replace('_', ' ') ?? Serial;
}

/// <summary>Runs the handful of one-shot adb commands the Android transport needs. Every
/// call starts and waits for a short-lived adb process; none of this is on the hot path,
/// which is the persistent "exec-out screenrecord" the transport reads continuously.</summary>
internal static class AdbCommands
{
    public static async Task<IReadOnlyList<AdbDevice>> ListDevicesAsync(string adbPath, CancellationToken token)
    {
        var output = await RunAsync(adbPath, ["devices", "-l"], token).ConfigureAwait(false);
        var devices = new List<AdbDevice>();

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.Ordinal)) continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var state = parts[1] switch
            {
                "device" => AdbDeviceState.Online,
                "unauthorized" => AdbDeviceState.Unauthorized,
                "offline" => AdbDeviceState.Offline,
                _ => AdbDeviceState.Other,
            };

            string? model = null;
            for (var i = 2; i < parts.Length; i++)
            {
                if (parts[i].StartsWith("model:", StringComparison.Ordinal))
                    model = parts[i]["model:".Length..];
            }

            devices.Add(new AdbDevice(parts[0], state, model));
        }

        return devices;
    }

    public static async Task<int?> GetSdkVersionAsync(string adbPath, string serial, CancellationToken token)
    {
        var output = await RunAsync(adbPath, ["-s", serial, "shell", "getprop", "ro.build.version.sdk"], token)
            .ConfigureAwait(false);
        return int.TryParse(output.Trim(), out var sdk) ? sdk : null;
    }

    /// <summary>
    /// Parses "wm size" output, e.g.:
    /// <code>Physical size: 1080x2400
    /// Override size: 900x2000</code>
    /// An override (set by the user or another tool via "wm size WxH") takes precedence,
    /// since it is what will actually be captured.
    /// </summary>
    public static async Task<(int Width, int Height)?> GetScreenSizeAsync(string adbPath, string serial, CancellationToken token)
    {
        var output = await RunAsync(adbPath, ["-s", serial, "shell", "wm", "size"], token).ConfigureAwait(false);

        (int Width, int Height)? physical = null;
        (int Width, int Height)? overrideSize = null;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            var colon = line.IndexOf(':');
            if (colon < 0) continue;

            var label = line[..colon];
            var dims = line[(colon + 1)..].Trim().Split('x');
            if (dims.Length != 2) continue;
            if (!int.TryParse(dims[0], out var w) || !int.TryParse(dims[1], out var h)) continue;

            if (label.StartsWith("Override", StringComparison.OrdinalIgnoreCase)) overrideSize = (w, h);
            else if (label.StartsWith("Physical", StringComparison.OrdinalIgnoreCase)) physical = (w, h);
        }

        return overrideSize ?? physical;
    }

    /// <summary>Pushes a local file to the device, e.g. the scrcpy server jar.</summary>
    public static Task PushAsync(string adbPath, string serial, string localPath, string remotePath, CancellationToken token)
        => RunCheckedAsync(adbPath, ["-s", serial, "push", localPath, remotePath], token);

    /// <summary>Maps a local TCP port to a device-side abstract socket, e.g. the one
    /// scrcpy-server listens on.</summary>
    public static Task ForwardAsync(string adbPath, string serial, int localPort, string remoteSocketName, CancellationToken token)
        => RunCheckedAsync(adbPath, ["-s", serial, "forward", $"tcp:{localPort}", $"localabstract:{remoteSocketName}"], token);

    /// <summary>Best-effort: a forward left dangling after the phone is unplugged is
    /// harmless (adb drops it with the device), so failures here are not worth surfacing.</summary>
    public static async Task RemoveForwardAsync(string adbPath, string serial, int localPort, CancellationToken token)
    {
        try { await RunAsync(adbPath, ["-s", serial, "forward", "--remove", $"tcp:{localPort}"], token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
    }

    private static async Task RunCheckedAsync(string adbPath, string[] args, CancellationToken token)
    {
        var (exitCode, stdout, stderr) = await RunWithExitCodeAsync(adbPath, args, token).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"adb {string.Join(' ', args)} failed (exit {exitCode}): {(stderr.Length > 0 ? stderr : stdout).Trim()}");
        }
    }

    private static async Task<string> RunAsync(string adbPath, string[] args, CancellationToken token)
    {
        var (_, stdout, _) = await RunWithExitCodeAsync(adbPath, args, token).ConfigureAwait(false);
        return stdout;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunWithExitCodeAsync(
        string adbPath, string[] args, CancellationToken token)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(adbPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(token);
        var stderrTask = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping a session mid-handshake (a push still transferring, a list still
            // waiting on a wedged daemon) must not leave an adb process behind: disposing
            // the Process object does not end the child.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw;
        }

        return (process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }
}
