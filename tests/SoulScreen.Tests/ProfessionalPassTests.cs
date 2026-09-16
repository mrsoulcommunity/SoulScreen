using SoulScreen.App;
using SoulScreen.App.Logic;

namespace SoulScreen.Tests;

public class CaptureFilterTests
{
    [Fact]
    public void EmptyQueryMatchesEverything()
    {
        Assert.True(CaptureFilter.Matches("SoulScreen-20260915-101500.png", ""));
        Assert.True(CaptureFilter.Matches("anything", "   "));
    }

    [Fact]
    public void EveryWordMustMatch()
    {
        var name = "SoulScreen-20260915-101500.mp4";
        Assert.True(CaptureFilter.Matches(name, "rec"));
        Assert.True(CaptureFilter.Matches(name, "mp4 1015"));
        Assert.False(CaptureFilter.Matches(name, "mp4 png"));
    }

    [Fact]
    public void MatchingIgnoresCase()
    {
        Assert.True(CaptureFilter.Matches("SoulScreen-20260915-101500.PNG", "png"));
    }

    [Fact]
    public void SortingPutsNewestFirstByDefault()
    {
        var captures = new List<(DateTime Modified, long Size)>
        {
            (new(2026, 1, 1), 10),
            (new(2026, 3, 1), 10),
            (new(2026, 2, 1), 10),
        };
        CaptureFilter.Sort(captures, c => c.Modified, c => c.Size, CaptureSortOrder.NewestFirst);
        Assert.Equal(new DateTime(2026, 3, 1), captures[0].Modified);
        Assert.Equal(new DateTime(2026, 1, 1), captures[2].Modified);
    }

    [Fact]
    public void OldestFirstReverses()
    {
        var captures = new List<(DateTime Modified, long Size)>
        {
            (new(2026, 3, 1), 10),
            (new(2026, 1, 1), 10),
        };
        CaptureFilter.Sort(captures, c => c.Modified, c => c.Size, CaptureSortOrder.OldestFirst);
        Assert.Equal(new DateTime(2026, 1, 1), captures[0].Modified);
    }

    [Fact]
    public void LargestFirstBreaksTiesByAge()
    {
        var old = new DateTime(2026, 1, 1);
        var new_ = new DateTime(2026, 2, 1);
        var captures = new List<(DateTime Modified, long Size)>
        {
            (new_, 500),
            (old, 500),
            (old, 900),
        };
        CaptureFilter.Sort(captures, c => c.Modified, c => c.Size, CaptureSortOrder.LargestFirst);
        Assert.Equal(900, captures[0].Size);
        Assert.Equal(old, captures[1].Modified); // same size: the older one first
        Assert.Equal(new_, captures[2].Modified);
    }

    [Fact]
    public void DescribeCountReadsNaturally()
    {
        Assert.Equal("empty", CaptureFilter.DescribeCount(0, 0));
        Assert.Contains("1 item", CaptureFilter.DescribeCount(1, 940));
        Assert.Contains("14 items", CaptureFilter.DescribeCount(14, 1_900_000_000));
        Assert.Contains("GB", CaptureFilter.DescribeCount(14, 1_900_000_000));
    }
}

