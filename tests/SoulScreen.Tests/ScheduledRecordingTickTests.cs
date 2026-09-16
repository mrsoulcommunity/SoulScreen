using SoulScreen.App.Logic;

namespace SoulScreen.Tests;

/// <summary>
/// Guards the scheduled-recording start against its one real regression: the feature was
/// fully built - menu, arm, schedule type, tests - yet <c>CheckRecordingSchedule()</c> was
/// never called from anywhere, so an armed schedule sat armed forever. These tests bind the
/// whole chain at the source level (tick → UpdateMetrics → CheckRecordingSchedule) so
/// removing any link turns the suite red, and cover the start decision's branches directly.
/// </summary>
public class ScheduledRecordingTickTests
{
    // ---------------------------------------------------------------- source-level binding

    private static string? SourceOf(string fileName)
    {
        var root = FindRepoRoot();
        if (root is null) return null;
        var candidates = new[]
        {
            Path.Combine(root, "src", "SoulScreen.App", fileName),
            Path.Combine(root, "src", "SoulScreen.App", "MainWindow.Video.cs"),
        };
        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        return null;
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var current = dir; current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "SoulScreen.sln")))
                return current.FullName;
        return null;
    }

    /// <summary>The tick handler must call UpdateMetrics, and UpdateMetrics must call the
    /// scheduled-start duty. Deleting either call is exactly the regression that shipped.</summary>
    [Fact]
    public void TheMetricsTickWiresTheScheduledStartDuty()
    {
        var xamlCs = SourceOf("MainWindow.xaml.cs");
        var metrics = SourceOf("MainWindow.Metrics.cs");
        Assert.False(xamlCs is null, "MainWindow.xaml.cs could not be located from the test run directory");
        Assert.False(metrics is null, "MainWindow.Metrics.cs could not be located from the test run directory");

        // Tick → UpdateMetrics (the half-second tick the schedule rides on).
        Assert.Matches(
            @"_metricsTimer\.Tick\s*\+=\s*\(_,?\s*_\)\s*=>\s*\{?[\s\S]{0,200}?UpdateMetrics\(\)",
            xamlCs!);

        // UpdateMetrics → CheckRecordingSchedule (the link that was missing once before).
        Assert.Matches(@"\bCheckRecordingSchedule\(\);", metrics!);
    }

    /// <summary>The duty itself must exist and be driven by the tick, not by a menu only:
    /// a private parameterless method called from UpdateMetrics is the contract.</summary>
    [Fact]
    public void CheckRecordingScheduleIsDrivenByTheTickNotOnlyByMenus()
    {
        var metrics = SourceOf("MainWindow.Metrics.cs");
        Assert.False(metrics is null);

        // The call sits inside UpdateMetrics's own body, not merely somewhere in the file.
        var body = BodyOf(metrics!, "private void UpdateMetrics()");
        Assert.False(body is null, "UpdateMetrics() was not found in MainWindow.Metrics.cs");
        Assert.Contains("CheckRecordingSchedule();", body!);
    }

    /// <summary>The start decision must be applied through ScheduledRecordingTick, so the
    /// tested branching and the shipped branching are one and the same.</summary>
    [Fact]
    public void CheckRecordingScheduleDecidesThroughTheTestedClass()
    {
        var video = SourceOf("MainWindow.Video.cs");
        Assert.False(video is null);
        Assert.Contains("ScheduledRecordingTick.Decide(", video!);
    }

    private static string? BodyOf(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) return null;
        var open = source.IndexOf('{', start);
        if (open < 0) return null;
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) return source[open..(i + 1)];
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- behaviour

    /// <summary>A due schedule with a free, able record control starts the recording and
    /// hands the schedule's duration over as the automatic stop.</summary>
    [Fact]
    public void ADueScheduleStartsTheRecordingAndCarriesItsDuration()
    {
        var now = DateTime.UtcNow;
        var schedule = RecordingSchedule.Compute(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), now);

        var action = ScheduledRecordingTick.Decide(schedule, now + TimeSpan.FromMinutes(5),
            alreadyRecording: false, canRecord: true, out var autoStop);

        Assert.Equal(ScheduledStartAction.Start, action);
        Assert.Equal(TimeSpan.FromMinutes(30), autoStop);
    }

    /// <summary>Before the deadline the tick waits, and nothing is handed to arm.</summary>
    [Fact]
    public void AnEarlyTickWaitsAndArmsNothing()
    {
        var now = DateTime.UtcNow;
        var schedule = RecordingSchedule.Compute(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), now);

        var action = ScheduledRecordingTick.Decide(schedule, now + TimeSpan.FromMinutes(4),
            alreadyRecording: false, canRecord: true, out var autoStop);

        Assert.Equal(ScheduledStartAction.Wait, action);
        Assert.Null(autoStop);
    }

    /// <summary>A due schedule with no duration starts the recording with no automatic stop,
    /// exactly like one started from the toolbar.</summary>
    [Fact]
    public void ADueScheduleWithoutADurationStartsWithoutAnAutoStop()
    {
        var now = DateTime.UtcNow;
        var schedule = RecordingSchedule.Compute(TimeSpan.FromMinutes(1), TimeSpan.Zero, now);

        var action = ScheduledRecordingTick.Decide(schedule, now + TimeSpan.FromMinutes(1),
            alreadyRecording: false, canRecord: true, out var autoStop);

        Assert.Equal(ScheduledStartAction.Start, action);
        Assert.Null(autoStop);
    }

    /// <summary>Already recording: the schedule is dropped, not stacked and not restarted.</summary>
    [Fact]
    public void ADueScheduleWhileAlreadyRecordingIsDropped()
    {
        var schedule = RecordingSchedule.Compute(TimeSpan.Zero, TimeSpan.FromMinutes(10), DateTime.UtcNow);

        var action = ScheduledRecordingTick.Decide(schedule, DateTime.UtcNow,
            alreadyRecording: true, canRecord: true, out var autoStop);

        Assert.Equal(ScheduledStartAction.DropAlreadyRecording, action);
        Assert.Null(autoStop);
    }

    /// <summary>Nothing connected: the miss is announced (DropNothingConnected) and the
    /// schedule is not left armed for whatever connects next.</summary>
    [Fact]
    public void ADueScheduleWithNothingConnectedIsDroppedNotDeferred()
    {
        var schedule = RecordingSchedule.Compute(TimeSpan.Zero, TimeSpan.Zero, DateTime.UtcNow);

        var action = ScheduledRecordingTick.Decide(schedule, DateTime.UtcNow,
            alreadyRecording: false, canRecord: false, out var autoStop);

        Assert.Equal(ScheduledStartAction.DropNothingConnected, action);
        Assert.Null(autoStop);
    }

    /// <summary>No schedule armed at all: the ordinary every-tick case, which must cost
    /// nothing and change nothing.</summary>
    [Fact]
    public void AnUnarmedTickIsAPlainWait()
    {
        var action = ScheduledRecordingTick.Decide(null, DateTime.UtcNow,
            alreadyRecording: false, canRecord: true, out var autoStop);

        Assert.Equal(ScheduledStartAction.Wait, action);
        Assert.Null(autoStop);
    }

    /// <summary>An armed schedule stays armed across any number of early ticks: the exact
    /// deadline itself is the first tick that starts, not one a moment sooner.</summary>
    [Fact]
    public void TheExactDeadlineIsTheFirstStartingTick()
    {
        var now = DateTime.UtcNow;
        var schedule = RecordingSchedule.Compute(TimeSpan.FromSeconds(10), TimeSpan.Zero, now);

        Assert.Equal(ScheduledStartAction.Wait,
            ScheduledRecordingTick.Decide(schedule, now.AddSeconds(10).AddMilliseconds(-1),
                alreadyRecording: false, canRecord: true, out _));
        Assert.Equal(ScheduledStartAction.Start,
            ScheduledRecordingTick.Decide(schedule, now + TimeSpan.FromSeconds(10),
                alreadyRecording: false, canRecord: true, out _));
    }
}
