using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SoulScreen.AirPlay.FairPlay;
using SoulScreen.App.Logic;
using SoulScreen.Core.Logging;
using SoulScreen.Core.Time;
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
    /// <summary>
    /// Nesting depth of settings-form population. A depth rather than a plain flag because the
    /// helpers that fill in one card - <see cref="PopulateResolutionPresets"/>,
    /// <see cref="SyncPictureControls"/>, <see cref="PopulateAudioDevices"/>,
    /// <see cref="PopulateDisplayChoices"/>, <see cref="SyncCaptureBudget"/> - are called from
    /// the method that fills in all the rest. With a flag, the first helper to finish cleared
    /// it while <see cref="PopulateSettingsForm"/> was still assigning values, and every
    /// assignment after that point ran as though the user had just made it: opening the
    /// settings panel silently rewrote the picture-controls placement to "in a corner" (see
    /// <see cref="SetCornerPlacement"/>) and re-applied other switches along with it.
    /// </summary>
    private int _populatingSettingsDepth;

    /// <summary>True while the settings form is being filled in from the settings, so a
    /// control's change handler knows the change is not the user's doing.</summary>
    private bool _populatingSettings => _populatingSettingsDepth > 0;

    private void BeginPopulateSettings() => _populatingSettingsDepth++;

    private void EndPopulateSettings()
    {
        if (_populatingSettingsDepth > 0) _populatingSettingsDepth--;
    }

    private static readonly (string Label, int Width, int Height, int Refresh)[] ResolutionPresets =
    [
        ("1280 × 720 at 60 fps", 1280, 720, 60),
        ("1920 × 1080 at 60 fps", 1920, 1080, 60),
        ("1920 × 1080 at 30 fps", 1920, 1080, 30),
        ("2560 × 1440 at 60 fps", 2560, 1440, 60),
        ("3840 × 2160 at 30 fps", 3840, 2160, 30),
    ];

    private const string CustomPresetLabel = "Custom…";

    private sealed record RecentDeviceView(string Name, string? Model, string Summary, string LastSeen, bool AutoRecord)
    {
        public string AutoRecordAutomationName => $"Start recording automatically when {Name} connects";
    }

    // ------------------------------------------------------------------ opening

    private void OnSettingsToggled(object sender, RoutedEventArgs e)
    {
        if (SettingsButton.IsChecked == true)
        {
            if (_isMiniPlayer) ExitMiniPlayer();
            CloseHelp();
            CapturesButton.IsChecked = false;
            PopulateSettingsForm();
            RefreshIdentity();
            RefreshRecentList();

            // Every visit starts from the whole list, at the top.
            if (SettingsSearchBox.Text.Length > 0) SettingsSearchBox.Text = string.Empty;
            else ApplySettingsSearch();

            SettingsPanel.Visibility = Visibility.Visible;
            FadeContentIn(SettingsPanel);
            // Once the panel has a width to decide the layout by.
            Dispatcher.BeginInvoke(UpdateSettingsLayout, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else
        {
            var hadFocus = SettingsPanel.IsKeyboardFocusWithin;
            SettingsPanel.Visibility = Visibility.Collapsed;
            // Focus left in a hidden field would swallow the next shortcut.
            if (hadFocus) Focus();
        }
    }

    private void OnCloseSettings(object sender, RoutedEventArgs e) => SettingsButton.IsChecked = false;

    private void PopulateSettingsForm()
    {
        BeginPopulateSettings();
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
            SyncAccentSwatches();

            SyncPictureControls();
            LatencySmooth.IsChecked = _settings.Latency == LatencyMode.Smooth;
            LatencyBalanced.IsChecked = _settings.Latency == LatencyMode.Balanced;
            LatencyResponsive.IsChecked = _settings.Latency == LatencyMode.Responsive;
            FitWindowCheck.IsChecked = _settings.FitWindowToVideo;
            LockAspectCheck.IsChecked = _settings.LockAspectRatio;
            KeepAwakeCheck.IsChecked = _settings.KeepDisplayAwake;
            StatsCheck.IsChecked = _settings.ShowStats;
            PerformanceGraphCheck.IsChecked = _settings.ShowPerformanceGraph;
            PerformanceGraphRow.IsEnabled = _settings.ShowStats;
            RoundedCornersCheck.IsChecked = _settings.RoundedCorners;

            // Picture controls placement: a segmented picker for the placement itself, with
            // a second picker for which corner when "in a corner" is the chosen mode. Both
            // pickers have to agree about the current state, or the saved pick will not be
            // the one that shows when the settings panel is reopened.
            ControlBarPlacementFloating.IsChecked = _settings.ControlBarPlacement == ControlBarPlacement.Floating;
            ControlBarPlacementCorner.IsChecked = _settings.ControlBarPlacement == ControlBarPlacement.Corner;
            ControlBarPlacementFree.IsChecked = _settings.ControlBarPlacement == ControlBarPlacement.Free;
            ControlBarPlacementDocked.IsChecked = _settings.ControlBarPlacement == ControlBarPlacement.Docked;
            ControlBarCornerTL.IsChecked = _settings.ControlBarCorner == ControlBarCorner.TopLeft;
            ControlBarCornerTR.IsChecked = _settings.ControlBarCorner == ControlBarCorner.TopRight;
            ControlBarCornerBL.IsChecked = _settings.ControlBarCorner == ControlBarCorner.BottomLeft;
            ControlBarCornerBR.IsChecked = _settings.ControlBarCorner == ControlBarCorner.BottomRight;
            ControlBarCornerBC.IsChecked = _settings.ControlBarCorner == ControlBarCorner.BottomCentre;
            ControlBarCornerRow.Visibility = _settings.ControlBarPlacement == ControlBarPlacement.Corner
                ? Visibility.Visible : Visibility.Collapsed;
            SyncCaptureBudget();
            AnimationsSystem.IsChecked = _settings.Animations == MotionPreference.FollowWindows;
            AnimationsOn.IsChecked = _settings.Animations == MotionPreference.AlwaysOn;
            AnimationsOff.IsChecked = _settings.Animations == MotionPreference.AlwaysOff;
            PopulateDisplayChoices();
            RefreshRecurringRecordingsList();
            PopulateLockSettingsForm();
            PopulateWatermarkSettingsForm();

            PopulateAudioDevices();

            CaptureFolderBox.Text = _settings.CaptureDirectory;
            RecordAudioCheck.IsChecked = _settings.RecordAudio;
            ClipboardCheck.IsChecked = _settings.CopyScreenshotToClipboard;
            OcrSearchCheck.IsChecked = _settings.EnableOcrSearch;
            FormatPng.IsChecked = _settings.ScreenshotFormat == ScreenshotFormat.Png;
            FormatJpeg.IsChecked = _settings.ScreenshotFormat == ScreenshotFormat.Jpeg;

            BringToFrontCheck.IsChecked = _settings.BringToFrontOnConnect;
            FullscreenOnConnectCheck.IsChecked = _settings.FullscreenOnConnect;
            RecordOnConnectCheck.IsChecked = _settings.RecordOnConnect;
            LeaveFullscreenCheck.IsChecked = _settings.LeaveFullscreenOnDisconnect;

            AskBeforeMirroringCheck.IsChecked = _settings.AskBeforeMirroring;
            RefreshDeviceRuleLists();

            OnTopCheck.IsChecked = _settings.AlwaysOnTop;
            MinimizeToTrayCheck.IsChecked = _settings.MinimizeToTray;
            CloseToTrayCheck.IsChecked = _settings.CloseToTray;
            TrayNotifyCheck.IsChecked = _settings.TrayNotifications;
            GlobalHotkeysCheck.IsChecked = _settings.GlobalHotkeys;
            // The registry is the truth for this one: Settings > Apps > Startup can change it
            // behind our back, and the switch should show what will actually happen.
            StartupCheck.IsChecked = StartupRegistration.IsEnabled();
            _settings.LaunchAtStartup = StartupCheck.IsChecked == true;

            ReceiverApplyBar.Visibility = Visibility.Collapsed;
            ReceiverError.Visibility = Visibility.Collapsed;
            ClearFieldErrors();

            // Regional: timestamps. A three-way UseShamsi toggle (off / on / follow culture)
            // maps to a 2-state checkbox plus the resolved state for the preview row.
            UseShamsiCheck.IsChecked = _settings.Timestamps.UseShamsi == true;
            ShowGregorianCheck.IsChecked = _settings.Timestamps.ShowGregorianAlongside;
            FirstDayBox.SelectedIndex = _settings.Timestamps.FirstDay switch
            {
                FirstDayOfWeek.Sunday => 1,
                FirstDayOfWeek.Monday => 2,
                _ => 0,
            };
            UpdateTimestampPreview();
        }
        finally
        {
            EndPopulateSettings();
        }
    }

    private void UpdateTimestampPreview()
    {
        if (TimestampPreview is null) return;
        var now = DateTime.Now;
        var mode = TimestampFormatting.Resolve(
            _settings.Timestamps.UseShamsi,
            _settings.Timestamps.ShowGregorianAlongside,
            CultureInfo.CurrentCulture);
        TimestampPreview.Text = $"Today: {TimestampFormatting.FormatDateTime(now, mode)}";
    }

    /// <summary>Puts the fit, rotation and mirror controls in step with the settings. Called
    /// from the context menu and shortcuts too, since they change the same things.</summary>
    private void SyncPictureControls()
    {
        if (FitFit is null) return;
        BeginPopulateSettings();
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
            EndPopulateSettings();
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
        _settings.BringToFrontOnConnect = BringToFrontCheck.IsChecked == true;
        _settings.FullscreenOnConnect = FullscreenOnConnectCheck.IsChecked == true;
        _settings.RecordOnConnect = RecordOnConnectCheck.IsChecked == true;
        _settings.LeaveFullscreenOnDisconnect = LeaveFullscreenCheck.IsChecked == true;
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
        ThemeManager.Apply(theme, _settings.Accent, animate: true);
    }

    private void OnTimestampSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_populatingSettings) return;
        // The checkbox is a 2-state view of a 3-state setting: "off" is explicit false,
        // "on" is explicit true. Reverting to "follow the culture" is a separate UI affordance
        // we can add later; for now explicit-true-or-false covers the prompt's contract.
        _settings.Timestamps.UseShamsi = UseShamsiCheck.IsChecked == true;
        _settings.Timestamps.ShowGregorianAlongside = ShowGregorianCheck.IsChecked == true;
        _settings.Save();
        UpdateTimestampPreview();
        RefreshTimestamps();
    }

    private void OnFirstDayChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populatingSettings || FirstDayBox.SelectedIndex < 0) return;
        _settings.Timestamps.FirstDay = FirstDayBox.SelectedIndex switch
        {
            1 => FirstDayOfWeek.Sunday,
            2 => FirstDayOfWeek.Monday,
            _ => FirstDayOfWeek.Saturday,
        };
        _settings.Save();
        UpdateTimestampPreview();
    }

    /// <summary>Re-renders every surface that shows a timestamp: gallery, log, session
    /// summary preview. Called when the toggle changes so a user sees the effect at once.</summary>
    private void RefreshTimestamps()
    {
        RefreshCaptures();
        RefreshLogPanel();
        // The session summary is built and immediately shown; the next one picks up the new
        // mode. No need to re-paint the current one (it's already dismissed by then).
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

    private void OnRoundedCornersChanged(object sender, RoutedEventArgs e)
    {
        if (_populatingSettings) return;
        SetRoundedCorners(RoundedCornersCheck.IsChecked == true);
    }

    private void OnControlBarPlacementChanged(object sender, RoutedEventArgs e)
    {
        if (_populatingSettings) return;
        var placement = ControlBarPlacementFloating.IsChecked == true ? ControlBarPlacement.Floating
            : ControlBarPlacementCorner.IsChecked == true ? ControlBarPlacement.Corner
            : ControlBarPlacementFree.IsChecked == true ? ControlBarPlacement.Free
            : ControlBarPlacement.Docked;
        // The corner picker only matters in Corner mode; show or hide it to match.
        ControlBarCornerRow.Visibility = placement == ControlBarPlacement.Corner
            ? Visibility.Visible : Visibility.Collapsed;
        SetControlBarPlacement(placement);
    }

    private void OnControlBarCornerChanged(object sender, RoutedEventArgs e)
    {        if (_populatingSettings) return;
        var corner = ControlBarCornerTL.IsChecked == true ? ControlBarCorner.TopLeft
            : ControlBarCornerTR.IsChecked == true ? ControlBarCorner.TopRight
            : ControlBarCornerBL.IsChecked == true ? ControlBarCorner.BottomLeft
            : ControlBarCornerBR.IsChecked == true ? ControlBarCorner.BottomRight
            : ControlBarCorner.BottomCentre;
        SetCornerPlacement(corner);
    }

    private void SetRoundedCorners(bool rounded)
    {
        if (_settings.RoundedCorners == rounded) return;
        _settings.RoundedCorners = rounded;
        _settings.Save();
        SyncCheck(RoundedCornersCheck, rounded);
        UpdatePictureCorners();
    }

    private void SetAskBeforeMirroring(bool ask)
    {
        if (_settings.AskBeforeMirroring == ask) return;
        _settings.AskBeforeMirroring = ask;
        _settings.Save();
        SyncCheck(AskBeforeMirroringCheck, ask);
        ShowToast(ask ? "New iPhones will ask before mirroring" : "iPhones mirror without asking", "\uEA18");
    }

    private void SetGlobalHotkeys(bool enabled)
    {
        if (_settings.GlobalHotkeys == enabled) return;
        _settings.GlobalHotkeys = enabled;
        _settings.Save();
        SyncCheck(GlobalHotkeysCheck, enabled);
        ApplyGlobalHotkeys(announce: true);
    }

    /// <summary>Moves a switch to match a setting changed elsewhere, without it reading as an edit.</summary>
    private void SyncCheck(CheckBox box, bool value)
    {
        BeginPopulateSettings();
        try { box.IsChecked = value; }
        finally { EndPopulateSettings(); }
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

    private void OnScreenshotFormatChanged(object sender, RoutedEventArgs e)
    {
        if (_populatingSettings) return;
        var format = FormatJpeg.IsChecked == true ? ScreenshotFormat.Jpeg : ScreenshotFormat.Png;
        if (_settings.ScreenshotFormat == format) return;
        _settings.ScreenshotFormat = format;
        _settings.Save();
    }

    private void OnStatsSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_populatingSettings) return;
        StatsButton.IsChecked = StatsCheck.IsChecked == true;
    }

    private void OnPerformanceGraphChanged(object sender, RoutedEventArgs e)
    {
        if (_populatingSettings) return;
        SetShowPerformanceGraph(PerformanceGraphCheck.IsChecked == true);
    }

    private void OnCaptureBudgetChanged(object sender, RoutedEventArgs e)
    {
        // BudgetOff is marked checked in the XAML, so this fires while the window is still
        // being built - before Budget5 and its siblings exist. The real state is applied
        // from the settings when the panel opens.
        if (Budget5 is null || _populatingSettings) return;
        long budget = Budget5.IsChecked == true ? 5L * 1024 * 1024 * 1024
            : Budget20.IsChecked == true ? 20L * 1024 * 1024 * 1024
            : Budget50.IsChecked == true ? 50L * 1024 * 1024 * 1024
            : 0;
        if (_settings.CaptureBudgetBytes == budget) return;
        _settings.CaptureBudgetBytes = budget;
        _settings.Save();
        if (budget > 0)
        {
            ShowToast($"Keeping captures under {CaptureNaming.FormatSize(budget)}", "\uEDD5");
            EnforceCaptureBudget();
        }
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

    // ------------------------------------------------------------- capture budget

    /// <summary>Syncs the budget segment with the setting, including a hand-edited
    /// budget no preset matches - the segment then shows Off and a hint names the file.</summary>
    private void SyncCaptureBudget()
    {
        BudgetOff.IsChecked = _settings.CaptureBudgetBytes == 0;
        Budget5.IsChecked = _settings.CaptureBudgetBytes == 5L * 1024 * 1024 * 1024;
        Budget20.IsChecked = _settings.CaptureBudgetBytes == 20L * 1024 * 1024 * 1024;
        Budget50.IsChecked = _settings.CaptureBudgetBytes == 50L * 1024 * 1024 * 1024;
        BudgetCustomHint.Visibility = _settings.CaptureBudgetBytes > 0
            && Budget5.IsChecked != true && Budget20.IsChecked != true && Budget50.IsChecked != true
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- animations

    private void OnAnimationsChanged(object sender, RoutedEventArgs e)
    {
        if (AnimationsOn is null || _populatingSettings) return;
        var preference = AnimationsOn.IsChecked == true ? MotionPreference.AlwaysOn
            : AnimationsOff.IsChecked == true ? MotionPreference.AlwaysOff
            : MotionPreference.FollowWindows;
        if (_settings.Animations == preference) return;
        _settings.Animations = preference;
        _settings.Save();
        ShowToast(preference switch
        {
            MotionPreference.AlwaysOn => "Animations will always play",
            MotionPreference.AlwaysOff => "Animations are switched off",
            _ => "Animations follow Windows",
        }, "\uE785");
    }

    // ----------------------------------------------------------------- displays

    /// <summary>The display picker lists this PC's real displays; the setting stores the
    /// chosen entry's key. Rebuilt when settings open and when displays change.</summary>
    private void PopulateDisplayChoices()
    {
        var displays = DisplayService.ListChoices(this);
        var items = new List<string>
        {
            displays.Count < 2 ? "This display" : "Where it is now",
            "Primary display",
        };
        for (var i = 0; i < displays.Count; i++)
        {
            var d = displays[i];
            items.Add($"Display {i + 1}{(d.IsPrimary ? " (primary)" : "")} - {(int)d.WorkArea.Width}x{(int)d.WorkArea.Height}");
        }

        BeginPopulateSettings();
        try
        {
            DisplayBox.ItemsSource = items;
            var choice = _settings.TargetDisplay;
            var index = choice == Logic.DisplayLayout.PrimaryDisplay ? 1
                : int.TryParse(choice, out var n) && n >= 0 && n < displays.Count ? 2 + n
                : 0;
            DisplayBox.SelectedIndex = index;
            // One display means one place to be; the choice is kept for when there are two.
            DisplayRow.IsEnabled = displays.Count > 1;
        }
        finally
        {
            EndPopulateSettings();
        }
        _displayChoices = displays;
    }

    private IReadOnlyList<Logic.DisplayChoice> _displayChoices = [];

    private void OnDisplayChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populatingSettings || DisplayBox.SelectedIndex < 0) return;
        var index = DisplayBox.SelectedIndex;
        var choice = index == 1 ? Logic.DisplayLayout.PrimaryDisplay
            : index >= 2 ? (index - 2).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : Logic.DisplayLayout.CurrentDisplay;
        if (_settings.TargetDisplay == choice) return;
        _settings.TargetDisplay = choice;
        _settings.Save();
        if (index >= 2 && _displayChoices.Count > index - 2)
        {
            DisplayService.MoveTo(this, _displayChoices[index - 2]);
            ShowToast("Moved to the chosen display", "\uE7F4");
        }
        else if (index == 1)
        {
            MoveWindowToResolvedDisplay();
        }
    }

    /// <summary>Applies the display preference now, where it names a display to move to.</summary>
    private void MoveWindowToResolvedDisplay()
    {
        var resolved = Logic.DisplayLayout.Resolve(_settings.TargetDisplay, _displayChoices, DisplayService.BoundsOf(this));
        if (resolved is { } display) DisplayService.MoveTo(this, display);
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
        BeginPopulateSettings();
        try { PopulateAudioDevices(); }
        finally { EndPopulateSettings(); }
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
            BeginPopulateSettings();
            try
            {
                WidthBox.Text = preset.Width.ToString(CultureInfo.InvariantCulture);
                HeightBox.Text = preset.Height.ToString(CultureInfo.InvariantCulture);
                RefreshBox.Text = preset.Refresh.ToString(CultureInfo.InvariantCulture);
            }
            finally
            {
                EndPopulateSettings();
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

    // ------------------------------------------------------- export and import

    /// <summary>Writes the settings to a JSON file of the same shape settings.json uses.
    /// A backup to move between machines, or to keep before an experiment.</summary>
    private void OnExportSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export settings",
            Filter = "SoulScreen settings (*.json)|*.json",
            FileName = "SoulScreen-settings.json",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, System.Text.Json.JsonSerializer.Serialize(_settings,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
            ShowToast("Settings exported", "\uE898");
        }
        catch (Exception ex)
        {
            _log.Warn($"could not export settings to {dialog.FileName}", ex);
            ShowToast("The settings could not be exported", "\uE7BA");
        }
    }

    /// <summary>Reads settings back from an export. Preferences are taken; the things
    /// that name this machine - its history, its trust decisions, its window place - are
    /// kept, and a file that is not settings at all changes nothing.</summary>
    private async void OnImportSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import settings",
            Filter = "SoulScreen settings (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        AppSettings? imported;
        try
        {
            imported = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(dialog.FileName),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        }
        catch (Exception ex)
        {
            _log.Warn($"could not read settings from {dialog.FileName}", ex);
            imported = null;
        }

        if (imported is null)
        {
            ShowToast("That file is not a SoulScreen settings export", "\uE7BA");
            return;
        }

        var changed = SettingsTransfer.ApplyImported(_settings, imported);
        _settings.Normalise();
        _settings.Save();

        // Everything the panels apply piecewise is applied here, once.
        ThemeManager.Apply(_settings.Theme, _settings.Accent, animate: true);
        ApplySettingsToChrome();
        ApplyPictureSettings();
        ApplyGlobalHotkeys(announce: false);
        SyncMarkupChoices();
        ApplyMarkupTool();
        UpdatePictureCorners();
        UpdateTray();
        if (Log.MinimumLevel != (_settings.TraceProtocol ? LogLevel.Trace : LogLevel.Debug))
            Log.MinimumLevel = _settings.TraceProtocol ? LogLevel.Trace : LogLevel.Debug;
        PopulateSettingsForm();
        ShowToast(changed.Count == 0 ? "Nothing to import - the settings already match"
            : $"Imported {changed.Count} setting{(changed.Count == 1 ? "" : "s")}", "\uE898");

        // Receiver-affecting fields may have arrived: restart it if it was running.
        await RestartReceiverAsync();
    }

    // ---------------------------------------------------------------- history

    private void RefreshRecentList()
    {
        var devices = _settings.RecentDevices.OrderByDescending(d => d.LastSeenUtc).ToList();
        RecentList.ItemsSource = devices.Select(d => new RecentDeviceView(
            d.Name,
            d.Model,
            $"{(d.Model is null ? "" : d.Model + " · ")}{d.SessionCount} session{(d.SessionCount == 1 ? "" : "s")} · {FormatDuration(TimeSpan.FromSeconds(d.TotalSeconds))}",
            FormatRelative(d.LastSeenUtc),
            d.AutoRecord)).ToList();
        NoRecentText.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentSummary.Visibility = devices.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RecentFooter.Visibility = devices.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        // What the history adds up to, across every phone this PC has mirrored.
        RecentSummary.Text = devices.Count == 0 ? ""
            : $"{devices.Count} phone{(devices.Count == 1 ? "" : "s")} · " +
              $"{devices.Sum(d => d.SessionCount)} sessions · {FormatDuration(TimeSpan.FromSeconds(devices.Sum(d => d.TotalSeconds)))} mirrored in all";
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

    /// <summary>Flips one phone's own auto-record switch, independent of every other
    /// phone's and of the general "record on connect" setting.</summary>
    private void OnRecentDeviceAutoRecordChanged(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RecentDeviceView view) return;
        if (sender is not CheckBox box) return;

        var profile = Logic.DeviceProfiles.Resolve(_settings.RecentDevices, view.Name, view.Model) with
        {
            AutoRecord = box.IsChecked == true,
        };
        Logic.DeviceProfiles.Save(_settings.RecentDevices, view.Name, view.Model, profile);
        _settings.Save();
        ShowToast(profile.AutoRecord ? $"{view.Name} will always record" : $"{view.Name} no longer records automatically", "");
    }

    // ------------------------------------------------------------------ about

    private void OnOpenLogFolder(object sender, RoutedEventArgs e) =>
        OpenFolder(App.LogDirectory ?? Path.Combine(AppSettings.Directory, "logs"));

    private void OnOpenDataFolder(object sender, RoutedEventArgs e) => OpenFolder(AppSettings.Directory);

    private async void OnResetSettings(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Every setting goes back to its default, including the receiver name and the window placement. " +
            "The pairing identity, the history of phones and the allowed and blocked iPhones are kept.\n\nReset everything?",
            "Reset settings", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        var fresh = AppSettings.Defaults();
        fresh.RecentDevices = _settings.RecentDevices;
        // Who may mirror is a decision about people rather than a preference, and a welcome once
        // seen stays seen.
        fresh.AllowedDevices = _settings.AllowedDevices;
        fresh.BlockedDevices = _settings.BlockedDevices;
        fresh.HasSeenWelcome = true;
        fresh.Normalise();
        _settings = fresh;
        // The window is not the only holder: whatever reads App.Settings must see the reset too.
        App.Settings = fresh;
        _settings.Save();

        // Recurring rules are gone (Defaults() starts empty); the per-rule state below is
        // keyed by rule id, so it would otherwise point at ids that no longer exist.
        _recurringNextFireLocal.Clear();
        _recurringEditorsOpen.Clear();

        ThemeManager.Apply(_settings.Theme, _settings.Accent, animate: true);
        ApplySettingsToChrome();
        ApplyPictureSettings();
        ApplyGlobalHotkeys(announce: false);
        SyncMarkupChoices();
        ApplyMarkupTool();
        UpdatePictureCorners();
        PopulateSettingsForm();
        UpdateTray();
        ShowToast("Settings reset", "");

        await RestartReceiverAsync();
    }
}
