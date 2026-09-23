using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SoulScreen.AirPlay.FairPlay;
using SoulScreen.Android;
using SoulScreen.App.Logic;
using SoulScreen.Core.Sources;
using SoulScreen.Core.Time;
using SoulScreen.Media;

namespace SoulScreen.App;

/// <summary>
/// The connection check: everything on this PC that decides whether an iPhone can find the
/// receiver and mirror to it, read in one place with a plain verdict on each and, where there
/// is one, the fix.
/// <para>
/// "The phone doesn't list my PC" is the question every mirroring app gets asked most, and its
/// answer is nearly always one of the same few things: the receiver is not running, the PC and
/// phone are on different networks, the network is marked public, or the firewall turned the
/// phone away after its prompt was dismissed. Reading and saying which beats a paragraph of
/// troubleshooting advice. Nothing is changed unless a fix is asked for, and the firewall fix
/// goes through Windows' own elevation prompt.
/// </para>
/// </summary>
public partial class MainWindow
{
    private enum CheckStatus
    {
        Checking,
        Good,
        Info,
        Warning,
        Problem,
    }

    private sealed record DoctorCheck(string Title, string Detail, CheckStatus Status, string? ActionLabel = null, Action? Action = null)
    {
        public bool IsFirst { get; init; }

        public string Glyph => Status switch
        {
            CheckStatus.Good => "",
            CheckStatus.Warning => "",
            CheckStatus.Problem => "",
            CheckStatus.Info => "",
            _ => "",
        };

        public Visibility ActionVisibility => Action is null || ActionLabel is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private int _doctorGeneration;
    private List<DoctorCheck> _doctorChecks = [];

    private bool IsDoctorOpen => DoctorPanel.Visibility == Visibility.Visible;

    private void OnShowDoctor(object sender, RoutedEventArgs e) => ShowDoctor();

    private void OnCloseDoctor(object sender, RoutedEventArgs e) => CloseDoctor();

    private void OnDoctorRerun(object sender, RoutedEventArgs e) => RunDoctor();

    private void ShowDoctor()
    {
        if (_shuttingDown) return;
        if (_isMiniPlayer) ExitMiniPlayer();
        ClosePalette();
        CloseHelp();
        SettingsButton.IsChecked = false;
        CapturesButton.IsChecked = false;

        DoctorPanel.Visibility = Visibility.Visible;
        FadeContentIn(DoctorPanel);
        RunDoctor();
    }

    private void CloseDoctor()
    {
        if (!IsDoctorOpen) return;
        _doctorGeneration++;
        var hadFocus = DoctorPanel.IsKeyboardFocusWithin;
        DoctorPanel.Visibility = Visibility.Collapsed;
        if (hadFocus) Focus();
    }

    private async void RunDoctor()
    {
        var generation = ++_doctorGeneration;
        DoctorRerun.IsEnabled = false;
        SetDoctorSummary("Checking this PC…", "", "TextTertiary");
        _doctorChecks = [new DoctorCheck("Reading the network and firewall settings", "This takes a moment.", CheckStatus.Checking) { IsFirst = true }];
        DoctorList.ItemsSource = _doctorChecks;

        var port = _settings.Port;
        var receiverHoldsPort = _receiver is not null;

        (IReadOnlyList<ConnectedNetwork>? Networks, FirewallSnapshot? Firewall, bool PortFree) readings;
        try
        {
            readings = await Task.Run(() => (
                SystemDiagnostics.ConnectedNetworks(),
                SystemDiagnostics.ReadFirewall(),
                receiverHoldsPort || SystemDiagnostics.IsTcpPortFree(port)));
        }
        catch (Exception ex)
        {
            _log.Warn("the connection check could not read the system", ex);
            readings = (null, null, true);
        }

        if (generation != _doctorGeneration || !IsDoctorOpen || _shuttingDown) return;

        var checks = BuildDoctorChecks(readings.Networks, readings.Firewall, readings.PortFree);
        if (checks.Count > 0) checks[0] = checks[0] with { IsFirst = true };
        _doctorChecks = checks;
        DoctorList.ItemsSource = checks;
        DoctorRerun.IsEnabled = true;

        var problems = checks.Count(check => check.Status == CheckStatus.Problem);
        var warnings = checks.Count(check => check.Status == CheckStatus.Warning);
        if (problems > 0)
            SetDoctorSummary($"{Plural(problems, "thing")} will stop an iPhone mirroring to this PC.", "", "Danger");
        else if (warnings > 0)
            SetDoctorSummary($"Mirroring should work, with {Plural(warnings, "thing")} worth a look.", "", "Warning");
        else
            SetDoctorSummary("Everything is ready. Your iPhone should find this PC under Screen Mirroring.", "", "Success");
    }

