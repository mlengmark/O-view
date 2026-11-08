using System.Windows;
using System.Windows.Controls;
using OView.App.Rendering;
using OView.Presentation.Panel;
using Brush = System.Windows.Media.Brush;

namespace OView.Tray.Popup;

/// <summary>
/// Draws a bar for each model with a weekly limit of its own, under the weekly meter.
///
/// <para>The session and weekly meters are fixed markup, because there is always exactly one of
/// each. How many of these there are comes from the data, so they are built here, with the same
/// sizes as the markup and filled through the same <see cref="PopupMeter"/>. With none the host
/// has no children, so the panel is laid out exactly as it was before this existed.</para>
///
/// <para><b>The space round each row is the density's</b>, the same as the other meters'
/// (<see cref="PopupPlanBlock.MeterMargin"/>). The fit changes density after the rows are built,
/// so it is reapplied to rows already in place rather than only read while building them, or a
/// compacted panel would keep these rows at their natural spacing.</para>
/// </summary>
internal sealed class PopupModelMeters(FrameworkElement owner, StackPanel host)
{
    private Thickness _margin = PopupPlanBlock.MeterMargin(PanelDensity.Normal);

    /// <param name="light">Whether the panel is in its light theme, which takes the darker shades.</param>
    public void Populate(IReadOnlyList<PanelMeterView> meters, bool light)
    {
        host.Children.Clear();

        foreach (var meter in meters)
        {
            host.Children.Add(Row(meter, light));
        }
    }

    public void ApplyDensity(PanelDensity density)
    {
        _margin = PopupPlanBlock.MeterMargin(density);

        foreach (FrameworkElement row in host.Children)
        {
            row.Margin = _margin;
        }
    }

    private StackPanel Row(PanelMeterView meter, bool light)
    {
        var heading = new Grid();
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Trimmed, because the model's name is whatever Claude Code cached: a long one gives up
        // its tail rather than being drawn over the figure.
        heading.Children.Add(new TextBlock
        {
            Text = meter.Label,
            FontSize = PanelMetrics.MeterLabelFontSize,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)owner.FindResource("TextPrimary"),
        });

        var figure = new TextBlock
        {
            FontSize = PanelMetrics.MeterLabelFontSize,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)owner.FindResource("TextSecondary"),
        };
        Grid.SetColumn(figure, 1);
        heading.Children.Add(figure);

        var bar = new Grid { Height = PanelMetrics.MeterBarHeightDip, Margin = new Thickness(0, 5, 0, 4) };
        bar.Children.Add(new Border
        {
            Background = (Brush)owner.FindResource("BarTrack"),
            CornerRadius = new CornerRadius(PanelMetrics.MeterBarRadiusDip),
        });

        // A statement of its own, because the guard on window positions reads a trailing
        // ".Left," in an initializer as a tuple placement.
        var fill = new Border { CornerRadius = new CornerRadius(PanelMetrics.MeterBarRadiusDip) };
        fill.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        bar.Children.Add(fill);

        PopupMeter.Populate(figure, bar, fill, meter, light);

        var reset = new TextBlock
        {
            FontSize = PanelMetrics.MeterResetFontSize,
            Foreground = (Brush)owner.FindResource("TextMuted"),
        };
        PopupText.SetLine(reset, meter.ResetLine ?? "");

        var row = new StackPanel { Margin = _margin };
        row.Children.Add(heading);
        row.Children.Add(bar);
        row.Children.Add(reset);

        return row;
    }
}
