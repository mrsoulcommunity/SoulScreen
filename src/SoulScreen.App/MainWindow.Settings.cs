using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SoulScreen.AirPlay.FairPlay;
using SoulScreen.Core.Logging;
using SoulScreen.Media;

namespace SoulScreen.App;

/// <summary>
/// The settings panel. Most options take effect the moment they are changed, as they do in
/// System Settings; the handful the receiver itself depends on - its name, port, advertised
/// resolution and whether it takes audio - are gathered and applied together, because each
/// costs a restart of the receiver.
/// </summary>
public partial class MainWindow
{
    /// <summary>True while the form is being filled from settings, so the change handlers
    /// do not read their own writes as edits.</summary>
    private bool _populatingSettings;

    private static readonly (string Label, int Width, int Height, int Refresh)[] ResolutionPresets =
    [
        ("1280 × 720 at 60 fps", 1280, 720, 60),
        ("1920 × 1080 at 60 fps", 1920, 1080, 60),
        ("1920 × 1080 at 30 fps", 1920, 1080, 30),
        ("2560 × 1440 at 60 fps", 2560, 1440, 60),
        ("3840 × 2160 at 30 fps", 3840, 2160, 30),
    ];

    private const string CustomPresetLabel = "Custom…";

    private sealed record RecentDeviceView(string Name, string Summary, string LastSeen);

    // ------------------------------------------------------------------ opening