    private void SetDoctorSummary(string text, string glyph, string brushKey)
    {
        DoctorSummary.Text = text;
        DoctorSummaryIcon.Text = glyph;
        DoctorSummaryIcon.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, brushKey);
    }

    private List<DoctorCheck> BuildDoctorChecks(IReadOnlyList<ConnectedNetwork>? networks, FirewallSnapshot? firewall, bool portFree)
    {
        var checks = new List<DoctorCheck>();

        // ---- the receiver
        if (_demo is not null)
        {
            checks.Add(new("The demo is running",
                "The receiver is paused while the test pattern plays, so no iPhone can find this PC.",
                CheckStatus.Warning, "End the demo", DisconnectDevice));
        }
        else if (_receiver is null)
        {
            checks.Add(new("The receiver is stopped",
                "An iPhone can only find this PC while the receiver is running.",
                CheckStatus.Problem, "Start it", StartReceiverFromDoctor));
        }
        else if (_receiver.State is MirrorSourceState.Ready or MirrorSourceState.Connecting or MirrorSourceState.Streaming)
        {
            var name = _receiver.AdvertisedName;
            checks.Add(new($"Advertising as “{name}”",
                _receiver.State == MirrorSourceState.Streaming
                    ? $"{ActiveSource?.Device?.Name ?? "An iPhone"} is mirroring right now."
                    : $"On the iPhone, open Control Center, tap Screen Mirroring and choose “{name}”.",
                CheckStatus.Good));
        }
        else
        {
            checks.Add(new("The receiver is not advertising",
                "It may still be starting. The activity log says what happened.",
                CheckStatus.Warning, "Restart it", StartReceiverFromDoctor));
        }

        // ---- the network
        var endpoints = NetworkInfo.ActiveEndpoints();
        if (endpoints.Count == 0)
        {
            checks.Add(new("Not connected to a network",
                "Join the Wi-Fi network the iPhone is on.",
                CheckStatus.Problem, "Network settings", () => OpenSystemPage("ms-settings:network-status")));
        }
        else
        {
            var first = endpoints[0];
            checks.Add(new($"Connected over {first.AdapterName}",
                $"This PC is {first.Address}. The iPhone has to be on the same network: a guest network, or one that keeps devices apart, will not work.",
                CheckStatus.Good));
        }

        var executable = Environment.ProcessPath ?? string.Empty;
        FirewallVerdict? verdict = firewall is null
            ? null
            : FirewallRules.Evaluate(firewall.Rules, firewall.ActiveProfiles, firewall.EnabledOnActiveProfiles, executable, _settings.Port);

        if (networks is { Count: > 0 })
        {
            var publicNetwork = networks.FirstOrDefault(network => network.IsPublic);
            if (publicNetwork is null)
            {
                checks.Add(new($"“{networks[0].Name}” is a private network",
                    "Windows lets the other devices on it find this PC.", CheckStatus.Good));
            }
            else if (verdict is FirewallVerdict.Allowed or FirewallVerdict.Off)
            {
                checks.Add(new($"“{publicNetwork.Name}” is set as a public network",
                    "SoulScreen is let through on it, so mirroring works. If this is your home or office network, private is the safer setting.",
                    CheckStatus.Info, "Network settings", () => OpenSystemPage("ms-settings:network-status")));
            }
            else
            {
                checks.Add(new($"“{publicNetwork.Name}” is set as a public network",
                    "Windows keeps this PC hidden from other devices on a public network. If this is your home or office network, make it private.",
                    CheckStatus.Warning, "Network settings", () => OpenSystemPage("ms-settings:network-status")));
            }
        }

        // ---- the firewall
        var canFix = firewall is not null && executable.Length > 0;
        Action? fix = canFix ? () => AllowThroughFirewall(firewall!.ActiveProfiles) : null;
        switch (verdict)
        {
            case null:
                checks.Add(new("The firewall settings could not be read",
                    "If the iPhone cannot find this PC, check that Windows Defender Firewall allows SoulScreen.",
                    CheckStatus.Info, "Firewall settings", () => OpenSystemPage("windowsdefender://network")));
                break;
            case FirewallVerdict.Off:
                checks.Add(new("Windows Firewall is off on this network",
                    "Nothing in Windows turns the iPhone away. Another security app may still run a firewall of its own.",
                    CheckStatus.Good));
                break;
            case FirewallVerdict.Allowed:
                checks.Add(new("Windows Firewall lets SoulScreen through",
                    $"Incoming connections are allowed on {DescribeProfiles(firewall!.ActiveProfiles)} networks.",
                    CheckStatus.Good));
                break;
            case FirewallVerdict.PartlyAllowed:
                checks.Add(new("Windows Firewall only partly lets SoulScreen through",
                    "The iPhone may connect and then show no picture, or play no sound.",
                    CheckStatus.Warning, fix is null ? null : "Allow SoulScreen", fix));
                break;
            case FirewallVerdict.Blocked:
                checks.Add(new("Windows Firewall is blocking SoulScreen",
                    "Usually left behind when Windows asked whether to allow SoulScreen and the question was closed. The iPhone cannot connect until it is allowed.",
                    CheckStatus.Problem, fix is null ? null : "Allow SoulScreen", fix));
                break;
            case FirewallVerdict.NoRule:
                checks.Add(new("Windows Firewall has no rule for SoulScreen",
                    "Windows asks the first time a phone connects, and mirroring works once that is answered “Allow”. Allowing it now saves the question.",
                    CheckStatus.Warning, fix is null ? null : "Allow SoulScreen", fix));
                break;
        }

        // ---- the control port, which only matters while the receiver is not holding it
        if (_receiver is null && _demo is null && !portFree)
        {
            checks.Add(new($"Port {_settings.Port} is taken",
                "Another program is listening on the receiver's control port, so the receiver cannot start. Choose another port under Settings, Receiver.",
                CheckStatus.Problem, "Open Settings", () => ShowSettingsSection("RECEIVER")));
        }

        // ---- the helpers
        checks.Add(NativeFairPlay.IsAvailable
            ? new("FairPlay support is installed", "The iPhone can complete the handshake that unlocks its screen.", CheckStatus.Good)
            : new("FairPlay support is missing",
                "The iPhone will find this PC and then refuse to mirror. Build the helper with 'pwsh tools/build-fairplay.ps1' and restart SoulScreen.",
                CheckStatus.Problem));

        checks.Add(FFmpegRuntime.IsAvailable
            ? new("The video decoder is ready", $"FFmpeg {FFmpegRuntime.Version}.", CheckStatus.Good)
            : new("The video decoder is missing",
                "A phone will connect but nothing will be drawn. Run 'pwsh tools/fetch-ffmpeg.ps1' and restart SoulScreen.",
                CheckStatus.Problem));

        if (_settings.EnableAndroid)
        {
            checks.Add(AdbRuntime.IsAvailable
                ? new("Android mirroring is ready", $"adb found at {AdbRuntime.ExecutablePath}.", CheckStatus.Good)
                : new("adb was not found", AdbRuntime.UnavailableReason ?? "", CheckStatus.Info));

            if (AdbRuntime.IsAvailable)
            {
                checks.Add(ScrcpyServerRuntime.IsAvailable
                    ? new("Android audio and rotation support is ready",
                        $"scrcpy-server {ScrcpyServerRuntime.ServerVersion} found.", CheckStatus.Good)
                    : new("Android mirroring will be video-only",
                        ScrcpyServerRuntime.UnavailableReason ?? "", CheckStatus.Info));
            }
        }

        var tier = RenderCapability.Tier >> 16;
        checks.Add(tier switch
        {
            >= 2 => new("Graphics acceleration is on", "The picture is drawn by the graphics card.", CheckStatus.Good),
            1 => new("Graphics acceleration is partial", "Mirroring works, but a high frame rate may not stay smooth.", CheckStatus.Warning),
            _ => new("Windows is drawing this window in software",
                "Usually a remote desktop session or a missing graphics driver. Mirroring works, but the picture will stutter.",
                CheckStatus.Warning),
        });

        // ---- sound
        if (!_settings.EnableAudio)
        {
            checks.Add(new("The phone's sound is switched off",
                "The screen mirrors without sound. Turn on “Receive audio” under Settings, Receiver.",
                CheckStatus.Info, "Open Settings", () => ShowSettingsSection("RECEIVER")));
        }
        else
        {
            var devices = AudioDevices.ListOutputs();
            var chosen = devices.Where(device => !device.IsDefault && device.Id == _settings.AudioOutputDeviceId)
                .Select(device => device.Name).FirstOrDefault();
            if (devices.All(device => device.IsDefault))
            {
                checks.Add(new("No speakers or headphones were found",
                    "The screen mirrors, but the phone's sound has nowhere to play.", CheckStatus.Warning));
            }
            else if (_settings.AudioOutputDeviceId is not null && chosen is null)
            {
                checks.Add(new("The chosen speakers are not connected",
                    "The phone's sound plays on the system default instead until they are back.",
                    CheckStatus.Info, "Open Settings", () => ShowSettingsSection("AUDIO")));
            }
            else
            {
                checks.Add(new($"Sound plays on {chosen ?? "the system default device"}",
                    "The phone's audio comes out here, in step with the picture.", CheckStatus.Good));
            }
        }

        // ---- who may mirror
        if (_settings.BlockedDevices.Count > 0)
        {
            checks.Add(new($"{Plural(_settings.BlockedDevices.Count, "iPhone")} blocked",
                "A blocked iPhone is disconnected as soon as it starts to mirror. If yours is one of them, unblock it.",
                CheckStatus.Info, "Review", () => ShowSettingsSection("PRIVACY")));
        }

        return checks;
    }

