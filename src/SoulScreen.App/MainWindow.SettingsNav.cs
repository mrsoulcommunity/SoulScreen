using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SoulScreen.App.Controls;
using SoulScreen.App.Logic;

namespace SoulScreen.App;

/// <summary>
/// Finding one's way around the settings: a sidebar of sections when the window is wide
/// enough to spare the room, a search field that narrows every card to the rows that match,
/// and the accent colour picker.
/// </summary>
public partial class MainWindow
{
    /// <summary>Panel width from which the sidebar is shown beside the cards.</summary>
    private const double SettingsSidebarBreakpoint = 700;

    /// <summary>A titled group of settings: its heading, its card, and the rows inside it.</summary>
    private sealed record SettingsSection(
        string Title, string Glyph, Brush IconBrush, string Keywords,
        TextBlock Heading, Border Card, IReadOnlyList<SettingRow> Rows);

    private List<SettingsSection> _settingsSections = [];

    /// <summary>Set while the sidebar selection is being moved to follow the scroll position,
    /// so that moving it does not scroll the page in turn.</summary>
    private bool _syncingSettingsNav;

    /// <summary>Set when a sidebar click has scrolled the page, so the scroll it causes does
    /// not move the selection off the section that was clicked - a short last section can never
    /// reach the top of the page.</summary>
    private bool _settingsScrollFromNav;

    /// <summary>How each section's heading appears in the sidebar.</summary>
    private static readonly Dictionary<string, (string Title, string Glyph, string Brush, string Keywords)> SectionLooks = new()
    {
        ["RECEIVER"] = ("Receiver", "", "NavBlue", "airplay name port resolution"),
        ["APPEARANCE"] = ("Appearance", "", "NavPurple", "theme dark light colour color accent"),
        ["PICTURE"] = ("Picture", "", "NavGreen", "video display screen"),
        ["AUDIO"] = ("Audio", "", "NavPink", "sound speakers"),
        ["WHEN A PHONE CONNECTS"] = ("When a phone connects", "", "NavOrange", "automatic connect session"),
        ["PRIVACY"] = ("Privacy", "\uEA18", "NavRed", "privacy ask approve allow block trust permission security"),
        ["SCREENSHOTS AND RECORDINGS"] = ("Captures", "", "NavIndigo", "screenshot recording capture folder"),
        ["WINDOW AND SYSTEM"] = ("Window and system", "", "NavGraphite", "tray startup notification"),
        ["RECENT IPHONES"] = ("Recent iPhones", "", "NavTeal", "history devices phones"),
        ["ABOUT"] = ("About", "", "NavGray", "version log folder data reset shortcuts fairplay ffmpeg identity"),
    };

    private void InitialiseSettingsNav()
    {
        var sectionTitleStyle = FindResource("SectionTitle");
        var children = SettingsContent.Children.OfType<FrameworkElement>().ToList();

        for (var i = 0; i < children.Count - 1; i++)
        {
            if (children[i] is not TextBlock heading || !ReferenceEquals(heading.Style, sectionTitleStyle)) continue;
            if (children[i + 1] is not Border card) continue;

            var look = SectionLooks.TryGetValue(heading.Text, out var known)
                ? known
                : (Title: heading.Text, Glyph: "", Brush: "NavGray", Keywords: "");

            var rows = card.Child is Panel panel ? panel.Children.OfType<SettingRow>().ToList() : [];
            _settingsSections.Add(new SettingsSection(look.Title, look.Glyph, (Brush)FindResource(look.Brush),
                look.Keywords, heading, card, rows));
        }

        SettingsNav.ItemsSource = _settingsSections;
        SettingsPanel.SizeChanged += (_, _) => UpdateSettingsLayout();
        BuildAccentSwatches();
    }

    // ------------------------------------------------------------------ layout

    /// <summary>Shows the sidebar when there is room for it, and moves the search field to
    /// wherever it belongs in that layout.</summary>
    private void UpdateSettingsLayout()
    {
        if (SettingsPanel.Visibility != Visibility.Visible || SettingsPanel.ActualWidth <= 0) return;

        var wide = SettingsPanel.ActualWidth >= SettingsSidebarBreakpoint;
        SettingsSidebar.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        HeaderSearchHost.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        MoveSettingsSearch(wide ? SidebarSearchHost : HeaderSearchHost);
        UpdateSettingsHeading();
    }

