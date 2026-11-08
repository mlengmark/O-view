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
internal sealed class PopupLocalBlock(
    FrameworkElement owner, Border block, LetterSpacedText title, TextBlock subtitle,
    Canvas chartHost, FrameworkElement chartLine, Grid tableHost,
    TextBlock windowCaveat, TextBlock scopeNote,
    FrameworkElement footer, TextBlock footerCaveat,
    RadioButton outputSegment, RadioButton overallSegment, Border highlight,
    Action<PanelViewChoice> choose)
{
    /// <summary>Space from the title's line to the chart, and from the table to the footer.</summary>
    private const double BodyGapDip = 8;

    private readonly PopupGraph _graph = new(owner, chartHost);
    private readonly PopupTotalsTable _table = new(owner, tableHost);
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
    }

    /// <summary>
    /// Where the table sits on the physical screen, so <c>--fold-check</c> can cut its filmstrip
    /// to the band that moves.
    /// </summary>
    public double TableScreenTop => tableHost.PointToScreen(new Point(0, 0)).Y;

    /// <summary>The table's current height, what <c>--fold-check</c> samples.</summary>
    public double TableHeight => tableHost.ActualHeight;

    /// <summary>Presses the segment that is not on, for <c>--fold-check</c>.</summary>
    public void ToggleMeasureForVerification() =>
        (_options.Count > 0 && _options[0].Selected ? overallSegment : outputSegment).IsChecked = true;

    /// <param name="note">
    /// The note explaining an empty block, or null when it does not apply; decided in
    /// <see cref="PanelSections.ScopeNote"/>.
    /// </param>
    public void Populate(PanelLocalTotalsView totals, PanelLocalFooterView footerView, string? note)
    {
        title.Text = totals.Title.ToUpperInvariant();
        AutomationProperties.SetName(title, totals.Title);
        subtitle.Text = totals.Subtitle;

        _chart = totals.Chart;
        _table.Populate(totals.Table);

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

        PlaceHighlight();
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
        var segment = _options.Count > 1 && _options[1].Selected ? overallSegment : outputSegment;
        if (segment.ActualWidth <= 0)
        {
            return;
        }

        _highlightOffset.X = segment.TranslatePoint(new Point(0, 0), (UIElement)highlight.Parent).X;
        highlight.Width = segment.ActualWidth;
        highlight.SetResourceReference(
            Border.BackgroundProperty, segment.IsKeyboardFocused ? "TilePressed" : "BarTrack");
    }
}
