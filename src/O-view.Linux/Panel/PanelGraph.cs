using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using OView.App.Rendering;
using OView.Presentation.Panel;
using static OView.Linux.Panel.PanelPrimitives;

namespace OView.Linux.Panel;

/// <summary>
/// The 31-day chart (<see cref="PanelLocalChartView"/>): a bar per day from a baseline hairline,
/// older days fading and today in the accent, a hairline at each week start with its date
/// written across, <c>today</c> at the right-hand end, and a hover card on each bar.
///
/// <para>Every height, date and card line arrives built; this places them.</para>
/// </summary>
internal sealed class PanelGraph(LinuxPanelTheme theme)
{
    /// <summary>Room left above the tallest bar, so it does not stand against the heading.</summary>
    private const double HeadroomDip = 4;

    /// <summary>A week's date starts this far right of its line, so the two do not touch.</summary>
    private const double WeekLabelGapDip = 3;

    /// <summary>From the top of the axis band to the top of its text.</summary>
    private const double AxisLabelTopDip = 2;

    /// <summary>How far in from the right edge <c>today</c> ends.</summary>
    private const double TodayLabelInsetDip = 2;

    /// <summary>The smallest gap a week's date may leave before <c>today</c>.</summary>
    private const double AxisLabelClearanceDip = 6;

    /// <summary>The bars' width below which they would stop reading as bars.</summary>
    private const double MinBarWidthDip = 2;

    private readonly LinuxPanelTheme _theme = theme;

    /// <summary>
    /// <para><b>A day with no bar draws nothing at all</b>, no zero-height stand-in, because a
    /// flat bar claims a measured day of no usage on a day that was never measured (rule 6). Its
    /// slot stays, so the week lines over it still line up.</para>
    /// </summary>
    public void AddTo(StackPanel root, string heading, PanelLocalChartView chart, PanelDensity density)
    {
        if (chart.Columns.Count == 0)
        {
            return;
        }

        // A whole number of pixels, so the baseline hairline at its foot stays one crisp row. The
        // axis band below it is NOT scaled: it holds text, and text does not shrink with the
        // spacing.
        var barArea = Math.Round(PanelMetrics.ChartHeightDip * density.GraphScale);

        var block = new StackPanel { Spacing = 4 };
        block.Children.Add(Text(heading, 13, _theme["TextPrimary"], FontWeight.SemiBold));
        block.Children.Add(new ChartSurface(_theme, chart, barArea));
        root.Children.Add(block);
    }

    /// <summary>
    /// The marks, placed against whatever width the panel's column turns out to be.
    ///
    /// <para><b>Why the placing is in the arrange.</b> Nothing has a width to divide when the
    /// panel is built, and the column is not one width: the compact density's padding widens it,
    /// and the scroll bar, which arrives after the build, narrows it. A fixed 368 would cross the
    /// panel's edge at the first of those it was too wide for. The arrange is the one moment the
    /// true width is known, it costs no rebuild when the width moves, and it runs in the
    /// offscreen renders as well, which lay the tree out by hand.</para>
    /// </summary>
    private sealed class ChartSurface : Avalonia.Controls.Panel
    {
        private readonly int _columns;
        private readonly double _barArea;
        private readonly Border _baseline;
        private readonly List<(Border Bar, int Column, double Height)> _bars = [];
        private readonly List<(Border Line, TextBlock Date, int Column)> _weeks = [];
        private readonly TextBlock _today;

        public ChartSurface(LinuxPanelTheme theme, PanelLocalChartView chart, double barArea)
        {
            _columns = chart.Columns.Count;
            _barArea = barArea;

            _baseline = Hairline(theme);
            Children.Add(_baseline);
            AddBars(theme, chart.Columns);

            // After the bars, so they sit above them: a Panel paints in child order, and a week
            // line overdrawn by every bar it crosses is missing exactly where usage is heaviest.
            foreach (var mark in chart.WeekMarks)
            {
                var line = Hairline(theme);
                var date = AxisLabel(mark.DateLabel, theme["TextMuted"]);
                Children.Add(line);
                Children.Add(date);
                _weeks.Add((line, date, mark.Column));
            }

            _today = AxisLabel(chart.TodayLabel, theme["AccentInk"]);
            Children.Add(_today);
        }

