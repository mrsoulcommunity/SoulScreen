using System.Windows;
using System.Windows.Shapes;
using System.Windows.Threading;
using SoulScreen.App.Logic;
using SoulScreen.Core.Sources;

namespace SoulScreen.App;

/// <summary>
/// Who may mirror. With "Ask before an iPhone mirrors" on, a phone that has not been allowed
/// before waits on a question, its picture and sound held back; a blocked phone is disconnected
/// as soon as it starts, whether asking is on or not.
/// <para>
/// Nothing is shown while the question is up - no picture, no sound, no screenshot, no
/// recording - because the controls that could capture it only appear once a session is shown.
/// </para>
/// </summary>
public partial class MainWindow
{
    /// <summary>True while a phone is waiting to be allowed or declined.</summary>
    private bool _approvalPending;

    /// <summary>The phone waiting, when it said who it was.</summary>
    private SourceDeviceInfo? _approvalDevice;

    /// <summary>True from the moment a session is declined or blocked until it has gone.</summary>
    private bool _sessionRejected;

    /// <summary>Keeps the phone's sound silent while it has not been let in.</summary>
    private bool _holdAudio;

    private sealed record DeviceRuleView(string Name, string? Model, bool Blocked)
    {
        public string Detail => Model ?? "Model not reported";
    }

    private ConnectDecision DecideFor(SourceDeviceInfo? device)
    {
        if (_demo is not null) return ConnectDecision.Allow;
        if (device is not { } known) return _settings.AskBeforeMirroring ? ConnectDecision.Ask : ConnectDecision.Allow;
        return DeviceTrust.Decide(_settings.AskBeforeMirroring, _settings.AllowedDevices, _settings.BlockedDevices, known.Name, known.Model);
    }

    /// <summary>Silences a phone that is still negotiating if it will not be let straight in:
    /// its sound can start before its picture does.</summary>
    private void HoldAudioFor(SourceDeviceInfo? device)
    {
        _holdAudio = DecideFor(device) != ConnectDecision.Allow;
        ApplyAudioMute();
    }

    /// <summary>The pipeline is muted by the user's choice or by a pending question, whichever holds.</summary>
    private void ApplyAudioMute()
    {
        if (_audio is null) return;
        _audio.Muted = MuteButton.IsChecked == true || _holdAudio;
    }

    /// <summary>Decides a new session's fate. Returns true if it may be shown now.</summary>
    private bool AdmitSession(SourceDeviceInfo? device)
    {
        switch (DecideFor(device))
        {
            case ConnectDecision.Block:
                RejectSession(device, blocked: true);
                return false;
            case ConnectDecision.Ask:
                AskToMirror(device);
                return false;
            default:
                _holdAudio = false;
                ApplyAudioMute();
                return true;
        }
    }

    private void AskToMirror(SourceDeviceInfo? device)
    {
        _approvalPending = true;
        _approvalDevice = device;
        _holdAudio = true;
        ApplyAudioMute();

        // Everything that sits above the question goes, or it would open out of sight - with
        // focus already moved onto its hidden Decline button, so the next key pressed in the
        // settings turned the phone away.
        CloseWelcome();
        ClosePalette();
        CloseHelp();
        if (IsViewerOpen) CloseViewer();
        if (IsDoctorOpen) CloseDoctor();
        SettingsButton.IsChecked = false;
        CapturesButton.IsChecked = false;
        MoreButton.IsChecked = false;

        var name = device?.Name;
        ApprovalTitle.Text = name is null ? "An iPhone wants to mirror to this PC" : $"“{name}” wants to mirror to this PC";
        ApprovalDetail.Text = (device?.Model is { Length: > 0 } model ? $"{model}. " : string.Empty)
                              + "Its screen and sound stay hidden until you allow it.";
        ApprovalRemember.IsChecked = false;
        ApprovalRemember.Content = name is null ? "Always allow this iPhone" : $"Always allow “{name}”";
        ApprovalRemember.Visibility = device is null ? Visibility.Collapsed : Visibility.Visible;
        ApprovalBlock.Visibility = device is null ? Visibility.Collapsed : Visibility.Visible;

        IdlePanel.Visibility = Visibility.Collapsed;
        ApprovalPanel.Visibility = Visibility.Visible;
        FadeContentIn(ApprovalPanel);
        StatusDot.SetResourceReference(Shape.FillProperty, "Warning");
        UpdateRipple();

        // A question in a hidden window would never be answered, and the phone would sit waiting.
        if (!IsVisible || WindowState == WindowState.Minimized) ActivateFromAnotherInstance();
        // Focus on Decline: a key pressed for something else must never be what lets a phone in.
        Dispatcher.BeginInvoke(() =>
        {
            if (ApprovalPanel.Visibility == Visibility.Visible) ApprovalDecline.Focus();
        }, DispatcherPriority.Input);

        _log.Info($"asking whether {name ?? "an unnamed device"} may mirror");
        UpdateTray();
    }

