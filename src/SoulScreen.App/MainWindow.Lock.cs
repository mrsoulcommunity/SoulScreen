using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SoulScreen.App.Logic;

namespace SoulScreen.App;

/// <summary>
/// The PIN lock: a screen that blocks the whole window - settings, captures, the mirrored
/// picture itself - until the right PIN is typed. Separate from Windows' own lock screen and
/// from "ask before an iPhone mirrors" (<see cref="MainWindow"/> Approval partial): this is
/// about who may use SoulScreen on an already-unlocked PC, not about which phone may mirror.
/// <para>
/// A phone that is mid-session keeps mirroring, recording and being written to disk while
/// locked - locking hides and disables the controls, it does not tear down what is running.
/// </para>
/// </summary>
public partial class MainWindow
{
    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint DwTime;
    }

    /// <summary>Checked every metrics tick against the idle-lock setting.</summary>
    private bool IsLocked => LockOverlay.Visibility == Visibility.Visible;

    private void InitialiseLock()
    {
        if (_settings.Lock is { Enabled: true, LockOnLaunch: true }) Lock();
    }

    // ------------------------------------------------------------------ locking

    /// <summary>Locks the window now. A no-op when the lock is not configured - "Lock now"
    /// only appears once it is, but a stray call (idle timer racing a setting change) must
    /// still be harmless.</summary>
    private void Lock()
    {
        if (!_settings.Lock.Enabled || IsLocked) return;

        // Everything that could show something private goes: the palette can search
        // command titles that name a phone, the welcome sheet and captures both show the
        // picture, and a mini player has no room for the lock screen's own layout.
        if (_isMiniPlayer) ExitMiniPlayer();
        ClosePalette();
        CloseWelcome();
        CloseHelp();
        if (IsViewerOpen) CloseViewer();
        if (IsDoctorOpen) CloseDoctor();
        SettingsButton.IsChecked = false;
        CapturesButton.IsChecked = false;
        MoreButton.IsChecked = false;

        LockErrorText.Visibility = Visibility.Collapsed;
        LockSubtitle.Text = "Enter your PIN to continue";
        LockPinBox.Password = string.Empty;
        LockOverlay.Visibility = Visibility.Visible;
        UpdateTaskbar();
        UpdateTray();

        Dispatcher.BeginInvoke(() =>
        {
            if (IsLocked) LockPinBox.Focus();
        }, DispatcherPriority.Input);

        _log.Info("SoulScreen locked");
    }

    private void Unlock()
    {
        if (!IsLocked) return;
        LockOverlay.Visibility = Visibility.Collapsed;
        LockPinBox.Password = string.Empty;
        UpdateTaskbar();
        UpdateTray();
        _log.Info("SoulScreen unlocked");
        Focus();
    }

    private void OnLockNowClicked(object sender, RoutedEventArgs e) => Lock();

    private void OnLockPinKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        TryUnlockWithEnteredPin();
        e.Handled = true;
    }

    private void OnLockUnlockClicked(object sender, RoutedEventArgs e) => TryUnlockWithEnteredPin();

    private void TryUnlockWithEnteredPin()
    {
        var pin = LockPinBox.Password;
        var settings = _settings.Lock;

        // A lockout in force: refuse without even checking the PIN, so the delay cannot be
        // bypassed by a guess that happens to be right - the wait itself is the deterrent.
        if (settings.LastFailureUtc is { } lastFailure)
        {
            var delay = LockoutPolicy.DelayAfter(settings.ConsecutiveFailures);
            if (delay > TimeSpan.Zero && !LockoutPolicy.HasElapsed(lastFailure, delay, DateTime.UtcNow))
            {
                var remaining = LockoutPolicy.Remaining(lastFailure, delay, DateTime.UtcNow);
                ShowLockError($"Too many wrong PINs - try again in {FormatLockoutRemaining(remaining)}.");
                return;
            }
        }

        var saltBase64 = SecretProtection.Unprotect(settings.ProtectedSaltBase64);
        var hashBase64 = SecretProtection.Unprotect(settings.ProtectedHashBase64);
        if (saltBase64 is null || hashBase64 is null)
        {
            // DPAPI could not open what is on disk - a different Windows account, or a
            // settings file copied from another machine. There is no PIN that unlocks this;
            // failing loudly beats a silent, unbreakable lock.
            ShowLockError("This PIN was set up under a different Windows account and cannot be checked here. " +
                          "Turn the lock off by editing settings.json's Lock section, then set a new PIN.");
            return;
        }

        if (AppLock.Verify(pin, saltBase64, hashBase64, settings.Iterations))
        {
            settings.ConsecutiveFailures = 0;
            settings.LastFailureUtc = null;
            _settings.Save();
            Unlock();
            return;
        }

        settings.ConsecutiveFailures++;
        settings.LastFailureUtc = DateTime.UtcNow;
        _settings.Save();
        LockPinBox.Password = string.Empty;

        var nextDelay = LockoutPolicy.DelayAfter(settings.ConsecutiveFailures);
        ShowLockError(nextDelay > TimeSpan.Zero
            ? $"Wrong PIN. Try again in {FormatLockoutRemaining(nextDelay)}."
            : "Wrong PIN.");
    }

    private void ShowLockError(string message)
    {
        LockErrorText.Text = message;
        LockErrorText.Visibility = Visibility.Visible;
    }

    private static string FormatLockoutRemaining(TimeSpan remaining) =>
        remaining.TotalSeconds < 60
            ? $"{Math.Ceiling(remaining.TotalSeconds):0}s"
            : $"{Math.Ceiling(remaining.TotalMinutes):0} min";

    // ------------------------------------------------------------------ idle

    /// <summary>Runs on the metrics tick: cheaper than a dedicated timer, and the feature
    /// does not need finer than half-second resolution against a multi-minute idle setting.</summary>
    private void CheckIdleLock()
    {
        if (!_settings.Lock.Enabled || _settings.Lock.AutoLockAfterMinutesIdle <= 0 || IsLocked) return;

        // Idle is measured against the whole PC's input, not just this window's: watching a
        // mirrored phone with the mouse resting elsewhere must not be read as "away". A
        // session actually mirroring or recording also holds the lock off - the point of the
        // idle timer is an unattended desk, not a phone left playing back to itself.
        if (VideoHost.Visibility == Visibility.Visible) return;

        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return;

        var idleMilliseconds = unchecked((uint)Environment.TickCount - info.DwTime);
        if (idleMilliseconds >= _settings.Lock.AutoLockAfterMinutesIdle * 60_000L) Lock();
    }

    // ------------------------------------------------------------------ settings UI

    private void PopulateLockSettingsForm()
    {
        var settings = _settings.Lock;
        LockEnabledCheck.IsChecked = settings.Enabled;
        LockConfiguredPanel.Visibility = settings.Enabled ? Visibility.Visible : Visibility.Collapsed;
        LockOnLaunchRow.Visibility = settings.Enabled ? Visibility.Visible : Visibility.Collapsed;
        LockOnMinimizeRow.Visibility = settings.Enabled ? Visibility.Visible : Visibility.Collapsed;
        LockIdleRow.Visibility = settings.Enabled ? Visibility.Visible : Visibility.Collapsed;
        LockNowButton.IsEnabled = settings.Enabled;
        LockOnLaunchCheck.IsChecked = settings.LockOnLaunch;
        LockOnMinimizeCheck.IsChecked = settings.LockOnMinimizeToTray;

        LockIdleOff.IsChecked = settings.AutoLockAfterMinutesIdle == 0;
        LockIdle5.IsChecked = settings.AutoLockAfterMinutesIdle == 5;
        LockIdle15.IsChecked = settings.AutoLockAfterMinutesIdle == 15;
        LockIdle30.IsChecked = settings.AutoLockAfterMinutesIdle == 30;
        // A hand-edited value that is none of the presets shows as Off rather than nothing
        // selected - the underlying number is untouched until the row is used again.
        if (settings.AutoLockAfterMinutesIdle is not (0 or 5 or 15 or 30)) LockIdleOff.IsChecked = true;

        LockSetupPanel.Visibility = Visibility.Collapsed;
        LockSetupPinBox.Password = string.Empty;
        LockSetupConfirmBox.Password = string.Empty;
        LockSetupError.Visibility = Visibility.Collapsed;
    }

    private void OnLockEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_populatingSettings) return;

        if (LockEnabledCheck.IsChecked == true)
        {
            // Turning the switch on does not itself enable the lock - a PIN has to be set
            // first, or "Enabled" would mean "locked out with no PIN that opens it".
            LockEnabledCheck.IsChecked = _settings.Lock.Enabled;
            OpenLockSetup();
            return;
        }

        _settings.Lock.Enabled = false;
        _settings.Save();
        PopulateLockSettingsForm();
        ShowToast("PIN lock turned off", "");
    }

    private void OnChangeLockPin(object sender, RoutedEventArgs e) => OpenLockSetup();

    private void OpenLockSetup()
    {
        LockSetupPanel.Visibility = Visibility.Visible;
        LockSetupPinBox.Password = string.Empty;
        LockSetupConfirmBox.Password = string.Empty;
        LockSetupError.Visibility = Visibility.Collapsed;
        Dispatcher.BeginInvoke(() => LockSetupPinBox.Focus(), DispatcherPriority.Input);
    }

    private void OnCancelLockSetup(object sender, RoutedEventArgs e)
    {
        LockSetupPanel.Visibility = Visibility.Collapsed;
        // Cancelling setup when the lock was never on leaves the switch showing its real,
        // still-off state rather than the "on" a click briefly optimistically drew.
        LockEnabledCheck.IsChecked = _settings.Lock.Enabled;
    }

    private void OnLockSetupConfirmKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OnSaveLockPin(sender, e);
        e.Handled = true;
    }

    private void OnSaveLockPin(object sender, RoutedEventArgs e)
    {
        var pin = LockSetupPinBox.Password;
        var confirm = LockSetupConfirmBox.Password;

        if (!AppLock.IsValidPin(pin))
        {
            ShowLockSetupError($"A PIN is {AppLock.MinPinLength} to {AppLock.MaxPinLength} digits, numbers only.");
            return;
        }
        if (pin != confirm)
        {
            ShowLockSetupError("The two PINs do not match.");
            return;
        }

        var (saltBase64, hashBase64) = AppLock.Hash(pin);
        _settings.Lock.ProtectedSaltBase64 = SecretProtection.Protect(saltBase64);
        _settings.Lock.ProtectedHashBase64 = SecretProtection.Protect(hashBase64);
        _settings.Lock.Iterations = AppLock.DefaultIterations;
        _settings.Lock.Enabled = true;
        _settings.Lock.ConsecutiveFailures = 0;
        _settings.Lock.LastFailureUtc = null;
        _settings.Save();

        LockSetupPanel.Visibility = Visibility.Collapsed;
        PopulateLockSettingsForm();
        ShowToast("PIN saved", "\uE73E");
    }

    private void ShowLockSetupError(string message)
    {
        LockSetupError.Text = message;
        LockSetupError.Visibility = Visibility.Visible;
    }

    private void OnLockOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_populatingSettings) return;
        _settings.Lock.LockOnLaunch = LockOnLaunchCheck.IsChecked == true;
        _settings.Lock.LockOnMinimizeToTray = LockOnMinimizeCheck.IsChecked == true;
        _settings.Save();
    }

    private void OnLockIdleChanged(object sender, RoutedEventArgs e)
    {
        if (_populatingSettings) return;
        _settings.Lock.AutoLockAfterMinutesIdle =
            LockIdle5.IsChecked == true ? 5
            : LockIdle15.IsChecked == true ? 15
            : LockIdle30.IsChecked == true ? 30
            : 0;
        _settings.Save();
    }
}
