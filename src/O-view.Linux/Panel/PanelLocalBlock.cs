using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using OView.App.Rendering;
using OView.Core.Models;
using OView.Presentation.Panel;
using OView.Presentation.Text;
using static OView.Linux.Panel.PanelPrimitives;

namespace OView.Linux.Panel;

/// <summary>
/// The "On this machine" block: its title and the line under it with the flip tag, the face the
/// reader chose (the 31-day chart and table, or <see cref="PanelModelFace"/>), the caveats that
/// apply, and the footer line with the Output | Overall switch.
///
/// <para>Every word and figure is read from the view models <see cref="PanelSections"/> built, the
/// ones the Windows panel reads, so the two cannot disagree about what a figure counts. What
/// pressing the switch means is decided there too: a segment carries the whole view it gives, and
/// this class hands that to <paramref name="choose"/> to store and redraw. The flip tag changes
/// the face of that same view.</para>
///
/// <para>Like the plan block it is flat on the panel. Its gaps follow the density's
/// <see cref="PanelDensity.SpacingScale"/> and its chart the <see cref="PanelDensity.GraphScale"/>,
/// so a short display tightens this block by the proportion it tightens the rest.</para>
///
/// <para><b>It moves as the Windows block does,</b> with <see cref="PanelMotion"/>'s figures: the
/// switch's highlight slides, the table folds (the old rows leave, then the height moves while the
/// new rows arrive) and the flip tag turns the block over. The panel is rebuilt on every press, so
/// what moves is kept here, outside the tree: the controls a motion is still taking off the screen
/// are carried into the new tree, and each value carries on from what is on screen. A press in the
/// middle of a motion therefore turns it round where it stands.</para>
/// </summary>
internal sealed class PanelLocalBlock(LinuxPanelTheme theme, Action<PanelViewChoice> choose)
{
    /// <summary>Space between the block's title and the line under it.</summary>
    private const double SubtitleGapDip = 1;

    /// <summary>Space from the title's line to the chart, and from the table to the footer.</summary>
    private const double BodyGapDip = 8;

    /// <summary>Space between the table and the caveat on its 31-day figures.</summary>
    private const double WindowCaveatGapDip = 4;

    /// <summary>Space above the note explaining a zero, and its size.</summary>
    private const double ScopeNoteGapDip = 6;

    private const double ScopeNoteFontSize = 11;

    /// <summary>Space between the footer's caveat and the switch.</summary>
    private const double SwitchGapDip = 10;

    /// <summary>The switch's inset, its rounding, and the gap between its segments.</summary>
    private const double SwitchPaddingDip = 2;

    private const double SwitchRadiusDip = 4;

    private const double SegmentGapDip = 2;

    private const double SegmentRadiusDip = 3;

    private const double SegmentPaddingXDip = 8;

    private const double SegmentPaddingYDip = 3;

    private readonly LinuxPanelTheme _theme = theme;
    private readonly PanelGraph _graph = new(theme);
    private readonly PanelTotalsTable _table = new(theme);
    private readonly PanelModelFace _modelFace = new(theme);

    /// <summary>The segment now on, for <see cref="FocusChosenSegment"/>.</summary>
    private RadioButton? _chosen;

    private Button? _tag;

    /// <summary>Whether the last press was the tag's rather than a segment's. Survives the rebuild that press causes.</summary>
    private bool _tagPressed;

    /// <summary>The flip tag on screen when it was the last thing pressed, otherwise null.</summary>
    public Control? PressedTag => _tagPressed ? _tag : null;

