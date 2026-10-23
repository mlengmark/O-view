using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using OView.App.Rendering;
using OView.Core.Models;
using OView.Presentation.Panel;
using OView.Presentation.Text;
using static OView.Linux.Panel.PanelPrimitives;

namespace OView.Linux.Panel;

/// <summary>
/// The four statistics tiles: today's tokens and estimate, and the 31-day pair, each with the
/// per-model face it flips to.
///
/// <para>Every figure and every caveat arrives built, in <see cref="PanelTileView"/>; this
/// places them.</para>
///
/// <para>Public rather than internal only because this head's tests are a separate assembly
/// and nothing in this repo grants one access to another's internals yet.</para>
/// </summary>
public sealed class PanelStatTiles(LinuxPanelTheme theme)
{
    private const double SegmentGap = PanelMetrics.TileSegmentGapDip;

    private const double DataEndRadius = PanelMetrics.TileDataEndRadiusDip;

    private readonly LinuxPanelTheme _theme = theme;
    private readonly List<TileFlip> _flips = [];

    private PanelDensity _density = PanelDensity.Normal;

    /// <summary>
    /// The flip of every tile that has a second face, in the order the panel places them.
    ///
    /// <para>Rebuilt by each <see cref="AddTo"/>, like the tiles themselves — a flip left over
    /// from a previous open would be holding controls no longer on screen.</para>
    /// </summary>
    public IReadOnlyList<TileFlip> Flips => _flips;

    /// <summary>
    /// One tile's flip: which face is showing, and how loud the affordance is.
    ///
    /// <para>The opacities are the Windows tile's — faint at rest, clearer under the pointer,
    /// clearest while the breakdown is open. The affordance should be findable and never louder
    /// than the number it sits beside.</para>
    /// </summary>
    public sealed class TileFlip
    {
        private readonly Control _summary;
        private readonly Control _breakdown;
        private readonly Control _glyph;
        private bool _pointerOver;

        public TileFlip(Control summary, Control breakdown, Control glyph)
        {
            _summary = summary;
            _breakdown = breakdown;
            _glyph = glyph;
            Apply();
        }

        /// <summary>Whether the per-model face is the one showing. False on every open: the
        /// panel is a transient view rather than a setting, which is why the Windows tile
        /// resets its own expansion too.</summary>
        public bool IsExpanded { get; private set; }

        /// <summary>The total's face, as the panel built it — for tests.</summary>
        public Control Summary => _summary;

        /// <summary>The per-model face, as the panel built it — for tests.</summary>
        public Control Breakdown => _breakdown;

        /// <summary>The affordance, as the panel built it — for tests.</summary>
        public Control Glyph => _glyph;

        public void Toggle()
        {
            IsExpanded = !IsExpanded;
            Apply();
        }

        public void PointerOver(bool over)
        {
            _pointerOver = over;
            Apply();
        }

        private void Apply()
        {
            // Opacity, not IsVisible: both faces stay in the layout so the tile keeps one
            // height. The hidden one also stops taking the pointer, so a click always lands on
            // the face being looked at.
            _summary.Opacity = IsExpanded ? 0 : 1;
            _summary.IsHitTestVisible = !IsExpanded;
            _breakdown.Opacity = IsExpanded ? 1 : 0;
            _breakdown.IsHitTestVisible = IsExpanded;
            _glyph.Opacity = IsExpanded
                ? PanelMetrics.TileGlyphOpenOpacity
                : _pointerOver ? PanelMetrics.TileGlyphHoverOpacity : PanelMetrics.TileGlyphIdleOpacity;
        }
    }

    public void AddTo(StackPanel root, PanelStatistics stats, PanelDensity density)
    {
        _flips.Clear();
        _density = density;

        // Both faces of all four tiles, including the coverage and unpriced caveats.
        var tiles = PanelSections.Tiles(stats);

        root.Children.Add(TileRow(Tile(tiles.TokensToday), Tile(tiles.EstToday)));

        // A muted line naming the UTC day's boundary stood here for one release, because the
        // tiles were a UTC day and this head has no hover to put the caveat behind (issue
        // #210). The tiles are the reader's own day now, so the line has nothing to say.
        root.Children.Add(TileRow(Tile(tiles.Tokens31Days), Tile(tiles.Est31Days)));

        // What none of the four tiles above cover (issue #235). The plan bars are account-wide;
        // these are not, because cloud-container Cowork sessions and chat leave no transcript on
        // this machine. Beneath the whole block rather than on a tile: it is true of all four,
        // and the per-tile caveat channel would print it twice on the 31-day pair and never on
        // today's.
        //
        // Always shown, unlike that caveat. Partial history and an unpriced model are conditions
        // that pass; this is the standing shape of the data, and a note appearing only sometimes
        // would teach a reader that its absence means full coverage.
        //
        // Not the line issue #232 removed — that one sat under the session BAR, described the
        // session window alone, and carried a disclosure.
        root.Children.Add(Muted(_theme, TokenText.TokenScopeCaveat));
    }

