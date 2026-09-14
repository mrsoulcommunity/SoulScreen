using System.Windows;
using System.Windows.Controls;

namespace SoulScreen.App.Controls;

/// <summary>
/// One row of a settings card: a title, an optional line of detail, and the control that
/// sets the option. The template lives in Theme.xaml; this class only declares the
/// properties the template binds.
/// </summary>
public sealed class SettingRow : ContentControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SettingRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(
        nameof(Detail), typeof(string), typeof(SettingRow), new PropertyMetadata(null));

    /// <summary>The first row of a card draws no divider above itself.</summary>
    public static readonly DependencyProperty IsFirstProperty = DependencyProperty.Register(
        nameof(IsFirst), typeof(bool), typeof(SettingRow), new PropertyMetadata(false));

    /// <summary>Puts the control under the text instead of beside it, for wide controls.</summary>
    public static readonly DependencyProperty StackedProperty = DependencyProperty.Register(
        nameof(Stacked), typeof(bool), typeof(SettingRow), new PropertyMetadata(false));

    static SettingRow()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SettingRow), new FrameworkPropertyMetadata(typeof(SettingRow)));
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Detail
    {
        get => (string?)GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    public bool IsFirst
    {
        get => (bool)GetValue(IsFirstProperty);
        set => SetValue(IsFirstProperty, value);
    }

    public bool Stacked
    {
        get => (bool)GetValue(StackedProperty);
        set => SetValue(StackedProperty, value);
    }
}