    private static string DescribeProfiles(int mask)
    {
        var names = FirewallRules.ProfileNames(mask).Split(',');
        return names.Length == 1 ? names[0] : string.Join(", ", names[..^1]) + " and " + names[^1];
    }

    private void OnDoctorAction(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DoctorCheck { Action: { } action } check) return;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _log.Warn($"the connection check's \"{check.ActionLabel}\" failed", ex);
            ShowToast("That did not work - the activity log has the details", "");
        }
    }

    private void OnDoctorCopy(object sender, RoutedEventArgs e)
    {
        var report = new StringBuilder();
        var mode = TimestampFormatting.Resolve(
            _settings.Timestamps.UseShamsi, _settings.Timestamps.ShowGregorianAlongside, CultureInfo.CurrentCulture);
        report.AppendLine($"SoulScreen connection check, {TimestampFormatting.FormatDateTime(DateTime.Now, mode)}");
        report.AppendLine(DoctorSummary.Text);
        report.AppendLine();
        foreach (var check in _doctorChecks)
        {
            var mark = check.Status switch
            {
                CheckStatus.Good => "[ok]",
                CheckStatus.Info => "[i] ",
                CheckStatus.Warning => "[!] ",
                CheckStatus.Problem => "[x] ",
                _ => "[..]",
            };
            report.AppendLine($"{mark} {check.Title}");
            report.AppendLine($"     {check.Detail}");
        }

        try
        {
            Clipboard.SetText(report.ToString());
            ShowToast("Report copied", "");
        }
        catch (Exception ex)
        {
            _log.Warn("could not copy the connection report", ex);
            ShowToast("The clipboard is busy; try again", "");
        }
    }

    private async void StartReceiverFromDoctor()
    {
        if (!TryBeginReceiverWork()) return;
        try { await StartReceiverAsync(); }
        finally { EndReceiverWork(); }
        if (IsDoctorOpen) RunDoctor();
    }

    private void OpenSystemPage(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warn($"could not open {uri}", ex);
            ShowToast("Windows could not open that page", "");
        }
    }

    /// <summary>
    /// Replaces whatever rules name this program with two that allow it in, over TCP and UDP, on
    /// the networks this PC is on now. Replaced rather than added to: the block a dismissed prompt
    /// leaves behind would otherwise still win. Runs through Windows' own elevation prompt.
    /// </summary>
    private async void AllowThroughFirewall(int activeProfiles)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable)) return;

        var program = executable.Replace("'", "''");
        var profiles = FirewallRules.ProfileNames(activeProfiles);
        // Stop on the first failure: the old rules are removed before the new ones are added, and a
        // rule that failed to be created must not end in a report that all went well.
        var script = new StringBuilder()
            .AppendLine("$ErrorActionPreference = 'Stop'")
            .AppendLine($"$program = '{program}'")
            .AppendLine("Get-NetFirewallApplicationFilter | Where-Object { $_.Program -eq $program } | Get-NetFirewallRule | Remove-NetFirewallRule")
            .AppendLine($"New-NetFirewallRule -DisplayName 'SoulScreen (TCP)' -Group 'SoulScreen' -Direction Inbound -Action Allow -Program $program -Protocol TCP -Profile {profiles} | Out-Null")
            .AppendLine($"New-NetFirewallRule -DisplayName 'SoulScreen (UDP)' -Group 'SoulScreen' -Direction Inbound -Action Allow -Program $program -Protocol UDP -Profile {profiles} | Out-Null")
            .ToString();
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        var start = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        if (IsDoctorOpen) SetDoctorSummary("Waiting for Windows to update the firewall…", "", "TextTertiary");

        try
        {
            using var process = Process.Start(start);
            if (process is not null)
            {
                await process.WaitForExitAsync();
                if (process.ExitCode == 0)
                {
                    _log.Info($"allowed {executable} through the firewall on {profiles} networks");
                    ShowToast("SoulScreen is allowed through the firewall", "");
                }
                else
                {
                    _log.Warn($"the firewall update exited with {process.ExitCode}");
                    ShowToast("Windows did not accept the firewall change", "");
                }
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The elevation prompt was declined.
            ShowToast("The firewall was left as it was", "");
        }
        catch (Exception ex)
        {
            _log.Warn("could not update the firewall", ex);
            ShowToast("The firewall could not be changed", "");
        }

        if (IsDoctorOpen) RunDoctor();
    }
}
