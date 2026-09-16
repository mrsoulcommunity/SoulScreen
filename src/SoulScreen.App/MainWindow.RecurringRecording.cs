using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SoulScreen.App.Logic;

namespace SoulScreen.App;

/// <summary>
/// Recurring recording: "record every weekday at 09:00", checked on the metrics tick and
/// started the same way a one-shot scheduled recording is. Kept apart from
/// <see cref="RecordingSchedule"/>, which fires once - a rule here never runs out, so it is
/// re-armed for its next occurrence immediately after firing (or being edited), rather than
/// being disarmed like a one-shot.
/// </summary>
public partial class MainWindow
{
    /// <summary>When each enabled rule is next due, in local time. Missing or stale entries
    /// (a rule just added, or edited since) are recomputed from "now" on the next tick.</summary>
    private readonly Dictionary<string, DateTime> _recurringNextFireLocal = [];

    /// <summary>Rule ids whose inline editor is open. Cleared only when a rule is removed;
    /// left alone across an ordinary refresh so an edit in progress is not folded away by the
    /// half-second tick redrawing something else on the page.</summary>
    private readonly HashSet<string> _recurringEditorsOpen = [];

    /// <summary>One row of the recurring-recording list: wraps a <see cref="RecurringRecordingRule"/>
    /// that lives in <c>_settings.RecurringRecordings</c>, so an edit here is already the
    /// settings change - there is nothing to copy back.</summary>
    private sealed class RecurringRuleView : INotifyPropertyChanged
    {
        public required RecurringRecordingRule Rule { get; init; }
        public required MainWindow Owner { get; init; }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>Marks the rule dirty so the tick recomputes its next occurrence, then
        /// saves and redraws this row's summary text.</summary>
        private void Changed()
        {
            Owner._recurringNextFireLocal.Remove(Rule.Id);
            Owner._settings.Save();
            Raise(nameof(DaysSummary));
            Raise(nameof(TimeSummary));
        }

        public bool Enabled
        {
            get => Rule.Enabled;
            set { if (Rule.Enabled == value) return; Rule.Enabled = value; Changed(); Raise(nameof(Enabled)); }
        }

        public string DaysSummary => RecurringRecordingSchedule.Describe(Rule.Days);

        public string TimeSummary
        {
            get
            {
                var time = Rule.StartTime.ToString("HH:mm", CultureInfo.InvariantCulture);
                var duration = Rule.DurationMinutes > 0 ? $" for {Rule.DurationMinutes} min" : " until stopped";
                var next = Owner._recurringNextFireLocal.TryGetValue(Rule.Id, out var when) && Rule.Enabled
                    ? $" · next {FormatNext(when)}"
                    : "";
                return $"{time}{duration}{next}";
            }
        }

        private static string FormatNext(DateTime local)
        {
            var days = (local.Date - DateTime.Now.Date).Days;
            var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
            return days switch
            {
                0 => $"today {time}",
                1 => $"tomorrow {time}",
                _ => $"{local:ddd} {time}",
            };
        }

        public string SwitchAutomationName => $"{(Enabled ? "Disable" : "Enable")} the {DaysSummary} {TimeSummary} schedule";
        public string RemoveAutomationName => $"Remove the {DaysSummary} {TimeSummary} schedule";

        public bool IncludesSunday { get => Includes(DayOfWeek.Sunday); set => SetDay(DayOfWeek.Sunday, value); }
        public bool IncludesMonday { get => Includes(DayOfWeek.Monday); set => SetDay(DayOfWeek.Monday, value); }
        public bool IncludesTuesday { get => Includes(DayOfWeek.Tuesday); set => SetDay(DayOfWeek.Tuesday, value); }
        public bool IncludesWednesday { get => Includes(DayOfWeek.Wednesday); set => SetDay(DayOfWeek.Wednesday, value); }
        public bool IncludesThursday { get => Includes(DayOfWeek.Thursday); set => SetDay(DayOfWeek.Thursday, value); }
        public bool IncludesFriday { get => Includes(DayOfWeek.Friday); set => SetDay(DayOfWeek.Friday, value); }
        public bool IncludesSaturday { get => Includes(DayOfWeek.Saturday); set => SetDay(DayOfWeek.Saturday, value); }

        private bool Includes(DayOfWeek day) => RecurringRecordingSchedule.Includes(Rule.Days, day);

        private void SetDay(DayOfWeek day, bool included)
        {
            var updated = RecurringRecordingSchedule.With(Rule.Days, day, included);
            if (updated == Rule.Days) return;
            Rule.Days = updated;
            Changed();
        }

        public string TimeText
        {
            get => Rule.StartTime.ToString("HH:mm", CultureInfo.InvariantCulture);
            set
            {
                if (!TimeOnly.TryParseExact(value.Trim(), "HH:mm", CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var parsed)
                    && !TimeOnly.TryParse(value.Trim(), CultureInfo.InvariantCulture, out parsed))
                {
                    ValidationMessage = "Enter a time as HH:mm, e.g. 09:00 or 21:30.";
                    Raise(nameof(ValidationMessage));
                    Raise(nameof(ValidationVisibility));
                    Raise(nameof(TimeText)); // snaps the box back to the last valid value
                    return;
                }

                ValidationMessage = null;
                Raise(nameof(ValidationVisibility));
                if (Rule.StartTime == parsed) return;
                Rule.StartTime = parsed;
                Changed();
            }
        }

