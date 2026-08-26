using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OView.App.Rendering;
using OView.Core.Models;
using Brush = System.Windows.Media.Brush;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using ToolTip = System.Windows.Controls.ToolTip;
using UserControl = System.Windows.Controls.UserControl;

namespace OView.Tray.Popup;

/// <summary>
/// One statistics tile, clickable to flip between its total and a per-model breakdown
/// (GitHub issue #37).
///
/// The four tiles were four near-identical copies of the same XAML; adding click
/// behaviour, a chart and a legend to each by hand would have quadrupled the thing
/// most likely to drift. They are one control now.
///
/// Nothing is fetched on click. The per-model split is already in the
/// <see cref="PanelStatistics"/> handed over when the panel opened — the rollup store
/// keeps its ledger at (date × model) grain and the totals were only ever the last
/// step — so flipping is a re-render of data in hand, which is what the issue's
/// "pre-loaded, no wait" requirement asks for.
/// </summary>
public partial class StatTile : UserControl
{
    /// <summary>Vertical gap between stacked segments, in the surface colour.</summary>
    private const double SegmentGap = 2;

    /// <summary>Rounding on the bar's data end; the baseline end stays square.</summary>
    private const double DataEndRadius = 4;

    // ── hover timing for the per-model figures ────────────────────────────────────
    //
    // The values and the reasoning behind them now live in HoverCard, shared with every
    // other tooltip in the panel. Still applied to every element that carries one:
    // setting them once on the control and relying on property inheritance LOOKS right
    // and silently does not work — the segments resolved to the framework defaults
    // instead, which is invisible in a screenshot and was only caught by reporting the
    // values (--tile-samples writes hover-timing.txt for exactly this reason).

    private static void ApplyHoverTiming(DependencyObject element) => HoverCard.ApplyTiming(element);

    private IReadOnlyList<ModelSlice> _slices = [];
    private BreakdownMeasure _measure = BreakdownMeasure.Tokens;

    /// <summary>Panel-wide slot order, so colour follows the model rather than its rank here.</summary>
    private IReadOnlyList<string> _colourOrder = [];
    private bool _expanded;

    public StatTile()
    {
        InitializeComponent();
        Root.Click += (_, _) => Toggle();
        Root.MouseEnter += (_, _) => UpdateGlyph();
        Root.MouseLeave += (_, _) => UpdateGlyph();
    }

    /// <summary>Renders one slice's measure for the legend — supplied by the panel, which owns formatting.</summary>
    public Func<ModelSlice, string> FormatSlice { get; set; } = _ => "";

    /// <summary>
    /// Inset inside the tile. Set by the panel from <see cref="PanelDensity"/> rather than
    /// fixed in the style, because two tile rows make this one of the larger pieces of
    /// spacing the panel can give back on a display too short for it.
    ///
    /// <para>Applies to <c>Root</c>, not to this control: the padding lives on the templated
    /// button, and setting it on the <c>UserControl</c> would silently do nothing.</para>
    /// </summary>
    public Thickness TilePadding
    {
        get => Root.Padding;
        set => Root.Padding = value;
    }

    /// <summary>
    /// Which measure this tile splits by. Named SplitBy rather than Measure because
    /// UIElement.Measure is a layout method — hiding it would be a trap.
    /// </summary>
    public BreakdownMeasure SplitBy
    {
        get => _measure;
        set => _measure = value;
    }

    /// <summary>
    /// Fills the tile. Resets to the summary view: a panel that reopened still showing
    /// a breakdown from twenty minutes ago would be showing stale intent, and the tile
    /// is a transient view rather than a setting.
    /// </summary>
    /// <param name="labelHint">
    /// Hover copy for the label, or empty for none. Carried by the label rather than by the
    /// tile: the tile's own hover band belongs to the breakdown segments, and the label is
    /// what the hint is about — it qualifies what the figure measures (issue #210).
    /// </param>
    public void Populate(
        string label,
        string value,
        string note,
        IReadOnlyList<ModelSlice> slices,
        BreakdownMeasure measure,
        IReadOnlyList<string> colourOrder,
        string labelHint = "")
    {
        LabelText.Text = label;
        LabelText.ToolTip = labelHint.Length > 0 ? HoverCard.Text(this, labelHint) : null;
        ApplyHoverTiming(LabelText);
        ValueText.Text = value;
        NoteText.Text = note;
        NoteText.Visibility = note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        BreakdownTotal.Text = value;
        // Same caveat, both views — see the XAML for why it is not treated as chrome.
        BreakdownNote.Text = note;
        BreakdownNote.Visibility = note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        _slices = slices;
        _measure = measure;
        _colourOrder = colourOrder;
        _expanded = false;

        // A tile with nothing to break down must not pretend to be clickable — an
        // affordance that leads nowhere is worse than none.
        var segments = ModelBreakdown.Segments(slices, measure, colourOrder);
        Root.IsEnabled = segments.Count > 0;
        Root.Cursor = segments.Count > 0 ? System.Windows.Input.Cursors.Hand : null;
        ExpandGlyph.Visibility = segments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Built now, not on first click. Two reasons, and the first is the one that
        // bites: a Hidden element only reserves the space its CONTENT needs, so an
        // empty breakdown reserves nothing and the tile visibly grows the first time
        // it is opened. Building here gives the Grid both real sizes up front, so the
        // tile is one fixed height and the panel never jumps. It also makes the flip a
        // pure visibility change, which is what "no wait between clicks" asks for.
        BuildBreakdown();
        ApplyView();
    }