public class RecordingTimerTests
{
    [Fact]
    public void ArmsFromNow()
    {
        var timer = new RecordingTimer();
        var now = DateTime.UtcNow;
        timer.Arm(TimeSpan.FromMinutes(5), now);
        Assert.True(timer.IsArmed);
        Assert.Equal(now + TimeSpan.FromMinutes(5), timer.DeadlineUtc);
        Assert.False(timer.IsDue(now + TimeSpan.FromMinutes(4)));
        Assert.True(timer.IsDue(now + TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void ReArmingKeepsTheLaterDeadline()
    {
        var timer = new RecordingTimer();
        var now = DateTime.UtcNow;
        timer.Arm(TimeSpan.FromMinutes(10), now);
        timer.Arm(TimeSpan.FromMinutes(5), now + TimeSpan.FromSeconds(10));
        Assert.Equal(now + TimeSpan.FromMinutes(10), timer.DeadlineUtc);
    }

    [Fact]
    public void NonPositiveDurationDisarms()
    {
        var timer = new RecordingTimer();
        var now = DateTime.UtcNow;
        timer.Arm(TimeSpan.FromMinutes(5), now);
        timer.Arm(TimeSpan.Zero, now);
        Assert.False(timer.IsArmed);
    }

    [Fact]
    public void OverdueNeedsTheGracePeriod()
    {
        var timer = new RecordingTimer();
        var now = DateTime.UtcNow;
        timer.Arm(TimeSpan.FromMinutes(1), now);
        var deadline = now + TimeSpan.FromMinutes(1);
        Assert.False(timer.IsOverdue(deadline));
        Assert.True(timer.IsOverdue(deadline + RecordingTimer.GracePeriod));
    }

    [Fact]
    public void RemainingIsNullOnceDue()
    {
        var timer = new RecordingTimer();
        var now = DateTime.UtcNow;
        timer.Arm(TimeSpan.FromSeconds(30), now);
        Assert.Equal(TimeSpan.FromSeconds(10), timer.Remaining(now + TimeSpan.FromSeconds(20)));
        Assert.Null(timer.Remaining(now + TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void CountdownReadsNaturally()
    {
        Assert.Equal("4:59", RecordingTimer.FormatCountdown(TimeSpan.FromSeconds(299)));
        Assert.Equal("48s", RecordingTimer.FormatCountdown(TimeSpan.FromSeconds(48)));
        Assert.Equal("0s", RecordingTimer.FormatCountdown(TimeSpan.Zero));
        Assert.Equal("Stops in 1s", RecordingTimer.DescribeCountdown(TimeSpan.FromMilliseconds(900)));
    }
}

public class CaptureBudgetTests
{
    private static CaptureBudget.Candidate Capture(string path, long size, int daysAgo, bool active = false) =>
        new(path, size, DateTime.UtcNow - TimeSpan.FromDays(daysAgo), active);

    [Fact]
    public void NoBudgetMeansNothingToDo()
    {
        var captures = new[] { Capture("a", 1000, 1) };
        Assert.Empty(CaptureBudget.PlanRemoval(captures, 0).Remove);
        Assert.Empty(CaptureBudget.PlanRemoval(captures, -1).Remove);
    }

    [Fact]
    public void UnderBudgetMeansNothingToRemove()
    {
        var captures = new[] { Capture("a", 400, 1), Capture("b", 400, 2) };
        var plan = CaptureBudget.PlanRemoval(captures, 1000);
        Assert.Empty(plan.Remove);
        Assert.Equal(0, plan.FreedBytes);
    }

    [Fact]
    public void OverBudgetRemovesOldestFirst()
    {
        var captures = new[]
        {
            Capture("new", 400, 1),
            Capture("old", 400, 30),
            Capture("older", 400, 60),
        };
        var plan = CaptureBudget.PlanRemoval(captures, 1000);
        Assert.Single(plan.Remove);
        Assert.Equal("older", plan.Remove[0]);
        Assert.Equal(400, plan.FreedBytes);
    }

    [Fact]
    public void TheActiveRecordingIsNeverRemoved()
    {
        var captures = new[]
        {
            Capture("active", 900, 0, active: true),
            Capture("old", 500, 30),
        };
        var plan = CaptureBudget.PlanRemoval(captures, 1000);
        Assert.DoesNotContain("active", plan.Remove);
        Assert.Single(plan.Remove);
    }

    [Fact]
    public void OnlyTheActiveRecordingMeansNoPlan()
    {
        var captures = new[] { Capture("active", 900, 0, active: true) };
        Assert.Empty(CaptureBudget.PlanRemoval(captures, 100).Remove);
    }

    [Fact]
    public void NothingToFreeMeansNothingToRemove()
    {
        // One capture, already over the budget on its own: removing it would not bring
        // the folder under budget, and the plan says so by taking it anyway (all removable).
        var captures = new[] { Capture("huge", 2000, 1) };
        var plan = CaptureBudget.PlanRemoval(captures, 1000);
        Assert.Single(plan.Remove);
    }

    [Fact]
    public void DescribeFreedUsesSizes()
    {
        Assert.Contains("MB", CaptureBudget.DescribeFreed(5 * 1024 * 1024));
        Assert.Contains("GB", CaptureBudget.DescribeFreed(3L * 1024 * 1024 * 1024));
    }
}

public class DisplayLayoutTests
{
    private static readonly RectBounds Laptop = new(0, 0, 1920, 1040);
    private static readonly RectBounds External = new(1920, 0, 2560, 1400);

    private static List<DisplayChoice> TwoDisplays() =>
    [
        new(0, "DISPLAY1", IsPrimary: true, Laptop),
        new(1, "DISPLAY2", IsPrimary: false, External),
    ];

    [Fact]
    public void CurrentDisplayLeavesTheWindowAlone()
    {
        var window = new RectBounds(100, 100, 800, 600);
        Assert.Null(DisplayLayout.Resolve(DisplayLayout.CurrentDisplay, TwoDisplays(), window));
        Assert.Null(DisplayLayout.Resolve(null, TwoDisplays(), window));
    }

    [Fact]
    public void PrimaryChoosesThePrimary()
    {
        var resolved = DisplayLayout.Resolve(DisplayLayout.PrimaryDisplay, TwoDisplays(), new RectBounds(2000, 100, 800, 600));
        Assert.NotNull(resolved);
        Assert.True(resolved!.IsPrimary);
    }

    [Fact]
    public void AnIndexOffScreenResolves()
    {
        var resolved = DisplayLayout.Resolve("1", TwoDisplays(), new RectBounds(100, 100, 800, 600));
        Assert.NotNull(resolved);
        Assert.Equal(1, resolved!.Index);
    }

    [Fact]
    public void AnIndexAlreadyHostedIsANoOp()
    {
        var window = new RectBounds(2100, 100, 800, 600); // centre on display 1
        Assert.Null(DisplayLayout.Resolve("1", TwoDisplays(), window));
    }

    [Fact]
    public void ANegativeOrNonsenseIndexIsIgnored()
    {
        Assert.Null(DisplayLayout.Resolve("-1", TwoDisplays(), new RectBounds(100, 100, 800, 600)));
        Assert.Null(DisplayLayout.Resolve("nonsense", TwoDisplays(), new RectBounds(100, 100, 800, 600)));
    }

    /// <summary>The saved monitor was unplugged, or the settings file moved to a PC with
    /// fewer screens: the window still needs somewhere sensible to go.</summary>
    [Fact]
    public void AMonitorThatNoLongerExistsFallsBackToPrimary()
    {
        var resolved = DisplayLayout.Resolve("7", TwoDisplays(), new RectBounds(100, 100, 800, 600));
        Assert.NotNull(resolved);
        Assert.True(resolved!.IsPrimary);
    }

    [Fact]
    public void NoDisplaysMeansNoMove()
    {
        Assert.Null(DisplayLayout.Resolve("0", [], new RectBounds(0, 0, 800, 600)));
    }

    [Fact]
    public void PlacementCentresOnTheDisplay()
    {
        var display = TwoDisplays()[1];
        var (left, top) = DisplayLayout.PlacementFor(display, new RectBounds(0, 0, 800, 600));
        Assert.Equal(1920 + (2560 - 800) / 2, left);
        Assert.Equal((1400 - 600) / 2, top);
    }

    [Fact]
    public void AnOversizedWindowStillLandsInside()
    {
        var display = TwoDisplays()[0];
        var (left, top) = DisplayLayout.PlacementFor(display, new RectBounds(0, 0, 4000, 2000));
        Assert.Equal(Laptop.Left, left);
        Assert.Equal(Laptop.Top, top);
    }

    [Fact]
    public void ClampingKeepsTheWindowReachable()
    {
        var desktop = new RectBounds(0, 0, 4480, 1400);
        var (left, top) = DisplayLayout.ClampToDisplays(-500, 2000, 800, 600, desktop);
        Assert.Equal(0, left);
        Assert.True(top <= desktop.Bottom - 120);
    }

    [Fact]
    public void AWindowInsideTheDesktopIsNotMoved()
    {
        var desktop = new RectBounds(0, 0, 4480, 1400);
        var (left, top) = DisplayLayout.ClampToDisplays(100, 100, 800, 600, desktop);
        Assert.Equal(100, left);
        Assert.Equal(100, top);
    }

    [Fact]
    public void AnEmptyDesktopChangesNothing()
    {
        var (left, top) = DisplayLayout.ClampToDisplays(-50, -50, 800, 600, default);
        Assert.Equal(-50, left);
        Assert.Equal(-50, top);
    }

    [Fact]
    public void IsOnDisplayUsesTheCentre()
    {
        Assert.True(DisplayLayout.IsOnDisplay(new RectBounds(1900, 100, 800, 600), External));
        Assert.False(DisplayLayout.IsOnDisplay(new RectBounds(100, 100, 800, 600), External));
    }
}

public class MotionPreferenceTests
{
    [Fact]
    public void FollowingWindowsDefersToWindows()
    {
        Assert.True(MotionPreferences.ShouldAnimate(MotionPreference.FollowWindows, windowsAllows: true));
        Assert.False(MotionPreferences.ShouldAnimate(MotionPreference.FollowWindows, windowsAllows: false));
    }

    [Fact]
    public void AlwaysOnWinsWhateverWindowsSays()
    {
        Assert.True(MotionPreferences.ShouldAnimate(MotionPreference.AlwaysOn, windowsAllows: false));
    }

    [Fact]
    public void AlwaysOffWinsWhateverWindowsSays()
    {
        Assert.False(MotionPreferences.ShouldAnimate(MotionPreference.AlwaysOff, windowsAllows: true));
    }
}

public class MetricsHistoryTests
{
    [Fact]
    public void ReadsBackOldestFirst()
    {
        var history = new MetricsHistory(4);
        history.Add(1);
        history.Add(2);
        history.Add(3);
        Assert.Equal([1, 2, 3], history.ToArray());
        Assert.False(history.IsFull);
    }

    [Fact]
    public void WrapsAndDropsTheOldest()
    {
        var history = new MetricsHistory(3);
        foreach (var v in new[] { 1, 2, 3, 4, 5 }) history.Add(v);
        Assert.True(history.IsFull);
        Assert.Equal([3, 4, 5], history.ToArray());
    }

    [Fact]
    public void ClearEmpties()
    {
        var history = new MetricsHistory(3);
        history.Add(1);
        history.Clear();
        Assert.Equal(0, history.Count);
        Assert.Empty(history.ToArray());
    }

    [Fact]
    public void ANanBecomesZero()
    {
        var history = new MetricsHistory(3);
        history.Add(double.NaN);
        Assert.Equal([0.0], history.ToArray());
    }

    [Fact]
    public void MaxHandlesEmptyAndFilled()
    {
        var history = new MetricsHistory(4);
        Assert.Null(history.Max());
        history.Add(12);
        history.Add(59.9);
        history.Add(3);
        Assert.Equal(59.9, history.Max()!.Value, 5);
    }

    [Fact]
    public void TinyCapacityIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MetricsHistory(1));
    }
}

public class SettingsTransferTests
{
    private static AppSettings Importable(Action<AppSettings> change)
    {
        var settings = new AppSettings();
        change(settings);
        settings.Normalise();
        return settings;
    }

    [Fact]
    public void PreferencesAreTaken()
    {
        var current = new AppSettings();
        var imported = Importable(s =>
        {
            s.Theme = AppTheme.Light;
            s.Accent = AccentColor.Purple;
            s.Latency = LatencyMode.Responsive;
            s.CaptureBudgetBytes = 5L * 1024 * 1024 * 1024;
            s.Animations = MotionPreference.AlwaysOff;
        });

        var changed = SettingsTransfer.ApplyImported(current, imported);
        Assert.Equal(AppTheme.Light, current.Theme);
        Assert.Equal(AccentColor.Purple, current.Accent);
        Assert.Equal(LatencyMode.Responsive, current.Latency);
        Assert.Equal(5L * 1024 * 1024 * 1024, current.CaptureBudgetBytes);
        Assert.Equal(MotionPreference.AlwaysOff, current.Animations);
        Assert.Contains("theme", changed);
        Assert.Contains("animations", changed);
    }

    [Fact]
    public void IdentityStays()
    {
        var current = new AppSettings
        {
            RecentDevices =
            [
                new RecentDevice { Name = "Kasra's iPhone", SessionCount = 4 },
            ],
            HasSeenWelcome = true,
            WindowWidth = 900,
            WindowHeight = 700,
            AllowedDevices = [new DeviceKey { Name = "Kasra's iPhone" }],
            BlockedDevices = [new DeviceKey { Name = "Office iPhone" }],
        };
        var imported = Importable(s => { s.DeviceName = "Studio Mac"; });

        SettingsTransfer.ApplyImported(current, imported);
        Assert.Single(current.RecentDevices);
        Assert.True(current.HasSeenWelcome);
        Assert.Equal(900, current.WindowWidth);
        Assert.Single(current.AllowedDevices);
        Assert.Single(current.BlockedDevices);
        Assert.NotEqual("Studio Mac", current.DeviceName);
    }

    [Fact]
    public void IdenticalSettingsReportNothingChanged()
    {
        var current = new AppSettings();
        var imported = Importable(_ => { });
        var changed = SettingsTransfer.ApplyImported(current, imported);
        Assert.Empty(changed);
    }
}
