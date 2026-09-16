using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using SoulScreen.App.Logic;

namespace SoulScreen.App;

/// <summary>
/// Checking GitHub for a newer SoulScreen release, and - only once the user presses
/// "Download and install" - fetching it, verifying it and handing off to the small script
/// in <see cref="UpdateService"/> that swaps the install directory and restarts the app.
/// <para>
/// A quiet check runs once at startup and every few hours after that (see
/// <see cref="UpdatePolicy.CheckInterval"/>), purely to decide whether the toast and the
/// Settings card have something to say; nothing downloads on its own. "Check for updates
/// automatically" in Settings turns the quiet check off without touching the manual
/// "Check now" button, which always works.
/// </para>
/// </summary>
public partial class MainWindow
{
    private readonly UpdateService _updates = new();
    private DispatcherTimer? _updateTimer;

    private bool _updateChecking;
    private bool _updateBusy;
    private UpdateRelease? _latestRelease;
    private CancellationTokenSource? _updateDownloadCts;

    private UpdateVersion CurrentVersion =>
        UpdateVersion.FromAssemblyVersion(System.Reflection.Assembly.GetExecutingAssembly().GetName().Version);

    // ------------------------------------------------------------------ startup

    private void InitialiseUpdates()
    {
        RefreshUpdateSection();

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _updateTimer.Tick += (_, _) => MaybeRunBackgroundCheck();
        _updateTimer.Start();

        MaybeRunBackgroundCheck();
    }

    private void DisposeUpdates()
    {
        _updateTimer?.Stop();
        _updateDownloadCts?.Cancel();
    }

    /// <summary>Runs a silent check when one is due and the setting allows it. Only a toast
    /// marks the result; nothing else on screen changes unless Settings is open.</summary>
    private async void MaybeRunBackgroundCheck()
    {
        if (!_settings.CheckForUpdatesAutomatically) return;
        if (!UpdatePolicy.IsCheckDue(_settings.LastUpdateCheckUtc, DateTime.UtcNow, UpdatePolicy.CheckInterval)) return;
        await RunUpdateCheckAsync(announce: true);
    }

    // ------------------------------------------------------------------ checking

    private async void OnCheckForUpdates(object sender, RoutedEventArgs e) => await RunUpdateCheckAsync(announce: false, manual: true);

    private async Task RunUpdateCheckAsync(bool announce, bool manual = false)
    {
        if (_updateChecking || _updateBusy) return;
        _updateChecking = true;
        if (manual) RefreshUpdateSection();

        try
        {
            var release = await _updates.GetLatestReleaseAsync();
            _settings.LastUpdateCheckUtc = DateTime.UtcNow;
            _settings.Save();

            if (release is null)
            {
                _log.Warn("could not reach GitHub to check for an update");
                if (manual) ShowToast("Could not check for an update - see the activity log", "");
                return;
            }

            _latestRelease = release;
            var available = UpdatePolicy.IsUpdateAvailable(CurrentVersion, release.Version, release.TagName, _settings.SkippedUpdateVersion);

            _log.Info(available
                ? $"an update is available: {release.TagName} (running {CurrentVersion})"
                : $"SoulScreen {CurrentVersion} is up to date (latest release: {release.TagName})");

            if (available && announce)
            {
                ShowToast($"SoulScreen {release.Version} is available", "\uEB52", "View",
                    () => ShowSettingsSection("UPDATES"));
            }
            else if (manual && !available)
            {
                ShowToast("You have the latest version", "");
            }
        }
        catch (Exception ex)
        {
            _log.Warn("the update check failed", ex);
            if (manual) ShowToast("Could not check for an update - see the activity log", "");
        }
        finally
        {
            _updateChecking = false;
            RefreshUpdateSection();
        }
    }

    // ------------------------------------------------------------------ the settings card

