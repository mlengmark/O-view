using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using OView.App.Rendering;
using OView.Presentation.Panel;
using static OView.Linux.Panel.PanelPrimitives;

namespace OView.Linux.Panel;

/// <summary>
/// The 31-day graph (<see cref="PanelGraphView"/>): a bar per day shaded by its intensity
/// within its own plan week, the weekly-reset gridlines over them, a date under every
/// column, and a hover card on each.
///
/// <para>Every height, intensity, date and card line arrives built; this places them.</para>
/// </summary>
internal sealed class PanelGraph(LinuxPanelTheme theme)
{
    /// <summary>Height of the bar strip at the natural density. The Windows chart is 86.</summary>
    private const double NaturalGraphHeight = 60;

    /// <summary>
    /// Column-to-column distance in the graph: a bar and its gutter. Fixed rather than the
    /// panel's width divided by 31, because nothing has a width to divide at build time and a
    /// chart that waited for one would rebuild on every resize. 31 columns then come to
    /// 341 px, inside the 400 px panel at either density.
    /// </summary>
    private const double GraphColumnPitch = 11;

    /// <summary>Width of one day's bar, centred in its column.</summary>
    private const double GraphBarWidth = 9;

    /// <summary>Gap between the foot of the bars and the top of the dates.</summary>
    private const double GraphDateGap = 6;

    /// <summary>
    /// How solid the least intense day of a week is drawn; its busiest day is fully opaque.
    ///
    /// <para>This head carries the within-week intensity as opacity on its one series colour
    /// rather than as a second hue, so the ramp needs no colour the shared palette does not
    /// have, and it reads the same way in both themes, which a fixed light-to-dark ramp
    /// does not. Floored well above nothing: the quietest day of a week is still a day with
    /// usage, and a bar fading towards invisible would read as a day with none.</para>
    /// </summary>
    private const double GraphFaintestDay = 0.45;

    private readonly LinuxPanelTheme _theme = theme;

    /// <summary>
    /// <para><b>A day with no data draws nothing at all</b>, no bar, and no zero-height
    /// stand-in for one, because a flat bar claims a measured day of no usage on a day that
    /// was never measured (rule 6). The column stays, so the dates and the gridlines over it
    /// still line up; it is simply empty.</para>
    ///
    /// <para><b>Why a Canvas rather than the strip of bars this was.</b> A gridline sits at
    /// its true fractional position <em>inside</em> a day column, which no stack of
    /// fixed-width children can express. The columns are laid out at a fixed pitch instead of
    /// against the panel's measured width, so every position here is known before layout,
    /// Avalonia offers no arranged width to divide up at build time, and a chart that waited
    /// for one would be redrawing itself on every resize.</para>
    /// </summary>
    public void AddTo(StackPanel root, PanelGraphView graph, PanelDensity density)
    {
        if (graph.Columns.Count == 0)
        {
            return;
        }

        // Scaled by the density ratio rather than given the Windows chart height: this head
        // ships a 60 px strip, and a display with room must keep exactly that. The date band
        // below it is NOT scaled, the labels are text, and text does not shrink with the
        // spacing.
        var barArea = NaturalGraphHeight * density.GraphScale;
        var dateTop = barArea + GraphDateGap;
        var chart = new Canvas { Width = graph.Columns.Count * GraphColumnPitch };

        // A Canvas has no desired size of its own, so the dates would hang below the chart
        // into whatever follows it unless the height counts them. Taken from the labels
        // themselves rather than a constant: how deep a date reaches on its side is its text's
        // width, which moves with the font the desktop hands us.
        var dateDepth = 0.0;

        for (var i = 0; i < graph.Columns.Count; i++)
        {
            var column = graph.Columns[i];
            var centre = (i + 0.5) * GraphColumnPitch;

            if (column.HasBar)
            {
                var height = Math.Max(PanelMetrics.GraphMinBarHeightDip, barArea * column.Height);
                var bar = new Border
                {
                    Width = GraphBarWidth,
                    Height = height,
                    Background = _theme["Series1"],
                    Opacity = GraphFaintestDay + (1 - GraphFaintestDay) * column.Intensity,
                    CornerRadius = new CornerRadius(1),
                };
                ToolTip.SetTip(bar, GraphTip(column.CardFigure, column.CardCaption));
                Canvas.SetLeft(bar, centre - GraphBarWidth / 2);
                Canvas.SetTop(bar, barArea - height);
                chart.Children.Add(bar);
            }

            dateDepth = Math.Max(dateDepth, AddGraphDate(chart, column, centre, dateTop));
        }

        chart.Height = dateTop + dateDepth;

        // Last, so they sit above the bars: a Canvas paints in child order, and a reset line
        // overdrawn by every bar it crosses is missing exactly where the usage is heaviest.
        foreach (var boundary in graph.Boundaries)
        {
            var x = (boundary.Column + boundary.FractionOfDay) * GraphColumnPitch;
            var line = new Line
            {
                StartPoint = new Point(x, 0),
                EndPoint = new Point(x, barArea + PanelMetrics.GraphBoundaryOverhangDip),
                Stroke = _theme["WarnText"],
                StrokeThickness = 1,
                StrokeDashArray = [PanelMetrics.GraphBoundaryDashOnDip, PanelMetrics.GraphBoundaryDashOffDip],
            };
            ToolTip.SetTip(line, GraphTip(boundary.CardFigure, boundary.CardCaption));
            chart.Children.Add(line);
        }

        var block = new StackPanel { Spacing = 4 };
        block.Children.Add(Text(graph.Heading, 13, _theme["TextPrimary"], FontWeight.SemiBold));
        block.Children.Add(chart);
        root.Children.Add(block);
    }

    /// <summary>
    /// One column's date, turned on its side so 31 of them fit.
    ///
    /// <para>Placed from the measured text rather than from a constant. Avalonia rotates about
    /// the element's centre, so the label is positioned by its centre and the rotation leaves
    /// that point where it was: the horizontal centre is the column's own, which is also the
    /// bar's, so a date cannot drift onto its neighbour. Measuring is what makes the vertical
    /// placement right, after a quarter turn the box's WIDTH is its height on screen.</para>
    /// </summary>
    /// <returns>How far below <paramref name="top"/> the label reaches.</returns>
    private double AddGraphDate(Canvas chart, PanelGraphColumnView column, double centre, double top)
    {
        var label = new TextBlock
        {
            Text = column.DateLabel,
            FontSize = PanelMetrics.GraphLabelFontSize,
            Foreground = _theme["TextMuted"],
            Opacity = column.Recorded ? 1.0 : PanelMetrics.GraphUnrecordedLabelOpacity,
            RenderTransform = new RotateTransform(90),
        };

        label.Measure(Size.Infinity);
        var ink = label.DesiredSize;
        Canvas.SetLeft(label, centre - ink.Width / 2);
        Canvas.SetTop(label, top + (ink.Width - ink.Height) / 2);
        chart.Children.Add(label);

        return ink.Width;
    }

    /// <summary>
    /// A graph hover card as this head can show one: a single line, figure first.
    ///
    /// <para>An empty figure is the Monday fallback gridline, which has no observed instant to
    /// state (rule 6), so the caption stands alone rather than behind a separator with
    /// nothing in front of it.</para>
    /// </summary>
    private static string GraphTip(string figure, string caption) =>
        figure.Length == 0 ? caption : $"{figure} · {caption}";
}