        private void AddBars(LinuxPanelTheme theme, IReadOnlyList<PanelGraphColumnView> columns)
        {
            var last = columns.Count - 1;

            for (var i = 0; i <= last; i++)
            {
                var column = columns[i];
                if (!column.HasBar)
                {
                    continue;
                }

                var age = last == 0 ? 1 : i / (double)last;
                var bar = new Border
                {
                    Background = i == last ? theme["AccentBg"] : theme["Series1"],
                    Opacity = PanelMetrics.ChartOldestBarOpacity
                              + (PanelMetrics.ChartNewestBarOpacity - PanelMetrics.ChartOldestBarOpacity) * age,
                    CornerRadius = new CornerRadius(PanelMetrics.ChartBarRadiusDip),

                    // At its true fractional place: a slot is rarely a whole number, and
                    // rounding each bar's edges to whole pixels leaves the 31 gaps visibly uneven.
                    UseLayoutRounding = false,
                };
                ToolTip.SetTip(bar, $"{column.CardFigure} · {column.CardCaption}");
                Children.Add(bar);
                _bars.Add((bar, i, Math.Max(PanelMetrics.GraphMinBarHeightDip, (_barArea - HeadroomDip) * column.Height)));
            }
        }

        /// <summary>Asks for its height and no width: the column it is arranged in decides that.</summary>
        protected override Size MeasureOverride(Size availableSize)
        {
            foreach (var child in Children)
            {
                child.Measure(Size.Infinity);
            }

            return new Size(0, _barArea + PanelMetrics.ChartAxisBandDip);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var width = finalSize.Width;
            var slot = width / _columns;

            _baseline.Arrange(new Rect(0, _barArea, width, 1));

            foreach (var (bar, column, height) in _bars)
            {
                bar.Arrange(new Rect(
                    column * slot + PanelMetrics.ChartBarInsetDip,
                    _barArea - height,
                    Math.Max(MinBarWidthDip, slot - 2 * PanelMetrics.ChartBarInsetDip),
                    height));
            }

            var top = _barArea + AxisLabelTopDip;
            var todayLeft = width - TodayLabelInsetDip - _today.DesiredSize.Width;
            _today.Arrange(new Rect(new Point(todayLeft, top), _today.DesiredSize));

            foreach (var (line, date, column) in _weeks)
            {
                var x = column * slot;
                line.Arrange(new Rect(x, 0, 1, _barArea));

                // A Monday among the last few days keeps its line and drops its date, rather
                // than running into today or past the panel's edge. Dropped by opacity, which only
                // repaints: IsVisible would invalidate the layout from inside this arrange.
                var left = x + WeekLabelGapDip;
                var fits = left + date.DesiredSize.Width <= todayLeft - AxisLabelClearanceDip;
                date.Opacity = fits ? 1 : 0;
                date.Arrange(fits ? new Rect(new Point(left, top), date.DesiredSize) : default);
            }

            return finalSize;
        }

        /// <summary>Layout rounding, left on, is what keeps a line at a fractional x to one crisp column.</summary>
        private static Border Hairline(LinuxPanelTheme theme) => new()
        {
            Background = theme["PanelBorder"],

            // A week line crosses bars, and each bar has a hover card of its own to answer.
            IsHitTestVisible = false,
        };

        private static TextBlock AxisLabel(string text, IBrush brush) => new()
        {
            Text = text,
            FontSize = PanelMetrics.ChartWeekLabelFontSize,
            Foreground = brush,
            IsHitTestVisible = false,
        };
    }
}