    private static Grid TileRow(Control left, Control right)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*") };
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>
    /// One statistics tile. A tile with something to break down flips in place between its
    /// total and the per-model chart the Windows panel has always shown (issue #37); one with
    /// nothing to break down is the plain tile this head has always drawn.
    /// </summary>
    private Control Tile(PanelTileView view)
    {
        if (!view.CanFlip)
        {
            // No second face is built at all, rather than one built and kept out of sight.
            // Both faces share a cell and are therefore both measured, so an empty breakdown
            // would still have a say in the tile's height — and a tile that cannot flip has to
            // stay exactly the tile it was.
            return TileShell(SummaryFace(view));
        }

        var summary = SummaryFace(view);
        var breakdown = BreakdownFace(view);
        var glyph = FlipGlyph();

        // Both faces in one cell, so the tile measures to the taller of the two and keeps one
        // height however often it is flipped — the sections below it must not jump (the same
        // reason the Windows tile uses Hidden rather than Collapsed). Avalonia has no "hidden
        // but still measured" visibility, so the face that is not showing is held at zero
        // opacity instead of being collapsed out of the layout.
        var faces = new Avalonia.Controls.Panel();
        faces.Children.Add(summary);
        faces.Children.Add(breakdown);
        faces.Children.Add(glyph);

        var flip = new TileFlip(summary, breakdown, glyph);
        _flips.Add(flip);

        var shell = TileShell(faces);

        // PointerPressed rather than a Button: this head's tile is a Border, and wrapping it
        // in a templated Button to get a click would also inherit that template's own
        // background and padding, which is the card's. Keyboard activation is what this costs
        // — noted on the slice, and the Windows tile keeps it.
        shell.PointerPressed += (_, _) => flip.Toggle();
        shell.PointerEntered += (_, _) => flip.PointerOver(true);
        shell.PointerExited += (_, _) => flip.PointerOver(false);

        return shell;
    }

    /// <summary>The card every tile sits in, whichever faces it has.</summary>
    private Border TileShell(Control child) => new()
    {
        Background = _theme["TileBg"],
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(NaturalTilePadding * _density.SpacingScale),
        Child = child,
    };

    /// <summary>The face a reader sees first: the tile's name, its total, and any caveat.</summary>
    private StackPanel SummaryFace(PanelTileView view)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(Text(view.Label, 12, _theme["TextSecondary"]));
        stack.Children.Add(Text(view.Value, 20, _theme["TextPrimary"], FontWeight.SemiBold));
        if (view.Note.Length > 0)
        {
            stack.Children.Add(Text(view.Note, 11, _theme["WarnText"]));
        }