    private void Toggle()
    {
        _expanded = !_expanded;
        ApplyView();
    }

    /// <summary>
    /// The hover timing as it actually resolves on a bar segment. The values are set on
    /// the control and reach the segments by property inheritance, which is the part that
    /// can silently fail — a segment built in code and parented at the wrong moment would
    /// quietly fall back to WPF's defaults (400/100/5000) and nothing would look wrong.
    /// Reported by --tile-samples so the inheritance is checked rather than assumed.
    /// </summary>
    internal (int InitialShowDelay, int BetweenShowDelay, int ShowDuration)? ResolvedHoverTiming()
    {
        if (BarHost.Children.Count == 0)
        {
            return null;
        }

        var segment = (FrameworkElement)BarHost.Children[0];
        return (ToolTipService.GetInitialShowDelay(segment),
                ToolTipService.GetBetweenShowDelay(segment),
                ToolTipService.GetShowDuration(segment));
    }

    /// <summary>
    /// A fresh hover card for one segment, unattached, so verification renders can show
    /// it. Built through the same path the real cards use — a hand-made copy would prove
    /// nothing about what actually appears on hover.
    /// </summary>
    internal ToolTip? BuildSampleTooltip(int segmentIndex)
    {
        var segments = ModelBreakdown.Segments(_slices, _measure, _colourOrder);
        if (segmentIndex >= segments.Count)
        {
            return null;
        }

        return SliceTooltip(segments[segmentIndex], SeriesBrushes(segments)[segmentIndex]);
    }