    private void OnApprovalAllow(object sender, RoutedEventArgs e)
    {
        if (!_approvalPending) return;
        var device = _approvalDevice;

        if (device is { } known && ApprovalRemember.IsChecked == true)
        {
            DeviceTrust.Remove(_settings.BlockedDevices, known.Name, known.Model);
            DeviceTrust.Add(_settings.AllowedDevices, known.Name, known.Model);
            _settings.Save();
            RefreshDeviceRuleLists();
        }

        EndApproval();
        _holdAudio = false;
        ApplyAudioMute();

        if (ActiveSource?.State != MirrorSourceState.Streaming)
        {
            // The phone gave up while the question was on screen.
            SetIdleState("Waiting for your iPhone", "This PC is advertising itself on your network.", MirrorSourceState.Ready);
            return;
        }

        _log.Info($"{device?.Name ?? "the device"} was allowed to mirror");
        StartShowingSession(device);
    }

    private void OnApprovalDecline(object sender, RoutedEventArgs e)
    {
        if (!_approvalPending) return;
        RejectSession(_approvalDevice, blocked: false);
    }

    private void OnApprovalBlock(object sender, RoutedEventArgs e)
    {
        if (!_approvalPending || _approvalDevice is not { } device) return;
        DeviceTrust.Remove(_settings.AllowedDevices, device.Name, device.Model);
        DeviceTrust.Add(_settings.BlockedDevices, device.Name, device.Model);
        _settings.Save();
        RefreshDeviceRuleLists();
        RejectSession(device, blocked: true);
    }

    private void EndApproval()
    {
        if (!_approvalPending && ApprovalPanel.Visibility != Visibility.Visible) return;
        var hadFocus = ApprovalPanel.IsKeyboardFocusWithin;
        _approvalPending = false;
        _approvalDevice = null;
        ApprovalPanel.Visibility = Visibility.Collapsed;
        if (hadFocus) Focus();
    }

    /// <summary>Turns a session away: says so, and drops it.</summary>
    private void RejectSession(SourceDeviceInfo? device, bool blocked)
    {
        var name = device?.Name ?? "The iPhone";

        // Showing the idle screen resets the approval state, so the flags are set after it.
        SetIdleState(
            blocked ? $"{name} is blocked" : $"{name} was declined",
            blocked
                ? "A blocked iPhone is disconnected as soon as it starts to mirror. Settings, Privacy can unblock it."
                : "It has been disconnected, and can ask again.",
            MirrorSourceState.Ready);
        _sessionRejected = true;
        _holdAudio = true;
        ApplyAudioMute();

        if (_receiver?.Disconnect() != true)
            _log.Warn($"{name} was turned away but its session could not be dropped yet; it stays hidden until it ends");
        else
            _log.Info($"{name} was {(blocked ? "blocked" : "declined")}");

        ShowToast(blocked ? $"Blocked {name}" : $"Declined {name}", "");
        UpdateTray();
    }

    /// <summary>Clears every trace of a question when the idle screen comes back.</summary>
    private void ResetApprovalState()
    {
        EndApproval();
        _sessionRejected = false;
        _holdAudio = false;
        ApplyAudioMute();
    }

    // ------------------------------------------------------------------ settings

    private void RefreshDeviceRuleLists()
    {
        if (AllowedList is null) return;
        var allowed = _settings.AllowedDevices.Select(key => new DeviceRuleView(key.Name, key.Model, Blocked: false)).ToList();
        var blocked = _settings.BlockedDevices.Select(key => new DeviceRuleView(key.Name, key.Model, Blocked: true)).ToList();
        AllowedList.ItemsSource = allowed;
        BlockedList.ItemsSource = blocked;
        NoAllowedText.Visibility = allowed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoBlockedText.Visibility = blocked.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRemoveDeviceRule(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DeviceRuleView view) return;
        DeviceTrust.Remove(view.Blocked ? _settings.BlockedDevices : _settings.AllowedDevices, view.Name, view.Model);
        _settings.Save();
        RefreshDeviceRuleLists();
        ShowToast(view.Blocked ? $"{view.Name} is no longer blocked" : $"{view.Name} will be asked about next time", "");
    }

    private void OnAskBeforeMirroringChanged(object sender, RoutedEventArgs e)
    {
        if (_populatingSettings) return;
        _settings.AskBeforeMirroring = AskBeforeMirroringCheck.IsChecked == true;
        _settings.Save();
    }
}
