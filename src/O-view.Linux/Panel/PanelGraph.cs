using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using OView.App.Rendering;
using OView.Presentation.Panel;

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
    /// <summary>Room left above the tallest bar, so it does not stand against the line above it.</summary>
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

    /// <summary>What moves the bars. Until the block hands one over, a motion that never runs.</summary>
    private LinuxMotion _motion = new(() => false, () => TimeSpan.Zero, _ => { });

    /// <summary>The chart last built, which <see cref="Rise"/> moves and the next build's bars move on from.</summary>
    private ChartSurface? _shown;

    /// <summary>Moves the bars on <paramref name="motion"/> from the next build on.</summary>
    public void Use(LinuxMotion motion) => _motion = motion;

    /// <summary>
    /// The chart, or null when there are no days to draw.
    ///
    /// <para><b>A day with no bar draws nothing at all</b>, no zero-height stand-in, because a
    /// flat bar claims a measured day of no usage on a day that was never measured (rule 6). Its
    /// slot stays, so the week lines over it still line up.</para>
    /// </summary>
    /// <param name="moveFromShown">
    /// True for the other measure's figures: every bar moves together from the height the last
    /// chart's has on screen, so a second press turns them round where they are. False draws the
    /// bars at rest, as any other rebuild of the panel must.
    /// </param>
    public Control? Chart(PanelLocalChartView chart, PanelDensity density, bool moveFromShown = false)
    {
        var previous = _shown;
        _shown = null;
        if (chart.Columns.Count == 0)
        {
            return null;
        }

        // A whole number of pixels, so the baseline hairline at its foot stays one crisp row. The
        // axis band below it is NOT scaled: it holds text, and text does not shrink with the
        // spacing.
        var barArea = Math.Round(PanelMetrics.ChartHeightDip * density.GraphScale);

        _shown = new ChartSurface(_theme, chart, barArea, _motion);
        if (moveFromShown && previous is not null && previous.Columns == chart.Columns.Count)
        {
            _shown.MoveFrom(previous);
        }

        return _shown;
    }

    /// <summary>
    /// The last chart's bars rise from the baseline, each <paramref name="startMs"/> of its turn
    /// after the start, counting only the days that have a bar, as the Windows chart does.
    /// </summary>
    public void Rise(Func<int, double> startMs) => _shown?.Rise(startMs);

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

        /// <summary>Each bar, its card and column, and its height: at rest its target, mid-motion where it is.</summary>
        private readonly List<(Border Bar, Border Card, int Column, MotionValue Height)> _bars = [];
        private readonly List<(Border Line, TextBlock Date, int Column)> _weeks = [];
        private readonly TextBlock _today;
        private readonly LinuxMotion _motion;

        public ChartSurface(LinuxPanelTheme theme, PanelLocalChartView chart, double barArea, LinuxMotion motion)
        {
            _columns = chart.Columns.Count;
            _barArea = barArea;
            _motion = motion;

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
            var cards = new PanelHoverCard(theme);
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
                var card = cards.Figure(column.CardFigure, column.CardCaption);
                ToolTip.SetTip(bar, card);
                Children.Add(bar);
                var height = Math.Max(PanelMetrics.GraphMinBarHeightDip, (_barArea - HeadroomDip) * column.Height);
                _bars.Add((bar, card, i, _motion.Value(height, _ => InvalidateArrange())));
            }
        }

        /// <summary>How many days the chart spans, bars or not.</summary>
        public int Columns => _columns;

        /// <summary>Each day's bar height on screen now, null for a day with no bar.</summary>
        private double? HeightAt(int column) =>
            _bars.FirstOrDefault(b => b.Column == column) is { Bar: not null } found ? found.Height.Current : null;

        /// <summary>
        /// Every bar moves together from where <paramref name="previous"/>'s is on screen to its own
        /// height. A day that had no bar there grows from the baseline, as it does on Windows.
        /// </summary>
        public void MoveFrom(ChartSurface previous)
        {
            var moveMs = PanelMotion.Ms(PanelMotion.ChartSeriesSwitchBaseMs);
            foreach (var (_, _, column, height) in _bars)
            {
                var target = height.Target;
                height.Set(previous.HeightAt(column) ?? 0);
                height.To(target, moveMs, PanelMotion.Decelerate);
            }
        }

        /// <inheritdoc cref="PanelGraph.Rise"/>
        public void Rise(Func<int, double> startMs)
        {
            var riseMs = PanelMotion.Ms(PanelMotion.ChartBarRiseBaseMs);
            var index = 0;
            foreach (var (_, _, _, height) in _bars)
            {
                var target = height.Target;
                height.Set(0);
                height.To(target, riseMs, PanelMotion.Decelerate, delayMs: startMs(index++));
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

            foreach (var (bar, card, column, motion) in _bars)
            {
                var height = motion.Current;
                var box = new Rect(
                    column * slot + PanelMetrics.ChartBarInsetDip,
                    _barArea - height,
                    Math.Max(MinBarWidthDip, slot - 2 * PanelMetrics.ChartBarInsetDip),
                    height);
                bar.Arrange(box);

                // At the pointer while the card has room right of the bar, otherwise beside the bar
                // on its left: the chart spans the panel, so a late day's card would run past its
                // edge. Only the side is set here, which needs no layout pass.
                var fitsRight = box.Right + ToolTip.GetHorizontalOffset(bar) + CardWidth(card) <= width;
                ToolTip.SetPlacement(bar, fitsRight ? PlacementMode.Pointer : PlacementMode.Left);
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

        /// <summary>
        /// A card's natural width, measured on its own the first time the chart is arranged, once
        /// the panel's own text has been measured, and never while the panel is being built.
        /// </summary>
        private static double CardWidth(Border card)
        {
            if (!card.IsMeasureValid)
            {
                card.Measure(Size.Infinity);
            }

            return card.DesiredSize.Width;
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