        public string DurationText
        {
            get => Rule.DurationMinutes.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) || minutes < 0)
                {
                    ValidationMessage = "Duration must be zero (until stopped) or a whole number of minutes.";
                    Raise(nameof(ValidationMessage));
                    Raise(nameof(ValidationVisibility));
                    Raise(nameof(DurationText));
                    return;
                }

                ValidationMessage = null;
                Raise(nameof(ValidationVisibility));
                if (Rule.DurationMinutes == minutes) return;
                Rule.DurationMinutes = minutes;
                Changed();
            }
        }

        public string? ValidationMessage { get; private set; }
        public Visibility ValidationVisibility => ValidationMessage is null ? Visibility.Collapsed : Visibility.Visible;

        public Visibility EditorVisibility => Owner._recurringEditorsOpen.Contains(Rule.Id) ? Visibility.Visible : Visibility.Collapsed;
        public string EditButtonLabel => Owner._recurringEditorsOpen.Contains(Rule.Id) ? "Done" : "Edit";

        public void RaiseEditorChanged()
        {
            Raise(nameof(EditorVisibility));
            Raise(nameof(EditButtonLabel));
        }
    }

    /// <summary>Rebuilds the list shown in Settings. Called when a rule is added or removed,
    /// and whenever settings are (re)loaded - never on an ordinary property edit, which would
    /// otherwise recreate the row a text box mid-edit is bound to.</summary>
    private void RefreshRecurringRecordingsList()
    {
        if (RecurringRecordingsList is null) return;
        var views = _settings.RecurringRecordings.Select(rule => new RecurringRuleView { Rule = rule, Owner = this }).ToList();
        RecurringRecordingsList.ItemsSource = views;
        NoRecurringText.Visibility = views.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAddRecurringRecording(object sender, RoutedEventArgs e)
    {
        var rule = new RecurringRecordingRule { Days = RecordingDays.Weekdays, StartTime = new TimeOnly(9, 0) };
        _settings.RecurringRecordings.Add(rule);
        _settings.Save();
        _recurringEditorsOpen.Add(rule.Id); // open for editing straight away - it was just created blank
        RefreshRecurringRecordingsList();
        ShowToast("Schedule added", "");
    }

    private void OnRemoveRecurringRecording(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RecurringRuleView view) return;
        _settings.RecurringRecordings.Remove(view.Rule);
        _settings.Save();
        _recurringEditorsOpen.Remove(view.Rule.Id);
        _recurringNextFireLocal.Remove(view.Rule.Id);
        RefreshRecurringRecordingsList();
        ShowToast("Schedule removed", "");
    }

    private void OnToggleRecurringRuleEditor(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RecurringRuleView view) return;
        if (!_recurringEditorsOpen.Add(view.Rule.Id)) _recurringEditorsOpen.Remove(view.Rule.Id);
        view.RaiseEditorChanged();
    }

    /// <summary>
    /// The metrics tick's recurring-recording duty. Half-second granularity is finer than the
    /// feature needs, but the check itself is a handful of comparisons against a short list -
    /// cheaper than the tick's own bitrate maths - so there is no reason to run it on a
    /// separate, slower timer.
    /// </summary>
    private void CheckRecurringRecordings()
    {
        if (_settings.RecurringRecordings.Count == 0) return;
        var now = DateTime.Now;

        foreach (var rule in _settings.RecurringRecordings)
        {
            if (!rule.Enabled)
            {
                _recurringNextFireLocal.Remove(rule.Id);
                continue;
            }

            if (!_recurringNextFireLocal.TryGetValue(rule.Id, out var due))
            {
                if (RecurringRecordingSchedule.NextOccurrence(rule.Days, rule.StartTime, now) is not { } computed) continue;
                _recurringNextFireLocal[rule.Id] = computed;
                continue;
            }

            if (now < due) continue;

            // Due now. Re-armed for the following occurrence immediately, whether or not this
            // firing actually starts a recording - a phone that is not connected right now must
            // not leave the rule stuck re-trying every tick until one turns up.
            if (RecurringRecordingSchedule.NextOccurrence(rule.Days, rule.StartTime, now) is { } next)
                _recurringNextFireLocal[rule.Id] = next;
            else
                _recurringNextFireLocal.Remove(rule.Id);

            FireRecurringRecording(rule);
        }
    }

    private void FireRecurringRecording(RecurringRecordingRule rule)
    {
        if (RecordButton.IsChecked == true) return; // already recording, by hand or by another rule/schedule
        if (!RecordButton.IsEnabled)
        {
            _log.Info($"a recurring recording ({RecurringRecordingSchedule.Describe(rule.Days)} {rule.StartTime:HH\\:mm}) " +
                      "was due but nothing is connected; skipped");
            return;
        }

        RecordButton.IsChecked = true; // OnRecordChanged does the rest
        _recordingTimer = null;
        if (rule.DurationMinutes > 0)
        {
            _recordingTimer = new RecordingTimer();
            _recordingTimer.Arm(TimeSpan.FromMinutes(rule.DurationMinutes), DateTime.UtcNow);
        }
        ShowToast("Recurring recording started", "\uE9A9");
        UpdateRecordingPill();
    }
}