    /// <param name="models">The model face, or null while the totals face is the one chosen.</param>
    /// <param name="note">Why the figures are zero while the meters are not, or null.</param>
    public void AddTo(
        StackPanel root, PanelLocalTotalsView totals, PanelModelFaceView? models, PanelLocalFooterView footer,
        string? note, PanelDensity density)
    {
        var scale = density.SpacingScale;
        var padding = PanelMetrics.BlockPaddingYDip * scale;

        // Read off the tree on screen before it is replaced: where each motion starts from.
        var face = models is null ? 0 : 1;
        var lit = footer.Options.TakeWhile(o => !o.Selected).Count();
        var turning = _shownFace is { } shown && shown != face;
        var folding = !turning && face == 0 && _shownLit is { } was && was != lit && !Turning;
        var reversing = folding && _outgoing is not null;
        var slideFrom = _slide.InFlight && _highlight is { } h ? (h.Margin.Left, h.Width)
            : _chosen is { } on ? (on.Bounds.X, on.Bounds.Width) : default;
        var tableFrom = _tablePinned ? _tableHeight.Current : (_tableHost ?? _tableGrid)?.Bounds.Height ?? 0;
        var (facesHeight, facesWidth) = Extent(_shownFace is { } s ? _faceParts[s] : []);
        var facesFrom = _facesPinned ? _facesHeight.Current : facesHeight;
        _outgoing = reversing ? null : folding ? _tableGrid : _outgoing;
        _tableHost = _tableGrid = _facesGrid = null;

        // Only a build something moves in wraps the faces and the table: at rest the block is
        // the tree it has always been, part after part.
        var moving = _motion.AnimationsOn && (turning || folding || Turning || _tablePinned || _outgoing is not null);

        // The panel's own spacing already stands between the plan block's line and this block, so
        // it counts towards the padding above, or the line would not sit midway between the two.
        var block = new StackPanel { Margin = new Thickness(0, padding - root.Spacing, 0, padding) };

        block.Children.Add(Head(totals.Title, models?.Subtitle ?? totals.Subtitle, footer, models is not null));

        var built = new StackPanel();
        if (models is not null)
        {
            _modelFace.AddTo(built, models, scale);
        }
        else
        {
            AddTotalsFace(built, totals, density, moving, folding);
        }

        _face[face] = built;
        _faceParts[face] = [.. built.Children];

        if (moving)
        {
            // The faces share one cell, so the one leaving and the one arriving overlap during a turn.
            _facesGrid = new Grid();
            _facesGrid.Children.Add(built);
            if (_face[1 - face] is { } going && (turning || _faceOpacity[1 - face].InFlight))
            {
                Detach(going);
                foreach (var part in _faceParts[1 - face].Where(p => p.Parent != going))
                {
                    Detach(part);
                    going.Children.Add(part);
                }

                // The totals beneath the models whichever is going, as the Windows block stacks them.
                going.IsHitTestVisible = false;
                _facesGrid.Children.Insert(face == 1 ? 0 : 1, going);
            }

            if (_outgoing is { } leaving && _tableHost is { } host)
            {
                Detach(leaving);
                leaving.Margin = default;
                host.Children.Add(leaving);
            }

            block.Children.Add(_facesGrid);
        }
        else
        {
            foreach (var part in _faceParts[face])
            {
                built.Children.Remove(part);
                block.Children.Add(part);
            }
        }

        AddFoot(block, models?.WindowCaveat ?? totals.WindowCaveat, note, footer, BodyGapDip * scale);

        root.Children.Add(block);
        Move(face, lit, turning, folding, reversing, slideFrom, tableFrom, facesFrom, facesWidth);
    }

    /// <param name="switching">The other measure on the same face, whose bars move on from the old ones.</param>
    private void AddTotalsFace(StackPanel block, PanelLocalTotalsView totals, PanelDensity density, bool moving, bool switching)
    {
        var scale = density.SpacingScale;
        var bodyGap = BodyGapDip * scale;
        var tableGap = PanelMetrics.ChartToTableGapDip * scale;

        var chart = _graph.Chart(totals.Chart, density, moveFromShown: switching);
        if (chart is not null)
        {
            chart.Margin = new Thickness(0, bodyGap, 0, 0);
            block.Children.Add(chart);
        }

        var line = SoftLine(_theme);
        line.Margin = new Thickness(0, tableGap, 0, 0);
        block.Children.Add(line);

        _tableGrid = _table.Build(totals.Table, scale);
        _rows = RowsOf(_tableGrid);
        if (!moving)
        {
            _tableGrid.Margin = new Thickness(0, tableGap, 0, 0);
            block.Children.Add(_tableGrid);
            return;
        }

        // In a host of its own while it moves: what the fold pins, and the old table leaves from.
        _tableHost = new Grid { Margin = new Thickness(0, tableGap, 0, 0) };
        _tableHost.Children.Add(_tableGrid);
        block.Children.Add(_tableHost);
    }

