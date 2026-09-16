using System.IO;
using System.Windows;
using Microsoft.Win32;
using SoulScreen.App.Logic;

namespace SoulScreen.App;

/// <summary>
/// Watermark settings: choosing an image, its corner, size and opacity. The actual drawing
/// lives with the rest of the screenshot pipeline in <see cref="MainWindow.ComposeWatermark"/>
/// (<c>MainWindow.Markup.cs</c>), since a screenshot is composed in one place regardless of
/// what puts pixels onto it.
/// </summary>
public partial class MainWindow
{
    /// <summary>The three sizes offered in the UI, as fractions of the canvas' shortest side.
    /// A free slider would let someone drag in a value that reads as "too small to see" or
    /// "covers half the picture" with no cue either way; three named choices do not.</summary>
    private const double SizeSmall = 0.08;
    private const double SizeMedium = 0.14;
    private const double SizeLarge = 0.22;

    private const double OpacitySubtle = 0.35;
    private const double OpacityMedium = 0.65;
    private const double OpacitySolid = 1.0;

    private void PopulateWatermarkSettingsForm()
    {
        var settings = _settings.Watermark;
        WatermarkEnabledCheck.IsChecked = settings.Enabled;
        WatermarkImageBox.Text = settings.ImagePath ?? "";

        var configured = !string.IsNullOrWhiteSpace(settings.ImagePath);
        WatermarkCornerRow.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
        WatermarkSizeRow.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
        WatermarkOpacityRow.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;

        var cornerButton = settings.Corner switch
        {
            WatermarkCorner.TopLeft => WatermarkTopLeft,
            WatermarkCorner.TopRight => WatermarkTopRight,
            WatermarkCorner.BottomLeft => WatermarkBottomLeft,
            WatermarkCorner.Center => WatermarkCenter,
            _ => WatermarkBottomRight,
        };
        cornerButton.IsChecked = true;

        // The three presets round-trip exactly; anything else (a hand-edited settings.json)
        // shows as the nearest preset rather than nothing selected.
        var sizeButton = settings.Scale <= (SizeSmall + SizeMedium) / 2 ? WatermarkSizeSmall
            : settings.Scale <= (SizeMedium + SizeLarge) / 2 ? WatermarkSizeMedium
            : WatermarkSizeLarge;
        sizeButton.IsChecked = true;

        var opacityButton = settings.Opacity <= (OpacitySubtle + OpacityMedium) / 2 ? WatermarkOpacitySubtle
            : settings.Opacity <= (OpacityMedium + OpacitySolid) / 2 ? WatermarkOpacityMedium
            : WatermarkOpacitySolid;
        opacityButton.IsChecked = true;
    }

    private void OnWatermarkEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_populatingSettings) return;

        if (WatermarkEnabledCheck.IsChecked == true && string.IsNullOrWhiteSpace(_settings.Watermark.ImagePath))
        {
            // Nothing to draw yet: send the user straight to picking a file rather than
            // silently leaving "enabled" checked with no image, which Normalise would just
            // turn back off on the next save anyway.
            WatermarkEnabledCheck.IsChecked = false;
            OnChooseWatermarkImage(sender, e);
            return;
        }

        _settings.Watermark.Enabled = WatermarkEnabledCheck.IsChecked == true;
        _settings.Save();
        PopulateWatermarkSettingsForm();
    }

    private void OnChooseWatermarkImage(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a watermark image",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (Path.GetDirectoryName(_settings.Watermark.ImagePath) is { } directory && Directory.Exists(directory))
            dialog.InitialDirectory = directory;

        if (dialog.ShowDialog(this) != true) return;

        _settings.Watermark.ImagePath = dialog.FileName;
        _settings.Watermark.Enabled = true;
        _settings.Save();
        PopulateWatermarkSettingsForm();
        ShowToast("Watermark set", "\uE898");
    }

    private void OnWatermarkCornerChanged(object sender, RoutedEventArgs e)
    {
        if (_populatingSettings) return;
        _settings.Watermark.Corner =
            WatermarkTopLeft.IsChecked == true ? WatermarkCorner.TopLeft
            : WatermarkTopRight.IsChecked == true ? WatermarkCorner.TopRight
            : WatermarkBottomLeft.IsChecked == true ? WatermarkCorner.BottomLeft
            : WatermarkCenter.IsChecked == true ? WatermarkCorner.Center
            : WatermarkCorner.BottomRight;
        _settings.Save();
    }

    private void OnWatermarkSizeChanged(object sender, RoutedEventArgs e)
    {
        if (_populatingSettings) return;
        _settings.Watermark.Scale =
            WatermarkSizeSmall.IsChecked == true ? SizeSmall
            : WatermarkSizeLarge.IsChecked == true ? SizeLarge
            : SizeMedium;
        _settings.Save();
    }

    private void OnWatermarkOpacityChanged(object sender, RoutedEventArgs e)
    {
        if (_populatingSettings) return;
        _settings.Watermark.Opacity =
            WatermarkOpacitySubtle.IsChecked == true ? OpacitySubtle
            : WatermarkOpacitySolid.IsChecked == true ? OpacitySolid
            : OpacityMedium;
        _settings.Save();
    }
}
