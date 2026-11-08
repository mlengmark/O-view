using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OView.App.Rendering;
using OView.Presentation.Panel;
using Brush = System.Windows.Media.Brush;
using Rectangle = System.Windows.Shapes.Rectangle;
using Size = System.Windows.Size;

namespace OView.Tray.Popup;

/// <summary>
/// Draws the 31-day chart on the Canvas: one bar per day from a baseline hairline, older days
/// fading out and today in the accent, a hairline at each week start with its date written
/// across, and <c>today</c> at the right-hand end. Days before install draw no bar (rule 6).
///
/// <para>Everything is read from the <see cref="PanelLocalChartView"/> for the chosen measure, so
/// what is left here is turning fractions into pixels against this canvas and the hover cards'
/// chrome.</para>
/// </summary>
internal sealed class PopupGraph(FrameworkElement owner, Canvas host)
{
    /// <summary>Room left above the tallest bar, so it does not stand against the title's line.</summary>
    private const double HeadroomDip = 4;

    /// <summary>A week's date starts this far right of its line, so the two do not touch.</summary>
    private const double WeekLabelGapDip = 3;

    /// <summary>From the top of the axis band to the top of its text.</summary>
    private const double AxisLabelTopDip = 2;

    /// <summary>How far in from the right edge <c>today</c> ends.</summary>
    private const double TodayLabelInsetDip = 2;

    /// <summary>The smallest gap a week's date may leave before <c>today</c>.</summary>
    private const double AxisLabelClearanceDip = 6;

    /// <summary>The bars' width below which they would stop reading as bars on a very narrow panel.</summary>
    private const double MinBarWidthDip = 2;

    public void Draw(PanelLocalChartView chart)
    {
        host.Children.Clear();
        if (chart.Columns.Count == 0)
        {
            return;
        }

        var width = host.ActualWidth > 0 ? host.ActualWidth : PanelMetrics.ContentWidthDip;

        // Measured from the canvas rather than fixed at the natural 96: a display too short for
        // the panel gets a shorter chart (PanelDensity), and the axis band keeps its height.
        var barArea = Math.Max(PanelMetrics.ChartAxisBandDip, host.Height - PanelMetrics.ChartAxisBandDip);
        var slot = width / chart.Columns.Count;

        AddHairline(0, barArea, width, 1);
        AddBars(chart.Columns, slot, barArea);
        AddAxis(chart, slot, barArea, width);
    }

    private void AddBars(IReadOnlyList<PanelGraphColumnView> columns, double slot, double barArea)
    {
        var bars = (Brush)owner.FindResource("Series1");
        var today = (Brush)owner.FindResource("AccentBg");
        var last = columns.Count - 1;

        for (var i = 0; i <= last; i++)
        {
            var column = columns[i];
            if (!column.HasBar)
            {
                continue;
            }

            var height = Math.Max(PanelMetrics.GraphMinBarHeightDip, (barArea - HeadroomDip) * column.Height);
            var age = last == 0 ? 1 : i / (double)last;
            var bar = new Rectangle
            {
                Width = Math.Max(MinBarWidthDip, slot - 2 * PanelMetrics.ChartBarInsetDip),
                Height = height,
                Fill = i == last ? today : bars,
                Opacity = PanelMetrics.ChartOldestBarOpacity
                          + (PanelMetrics.ChartNewestBarOpacity - PanelMetrics.ChartOldestBarOpacity) * age,
                RadiusX = PanelMetrics.ChartBarRadiusDip,
                RadiusY = PanelMetrics.ChartBarRadiusDip,

                // Figure first, date beneath, the panel's shared hover card. A bare string here
                // renders as system chrome, a pale rectangle on an otherwise rounded panel.
                ToolTip = HoverCard.Figure(owner, column.CardFigure, column.CardCaption),
            };
            HoverCard.ApplyTiming(bar);
            Canvas.SetLeft(bar, i * slot + PanelMetrics.ChartBarInsetDip);
            Canvas.SetTop(bar, barArea - height);
            host.Children.Add(bar);
        }
    }

    /// <summary>
    /// The week lines over the bars, with their dates, and <c>today</c> at the end.
    ///
    /// <para><c>today</c> is placed first because it always shows: a Monday among the last few
    /// days keeps its line but drops its date rather than running into the label, or past the
    /// edge of the panel, which is what its width would otherwise do.</para>
    /// </summary>
    private void AddAxis(PanelLocalChartView chart, double slot, double barArea, double width)
    {
        var top = barArea + AxisLabelTopDip;
        var todayLabel = AddLabel(chart.TodayLabel, "AccentInk", top);
        var todayLeft = width - TodayLabelInsetDip - todayLabel.DesiredSize.Width;
        Canvas.SetLeft(todayLabel, todayLeft);

        foreach (var mark in chart.WeekMarks)
        {
            var x = mark.Column * slot;
            AddHairline(x, 0, 1, barArea);

            var label = AddLabel(mark.DateLabel, "TextMuted", top);
            var left = x + WeekLabelGapDip;
            if (left + label.DesiredSize.Width > todayLeft - AxisLabelClearanceDip)
            {
                host.Children.Remove(label);
                continue;
            }

            Canvas.SetLeft(label, left);
        }
    }

    /// <summary>A label on the axis band, measured so the caller can place it by its width.</summary>
    private TextBlock AddLabel(string text, string brushKey, double top)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = PanelMetrics.ChartWeekLabelFontSize,
            Foreground = (Brush)owner.FindResource(brushKey),
        };

        // Parent first, so the font properties measured are the inherited ones.
        host.Children.Add(label);
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetTop(label, top);
        return label;
    }

    /// <summary>
    /// A one-pixel line as a snapped rectangle: a <c>Line</c> at a fractional position, and a
    /// slot is rarely a whole number, spreads over two pixels at half strength.
    /// </summary>
    private void AddHairline(double x, double y, double width, double height)
    {
        var line = new Rectangle
        {
            Width = width,
            Height = height,
            Fill = (Brush)owner.FindResource("PanelBorder"),
            SnapsToDevicePixels = true,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(line, x);
        Canvas.SetTop(line, y);
        host.Children.Add(line);
    }
}
