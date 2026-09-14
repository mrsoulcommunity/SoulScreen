using System.Diagnostics;
using Microsoft.Win32;
using SoulScreen.Core.Logging;

namespace SoulScreen.App;

/// <summary>
/// Registers the app to start when the user signs in, through the per-user Run key. No
/// elevation, no scheduled task, and Settings &gt; Apps &gt; Startup lists and can disable it
/// like any other program.
/// </summary>
internal static class StartupRegistration
{
    private static readonly ILogger Log_ = Log.For("startup");

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SoulScreen";

    /// <summary>Passed on the command line when launched at sign-in, so the window opens minimised.</summary>
    public const string MinimisedArgument = "--minimized";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Adds or removes the entry. Returns false if the registry refused.</summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return false;

            if (enabled)
            {
                var executable = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(executable)) return false;
                key.SetValue(ValueName, $"\"{executable}\" {MinimisedArgument}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch (Exception ex)
        {
            Log_.Warn("could not change the startup registration", ex);
            return false;
        }
    }
}