    private void OnSettingsToggled(object sender, RoutedEventArgs e)
    {
        if (SettingsButton.IsChecked == true)
        {
            CloseHelp();
            PopulateSettingsForm();
            RefreshIdentity();
            RefreshRecentList();
            SettingsPanel.Visibility = Visibility.Visible;
            FadeContentIn(SettingsPanel);
        }
        else
        {
            SettingsPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void OnCloseSettings(object sender, RoutedEventArgs e) => SettingsButton.IsChecked = false;

    private void PopulateSettingsForm()
    {
        _populatingSettings = true;
        try
        {
            NameBox.Text = _settings.DeviceName;
            PortBox.Text = _settings.Port.ToString(CultureInfo.InvariantCulture);
            WidthBox.Text = _settings.DisplayWidth.ToString(CultureInfo.InvariantCulture);
            HeightBox.Text = _settings.DisplayHeight.ToString(CultureInfo.InvariantCulture);
            RefreshBox.Text = _settings.DisplayRefreshRate.ToString(CultureInfo.InvariantCulture);
            PopulateResolutionPresets();
            AudioCheck.IsChecked = _settings.EnableAudio;
            AutoStartCheck.IsChecked = _settings.StartReceiverOnLaunch;
            TraceCheck.IsChecked = _settings.TraceProtocol;

            ThemeSystem.IsChecked = _settings.Theme == AppTheme.System;
            ThemeDark.IsChecked = _settings.Theme == AppTheme.Dark;
            ThemeLight.IsChecked = _settings.Theme == AppTheme.Light;

            SyncPictureControls();
            LatencySmooth.IsChecked = _settings.Latency == LatencyMode.Smooth;
            LatencyBalanced.IsChecked = _settings.Latency == LatencyMode.Balanced;
            LatencyResponsive.IsChecked = _settings.Latency == LatencyMode.Responsive;
            FitWindowCheck.IsChecked = _settings.FitWindowToVideo;
            LockAspectCheck.IsChecked = _settings.LockAspectRatio;
            KeepAwakeCheck.IsChecked = _settings.KeepDisplayAwake;
            StatsCheck.IsChecked = _settings.ShowStats;

            PopulateAudioDevices();

            CaptureFolderBox.Text = _settings.CaptureDirectory;
            RecordAudioCheck.IsChecked = _settings.RecordAudio;
            ClipboardCheck.IsChecked = _settings.CopyScreenshotToClipboard;

            OnTopCheck.IsChecked = _settings.AlwaysOnTop;
            MinimizeToTrayCheck.IsChecked = _settings.MinimizeToTray;
            CloseToTrayCheck.IsChecked = _settings.CloseToTray;
            TrayNotifyCheck.IsChecked = _settings.TrayNotifications;
            // The registry is the truth for this one: Settings > Apps > Startup can change it
            // behind our back, and the switch should show what will actually happen.
            StartupCheck.IsChecked = StartupRegistration.IsEnabled();
            _settings.LaunchAtStartup = StartupCheck.IsChecked == true;

            ReceiverApplyBar.Visibility = Visibility.Collapsed;
            ReceiverError.Visibility = Visibility.Collapsed;
            ClearFieldErrors();
        }
        finally
        {
            _populatingSettings = false;
        }
    }

    /// <summary>Puts the fit, rotation and mirror controls in step with the settings. Called
    /// from the context menu and shortcuts too, since they change the same things.</summary>
    private void SyncPictureControls()
    {
        if (FitFit is null) return;
        _populatingSettings = true;
        try
        {
            FitFit.IsChecked = _settings.VideoFit == VideoFit.Fit;
            FitFill.IsChecked = _settings.VideoFit == VideoFit.Fill;
            FitStretch.IsChecked = _settings.VideoFit == VideoFit.Stretch;
            FitActual.IsChecked = _settings.VideoFit == VideoFit.Actual;
            RotationBox.SelectedIndex = _settings.Rotation / 90;
            MirrorCheck.IsChecked = _settings.MirrorHorizontally;
        }
        finally
        {
            _populatingSettings = false;
        }
    }

    private void RefreshIdentity()
    {
        var lines = new List<string>();
        if (_receiver is { } receiver)
        {
            lines.Add($"device id   {receiver.Identity.DeviceId}");
            lines.Add($"public key  {receiver.Identity.Ed25519PublicKeyHex[..24]}…");
            lines.Add($"port        {_settings.Port}");
        }
        else
        {
            lines.Add("The receiver is not running.");
        }
        lines.Add($"FairPlay    {(NativeFairPlay.IsAvailable ? "available" : "not installed")}");
        lines.Add($"FFmpeg      {FFmpegRuntime.Version ?? "not found"}");
        IdentityText.Text = string.Join("\n", lines);

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var shown = version is null ? "" : $"{version.Major}.{version.Minor}.{version.Build}";
        AboutText.Text = $"SoulScreen {shown} · .NET {Environment.Version.ToString(2)} · " +
                         $"Settings and pairing keys live in {AppSettings.Directory}";
    }

    // ------------------------------------------------------------ instant apply

    /// <summary>Options that take effect the moment they change, and need no restart.</summary>
    private void OnInstantSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_populatingSettings) return;

        _settings.StartReceiverOnLaunch = AutoStartCheck.IsChecked == true;
        _settings.TraceProtocol = TraceCheck.IsChecked == true;
        _settings.FitWindowToVideo = FitWindowCheck.IsChecked == true;
        _settings.LockAspectRatio = LockAspectCheck.IsChecked == true;
        _settings.KeepDisplayAwake = KeepAwakeCheck.IsChecked == true;
        _settings.RecordAudio = RecordAudioCheck.IsChecked == true;
        _settings.CopyScreenshotToClipboard = ClipboardCheck.IsChecked == true;
        _settings.MinimizeToTray = MinimizeToTrayCheck.IsChecked == true;
        _settings.CloseToTray = CloseToTrayCheck.IsChecked == true;
        _settings.TrayNotifications = TrayNotifyCheck.IsChecked == true;
        _settings.Save();

        // Tracing takes effect straight away; only something the receiver itself depends
        // on is worth the few seconds a restart costs.
        Log.MinimumLevel = _settings.TraceProtocol ? LogLevel.Trace : LogLevel.Debug;
        if (_pipeline is not null) _pipeline.RecordAudio = _settings.RecordAudio && _audio is not null;
        if (VideoHost.Visibility == Visibility.Visible)
        {
            if (_settings.KeepDisplayAwake) DisplaySleep.Hold();
            else DisplaySleep.Release();
        }
        UpdateAspectLock();
        UpdateTray();
    }