    /// <summary>Redraws the Updates card in Settings from whatever the last check found.
    /// Safe to call whether or not Settings is open.</summary>
    private void RefreshUpdateSection()
    {
        if (UpdateStatusText is null) return;

        _populatingSettings = true;
        try { UpdateAutoCheckToggle.IsChecked = _settings.CheckForUpdatesAutomatically; }
        finally { _populatingSettings = false; }

        UpdateCheckButton.IsEnabled = !_updateChecking && !_updateBusy;
        UpdateInstallButton.Visibility = Visibility.Collapsed;
        UpdateSkipButton.Visibility = Visibility.Collapsed;
        UpdateOpenPageButton.Visibility = Visibility.Collapsed;
        UpdateProgressBar.Visibility = Visibility.Collapsed;

        if (_updateChecking)
        {
            UpdateStatusText.Text = "Checking for an update…";
            UpdateDetailText.Text = "";
            return;
        }

        if (_updateBusy)
        {
            UpdateStatusText.Text = $"Installing {_latestRelease?.TagName}…";
            UpdateDetailText.Text = "SoulScreen will restart itself once this finishes.";
            UpdateProgressBar.Visibility = Visibility.Visible;
            return;
        }

        if (_latestRelease is not { } release)
        {
            UpdateStatusText.Text = $"SoulScreen {CurrentVersion}";
            UpdateDetailText.Text = _settings.LastUpdateCheckUtc is null
                ? "Never checked for an update."
                : $"No check has succeeded yet. Last tried {FormatCheckTime(_settings.LastUpdateCheckUtc.Value)}.";
            return;
        }

        var available = UpdatePolicy.IsUpdateAvailable(CurrentVersion, release.Version, release.TagName, _settings.SkippedUpdateVersion);
        if (!available)
        {
            UpdateStatusText.Text = $"SoulScreen {CurrentVersion} is up to date";
            UpdateDetailText.Text = _settings.LastUpdateCheckUtc is { } checkedAt
                ? $"Last checked {FormatCheckTime(checkedAt)}. Latest release: {release.TagName}."
                : $"Latest release: {release.TagName}.";
            return;
        }

        UpdateStatusText.Text = $"SoulScreen {release.Version} is available";
        UpdateDetailText.Text = $"Running {CurrentVersion}.";

        if (UpdateService.CanSelfUpdate(out var reason))
        {
            UpdateInstallButton.Visibility = Visibility.Visible;
            UpdateSkipButton.Visibility = Visibility.Visible;
        }
        else
        {
            UpdateDetailText.Text += $" {reason}";
            UpdateOpenPageButton.Visibility = Visibility.Visible;
        }
    }

    private static string FormatCheckTime(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var age = DateTime.Now - local;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} minute(s) ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} hour(s) ago";
        return local.ToString("d");
    }

    private void OnUpdateAutoCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_populatingSettings) return;
        _settings.CheckForUpdatesAutomatically = UpdateAutoCheckToggle.IsChecked == true;
        _settings.Save();
    }

    private void OnSkipUpdate(object sender, RoutedEventArgs e)
    {
        if (_latestRelease is not { } release) return;
        _settings.SkippedUpdateVersion = release.TagName;
        _settings.Save();
        ShowToast($"{release.TagName} will not be offered again", "");
        RefreshUpdateSection();
    }

    private void OnOpenReleasePage(object sender, RoutedEventArgs e)
    {
        var url = _latestRelease?.HtmlUrl;
        if (string.IsNullOrEmpty(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { _log.Warn($"could not open {url}", ex); }
    }

    // ----------------------------------------------------------------- installing

    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (_updateBusy || _latestRelease is not { } release) return;

        var asset = UpdatePolicy.SelectWindowsAssetName(release.Assets.Select(a => a.Name)) is { } name
            ? release.Assets.First(a => a.Name == name)
            : null;
        if (asset is null)
        {
            ShowToast("That release has no Windows build to download", "");
            return;
        }

        var answer = MessageBox.Show(this,
            $"Download and install SoulScreen {release.Version}?\n\nSoulScreen will close and restart itself once the download " +
            "is verified. Nothing is installed until this finishes.",
            "Install update", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer != MessageBoxResult.Yes) return;

        _updateBusy = true;
        _updateDownloadCts = new CancellationTokenSource();
        RefreshUpdateSection();

        var progress = new Progress<double>(fraction =>
        {
            UpdateProgressBar.Value = Math.Clamp(fraction, 0, 1) * 100;
            UpdateDetailText.Text = $"Downloading… {(int)Math.Round(Math.Clamp(fraction, 0, 1) * 100)}%";
        });

        try
        {
            var stagingDir = await _updates.DownloadAndStageAsync(asset, release.Version.ToString(), progress, _updateDownloadCts.Token);
            var zipPath = Path.Combine(Path.GetTempPath(), "SoulScreen-update", asset.Name);

            _log.Info($"update staged at {stagingDir}; handing off and restarting");
            _quitRequested = true;
            UpdateService.LaunchInstallerAndExit(stagingDir, zipPath);
            Close();
        }
        catch (UpdateException ex)
        {
            _log.Warn($"the update could not be installed: {ex.Message}");
            ShowToast(ex.Message, "");
        }
        catch (OperationCanceledException)
        {
            // The window closed mid-download; nothing to report.
        }
        catch (Exception ex)
        {
            _log.Warn("the update could not be installed", ex);
            ShowToast("The update could not be installed - see the activity log", "");
        }
        finally
        {
            _updateBusy = false;
            RefreshUpdateSection();
        }
    }
}
