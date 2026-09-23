using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SoulScreen.App.Logic;
using SoulScreen.Core.Sources;

namespace SoulScreen.App;

/// <summary>
/// A phone that drops and comes straight back. iOS tears a mirroring session down and sets a
/// new one up when the phone sleeps, changes network, or loses the receiver for a moment, and
/// to the person watching that is a flicker, not the end of the session - the picture, the
/// clock and the recording are all still theirs.
/// <para>
/// So a session whose phone has just gone away is held open for a minute rather than closed:
/// the last frame stays on screen, the recording keeps running, and the same phone coming back
/// resumes it. A different phone, a longer wait, or the receiver being stopped ends it exactly
/// as it did before. Which phone counts as the same one, and for how long, is
/// <see cref="ReconnectPolicy"/>; this is the window that carries it out.
/// </para>
/// </summary>
public partial class MainWindow
{
    /// <summary>How long the reconnected badge stays up once the phone is back.</summary>
    private static readonly TimeSpan ReconnectedBadgeDuration = TimeSpan.FromSeconds(6);

    private readonly ReconnectPolicy _reconnect = new();

    private DispatcherTimer? _reconnectExpiry;
    private DispatcherTimer? _reconnectedFade;

    private bool IsHeldForReconnect => _reconnect.IsHolding;

    // ------------------------------------------------------------- holding one open

    /// <summary>
    /// Holds the session open when the phone that was on screen goes away, so that the same
    /// phone coming back inside the grace resumes it rather than starting a new one.
    /// </summary>
    /// <returns>True when the session is held; the caller then ends nothing.</returns>
    private bool HoldSessionForReconnect()
    {
        if (IsHeldForReconnect) return true;

        // Nothing worth holding: the app or the user is ending this session anyway, no session
        // had begun, a question is on screen, or this is the test pattern rather than a phone.
        if (_shuttingDown || _userStopRequested || _sessionIsDemo || _approvalPending || _sessionRejected) return false;
        if (_sessionStartedUtc is null || VideoHost.Visibility != Visibility.Visible) return false;
        // Only a session that had put a picture on screen is worth holding: holding one that
        // never showed anything leaves a blank window and a badge, waiting for what was never there.
        if (Video.PresentedFrameCount == 0) return false;
        // A phone that never named itself cannot be recognised when it comes back.
        if (_sessionDevice is not { } device) return false;

        if (!_reconnect.Hold(device.Name, device.Model, DateTime.UtcNow)) return false;

        _log.Info($"{device.Name} dropped out of the session; holding it for " +
                  $"{_reconnect.Grace.TotalSeconds:0} seconds in case it comes back");

        ShowReconnectPill($"Waiting for {device.Name}…", pulsing: true);

        if (_reconnectExpiry is null)
        {
            _reconnectExpiry = new DispatcherTimer();
            _reconnectExpiry.Tick += (_, _) => ExpireReconnectHold();
        }
        _reconnectExpiry.Interval = _reconnect.Grace;
        _reconnectExpiry.Stop();
        _reconnectExpiry.Start();
        return true;
    }

    /// <summary>The minute passed with no sign of the phone: the session ends the way it
    /// would have at once, with its summary and its recording finished off.</summary>
    private void ExpireReconnectHold()
    {
        if (!_reconnect.HasExpired(DateTime.UtcNow)) return;
        _log.Info($"{_reconnect.DeviceName ?? "the phone"} did not come back; the session ends here");

        // The source is already back in Ready and still advertising. End the held UI
        // session without tearing the receiver itself down, so another phone can connect
        // immediately instead of waiting for a restart.
        ClearReconnectHold();
        EndSessionBookkeeping(SessionEndReason.PhoneEnded);
        ShowWaitingForDevice(MirrorSourceState.Ready);
        UpdateTray();
    }

    /// <summary>True when a session starting now is the held phone coming back rather than a
    /// second phone taking the receiver over.</summary>
    private bool IsReconnectSession(SourceDeviceInfo? arriving) =>
        arriving is { } device && _reconnect.IsReturning(device.Name, device.Model);

    /// <summary>
    /// The phone that is back inside its grace: the session it interrupted carries on, so
    /// nothing is ended, nothing is asked, and the recording keeps its file.
    /// </summary>
    private void CompleteReconnect(SourceDeviceInfo? arriving)
    {
        var name = arriving?.Name ?? _reconnect.DeviceName ?? "iPhone";
        _reconnectExpiry?.Stop();
        // Not ClearReconnectHold: the badge that replaces this one is the point of the moment.
        _reconnect.Clear();

        if (arriving is { } device) _sessionDevice = device;
        _log.Info($"{name} came back; the session carried on");

        // The clock, the counters and the recording are all still the ones from before the
        // drop, so only the labels are brought up to date.
        Title = $"{name} - SoulScreen";
        ReceiverName.Text = name;
        UpdateRecordingPill();
        UpdateTaskbar();

        ShowReconnectPill($"{name} reconnected", pulsing: false);
        if (_reconnectedFade is null)
        {
            _reconnectedFade = new DispatcherTimer { Interval = ReconnectedBadgeDuration };
            _reconnectedFade.Tick += (_, _) => HideReconnectPill();
        }
        _reconnectedFade.Stop();
        _reconnectedFade.Start();
    }

    /// <summary>Ends a held session that is not going to be resumed: another phone is
    /// arriving, or the receiver is being put away.</summary>
    private void EndHeldSession(SessionEndReason reason)
    {
        if (!IsHeldForReconnect) return;
        ClearReconnectHold();
        EndSessionBookkeeping(reason);
        ShowIdle();
    }

    /// <summary>Forgets the hold and takes its badge down, whatever happens next.</summary>
    private void ClearReconnectHold()
    {
        _reconnect.Clear();
        _reconnectExpiry?.Stop();
        HideReconnectPill();
    }

    // ------------------------------------------------------------------ the badge

    /// <summary>
    /// The badge over the picture: quiet, and only while it has something to say - waiting for
    /// the phone, or just back. It is not a button: the phone needs nothing from the user.
    /// </summary>
    private void ShowReconnectPill(string text, bool pulsing)
    {
        _reconnectedFade?.Stop();
        // A phone's name can be a long one, and the badge shares its line with the recording and
        // paused pills. Trimmed to what the picture can spare, so the outermost pill is not
        // pushed off the edge of a narrow window. The badges themselves wrap when even that is
        // not enough room for all three.
        var room = ContentGrid.ActualWidth > 0 ? ContentGrid.ActualWidth - 150 : 240;
        ReconnectPillText.MaxWidth = Math.Max(72, room);
        ReconnectPillText.Text = text;
        ReconnectPill.Visibility = Visibility.Visible;

        if (!pulsing)
        {
            ReconnectPillDot.BeginAnimation(OpacityProperty, null);
            ReconnectPillDot.Opacity = 1;
            return;
        }

        ReconnectPillDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(700))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    private void HideReconnectPill()
    {
        _reconnectedFade?.Stop();
        ReconnectPill.Visibility = Visibility.Collapsed;
        ReconnectPillDot.BeginAnimation(OpacityProperty, null);
        ReconnectPillDot.Opacity = 1;
    }
}