    /// <summary>What stands under either face: the caveats that apply, and the footer line.</summary>
    private void AddFoot(StackPanel block, string windowCaveat, string? note, PanelLocalFooterView footer, double bodyGap)
    {
        // Under the table and flush right, so it ends beneath the 31-day figures it qualifies.
        if (windowCaveat is { Length: > 0 } caveat)
        {
            var coverage = Text(caveat, PanelMetrics.OffPlanCaveatFontSize, _theme["WarnText"]);
            coverage.TextAlignment = TextAlignment.Right;
            coverage.Margin = new Thickness(0, WindowCaveatGapDip, 0, 0);
            block.Children.Add(coverage);
        }

        if (note is not null)
        {
            var explained = Text(note, ScopeNoteFontSize, _theme["TextSecondary"]);
            explained.Margin = new Thickness(0, ScopeNoteGapDip, 0, 0);
            block.Children.Add(explained);
        }

        block.Children.Add(FooterLine(footer, bodyGap));
    }

    /// <summary>
    /// Puts the keyboard back on what was pressed, the segment now on or the flip tag. Every press
    /// rebuilds the panel, and the old control takes the focus with it.
    /// </summary>
    public void FocusChosenSegment() => (PressedTag ?? (Control?)_chosen)?.Focus();

    /// <summary>The title and the line under it, with the flip tag beside them at the block's top right.</summary>
    private Grid Head(string titleText, string subtitleText, PanelLocalFooterView footer, bool showingModels)
    {
        var words = new StackPanel();
        words.Children.Add(Caption(titleText, PanelMetrics.BlockTitleFontSize, _theme["TextSecondary"], FontWeight.SemiBold));

        var subtitle = Text(subtitleText, PanelMetrics.BlockSubtitleFontSize, _theme["TextMuted"]);
        subtitle.Margin = new Thickness(0, SubtitleGapDip, 0, 0);
        words.Children.Add(subtitle);

        _tag = FlipTag(footer, showingModels);
        Grid.SetColumn(_tag, 1);

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(words);
        head.Children.Add(_tag);
        return head;
    }

    /// <summary>
    /// The tag that turns the block over: the view now chosen with the other face, stored and
    /// rebuilt exactly as a press on the switch is.
    /// </summary>
    private Button FlipTag(PanelLocalFooterView footer, bool showingModels)
    {
        var current = footer.Options.First(o => o.Selected).Choice;
        var label = TokenText.FlipTagLabel(showingModels ? PanelFace.Models : PanelFace.Totals);

        var tag = new Button
        {
            // The word a screen reader announces; the one on screen is drawn by the template.
            Content = label,
            Template = FlipTagTemplate(label),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = Application.Current is null ? null : new Cursor(StandardCursorType.Hand),
        };

        tag.Click += (_, _) =>
        {
            _tagPressed = true;
            choose(current with { Face = showingModels ? PanelFace.Totals : PanelFace.Models });
        };

        return tag;
    }

