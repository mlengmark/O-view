using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using OView.App.Rendering;
using OView.Presentation.Panel;
using Point = System.Windows.Point;
using RadioButton = System.Windows.Controls.RadioButton;

namespace OView.Tray.Popup;

/// <summary>
/// The "On this machine" block's totals face: the title and its line, the chart, the table, the
/// caveats that apply, and the footer with the Output | Overall switch.
///
/// <para>Every word and figure is read from the view models <see cref="PanelSections"/> built for
/// the chosen measure. What pressing the switch means is decided there too: a segment carries the
/// whole view it gives, and this class hands that back to the window to store and redraw.</para>
///
/// <para>Like the plan block, it is flat on the panel, and its gaps follow the density's
/// <see cref="PanelDensity.SpacingScale"/> and its chart the <see cref="PanelDensity.GraphScale"/>,
/// so a short display tightens this block by the proportion it tightens the rest.</para>
/// </summary>
/// <param name="refitForPendingGrowth">Re-fits the panel for height the table is about to gain or lose.</param>
/// <param name="redock">Re-docks the panel at its current height.</param>
internal sealed class PopupLocalBlock(
    Window owner, Border block, LetterSpacedText title, TextBlock subtitle,
    Canvas chartHost, FrameworkElement chartLine, Grid tableHost,
    TextBlock windowCaveat, TextBlock scopeNote,
    FrameworkElement footer, TextBlock footerCaveat,
    RadioButton outputSegment, RadioButton overallSegment, Border highlight,
    Action<PanelViewChoice> choose, Action<double> refitForPendingGrowth, Action redock)
{
    /// <summary>Space from the title's line to the chart, and from the table to the footer.</summary>
    private const double BodyGapDip = 8;

    private static readonly double HighlightMs = PanelMotion.Ms(PanelMotion.SwitchHighlightBaseMs);

    private readonly PopupGraph _graph = new(owner, chartHost);
    private readonly PopupTotalsTable _table = new(owner, tableHost, refitForPendingGrowth, redock);
    private readonly TranslateTransform _highlightOffset = (TranslateTransform)highlight.RenderTransform;

    private IReadOnlyList<PanelMeasureOption> _options = [];
    private PanelLocalChartView? _chart;
    private bool _populating;

    /// <summary>Hooks up the switch; a method of its own for the reason <see cref="PopupDivergence.Start"/> is.</summary>
    public void Start()
    {
        // Checked fires on the segment being turned on only, so one handler per segment is one
        // handler per state, with nothing to read back off the group.
        outputSegment.Checked += (_, _) => Choose(0);
        overallSegment.Checked += (_, _) => Choose(1);

        // A segment's size comes from its label and the density, neither known until layout.
        outputSegment.SizeChanged += (_, _) => PlaceHighlight();
        overallSegment.SizeChanged += (_, _) => PlaceHighlight();

        outputSegment.IsKeyboardFocusedChanged += (_, _) => PlaceHighlight();
        overallSegment.IsKeyboardFocusedChanged += (_, _) => PlaceHighlight();

        foreach (var segment in new[] { outputSegment, overallSegment })
        {
            segment.ApplyTemplate();
            segment.MouseEnter += (_, _) => UpdateHoverFill(segment, animate: true);
            segment.MouseLeave += (_, _) => UpdateHoverFill(segment, animate: true);

            // A press is never animated: the fill goes the moment the segment lights.
            segment.Checked += (_, _) => UpdateHoverFill(segment, animate: false);
            segment.Unchecked += (_, _) => UpdateHoverFill(segment, animate: false);
        }
    }

    /// <summary>
    /// Where the table sits on the physical screen, so <c>--fold-check</c> can cut its filmstrip
    /// to the band that moves.
    /// </summary>
    public double TableScreenTop => tableHost.PointToScreen(new Point(0, 0)).Y;

    /// <summary>The table's current height, what <c>--fold-check</c> samples.</summary>
    public double TableHeight => _table.Height;

    /// <summary>Presses the segment that is not on, for <c>--fold-check</c>.</summary>
    public void ToggleMeasureForVerification() =>
        (_options.Count > 0 && _options[0].Selected ? overallSegment : outputSegment).IsChecked = true;

    /// <param name="note">
    /// The note explaining an empty block, or null when it does not apply; decided in
    /// <see cref="PanelSections.ScopeNote"/>.
    /// </param>
    public void Populate(PanelLocalTotalsView totals, PanelLocalFooterView footerView, string? note)
    {
        _highlightOffset.BeginAnimation(TranslateTransform.XProperty, null);
        highlight.BeginAnimation(FrameworkElement.WidthProperty, null);

        ShowWords(totals, footerView, note);
        _chart = totals.Chart;
        _table.Populate(totals.Table);
        PlaceHighlight();
    }

    /// <summary>
    /// The switch was pressed: the words change at once, because a figure is real from its first
    /// frame, while the highlight, the chart and the table each move to the new view from wherever
    /// they are on screen.
    /// </summary>
    /// <inheritdoc cref="Populate" path="/param"/>
    public void SwitchTo(PanelLocalTotalsView totals, PanelLocalFooterView footerView, string? note)
    {
        ShowWords(totals, footerView, note);
        SlideHighlight();

        _chart = totals.Chart;
        _graph.MoveTo(totals.Chart);
        _table.SwitchTo(totals.Table);
    }

    /// <summary>A frame of the press at <paramref name="atMs"/> after it, for a verification render.</summary>
    /// <inheritdoc cref="Populate" path="/param"/>
    public void ApplyPartialSwitch(
        PanelLocalTotalsView totals, PanelLocalFooterView footerView, string? note, double atMs)
    {
        var fromX = _highlightOffset.X;
        var fromWidth = highlight.Width;

        ShowWords(totals, footerView, note);
        PlaceHighlight();

        var travel = PanelMotion.Decelerate.ValueAt(atMs / HighlightMs);
        _highlightOffset.X = fromX + (_highlightOffset.X - fromX) * travel;
        highlight.Width = fromWidth + (highlight.Width - fromWidth) * travel;

        _chart = totals.Chart;
        _graph.ApplyPartialMove(totals.Chart, atMs);
        _table.ApplyPartialSwitch(totals.Table, atMs);
    }

    /// <summary>The chart's half of the fills that play as the panel arrives; the plan bars are <see cref="PopupMeter.Arrive"/>.</summary>
    public void Arrive() => _graph.Rise();

    private void ShowWords(PanelLocalTotalsView totals, PanelLocalFooterView footerView, string? note)
    {
        title.Text = totals.Title.ToUpperInvariant();
        AutomationProperties.SetName(title, totals.Title);
        subtitle.Text = totals.Subtitle;

        // Under the table and flush right, so it ends beneath the 31-day figures it qualifies.
        PopupText.SetLine(windowCaveat, totals.WindowCaveat);
        PopupText.SetLine(scopeNote, note ?? "");
        footerCaveat.Text = footerView.Caveat;

        _options = footerView.Options;
        _populating = true;
        outputSegment.Content = _options[0].Label;
        outputSegment.IsChecked = _options[0].Selected;
        overallSegment.Content = _options[1].Label;
        overallSegment.IsChecked = _options[1].Selected;
        _populating = false;
    }

    /// <summary>
    /// Draws the chart against the height the fit gave it. Separate from <see cref="Populate"/>
    /// because it needs the canvas's laid-out width.
    /// </summary>
    public void DrawChart()
    {
        if (_chart is { } chart)
        {
            _graph.Draw(chart);
        }
    }

    public void ApplyDensity(PanelDensity density)
    {
        var scale = density.SpacingScale;
        var padding = PanelMetrics.BlockPaddingYDip * scale;
        block.Padding = new Thickness(0, padding, 0, padding);

        chartHost.Height = PanelMetrics.ChartHeightDip * density.GraphScale + PanelMetrics.ChartAxisBandDip;
        chartHost.Margin = new Thickness(0, BodyGapDip * scale, 0, 0);
        chartLine.Margin = new Thickness(0, PanelMetrics.ChartToTableGapDip * scale, 0, 0);
        tableHost.Margin = new Thickness(0, PanelMetrics.ChartToTableGapDip * scale, 0, 0);
        footer.Margin = new Thickness(0, BodyGapDip * scale, 0, 0);

        _table.ApplyDensity(scale);
    }

    private void Choose(int option)
    {
        // A populate turning a segment on is the window saying what is already chosen.
        if (!_populating && _options.Count > option)
        {
            choose(_options[option].Choice);
        }
    }

    /// <summary>
    /// Puts the lit fill behind the segment that is on. Not before layout: a segment with no
    /// size yet has nowhere to say it is, and its first <c>SizeChanged</c> comes back here.
    /// </summary>
    private void PlaceHighlight()
    {
        var segment = LitSegment;
        if (segment.ActualWidth <= 0)
        {
            return;
        }

        _highlightOffset.X = HighlightX(segment);
        highlight.Width = segment.ActualWidth;
        highlight.SetResourceReference(
            Border.BackgroundProperty, segment.IsKeyboardFocused ? "TilePressed" : "BarTrack");
    }

    /// <summary>
    /// The lit fill slides to the segment now on, from wherever it has got to. Reading the
    /// properties gives the on-screen value mid-slide, and <see cref="PlaceHighlight"/> writes the
    /// resting one beneath, so nothing is left to release when the slide ends.
    /// </summary>
    private void SlideHighlight()
    {
        var segment = LitSegment;
        var fromX = _highlightOffset.X;
        var fromWidth = highlight.Width;

        PlaceHighlight();

        if (PanelMotion.Runs(WpfMotion.AnimationsOn, HighlightMs) && segment.ActualWidth > 0)
        {
            _highlightOffset.BeginAnimation(
                TranslateTransform.XProperty, WpfMotion.Travel(fromX, HighlightX(segment), HighlightMs));
            highlight.BeginAnimation(
                FrameworkElement.WidthProperty, WpfMotion.Travel(fromWidth, segment.ActualWidth, HighlightMs));
        }
    }

    private RadioButton LitSegment => _options.Count > 1 && _options[1].Selected ? overallSegment : outputSegment;

    private double HighlightX(RadioButton segment) =>
        segment.TranslatePoint(new Point(0, 0), (UIElement)highlight.Parent).X;

    /// <summary>
    /// A segment's hover fill, on the lit one never: the lit fill is the highlight, and a hover
    /// over it would read as though the selection had moved.
    /// </summary>
    private static void UpdateHoverFill(RadioButton segment, bool animate)
    {
        var fill = (Border)segment.Template.FindName("HoverFill", segment);
        var lit = segment.IsMouseOver && segment.IsChecked != true;
        var from = fill.Opacity;
        var to = lit ? 1.0 : 0.0;
        var ms = PanelMotion.Ms(lit ? PanelMotion.HoverFillInBaseMs : PanelMotion.HoverFillOutBaseMs);

        fill.Opacity = to;

        if (animate && PanelMotion.Runs(WpfMotion.AnimationsOn, ms))
        {
            fill.BeginAnimation(UIElement.OpacityProperty, WpfMotion.Travel(
                from, to, ms, curve: lit ? PanelMotion.Decelerate : PanelMotion.Accelerate));
        }
        else
        {
            fill.BeginAnimation(UIElement.OpacityProperty, null);
        }
    }
}
