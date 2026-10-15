using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using OView.Core.Models;
using OView.Presentation.Panel;
using Binding = System.Windows.Data.Binding;
using Color = System.Windows.Media.Color;

namespace OView.Tray.Popup;

/// <summary>
/// Draws one usage meter (session or weekly): the figure's own words, the band it falls in,
/// and how wide the fill runs. The panel's three colours are the only part of it that is this
/// head's own.
/// </summary>
internal static class PopupMeter
{
    private static readonly Color Green = Color.FromRgb(64, 200, 110);
    private static readonly Color Amber = Color.FromRgb(240, 170, 40);
    private static readonly Color Red = Color.FromRgb(232, 72, 72);

    public static void Populate(TextBlock pctText, Grid bar, Border fill, PanelMeterView meter)
    {
        BindingOperations.ClearBinding(fill, FrameworkElement.WidthProperty);
        pctText.Text = meter.Value;

        if (meter.FillPercent is not { } p)
        {
            fill.Width = 0;
            return;
        }

        fill.Background = new SolidColorBrush(meter.Band switch
        {
            UsageLevel.Critical => Red,
            UsageLevel.Warning => Amber,
            _ => Green,
        });
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
