using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OView.Core.Models;
using Brush = System.Windows.Media.Brush;
using Orientation = System.Windows.Controls.Orientation;
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

    private IReadOnlyList<ModelSlice> _slices = [];
    private BreakdownMeasure _measure = BreakdownMeasure.Tokens;
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
    public void Populate(
        string label,
        string value,
        string note,
        IReadOnlyList<ModelSlice> slices,
        BreakdownMeasure measure)
    {
        LabelText.Text = label;
        ValueText.Text = value;
        NoteText.Text = note;
        NoteText.Visibility = note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        BreakdownTotal.Text = value;

        _slices = slices;
        _measure = measure;
        _expanded = false;

        // A tile with nothing to break down must not pretend to be clickable — an
        // affordance that leads nowhere is worse than none.
        var segments = ModelBreakdown.Segments(slices, measure);
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

        var segments = ModelBreakdown.Segments(_slices, _measure);
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
                ToolTip = SliceTooltip(segments[i]),
            };
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
            ? $"No published rate for {string.Join(", ", unpriced)} — excluded from this chart, "
              + "so the total shown is only the part O-view can price."
            : null;
    }

    private UIElement LegendEntry(ModelSlice slice, Brush brush, bool last)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, last ? 0 : 8, 0),
            ToolTip = SliceTooltip(slice),
        };

        panel.Children.Add(new Border
        {
            Width = 7,
            Height = 7,
            CornerRadius = new CornerRadius(2),
            Background = brush,
            VerticalAlignment = VerticalAlignment.Center,
        });

        // Text wears text tokens, never the series colour: the swatch beside it carries
        // identity, and a light categorical hue is illegible as small text.
        panel.Children.Add(new TextBlock
        {
            Text = $"{slice.DisplayName} {FormatSlice(slice)}",
            FontSize = 9,
            Margin = new Thickness(3, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("TextSecondary"),
        });

        return panel;
    }

    /// <summary>
    /// Exact figures for one segment. This is the tile's table view — the relief the
    /// colour rules require where a series sits below 3:1 on the light surface, and
    /// where the folded "Other" bucket says how many models it stands for.
    /// </summary>
    private string SliceTooltip(ModelSlice slice)
    {
        var name = slice.DisplayName == ModelDisplayName.Other
            ? $"{ModelDisplayName.Other} ({slice.Model})"
            : slice.DisplayName;
        return $"{name} · {FormatSlice(slice)}";
    }

    /// <summary>
    /// Colour by position in the validated order, except the folded remainder, which
    /// always takes the neutral so it can never impersonate a model.
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
            .Select((s, i) => s.DisplayName == ModelDisplayName.Other ? other : chromatic[i])
            .ToArray();
    }
}
