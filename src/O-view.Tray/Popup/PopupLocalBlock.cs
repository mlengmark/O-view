using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using OView.App.Rendering;
using OView.Core.Models;
using OView.Presentation.Panel;
using OView.Presentation.Text;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;
using RadioButton = System.Windows.Controls.RadioButton;

namespace OView.Tray.Popup;

/// <summary>What the local block shows for the chosen view; a null <paramref name="Models"/> is the totals face.</summary>
internal sealed record LocalBlockContent(
    PanelLocalTotalsView Totals, PanelModelFaceView? Models, PanelLocalFooterView Footer, string? Note);

/// <summary>
/// The "On this machine" block: the title and its line with the flip tag, the face the reader
/// chose (the chart and table, or <see cref="PopupModelFace"/>), the caveats that apply, and the
/// footer with the Output | Overall switch.
///
/// <para>Every word and figure is read from the view models <see cref="PanelSections"/> built for
/// the chosen measure. What pressing the switch means is decided there too: a segment carries the
/// whole view it gives, and this class hands that back to the window to store and redraw. The
/// flip tag changes the face of that same view.</para>
///
/// <para>Like the plan block, it is flat on the panel, and its gaps follow the density's
/// <see cref="PanelDensity.SpacingScale"/> and its chart the <see cref="PanelDensity.GraphScale"/>,
/// so a short display tightens this block by the proportion it tightens the rest.</para>
/// </summary>
/// <param name="refitForPendingGrowth">Re-fits the panel for height the table is about to gain or lose.</param>
/// <param name="redock">Re-docks the panel at its current height.</param>
internal sealed class PopupLocalBlock(
    Window owner, Border block, LetterSpacedText title, TextBlock subtitle,
    Button flipTag, FrameworkElement totalsFace, Canvas chartHost, FrameworkElement chartLine, Grid tableHost,
    FrameworkElement modelFaceHost, PopupModelFace modelFace,
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
    private bool _showingModels;

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

        flipTag.Click += (_, _) => Flip();
        modelFace.Start();

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

    public void Populate(LocalBlockContent content)
    {
        _highlightOffset.BeginAnimation(TranslateTransform.XProperty, null);
        highlight.BeginAnimation(FrameworkElement.WidthProperty, null);

        ShowWords(content);
        ShowFace(content);
        PlaceHighlight();
    }

    /// <summary>
    /// The switch was pressed: the words change at once, because a figure is real from its first
    /// frame, while the highlight, the chart and the table each move to the new view from wherever
    /// they are on screen. The flip tag lands here too, and turning the block, or changing the
    /// measure on the model face, has nothing to move from, so it happens at once.
    /// </summary>
    public void SwitchTo(LocalBlockContent content)
    {
        var staysOnTotals = !_showingModels && content.Models is null;

        ShowWords(content);
        SlideHighlight();

        if (staysOnTotals)
        {
            _chart = content.Totals.Chart;
            _graph.MoveTo(content.Totals.Chart);
            _table.SwitchTo(content.Totals.Table);
            return;
        }

        ShowFace(content);
        refitForPendingGrowth(0);
        redock();
    }

    /// <summary>A frame of the press at <paramref name="atMs"/> after it, for a verification render.</summary>
    public void ApplyPartialSwitch(LocalBlockContent content, double atMs)
    {
        var fromX = _highlightOffset.X;
        var fromWidth = highlight.Width;

        ShowWords(content);
        PlaceHighlight();

        var travel = PanelMotion.Decelerate.ValueAt(atMs / HighlightMs);
        _highlightOffset.X = fromX + (_highlightOffset.X - fromX) * travel;
        highlight.Width = fromWidth + (highlight.Width - fromWidth) * travel;

        _chart = content.Totals.Chart;
        _graph.ApplyPartialMove(content.Totals.Chart, atMs);
        _table.ApplyPartialSwitch(content.Totals.Table, atMs);
    }

    /// <summary>The chart's half of the fills that play as the panel arrives; the plan bars are <see cref="PopupMeter.Arrive"/>.</summary>
    public void Arrive()
    {
        if (!_showingModels)
        {
            _graph.Rise();
        }
    }

    private void ShowWords(LocalBlockContent content)
    {
        var totals = content.Totals;
        var footerView = content.Footer;

        title.Text = totals.Title.ToUpperInvariant();
        AutomationProperties.SetName(title, totals.Title);
        subtitle.Text = content.Models?.Subtitle ?? totals.Subtitle;
        flipTag.Content = TokenText.FlipTagLabel(content.Models is null ? PanelFace.Totals : PanelFace.Models);

        // Under the table and flush right, so it ends beneath the 31-day figures it qualifies.
        PopupText.SetLine(windowCaveat, content.Models?.WindowCaveat ?? totals.WindowCaveat);
        PopupText.SetLine(scopeNote, content.Note ?? "");
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
    /// Shows the face the view asks for. The totals table is filled either way, because that is
    /// what ends a fold a flip caught halfway; the chart waits, having no width while it is hidden.
    /// </summary>
    private void ShowFace(LocalBlockContent content)
    {
        _showingModels = content.Models is not null;
        totalsFace.Visibility = _showingModels ? Visibility.Collapsed : Visibility.Visible;
        modelFaceHost.Visibility = _showingModels ? Visibility.Visible : Visibility.Collapsed;

        _table.Populate(content.Totals.Table);
        _chart = _showingModels ? null : content.Totals.Chart;

        if (content.Models is { } models)
        {
            modelFace.Populate(models);
        }
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
        modelFace.ApplyDensity(scale);
    }

    private void Choose(int option)
    {
        // A populate turning a segment on is the window saying what is already chosen.
        if (!_populating && _options.Count > option)
        {
            choose(_options[option].Choice);
        }
    }

    /// <summary>The flip tag was pressed: the current view with the other face, stored and shown like a press on the switch.</summary>
    private void Flip()
    {
        if (_options.FirstOrDefault(o => o.Selected)?.Choice is { } current)
        {
            choose(current with { Face = _showingModels ? PanelFace.Totals : PanelFace.Models });
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