    /// <summary>Forces the view for verification renders (the --tile-samples hook).</summary>
    internal void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        ApplyView();
    }

    private void ApplyView()
    {
        // Hidden, not Collapsed — see the XAML: it keeps the tile one fixed size.
        SummaryView.Visibility = _expanded ? Visibility.Hidden : Visibility.Visible;
        BreakdownView.Visibility = _expanded ? Visibility.Visible : Visibility.Hidden;
        UpdateGlyph();
    }

    private void UpdateGlyph() =>
        ExpandGlyph.Opacity = _expanded ? 0.9 : Root.IsMouseOver ? 0.75 : 0.35;

    private void BuildBreakdown()
    {
        BarHost.ColumnDefinitions.Clear();
        BarHost.Children.Clear();
        LegendHost.Children.Clear();

        var segments = ModelBreakdown.Segments(_slices, _measure, _colourOrder);
        if (segments.Count == 0)
        {
            return;
        }

        var brushes = SeriesBrushes(segments);
        var total = segments.Sum(s => ModelBreakdown.Measure(s, _measure));

        for (var i = 0; i < segments.Count; i++)
        {
            // Star widths proportion the segments without needing the laid-out pixel
            // width, so the bar is correct at any tile size or DPI.
            if (i > 0)
            {
                BarHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SegmentGap) });
            }
            var share = total > 0 ? ModelBreakdown.Measure(segments[i], _measure) / total : 0;
            BarHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(share, GridUnitType.Star) });

            // Square at the baseline, 4px rounded at the data end — the bar grows from
            // the left, so only the final segment's right corners are rounded.
            var last = i == segments.Count - 1;
            var segment = new Border
            {
                Background = brushes[i],
                CornerRadius = last
                    ? new CornerRadius(0, DataEndRadius, DataEndRadius, 0)
                    : new CornerRadius(0),
                ToolTip = SliceTooltip(segments[i], brushes[i]),
            };
            ApplyHoverTiming(segment);
            Grid.SetColumn(segment, BarHost.ColumnDefinitions.Count - 1);
            BarHost.Children.Add(segment);

            LegendHost.Children.Add(LegendEntry(segments[i], brushes[i], last));
        }

        // Unpriced models cannot be placed on a value chart — their worth is unknown,
        // not zero — so they are named rather than quietly missing from a total the
        // chart would otherwise imply is complete (CLAUDE.md rule 6).
        var unpriced = _measure == BreakdownMeasure.EstValue
            ? ModelBreakdown.Unpriced(_slices)
            : [];
        // Counted, not listed: a raw model id is longer than the space and would either
        // overflow the tile or crowd out the total. The count says something is missing
        // — which is the part rule 6 requires — and the tooltip names it exactly.
        BreakdownCaption.Text = unpriced.Count > 0
            ? $"excl. {unpriced.Count} unpriced"
            : "by model";
        BreakdownCaption.ToolTip = unpriced.Count > 0
            ? TextTooltip($"No published rate for {string.Join(", ", unpriced)} — excluded from this "
                          + "chart, so the total shown is only the part O-view can price.")
            : null;
        ApplyHoverTiming(BreakdownCaption);
    }

    private UIElement LegendEntry(ModelSlice slice, Brush brush, bool last)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, last ? 0 : 8, 0),
            ToolTip = SliceTooltip(slice, brush),
        };
        ApplyHoverTiming(panel);

        panel.Children.Add(new Border
        {
            Width = 7,
            Height = 7,
            CornerRadius = new CornerRadius(2),
            Background = brush,
            VerticalAlignment = VerticalAlignment.Center,
        });

        // Name only. The figure moved into the bar, where it sits on its own colour and
        // needs no repetition here; dropping it also shortens every entry enough that
        // the legend usually stops wrapping, which is the space this buys back.
        // Text still wears text tokens — the swatch beside it carries identity, and a
        // light categorical hue is illegible as small text.
        panel.Children.Add(new TextBlock
        {
            Text = slice.DisplayName,
            FontSize = 9,
            Margin = new Thickness(3, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("TextSecondary"),
        });

        return panel;
    }

    /// <summary>
    /// The hover card for one segment: swatch and model name, the figure below it, and
    /// the raw model id underneath. This is the tile's table view — the relief the
    /// colour rules require where a series sits below 3:1 on the light surface — so it
    /// is built as a small piece of the panel's own design rather than left as a line of
    /// system-chrome text.
    ///
    /// The swatch matters beyond decoration: it ties the card to the exact colour being
    /// pointed at, which is the one thing a floating card otherwise loses.
    /// </summary>
    private ToolTip SliceTooltip(ModelSlice slice, Brush swatch) =>
        // Figure first, model id beneath, swatch alongside — the shared card shape, which
        // this one defined before the graph needed it too.
        //
        // The id line is ALWAYS rendered. It used to be suppressed when it matched the
        // friendly name — which is exactly the case for an UNRECOGNISED model, where
        // DisplayName IS the raw id — so those cards would have shown a number and
        // nothing identifying it.
        HoverCard.Figure(
            this,
            FormatSlice(slice),
            slice.DisplayName == ModelDisplayName.Other ? $"{slice.Model} models" : slice.Model,
            swatch);

    /// <summary>A styled hover card carrying a sentence — used for the caption's caveat.</summary>
    private ToolTip TextTooltip(string text) => HoverCard.Text(this, text);

    /// <summary>
    /// Colour by the MODEL, from the panel-wide slot order — never by the segment's
    /// position in this tile.
    ///
    /// Position-based colouring is what made the same model change colour between tiles:
    /// Opus 5 came out blue where it happened to be largest and orange where it was
    /// second, so the four tiles could not be read against one another. Anything outside
    /// the slot order — including the folded remainder — takes the neutral.
    /// </summary>
    private Brush[] SeriesBrushes(IReadOnlyList<ModelSlice> segments)
    {
        var chromatic = new[]
        {
            (Brush)FindResource("Series1"),
            (Brush)FindResource("Series2"),
            (Brush)FindResource("Series3"),
        };
        var other = (Brush)FindResource("SeriesOther");

        return segments
            .Select(s => ModelBreakdown.SlotFor(s.Model, _colourOrder) is var slot && slot >= 0
                ? chromatic[slot]
                : other)
            .ToArray();
    }
}
