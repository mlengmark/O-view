using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using OView.App.Rendering;
using OView.Core.Models;
using OView.Presentation.Panel;
using Binding = System.Windows.Data.Binding;
using Color = System.Windows.Media.Color;

namespace OView.Tray.Popup;

/// <summary>
/// Draws one usage meter (session or weekly): the figure's own words, the band it falls in,
/// and how wide the fill runs. The band's colour comes from the tray icon's shared rule
/// (<see cref="TrayIconGeometry.LevelColor"/>), theme-aware, exactly as the Linux panel's
/// does; this head used to restate the dark-theme shades and so painted the same reading a
/// different colour in the light theme.
/// </summary>
internal static class PopupMeter
{
    /// <param name="light">Whether the panel is in its light theme, which takes the darker shades.</param>
    public static void Populate(TextBlock pctText, Grid bar, Border fill, PanelMeterView meter, bool light)
    {
        BindingOperations.ClearBinding(fill, FrameworkElement.WidthProperty);
        pctText.Text = meter.Value;

        if (meter.FillPercent is not { } p)
        {
            fill.Width = 0;
            return;
        }

        var band = TrayIconGeometry.LevelColor(meter.Band ?? UsageLevel.Normal, lightTaskbar: light);
        fill.Background = new SolidColorBrush(Color.FromArgb(band.A, band.R, band.G, band.B));
        // Fill width tracks the bar's laid-out width × percent.
        fill.SetBinding(FrameworkElement.WidthProperty, new Binding(nameof(bar.ActualWidth))
        {
            Source = bar,
            Converter = new PercentWidthConverter(),
            ConverterParameter = p,
        });
    }
}

/// <summary>Bar fill width = track ActualWidth × percent.</summary>
internal sealed class PercentWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double actual && parameter is int percent ? Math.Max(percent == 0 ? 0.0 : 4.0, actual * percent / 100.0) : 0.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