        return stack;
    }

    /// <summary>
    /// The second face: the same total, the per-model bar, its legend, and the caveat again.
    ///
    /// <para>The tile's <b>label</b> is dropped here to buy the room for the chart, as the
    /// issue allows, and the total stays so the tile still answers its own question at a
    /// glance. The <b>caveat</b> is not expendable in the same way — "465.3M" without "8 of 31
    /// days recorded" reads as low usage rather than short history, which is the exact
    /// misreading rule 6 exists to prevent.</para>
    ///
    /// <para>The legend is mandatory rather than optional polish: on the light surface two of
    /// the series sit below 3:1 against the tile, which the colour rules allow only when
    /// visible labels ship alongside. It is also the colour-vision fallback — identity never
    /// rests on hue alone, and this head has no hover card to fall back on.</para>
    /// </summary>
    private StackPanel BreakdownFace(PanelTileView view)
    {
        var stack = new StackPanel { Spacing = 5 };

        // Caption right-aligned beside the total, clear of the glyph at the tile's edge.
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(Text(view.Value, 13, _theme["TextPrimary"], FontWeight.SemiBold));
        var caption = Text(view.Caption, 9, _theme["TextMuted"]);
        caption.Margin = new Thickness(0, 0, 13, 0);
        caption.VerticalAlignment = VerticalAlignment.Center;
        caption.TextWrapping = TextWrapping.NoWrap;
        caption.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(caption, 1);
        head.Children.Add(caption);
        stack.Children.Add(head);

        stack.Children.Add(SegmentBar(view.Segments));
        stack.Children.Add(Legend(view.Segments));

        if (view.Note.Length > 0)
        {
            stack.Children.Add(Text(view.Note, 10, _theme["WarnText"]));
        }

        return stack;
    }

    /// <summary>
    /// The per-model bar. Star widths proportion the segments from each one's share, so the
    /// bar is right at any tile width without needing the laid-out pixels.
    ///
    /// <para>Thin and textless: this head cannot reveal a figure on hover, so the figures are
    /// in the legend rather than inside the segments, and the bar needs no height for them.</para>
    /// </summary>
    private Control SegmentBar(IReadOnlyList<PanelTileSegmentView> segments)
    {
        var bar = new Grid { Height = 10 };

        for (var i = 0; i < segments.Count; i++)
        {
            if (i > 0)
            {
                bar.ColumnDefinitions.Add(new ColumnDefinition(SegmentGap, GridUnitType.Pixel));
            }
            bar.ColumnDefinitions.Add(new ColumnDefinition(segments[i].Share, GridUnitType.Star));

            // Square at the baseline, rounded at the data end — the bar grows from the left,
            // so only the last segment's right corners are rounded.
            var segment = new Border
            {
                Background = _theme.Series(segments[i].Slot),
                CornerRadius = i == segments.Count - 1
                    ? new CornerRadius(0, DataEndRadius, DataEndRadius, 0)
                    : new CornerRadius(0),
            };
            Grid.SetColumn(segment, bar.ColumnDefinitions.Count - 1);
            bar.Children.Add(segment);
        }

        return bar;
    }

    /// <summary>
    /// The legend: a swatch, the model's name, and its figure.
    ///
    /// <para><b>The figure is here, unlike on Windows.</b> That head drops it from the legend
    /// because its hover card carries it, and a compositor may never deliver hover at all here
    /// — so a figure only reachable by pointer would be a figure some users cannot read. It is
    /// the same decision the composition breakdown's table made for this head.</para>
    /// </summary>
    private Control Legend(IReadOnlyList<PanelTileSegmentView> segments)
    {
        var legend = new WrapPanel { Orientation = Orientation.Horizontal };

        foreach (var segment in segments)
        {
            var entry = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 3,
                Margin = new Thickness(0, 0, 8, 0),
            };

            entry.Children.Add(new Border
            {
                Width = 7,
                Height = 7,
                CornerRadius = new CornerRadius(2),
                Background = _theme.Series(segment.Slot),
                VerticalAlignment = VerticalAlignment.Center,
            });

            // Text wears text colours — the swatch beside it carries the identity, and a light
            // categorical hue is illegible as small text.
            var label = Text($"{segment.Name} {segment.Figure}", 9, _theme["TextSecondary"]);
            label.TextWrapping = TextWrapping.NoWrap;
            label.VerticalAlignment = VerticalAlignment.Center;
            entry.Children.Add(label);

            legend.Children.Add(entry);
        }

        return legend;
    }

    /// <summary>
    /// The flip affordance: the same mark the Windows tile draws, at the same coordinates.
    ///
    /// <para>Two opposed arrows rather than a chart glyph (issue #179): the tile does not draw
    /// a chart, it swaps between two views of one figure, and a bar-chart icon said the wrong
    /// thing about what clicking does. Strokes with no fill — a filled or boxed icon reads as a
    /// button pasted onto the tile, and the tile is the button.</para>
    ///
    /// <para>Built from four shapes rather than one parsed path, which is the only difference
    /// from the Windows glyph. <c>Geometry.Parse</c> needs a rendering platform, and CI has
    /// none — a tile that could not be constructed in a test could not have its flip proved by
    /// one, and this head's flip has no golden image to prove it instead.</para>
    /// </summary>
    private Control FlipGlyph()
    {
        var glyph = new Canvas
        {
            Width = 9,
            Height = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
        };

        // Top arrow points right, bottom arrow points left — a shaft and a two-stroke head
        // each, with the heads left open.
        glyph.Children.Add(Stroke(new Line { StartPoint = new Point(0, 2.5), EndPoint = new Point(8, 2.5) }));
        glyph.Children.Add(Stroke(new Polyline
        {
            Points = [new Point(5.6, 0.4), new Point(8, 2.5), new Point(5.6, 4.6)],
        }));
        glyph.Children.Add(Stroke(new Line { StartPoint = new Point(8, 7.5), EndPoint = new Point(0, 7.5) }));
        glyph.Children.Add(Stroke(new Polyline
        {
            Points = [new Point(2.4, 5.4), new Point(0, 7.5), new Point(2.4, 9.6)],
        }));

        return glyph;
    }

    /// <summary>The glyph's shared stroke — rounded, unfilled, in the muted text colour.</summary>
    private Shape Stroke(Shape shape)
    {
        shape.Stroke = _theme["TextMuted"];
        shape.StrokeThickness = 1.3;
        shape.StrokeLineCap = PenLineCap.Round;
        shape.StrokeJoin = PenLineJoin.Round;
        // Polyline fills the area between its points unless told not to.
        shape.Fill = null;
        return shape;
    }
}
