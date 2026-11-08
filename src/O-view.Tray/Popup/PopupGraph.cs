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

    /// <summary>One per column, a day with no bar included: it has no height, and a switch to the other measure can give it one.</summary>
    private readonly List<Rectangle> _bars = [];

    private PanelLocalChartView? _drawn;
    private double _width;
    private double _barArea;

    /// <summary>
    /// Draws the chart at rest. Nothing happens if it is already drawn at this size, because the
    /// panel's fit asks for a redraw on every pass, including while the bars are moving.
    /// </summary>
    public void Draw(PanelLocalChartView chart)
    {
        var width = host.ActualWidth > 0 ? host.ActualWidth : PanelMetrics.ContentWidthDip;

        // Measured from the canvas rather than fixed at the natural 96: a display too short for
        // the panel gets a shorter chart (PanelDensity), and the axis band keeps its height.
        var barArea = Math.Max(PanelMetrics.ChartAxisBandDip, host.Height - PanelMetrics.ChartAxisBandDip);

        if (ReferenceEquals(chart, _drawn) && width == _width && barArea == _barArea)
        {
            return;
        }

        _drawn = chart;
        _width = width;
        _barArea = barArea;
        _bars.Clear();
        host.Children.Clear();
        if (chart.Columns.Count == 0)
        {
            return;
        }

        var slot = width / chart.Columns.Count;

        AddHairline(0, barArea, width, 1);
        AddBars(chart.Columns, slot, barArea);
        AddAxis(chart, slot, barArea, width);
    }

    /// <summary>
    /// The bars rise from the baseline, as the panel arrives or as the totals face comes back,
    /// each a few milliseconds after the one to its left. A day with no bar neither rises nor
    /// takes a turn.
    /// </summary>
    /// <param name="startMs">When bar <c>i</c> starts rising.</param>
    /// <returns>When the last bar finishes, in milliseconds.</returns>
    public double Rise(Func<int, double> startMs)
    {
        var riseMs = PanelMotion.Ms(PanelMotion.ChartBarRiseBaseMs);
        if (!PanelMotion.Runs(WpfMotion.AnimationsOn, riseMs))
        {
            return 0;
        }

        var index = 0;
        foreach (var bar in _bars.Where(bar => bar.Height > 0))
        {
            Animate(bar, 0, bar.Height, riseMs, startMs(index++));
        }

        return startMs(Math.Max(0, index - 1)) + riseMs;
    }

    /// <summary>
    /// The bars move to the other measure's heights, all together, each from the height it is at
    /// on screen, so a second press turns them round wherever they have got to.
    /// </summary>
    public void MoveTo(PanelLocalChartView chart)
    {
        if (_bars.Count != chart.Columns.Count)
        {
            Draw(chart);
            return;
        }

        var moveMs = PanelMotion.Ms(PanelMotion.ChartSeriesSwitchBaseMs);
        var animate = PanelMotion.Runs(WpfMotion.AnimationsOn, moveMs);

        _drawn = chart;
        foreach (var (bar, column) in _bars.Zip(chart.Columns))
        {
            var from = bar.Height;
            var to = TargetHeight(column);
            PlaceBar(bar, to);
            SetCard(bar, column);

            if (animate)
            {
                Animate(bar, from, to, moveMs, 0);
            }
        }
    }

    /// <summary>
    /// The bars <paramref name="atMs"/> after the press, from the same curve and figure
    /// <see cref="MoveTo"/> runs, for a verification render.
    /// </summary>
    public void ApplyPartialMove(PanelLocalChartView chart, double atMs)
    {
        var travel = PanelMotion.Decelerate.ValueAt(atMs / PanelMotion.Ms(PanelMotion.ChartSeriesSwitchBaseMs));

        foreach (var (bar, column) in _bars.Zip(chart.Columns))
        {
            PlaceBar(bar, bar.Height + (TargetHeight(column) - bar.Height) * travel);
        }
    }

    private double TargetHeight(PanelGraphColumnView column) =>
        column.HasBar ? Math.Max(PanelMetrics.GraphMinBarHeightDip, (_barArea - HeadroomDip) * column.Height) : 0;

    /// <summary>Bottom-aligned on the baseline, because a Canvas places a child by its top edge.</summary>
    private void PlaceBar(Rectangle bar, double height)
    {
        bar.Height = height;
        Canvas.SetTop(bar, _barArea - height);
    }

    private void Animate(Rectangle bar, double from, double to, double durationMs, double delayMs)
    {
        bar.BeginAnimation(FrameworkElement.HeightProperty, WpfMotion.Travel(from, to, durationMs, delayMs));
        bar.BeginAnimation(Canvas.TopProperty, WpfMotion.Travel(_barArea - from, _barArea - to, durationMs, delayMs));
    }

    /// <summary>
    /// Figure first, date beneath, the panel's shared hover card. A bare string here renders as
    /// system chrome, a pale rectangle on an otherwise rounded panel.
    /// </summary>
    private void SetCard(Rectangle bar, PanelGraphColumnView column) =>
        bar.ToolTip = column.HasBar ? HoverCard.Figure(owner, column.CardFigure, column.CardCaption) : null;

    private void AddBars(IReadOnlyList<PanelGraphColumnView> columns, double slot, double barArea)
    {
        var bars = (Brush)owner.FindResource("Series1");
        var today = (Brush)owner.FindResource("AccentBg");
        var last = columns.Count - 1;

        for (var i = 0; i <= last; i++)
        {
            var age = last == 0 ? 1 : i / (double)last;
            var bar = new Rectangle
            {
                Width = Math.Max(MinBarWidthDip, slot - 2 * PanelMetrics.ChartBarInsetDip),
                Fill = i == last ? today : bars,
                Opacity = PanelMetrics.ChartOldestBarOpacity
                          + (PanelMetrics.ChartNewestBarOpacity - PanelMetrics.ChartOldestBarOpacity) * age,
                RadiusX = PanelMetrics.ChartBarRadiusDip,
                RadiusY = PanelMetrics.ChartBarRadiusDip,
            };
            HoverCard.ApplyTiming(bar);
            SetCard(bar, columns[i]);
            PlaceBar(bar, TargetHeight(columns[i]));
            Canvas.SetLeft(bar, i * slot + PanelMetrics.ChartBarInsetDip);
            host.Children.Add(bar);
            _bars.Add(bar);
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