    /// <summary>The tag's word and the two-arrow mark on its pill, the Windows panel's drawing, in the template for the reason a segment's word is.</summary>
    private FuncControlTemplate<Button> FlipTagTemplate(string label) => new((_, _) =>
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = PanelMetrics.FlipTagFontSize,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _theme["TextMuted"],
        });
        _mark = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M0,2.5 L8,2.5 M5.6,0.4 L8,2.5 L5.6,4.6 M8,7.5 L0,7.5 M2.4,5.4 L0,7.5 L2.4,9.6"),
            StrokeThickness = 1.3,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Opacity = 0.8,
            Margin = new Thickness(5, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Stroke = _theme["TextMuted"],

            // The arrows' own centre, about which a half turn draws them exactly as they were.
            RenderTransformOrigin = new RelativePoint(4, 5, RelativeUnit.Absolute),
        };
        ShowMark(_markTurn.Current);
        row.Children.Add(_mark);

        return new Border { Background = _theme["TileBg"], CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1), Child = row };
    });

    /// <summary>The caveat on the left, and on the right the switch.</summary>
    private Grid FooterLine(PanelLocalFooterView footer, double gapAbove)
    {
        var surfaces = Text(footer.Caveat, PanelMetrics.FooterFontSize, _theme["TextMuted"]);
        surfaces.VerticalAlignment = VerticalAlignment.Center;

        var measures = MeasureSwitch(footer.Options);
        measures.Margin = new Thickness(SwitchGapDip, 0, 0, 0);
        Grid.SetColumn(measures, 1);

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, gapAbove, 0, 0),
        };
        row.Children.Add(surfaces);
        row.Children.Add(measures);
        return row;
    }

    /// <summary>
    /// The segments on one strip. Radio buttons rather than the plain two the Bars/Breakdown switch
    /// uses, because the measures exclude each other and a screen reader should say which is on.
    /// At rest the lit segment wears its own fill; while it slides the fill is
    /// <see cref="_highlight"/>, a layer beneath the segments that is hidden again on landing.
    /// </summary>
    private Border MeasureSwitch(IReadOnlyList<PanelMeasureOption> options)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = SegmentGapDip };
        foreach (var option in options)
        {
            var segment = Segment(option);
            row.Children.Add(segment);

            if (option.Selected)
            {
                _chosen = segment;
            }
        }

        _highlight = new Border
        {
            Background = _theme["BarTrack"],
            CornerRadius = new CornerRadius(SegmentRadiusDip),
            HorizontalAlignment = HorizontalAlignment.Left,
            IsVisible = false,
        };

        var strip = new Grid();
        strip.Children.Add(_highlight);
        strip.Children.Add(row);

        return new Border
        {
            Background = _theme["TileBg"],
            CornerRadius = new CornerRadius(SwitchRadiusDip),
            Padding = new Thickness(SwitchPaddingDip),
            VerticalAlignment = VerticalAlignment.Center,
            Child = strip,
        };
    }

    private RadioButton Segment(PanelMeasureOption option)
    {
        var segment = new RadioButton
        {
            // The word a screen reader announces; the one on screen is drawn by the template.
            Content = option.Label,
            IsChecked = option.Selected,
            Template = SegmentTemplate(option.Label),
            CornerRadius = new CornerRadius(SegmentRadiusDip),
            Padding = new Thickness(SegmentPaddingXDip, SegmentPaddingYDip),

            // Explicit on both, the transparent one too: a control with no background is not
            // hit-testable between its letters, so a segment would answer only on the word.
            Background = option.Selected ? _theme["BarTrack"] : Brushes.Transparent,
            Foreground = _theme[option.Selected ? "TextPrimary" : "TextMuted"],

            // A cursor can only be made once an Avalonia application has started the windowing
            // platform, and the switch is built in a test process as well.
            Cursor = Application.Current is null ? null : new Cursor(StandardCursorType.Hand),
        };

        // A press on the segment already on is no change, and rebuilds nothing.
        segment.Click += (_, _) =>
        {
            if (!option.Selected)
            {
                _tagPressed = false;
                choose(option.Choice);
            }
        };

        return segment;
    }

    /// <summary>
    /// What a segment is drawn as. The word is in the template for the reason the Bars/Breakdown
    /// switch's is: with no theme a button's content presenter stays empty offscreen.
    /// </summary>
    private static FuncControlTemplate<RadioButton> SegmentTemplate(string label) => new((_, _) => new Border
    {
        [!Border.BackgroundProperty] = new TemplateBinding(TemplatedControl.BackgroundProperty),
        [!Border.CornerRadiusProperty] = new TemplateBinding(TemplatedControl.CornerRadiusProperty),
        [!Border.PaddingProperty] = new TemplateBinding(TemplatedControl.PaddingProperty),
        Child = new TextBlock
        {
            Text = label,
            FontSize = PanelMetrics.FooterFontSize,
            [!TextBlock.ForegroundProperty] = new TemplateBinding(TemplatedControl.ForegroundProperty),
        },
    });

    // ── motion ──────────────────────────────────────────────────────────────────────

    private static readonly double HighlightMs = PanelMotion.Ms(PanelMotion.SwitchHighlightBaseMs);
    private static readonly double RowsLeaveMs = PanelMotion.Ms(PanelMotion.RowsCloseFadeBaseMs);
    private static readonly double RowFadeMs = PanelMotion.Ms(PanelMotion.RowFadeBaseMs);
    private static readonly double FaceOutMs = PanelMotion.Ms(PanelMotion.TileFaceOutBaseMs);
    private static readonly double FaceInMs = PanelMotion.Ms(PanelMotion.TileFaceInBaseMs);
    private static readonly double FaceInDelayMs = PanelMotion.Ms(PanelMotion.TileFaceInDelayBaseMs);
    private static readonly double MarkTurnMs = PanelMotion.Ms(PanelMotion.TileGlyphTurnBaseMs);
    private static readonly double FlipHeightMs = PanelMotion.Ms(PanelMotion.BlockFlipHeightBaseMs);

    /// <summary>A straight line: the rows' clock runs in milliseconds, and the old rows leave linearly, as on Windows.</summary>
    private static readonly PanelMotion.Curve Linear = new(1.0 / 3, 1.0 / 3, 2.0 / 3, 2.0 / 3);

    /// <summary>The rows' clock at rest: past the end of any stagger, so every row is in place.</summary>
    private const double RowsAtRestMs = 1e9;

    private LinuxMotion _motion = null!;
    private MotionValue _slide = null!, _outFade = null!, _inFade = null!, _rowsClock = null!;
    private MotionValue _tableHeight = null!, _facesHeight = null!, _markTurn = null!;
    private MotionValue[] _faceOpacity = [], _faceShift = [];

    /// <summary>The face and the lit segment last built, null after an open, which starts at rest.</summary>
    private int? _shownFace, _shownLit;

    private Border? _highlight;
    private (double X, double W) _slideFrom;
    private Avalonia.Controls.Shapes.Path? _mark;

    /// <summary>Each face's control, the totals then the models, kept while it is on screen.</summary>
    private readonly StackPanel?[] _face = new StackPanel?[2];

    /// <summary>What each face was built from, so a face built flat can be gathered up to leave.</summary>
    private readonly List<Control>[] _faceParts = [[], []];
    private Grid? _facesGrid;
    private bool _facesPinned;

    private Grid? _tableHost, _tableGrid, _outgoing;
    private List<(Control Cell, double Resting)[]> _rows = [];
    private double _tableWidth;
    private bool _tablePinned;

    private bool Turning => _facesPinned || _faceOpacity.Any(v => v.InFlight);

    /// <summary>A motion that never runs, so every change lands at once: a panel with no window.</summary>
    public static LinuxMotion Still() => new(() => false, () => TimeSpan.Zero, _ => { });

    /// <summary>Moves this block on <paramref name="motion"/>, from rest.</summary>
    public void Use(LinuxMotion motion)
    {
        _motion = motion;
        _graph.Use(motion);
        _slide = motion.Value(1, ShowSlide);
        _outFade = motion.Value(1, ShowOutgoing);
        _inFade = motion.Value(1, ShowIncoming);
        _rowsClock = motion.Value(RowsAtRestMs, ShowRows);
        _tableHeight = motion.Value(0, ShowTableHeight);
        _facesHeight = motion.Value(0, ShowFacesHeight);
        _markTurn = motion.Value(0, ShowMark);
        _faceOpacity = [motion.Value(1, o => ShowFaceOpacity(0, o)), motion.Value(1, o => ShowFaceOpacity(1, o))];
        _faceShift = [motion.Value(0, y => ShowFaceShift(0, y)), motion.Value(0, y => ShowFaceShift(1, y))];
        Settle();
    }

    /// <summary>Ends every motion where it was going, for an open: every open shows the view at rest.</summary>
    public void Settle()
    {
        EndFold();
        _slide.Set(1);
        _markTurn.Set(0);
        _facesHeight.Set(0);
        UnpinFaces();
        _face[0] = _face[1] = null;
        _faceParts[0] = _faceParts[1] = [];
        _shownFace = _shownLit = null;
    }

    /// <summary>
    /// The face's half of what plays as the panel arrives, counted from the start of its slide: the
    /// chart's bars rise, or on the model face the doughnuts sweep in. Called only by an open, so a
    /// rebuild for anything else, a hidden banner or a refresh, replays nothing.
    /// </summary>
    public void Arrive()
    {
        if (_shownFace == 1)
        {
            _modelFace.Sweep(_motion, PanelMotion.Ms(PanelMotion.DoughnutSweepOpenDelayBaseMs));
        }
        else
        {
            _graph.Rise(PanelMotion.ChartBarStartMs);
        }
    }

    /// <summary>
    /// <paramref name="measure"/>, answered for the block at rest: the heights let go and what is
    /// leaving hidden. The window is fitted for where a motion ends, not for a frame of it.
    /// </summary>
    public double AtRest(Func<double> measure)
    {
        var leaving = new Control?[] { _outgoing, _shownFace is { } f ? _face[1 - f] : null }
            .OfType<Control>().Where(c => c.Parent is not null).ToList();
        leaving.ForEach(c => c.IsVisible = false);
        _tableHost?.SetValue(Layoutable.HeightProperty, double.NaN);
        _facesGrid?.SetValue(Layoutable.HeightProperty, double.NaN);
        Invalidate();

        var height = measure();

        leaving.ForEach(c => c.IsVisible = true);
        ShowTableHeight(_tableHeight.Current);
        ShowFacesHeight(_facesHeight.Current);
        Invalidate();
        return height;
    }

    /// <summary>
    /// Invalidates the moving controls and every ancestor: a measure from the panel stops at the
    /// first ancestor still valid, so a height changed below it would not be seen.
    /// </summary>
    private void Invalidate()
    {
        foreach (var control in new Control?[] { _tableHost, _facesGrid }.OfType<Control>())
        {
            foreach (var layoutable in control.GetSelfAndVisualAncestors().OfType<Layoutable>())
            {
                layoutable.InvalidateMeasure();
            }
        }
    }

    /// <summary>Starts what this build changed, each from where it was, then puts every value on the new controls.</summary>
    private void Move(
        int face, int lit, bool turning, bool folding, bool reversing,
        (double X, double W) slideFrom, double tableFrom, double facesFrom, double facesWidth)
    {
        var opening = _shownFace is null;
        var slides = _shownLit is { } was && was != lit;
        _shownFace = face;
        _shownLit = lit;

        if (opening)
        {
            _faceOpacity[face].Set(1);
            _faceOpacity[1 - face].Set(0);
            _faceShift[face].Set(0);
            _faceShift[1 - face].Set(0);
        }

        if (slides)
        {
            _slideFrom = slideFrom;
            _slide.Set(0);
            _slide.To(1, HighlightMs, PanelMotion.Decelerate);
        }

        if (turning)
        {
            // Only a face that had fully left fills in again; one a press caught leaving comes back as it is.
            var fresh = !_faceOpacity[face].InFlight && _faceOpacity[face].Current == 0;
            Turn(face, facesFrom, facesWidth);
            if (fresh && face == 1)
            {
                _modelFace.Sweep(_motion, PanelMotion.Ms(PanelMotion.DoughnutSweepDelayBaseMs));
            }
            else if (fresh)
            {
                _graph.Rise(PanelMotion.ChartBarBackStartMs);
            }
        }
        else if (_facesPinned && _face[face] is { } coming)
        {
            // The measure changed mid-turn: the arriving face has the new figures, so its height moves on to theirs.
            MoveFacesHeight(facesFrom, Natural(coming, facesWidth));
        }

        if (reversing)
        {
            Return(tableFrom);
        }
        else if (folding)
        {
            Fold(tableFrom);
        }

        Reapply();
    }

    /// <summary>
    /// The tile's crossfade on the block: the face going fades and moves away, the face coming
    /// fades in after it has begun, the height moves to the new face's and the tag's mark makes
    /// a half turn. A face caught leaving comes back from where it is.
    /// </summary>
    private void Turn(int face, double from, double width)
    {
        EndFold();
        var other = 1 - face;

        // Totals to models: the totals leave upward and the models arrive from below.
        var travel = face == 1 ? PanelMotion.TileFaceTravelDip : -PanelMotion.TileFaceTravelDip;
        if (!_faceOpacity[face].InFlight && _faceOpacity[face].Current == 0)
        {
            _faceShift[face].Set(travel);
        }

        _faceOpacity[face].To(1, FaceInMs, PanelMotion.Decelerate, delayMs: FaceInDelayMs);
        _faceShift[face].To(0, FaceInMs, PanelMotion.Decelerate, delayMs: FaceInDelayMs);
        _faceOpacity[other].To(0, FaceOutMs, PanelMotion.Accelerate, () => Retire(other));
        _faceShift[other].To(-travel, FaceOutMs, PanelMotion.Accelerate);

        // Back to no turn at all on landing: a half turn draws the arrows exactly as they were.
        _markTurn.To(_markTurn.Target + (face == 1 ? 180 : -180), MarkTurnMs, PanelMotion.Decelerate, () => _markTurn.Set(0));

        if (_face[face] is { } coming)
        {
            MoveFacesHeight(from, Natural(coming, width));
        }
    }

    private void MoveFacesHeight(double from, double to)
    {
        _facesPinned = true;
        _facesHeight.Set(from);
        _facesHeight.To(to, FlipHeightMs, PanelMotion.Decelerate, UnpinFaces);
    }

    /// <summary>Takes a face that has finished leaving out of the tree.</summary>
    private void Retire(int face)
    {
        if (face != _shownFace && _face[face] is { } gone)
        {
            Detach(gone);
            _face[face] = null;
        }
    }

    /// <summary>
    /// The table folds to the other measure: the old rows leave together while the height holds,
    /// then <see cref="Swap"/> moves the height while the new rows arrive one after another.
    /// </summary>
    private void Fold(double from)
    {
        PinTable(from);
        _inFade.Set(1);
        _rowsClock.Set(0);
        _outFade.Set(1);
        _outFade.To(0, RowsLeaveMs, Linear, Swap);
    }

    /// <summary>The old rows have gone: the height moves to the new table's while its rows arrive.</summary>
    private void Swap()
    {
        if (_outgoing is { } gone)
        {
            Detach(gone);
            _outgoing = null;
        }

        MoveTableHeight();
        var end = PanelMotion.RowStartMs(Math.Max(0, _rows.Count - 1)) + RowFadeMs;
        _rowsClock.To(end, end, Linear, () => _rowsClock.Set(RowsAtRestMs));
    }

    /// <summary>
    /// A press while the old rows are still leaving asks for those very rows: they come back from
    /// where they have faded to, and nothing is replaced.
    /// </summary>
    private void Return(double from)
    {
        PinTable(from);
        _rowsClock.Set(RowsAtRestMs);
        _inFade.Set(_outFade.Current);
        _outFade.Set(1);
        _inFade.To(1, RowFadeMs, PanelMotion.Decelerate);
        MoveTableHeight();
    }

    private void MoveTableHeight()
    {
        if (_tableGrid is not { } table || _tableHost is not { } host)
        {
            return;
        }

        var from = _tableHeight.Current;
        var to = Natural(table, host.Bounds.Width > 0 ? host.Bounds.Width : _tableWidth);
        var grows = to >= from;
        _tableHeight.To(
            to, PanelMotion.Ms(grows ? PanelMotion.FoldOpenBaseMs : PanelMotion.FoldCloseBaseMs),
            grows ? PanelMotion.Decelerate : PanelMotion.Accelerate, UnpinTable);
    }

    private void PinTable(double from)
    {
        _tablePinned = true;
        _tableHeight.Set(from);
    }

    /// <summary>Puts the table at rest at once: an open, and a turn, which takes the table off the screen.</summary>
    private void EndFold()
    {
        if (_outgoing is { } gone)
        {
            Detach(gone);
            _outgoing = null;
        }

        _outFade.Set(1);
        _inFade.Set(1);
        _rowsClock.Set(RowsAtRestMs);
        _tableHeight.Set(0);
        UnpinTable();
    }

    private void UnpinTable()
    {
        _tablePinned = false;
        ShowTableHeight(0);
    }

    private void UnpinFaces()
    {
        _facesPinned = false;
        ShowFacesHeight(0);
    }

    /// <summary>Every value on the controls this build made, so a rebuild mid-motion shows the same frame.</summary>
    private void Reapply()
    {
        _tableWidth = _tableHost?.Bounds.Width is > 0 and var width ? width : _tableWidth;
        ShowSlide(_slide.Current);
        ShowOutgoing(_outFade.Current);
        ShowIncoming(_inFade.Current);
        ShowRows(_rowsClock.Current);
        ShowTableHeight(_tableHeight.Current);
        ShowFacesHeight(_facesHeight.Current);
        ShowMark(_markTurn.Current);

        for (var i = 0; i < 2; i++)
        {
            ShowFaceOpacity(i, _faceOpacity[i].Current);
            ShowFaceShift(i, _faceShift[i].Current);
        }
    }

    /// <summary>
    /// The highlight <paramref name="travel"/> of the way from where it started to the lit segment;
    /// at 1, the segment's own fill. The segment's own background is never changed: while the
    /// highlight moves, the fill its template draws is covered by a local value, and at rest that is
    /// cleared, so the resting fill never depends on the template following a change (CI's Ubuntu
    /// run saw the template keep a transparent fill after the segment's own was set back).
    /// </summary>
    private void ShowSlide(double travel)
    {
        if (_highlight is not { } fill || _chosen is not { } lit)
        {
            return;
        }

        var moving = travel < 1;
        fill.IsVisible = moving;
        lit.ApplyTemplate();
        if (lit.GetVisualChildren().FirstOrDefault() is Border face)
        {
            if (moving)
            {
                face.Background = Brushes.Transparent;
            }
            else
            {
                face.ClearValue(Border.BackgroundProperty);
            }
        }

        if (moving)
        {
            fill.Margin = new Thickness(Between(_slideFrom.X, lit.Bounds.X, travel), 0, 0, 0);
            fill.Width = Between(_slideFrom.W, lit.Bounds.Width, travel);
        }
    }

    private void ShowOutgoing(double opacity)
    {
        if (_outgoing is { } table)
        {
            table.Opacity = opacity;
        }
    }

    private void ShowIncoming(double opacity)
    {
        if (_tableGrid is { } table)
        {
            table.Opacity = opacity;
        }
    }

    /// <summary>Each row <paramref name="ms"/> into the arrival: faded in and risen into place by its own start.</summary>
    private void ShowRows(double ms)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            var shown = PanelMotion.Decelerate.ValueAt((ms - PanelMotion.RowStartMs(i)) / RowFadeMs);
            var rise = Math.Round(PanelMotion.RowRiseDip * (1 - shown));

            foreach (var (cell, rest) in _rows[i])
            {
                cell.Opacity = rest * shown;
                cell.RenderTransform = rise == 0 ? null : new TranslateTransform(0, rise);
            }
        }
    }

    private void ShowTableHeight(double height) => Pin(_tableHost, _tablePinned, height);

    private void ShowFacesHeight(double height) => Pin(_facesGrid, _facesPinned, height);

    /// <summary>A pinned height clips what it cuts short; at rest the control sizes itself and clips nothing.</summary>
    private static void Pin(Control? control, bool pinned, double height)
    {
        if (control is not null)
        {
            control.Height = pinned ? height : double.NaN;
            control.ClipToBounds = pinned;
        }
    }

    private void ShowMark(double angle)
    {
        if (_mark is { } mark)
        {
            mark.RenderTransform = angle % 360 == 0 ? null : new RotateTransform(angle);
        }
    }

    private void ShowFaceOpacity(int face, double opacity)
    {
        if (_face[face] is { } control)
        {
            control.Opacity = opacity;
        }
    }

    private void ShowFaceShift(int face, double offset)
    {
        if (_face[face] is { } control)
        {
            var y = Math.Round(offset);
            control.RenderTransform = y == 0 ? null : new TranslateTransform(0, y);
        }
    }

    private static double Between(double from, double to, double travel) => from + (to - from) * travel;

    /// <summary>How tall <paramref name="control"/> is at <paramref name="width"/>, measured on its own.</summary>
    private static double Natural(Control control, double width)
    {
        control.Measure(new Size(width > 0 ? width : double.PositiveInfinity, double.PositiveInfinity));
        return control.DesiredSize.Height;
    }

    /// <summary>How tall and wide <paramref name="parts"/> stand together, top margin included, as a face's cell would hold them.</summary>
    private static (double Height, double Width) Extent(List<Control> parts) => parts.Count == 0
        ? (0, 0)
        : (parts[^1].Bounds.Bottom - parts[0].Bounds.Y + parts[0].Margin.Top, parts[0].Bounds.Width);

    private static void Detach(Control control) => (control.Parent as Avalonia.Controls.Panel)?.Children.Remove(control);

    /// <summary>The table's cells by row, top first, each with its resting opacity. A line down several rows arrives with the last.</summary>
    private static List<(Control Cell, double Resting)[]> RowsOf(Grid table)
    {
        var last = table.RowDefinitions.Count - 1;
        return table.Children
            .GroupBy(c => Grid.GetRowSpan(c) > 1 ? last : Grid.GetRow(c))
            .OrderBy(row => row.Key)
            .Select(row => row.Select(c => (c, c.Opacity)).ToArray())
            .ToList();
    }
}