    private void OnThemeChanged(object sender, RoutedEventArgs e)
    {
        if (_populatingSettings) return;
        var theme = ThemeDark.IsChecked == true ? AppTheme.Dark
            : ThemeLight.IsChecked == true ? AppTheme.Light
            : AppTheme.System;
        if (_settings.Theme == theme) return;
        _settings.Theme = theme;
        _settings.Save();
        ThemeManager.Apply(theme, animate: true);
    }

    private void OnFitChanged(object sender, RoutedEventArgs e)
    {
        if (_populatingSettings) return;
        SetVideoFit(FitFill.IsChecked == true ? VideoFit.Fill
            : FitStretch.IsChecked == true ? VideoFit.Stretch
            : FitActual.IsChecked == true ? VideoFit.Actual
            : VideoFit.Fit);
    }

    private void OnRotationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populatingSettings || RotationBox.SelectedIndex < 0) return;
        SetRotation(RotationBox.SelectedIndex * 90);
    }

    private void OnMirrorChanged(object sender, RoutedEventArgs e)
    {
        if (_populatingSettings) return;
        SetMirror(MirrorCheck.IsChecked == true);
    }

    private void OnLatencyChanged(object sender, RoutedEventArgs e)
    {
        if (_populatingSettings) return;
        var mode = LatencySmooth.IsChecked == true ? LatencyMode.Smooth
            : LatencyResponsive.IsChecked == true ? LatencyMode.Responsive
            : LatencyMode.Balanced;
        if (_settings.Latency == mode) return;
        _settings.Latency = mode;
        _settings.Save();
        ApplyPictureSettings();
        var delay = AppSettings.PresentationDelayFor(mode);
        ShowToast($"Holding {delay.TotalMilliseconds:0} ms of picture", "");
    }

    private void OnStatsSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_populatingSettings) return;
        StatsButton.IsChecked = StatsCheck.IsChecked == true;
    }

    private void OnOnTopSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_populatingSettings) return;
        PinButton.IsChecked = OnTopCheck.IsChecked == true;
    }

    private void OnStartupSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_populatingSettings) return;
        var wanted = StartupCheck.IsChecked == true;
        if (!StartupRegistration.SetEnabled(wanted))
        {
            StartupCheck.IsChecked = !wanted;
            ShowToast("Windows refused the startup change", "");
            return;
        }
        _settings.LaunchAtStartup = wanted;
        _settings.Save();
    }

    // ----------------------------------------------------------------- audio

    private void PopulateAudioDevices()
    {
        var devices = AudioDevices.ListOutputs();
        AudioDeviceBox.ItemsSource = devices.Select(d => d.Name).ToList();
        var index = devices.ToList().FindIndex(d => string.Equals(d.Id, _settings.AudioOutputDeviceId, StringComparison.Ordinal));
        AudioDeviceBox.Tag = devices;
        AudioDeviceBox.SelectedIndex = index >= 0 ? index : 0;
    }

    private void OnRefreshAudioDevices(object sender, RoutedEventArgs e)
    {
        _populatingSettings = true;
        try { PopulateAudioDevices(); }
        finally { _populatingSettings = false; }
    }

    private void OnAudioDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populatingSettings || AudioDeviceBox.Tag is not IReadOnlyList<AudioOutputDevice> devices) return;
        var index = AudioDeviceBox.SelectedIndex;
        if (index < 0 || index >= devices.Count) return;

        var id = devices[index].Id;
        if (string.Equals(_settings.AudioOutputDeviceId, id, StringComparison.Ordinal)) return;
        _settings.AudioOutputDeviceId = id;
        _settings.Save();
        if (_audio is not null) _audio.OutputDeviceId = id;
        ShowToast($"Audio on {devices[index].Name}", "");
    }

    // ---------------------------------------------------------------- capture

    private void OnChooseCaptureFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Where screenshots and recordings are saved",
            InitialDirectory = Directory.Exists(_settings.CaptureDirectory) ? _settings.CaptureDirectory : null,
        };
        if (dialog.ShowDialog(this) != true) return;

        _settings.CaptureDirectory = dialog.FolderName;
        _settings.Save();
        CaptureFolderBox.Text = _settings.CaptureDirectory;
    }

    // --------------------------------------------------------------- receiver

    private void PopulateResolutionPresets()
    {
        var items = ResolutionPresets.Select(p => p.Label).ToList();
        items.Add(CustomPresetLabel);
        ResolutionPresetBox.ItemsSource = items;

        var match = Array.FindIndex(ResolutionPresets, p =>
            p.Width == _settings.DisplayWidth && p.Height == _settings.DisplayHeight && p.Refresh == _settings.DisplayRefreshRate);
        ResolutionPresetBox.SelectedIndex = match >= 0 ? match : items.Count - 1;
        CustomResolutionRow.Visibility = match >= 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnResolutionPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populatingSettings) return;
        var index = ResolutionPresetBox.SelectedIndex;
        if (index < 0) return;

        if (index < ResolutionPresets.Length)
        {
            var preset = ResolutionPresets[index];
            _populatingSettings = true;
            try
            {
                WidthBox.Text = preset.Width.ToString(CultureInfo.InvariantCulture);
                HeightBox.Text = preset.Height.ToString(CultureInfo.InvariantCulture);
                RefreshBox.Text = preset.Refresh.ToString(CultureInfo.InvariantCulture);
            }
            finally
            {
                _populatingSettings = false;
            }
            CustomResolutionRow.Visibility = Visibility.Collapsed;
        }
        else
        {
            CustomResolutionRow.Visibility = Visibility.Visible;
        }

        OnReceiverFieldChanged(sender, e);
    }

    /// <summary>A receiver field was edited: show the apply bar if anything now differs.</summary>
    private void OnReceiverFieldChanged(object sender, RoutedEventArgs e)
    {
        if (_populatingSettings) return;
        ReceiverApplyBar.Visibility = IsReceiverDirty() ? Visibility.Visible : Visibility.Collapsed;
        ReceiverError.Visibility = Visibility.Collapsed;
        ClearFieldErrors();
    }

    private bool IsReceiverDirty()
    {
        if (NameBox.Text.Trim() != _settings.DeviceName) return true;
        if (PortBox.Text.Trim() != _settings.Port.ToString(CultureInfo.InvariantCulture)) return true;
        if (WidthBox.Text.Trim() != _settings.DisplayWidth.ToString(CultureInfo.InvariantCulture)) return true;
        if (HeightBox.Text.Trim() != _settings.DisplayHeight.ToString(CultureInfo.InvariantCulture)) return true;
        if (RefreshBox.Text.Trim() != _settings.DisplayRefreshRate.ToString(CultureInfo.InvariantCulture)) return true;
        if ((AudioCheck.IsChecked == true) != _settings.EnableAudio) return true;
        return false;
    }

    private void OnDiscardReceiverChanges(object sender, RoutedEventArgs e) => PopulateSettingsForm();

    private async void OnApplySettings(object sender, RoutedEventArgs e)
    {
        if (!TryBeginReceiverWork()) return;

        try
        {
            await ApplyReceiverSettingsAsync();
        }
        finally
        {
            EndReceiverWork();
        }
    }

    private async Task ApplyReceiverSettingsAsync()
    {
        ClearFieldErrors();

        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowReceiverError("The receiver needs a name.", NameBox);
            return;
        }

        if (!ushort.TryParse(PortBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port == 0)
        {
            ShowReceiverError("The control port must be a number between 1 and 65535.", PortBox);
            return;
        }

        // Every number is parsed and ranged before any is saved. A field that failed used
        // to be silently ignored while the rest applied, which read as a typo that did
        // nothing.
        if (!int.TryParse(WidthBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
            || width is < AppSettings.MinDisplayWidth or > AppSettings.MaxDisplayWidth)
        {
            ShowReceiverError($"Width must be a whole number from {AppSettings.MinDisplayWidth} to {AppSettings.MaxDisplayWidth}.", WidthBox);
            return;
        }

        if (!int.TryParse(HeightBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)
            || height is < AppSettings.MinDisplayHeight or > AppSettings.MaxDisplayHeight)
        {
            ShowReceiverError($"Height must be a whole number from {AppSettings.MinDisplayHeight} to {AppSettings.MaxDisplayHeight}.", HeightBox);
            return;
        }

        if (!int.TryParse(RefreshBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var refresh)
            || refresh is < AppSettings.MinRefreshRate or > AppSettings.MaxRefreshRate)
        {
            ShowReceiverError($"Refresh rate must be a whole number from {AppSettings.MinRefreshRate} to {AppSettings.MaxRefreshRate}.", RefreshBox);
            return;
        }

        _settings.DeviceName = name;
        _settings.Port = port;
        _settings.DisplayWidth = width;
        _settings.DisplayHeight = height;
        _settings.DisplayRefreshRate = refresh;
        _settings.EnableAudio = AudioCheck.IsChecked == true;
        _settings.Save();

        ApplySettingsToChrome();
        ReceiverApplyBar.Visibility = Visibility.Collapsed;
        SettingsButton.IsChecked = false;

        if (_receiver is not null)
        {
            await StartReceiverAsync();
            ShowToast("Receiver restarted", "");
        }
        else
        {
            RefreshWarnings();
        }
    }

    private void ShowReceiverError(string message, TextBox field)
    {
        ReceiverError.Text = message;
        ReceiverError.Visibility = Visibility.Visible;
        ReceiverApplyBar.Visibility = Visibility.Visible;
        field.Style = (Style)FindResource("InvalidTextBox");
        field.Focus();
        field.SelectAll();
    }

    private void ClearFieldErrors()
    {
        foreach (var box in new[] { NameBox, PortBox, WidthBox, HeightBox, RefreshBox })
            box.ClearValue(StyleProperty);
    }

    // ---------------------------------------------------------------- history

    private void RefreshRecentList()
    {
        var devices = _settings.RecentDevices.OrderByDescending(d => d.LastSeenUtc).ToList();
        RecentList.ItemsSource = devices.Select(d => new RecentDeviceView(
            d.Name,
            $"{(d.Model is null ? "" : d.Model + " · ")}{d.SessionCount} session{(d.SessionCount == 1 ? "" : "s")} · {FormatDuration(TimeSpan.FromSeconds(d.TotalSeconds))}",
            FormatRelative(d.LastSeenUtc))).ToList();
        NoRecentText.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentFooter.Visibility = devices.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string FormatRelative(DateTime utc)
    {
        var age = DateTime.UtcNow - utc;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
        if (age < TimeSpan.FromHours(24)) return $"{(int)age.TotalHours} h ago";
        if (age < TimeSpan.FromDays(2)) return "yesterday";
        if (age < TimeSpan.FromDays(7)) return $"{(int)age.TotalDays} days ago";
        return utc.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }

    private void OnClearHistory(object sender, RoutedEventArgs e)
    {
        _settings.RecentDevices.Clear();
        _settings.Save();
        RefreshRecentList();
    }

    // ------------------------------------------------------------------ about

    private void OnOpenLogFolder(object sender, RoutedEventArgs e) =>
        OpenFolder(App.LogDirectory ?? Path.Combine(AppSettings.Directory, "logs"));

    private void OnOpenDataFolder(object sender, RoutedEventArgs e) => OpenFolder(AppSettings.Directory);

    private async void OnResetSettings(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Every setting goes back to its default, including the receiver name and the window placement. " +
            "The pairing identity and the history of phones are kept.\n\nReset everything?",
            "Reset settings", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        var fresh = AppSettings.Defaults();
        fresh.RecentDevices = _settings.RecentDevices;
        fresh.Normalise();
        _settings = fresh;
        // The window is not the only holder: whatever reads App.Settings must see the reset too.
        App.Settings = fresh;
        _settings.Save();

        ThemeManager.Apply(_settings.Theme, animate: true);
        ApplySettingsToChrome();
        ApplyPictureSettings();
        PopulateSettingsForm();
        UpdateTray();
        ShowToast("Settings reset", "");

        await RestartReceiverAsync();
    }
}