    private void MoveSettingsSearch(Border host)
    {
        if (ReferenceEquals(SettingsSearchBox.Parent, host)) return;

        var hadFocus = SettingsSearchBox.IsKeyboardFocusWithin;
        var caret = SettingsSearchBox.CaretIndex;
        if (SettingsSearchBox.Parent is Border previous) previous.Child = null;
        host.Child = SettingsSearchBox;

        if (!hadFocus) return;
        SettingsSearchBox.Focus();
        SettingsSearchBox.CaretIndex = caret;
    }

    /// <summary>With the sidebar showing, the page is titled by the section in view, as System
    /// Settings titles it; without one, simply "Settings".</summary>
    private void UpdateSettingsHeading()
    {
        var wide = SettingsSidebar.Visibility == Visibility.Visible;
        SettingsHeading.Text = wide && SettingsNav.SelectedItem is SettingsSection section ? section.Title : "Settings";
    }

    // ------------------------------------------------------------------ navigation

    private void OnSettingsNavChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSettingsHeading();
        if (_syncingSettingsNav || SettingsNav.SelectedItem is not SettingsSection section) return;
        ScrollToSection(section);
    }

    private void ScrollToSection(SettingsSection section)
    {
        if (section.Heading.Visibility != Visibility.Visible) return;

        var top = OffsetInSettings(section.Heading);
        if (top is null) return;

        var target = Math.Clamp(top.Value - 6, 0, SettingsScroller.ScrollableHeight);
        if (Math.Abs(target - SettingsScroller.VerticalOffset) < 0.5) return;

        _settingsScrollFromNav = true;
        SettingsScroller.ScrollToVerticalOffset(target);
    }

    private void OnSettingsScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 && e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0) return;

        if (_settingsScrollFromNav)
        {
            _settingsScrollFromNav = false;
            return;
        }

        var current = SectionInView();
        if (current is null || ReferenceEquals(current, SettingsNav.SelectedItem)) return;

        _syncingSettingsNav = true;
        try { SettingsNav.SelectedItem = current; }
        finally { _syncingSettingsNav = false; }
    }

    /// <summary>The last visible section whose heading has reached the top of the page - or
    /// the last one of all once the page is scrolled to its end.</summary>
    private SettingsSection? SectionInView()
    {
        var visible = _settingsSections.Where(s => s.Heading.Visibility == Visibility.Visible).ToList();
        if (visible.Count == 0) return null;

        if (SettingsScroller.ScrollableHeight > 0 && SettingsScroller.VerticalOffset >= SettingsScroller.ScrollableHeight - 1)
            return visible[^1];

        SettingsSection? current = visible[0];
        foreach (var section in visible)
        {
            if (OffsetInSettings(section.Heading) is not { } top) continue;
            if (top - 30 <= SettingsScroller.VerticalOffset) current = section;
        }
        return current;
    }

    private double? OffsetInSettings(FrameworkElement element)
    {
        try
        {
            return element.TransformToAncestor(SettingsContent).Transform(new Point(0, 0)).Y;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Opens the settings at one section, as a link from elsewhere in the app does.</summary>
    /// <param name="heading">The section's heading as the panel spells it, e.g. "PRIVACY".</param>
    private void ShowSettingsSection(string heading)
    {
        if (IsDoctorOpen) CloseDoctor();
        if (IsWelcomeOpen) CloseWelcome();
        if (SettingsButton.IsChecked != true) SettingsButton.IsChecked = true;
        else if (SettingsSearchBox.Text.Length > 0) SettingsSearchBox.Text = string.Empty;

        // After the panel has been laid out, and after its own scroll back to the top.
        Dispatcher.BeginInvoke(() =>
        {
            if (SettingsPanel.Visibility != Visibility.Visible) return;
            var section = _settingsSections.FirstOrDefault(s => s.Heading.Text == heading);
            if (section is null) return;

            SettingsScroller.UpdateLayout();
            _syncingSettingsNav = true;
            try { SettingsNav.SelectedItem = section; }
            finally { _syncingSettingsNav = false; }
            ScrollToSection(section);
            UpdateSettingsHeading();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ------------------------------------------------------------------ search

    private void OnSettingsSearchChanged(object sender, TextChangedEventArgs e) => ApplySettingsSearch();

    private void OnSettingsSearchKeyDown(object sender, KeyEventArgs e)
    {
        // The first Escape empties the field; the next one, with nothing left to clear, falls
        // through to the window and closes the settings.
        if (e.Key != Key.Escape || SettingsSearchBox.Text.Length == 0) return;
        SettingsSearchBox.Clear();
        e.Handled = true;
    }

    /// <summary>
    /// Narrows every card to the rows whose title, description or section mentions every word
    /// typed. A card with nothing left in it disappears along with its heading and its place in
    /// the sidebar, and the divider rule moves to whichever row is now first.
    /// </summary>
    private void ApplySettingsSearch()
    {
        var terms = SettingsSearchBox.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var anyShown = false;

        foreach (var section in _settingsSections)
        {
            var sectionText = $"{section.Title} {section.Heading.Text} {section.Keywords}";
            SettingRow? first = null;

            foreach (var row in section.Rows)
            {
                var shown = terms.Length == 0 || MatchesAll(terms, $"{row.Title} {row.Detail} {sectionText}");
                row.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
                row.IsFirst = false;
                if (shown) first ??= row;
            }

            if (first is not null) first.IsFirst = true;

            // Sections made of lists rather than rows show whenever the search names them.
            var sectionShown = section.Rows.Count == 0
                ? terms.Length == 0 || MatchesAll(terms, sectionText)
                : first is not null;

            section.Heading.Visibility = sectionShown ? Visibility.Visible : Visibility.Collapsed;
            section.Card.Visibility = sectionShown ? Visibility.Visible : Visibility.Collapsed;
            anyShown |= sectionShown;
        }

        SettingsNoMatch.Visibility = anyShown ? Visibility.Collapsed : Visibility.Visible;
        SettingsNoMatchText.Text = $"No settings match “{SettingsSearchBox.Text.Trim()}”.";

        _syncingSettingsNav = true;
        try
        {
            var visible = _settingsSections.Where(s => s.Heading.Visibility == Visibility.Visible).ToList();
            SettingsNav.ItemsSource = visible;
            SettingsNav.SelectedItem = visible.FirstOrDefault();
        }
        finally
        {
            _syncingSettingsNav = false;
        }

        UpdateSettingsHeading();
        SettingsScroller.ScrollToTop();
    }

    private static bool MatchesAll(IEnumerable<string> terms, string text) =>
        terms.All(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ accent

    private void BuildAccentSwatches()
    {
        var style = (Style)FindResource("AccentSwatch");
        foreach (var accent in Enum.GetValues<AccentColor>())
        {
            var swatch = new RadioButton
            {
                Style = style,
                GroupName = "Accent",
                Tag = accent,
                ToolTip = accent.ToString(),
            };
            AutomationProperties.SetName(swatch, $"{accent} accent colour");
            swatch.Checked += OnAccentSwatchChecked;
            AccentSwatches.Children.Add(swatch);
        }

        RecolourAccentSwatches();
    }

    /// <summary>Paints each swatch in its accent as the current theme shows that accent.</summary>
    private void RecolourAccentSwatches()
    {
        foreach (var swatch in AccentSwatches.Children.OfType<RadioButton>())
        {
            if (swatch.Tag is not AccentColor accent) continue;
            var brush = new SolidColorBrush(ThemeManager.SwatchColor(accent));
            brush.Freeze();
            swatch.Background = brush;
        }
    }

    private void SyncAccentSwatches()
    {
        foreach (var swatch in AccentSwatches.Children.OfType<RadioButton>())
            swatch.IsChecked = swatch.Tag is AccentColor accent && accent == _settings.Accent;
    }

    private void OnAccentSwatchChecked(object sender, RoutedEventArgs e)
    {
        if (_populatingSettings || sender is not RadioButton { Tag: AccentColor accent }) return;
        SetAccent(accent);
    }

    private void SetAccent(AccentColor accent)
    {
        if (_settings.Accent == accent) return;
        _settings.Accent = accent;
        _settings.Save();
        ThemeManager.Apply(_settings.Theme, accent, animate: true);

        _populatingSettings = true;
        try { SyncAccentSwatches(); }
        finally { _populatingSettings = false; }
    }

    private void SetTheme(AppTheme theme)
    {
        if (_settings.Theme == theme) return;
        _settings.Theme = theme;
        _settings.Save();
        ThemeManager.Apply(theme, _settings.Accent, animate: true);

        _populatingSettings = true;
        try
        {
            ThemeSystem.IsChecked = theme == AppTheme.System;
            ThemeDark.IsChecked = theme == AppTheme.Dark;
            ThemeLight.IsChecked = theme == AppTheme.Light;
        }
        finally
        {
            _populatingSettings = false;
        }
    }
}
