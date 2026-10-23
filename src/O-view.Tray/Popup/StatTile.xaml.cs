using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OView.App.Rendering;
using OView.Presentation.Panel;
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
    private const double SegmentGap = PanelMetrics.TileSegmentGapDip;

    private const double DataEndRadius = PanelMetrics.TileDataEndRadiusDip;

    // ── hover timing for the per-model figures ────────────────────────────────────
    //
    // The values and the reasoning behind them now live in HoverCard, shared with every
    // other tooltip in the panel. Still applied to every element that carries one:
    // setting them once on the control and relying on property inheritance LOOKS right
    // and silently does not work — the segments resolved to the framework defaults
    // instead, which is invisible in a screenshot and was only caught by reporting the
    // values (--tile-samples writes hover-timing.txt for exactly this reason).

    private static void ApplyHoverTiming(DependencyObject element) => HoverCard.ApplyTiming(element);

    /// <summary>
    /// Both faces as Presentation built them, or an empty tile before the first
    /// <see cref="Populate"/>.
    /// </summary>
    private PanelTileView _view = new("", "", "", [], "", null);
    private bool _expanded;

    public StatTile()
    {
        InitializeComponent();
        Root.Click += (_, _) => Toggle();
        Root.MouseEnter += (_, _) => UpdateGlyph();
        Root.MouseLeave += (_, _) => UpdateGlyph();
    }

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
    /// Fills the tile from the view model Presentation built. Resets to the summary view: a
    /// panel that reopened still showing a breakdown from twenty minutes ago would be showing
    /// stale intent, and the tile is a transient view rather than a setting.
    /// </summary>
    public void Populate(PanelTileView view)
    {
        _view = view;
        _expanded = false;

        LabelText.Text = view.Label;
        ValueText.Text = view.Value;
        NoteText.Text = view.Note;
        NoteText.Visibility = view.Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        BreakdownTotal.Text = view.Value;
        // Same caveat, both views — see the XAML for why it is not treated as chrome. One
        // string now, so the two faces cannot be given different caveats.
        BreakdownNote.Text = view.Note;
        BreakdownNote.Visibility = view.Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // A tile with nothing to break down must not pretend to be clickable — an
        // affordance that leads nowhere is worse than none.
        Root.IsEnabled = view.CanFlip;
        Root.Cursor = view.CanFlip ? System.Windows.Input.Cursors.Hand : null;
        ExpandGlyph.Visibility = view.CanFlip ? Visibility.Visible : Visibility.Collapsed;

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
        if (segmentIndex >= _view.Segments.Count)
        {
            return null;
        }

        return SliceTooltip(
            _view.Segments[segmentIndex], SeriesBrushes(_view.Segments)[segmentIndex]);
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
        ExpandGlyph.Opacity = _expanded
            ? PanelMetrics.TileGlyphOpenOpacity
            : Root.IsMouseOver ? PanelMetrics.TileGlyphHoverOpacity : PanelMetrics.TileGlyphIdleOpacity;

    private void BuildBreakdown()
    {
        BarHost.ColumnDefinitions.Clear();
        BarHost.Children.Clear();
        LegendHost.Children.Clear();

        var segments = _view.Segments;
        if (segments.Count == 0)
        {
            return;
        }

        var brushes = SeriesBrushes(segments);

        for (var i = 0; i < segments.Count; i++)
        {
            // Star widths proportion the segments without needing the laid-out pixel
            // width, so the bar is correct at any tile size or DPI.
            if (i > 0)
            {
                BarHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SegmentGap) });
            }
            BarHost.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(segments[i].Share, GridUnitType.Star) });

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
        // chart would otherwise imply is complete (CLAUDE.md rule 6). Which models those
        // are, and whether the caption counts or names them, is Presentation's decision.
        BreakdownCaption.Text = _view.Caption;
        BreakdownCaption.ToolTip = _view.CaptionHint is { } hint ? TextTooltip(hint) : null;
        ApplyHoverTiming(BreakdownCaption);
    }

    private UIElement LegendEntry(PanelTileSegmentView slice, Brush brush, bool last)
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
            Text = slice.Name,
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
    private ToolTip SliceTooltip(PanelTileSegmentView slice, Brush swatch) =>
        // Figure first, model id beneath, swatch alongside — the shared card shape, which
        // this one defined before the graph needed it too. Both lines are the view model's;
        // see PanelTileSegmentView.Identity for why the second is never suppressed.
        HoverCard.Figure(this, slice.Figure, slice.Identity, swatch);

    /// <summary>A styled hover card carrying a sentence — used for the caption's caveat.</summary>
    private ToolTip TextTooltip(string text) => HoverCard.Text(this, text);

    /// <summary>
    /// The brush for each segment's slot. Which slot a segment takes is the view model's —
    /// decided from the panel-wide order so colour follows the MODEL, never its rank in this
    /// tile (<see cref="PanelTileSegmentView.Slot"/>). All this does is look the palette up.
    /// </summary>
    private Brush[] SeriesBrushes(IReadOnlyList<PanelTileSegmentView> segments)
    {
        var chromatic = new[]
        {
            (Brush)FindResource("Series1"),
            (Brush)FindResource("Series2"),
            (Brush)FindResource("Series3"),
        };
        var other = (Brush)FindResource("SeriesOther");

        return segments.Select(s => s.Slot >= 0 ? chromatic[s.Slot] : other).ToArray();
    }
}
