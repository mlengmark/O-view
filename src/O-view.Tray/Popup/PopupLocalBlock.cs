using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OView.App.Rendering;
using OView.Core.Models;
using OView.Presentation.Panel;
using OView.Presentation.Text;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Point = System.Windows.Point;
using RadioButton = System.Windows.Controls.RadioButton;
using Size = System.Windows.Size;

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
///
/// <para><b>The flip tag turns the block over</b> with the tile's crossfade: the two faces sit in
/// one cell, the height is pinned on that cell and moved to the new face's, and every part starts
/// from what is on screen, so a second press turns the block round where it stands.</para>
/// </summary>
/// <param name="refitForPendingGrowth">Re-fits the panel for height the table is about to gain or lose.</param>
/// <param name="redock">Re-docks the panel at its current height.</param>
internal sealed class PopupLocalBlock(
    Window owner, Border block, LetterSpacedText title, TextBlock subtitle,
    Button flipTag, Border hoverCue, Grid faces,
    FrameworkElement totalsFace, Canvas chartHost, FrameworkElement chartLine, Grid tableHost,
    FrameworkElement modelFaceHost, PopupModelFace modelFace,
    TextBlock windowCaveat, TextBlock scopeNote,
    FrameworkElement footer, TextBlock footerCaveat,
    RadioButton outputSegment, RadioButton overallSegment, Border highlight,
    Action<PanelViewChoice> choose, Action<double> refitForPendingGrowth, Action redock)
{
    /// <summary>Space from the title's line to the chart, and from the table to the footer.</summary>
    private const double BodyGapDip = 8;

    /// <summary>How long after its last animation a flip is settled by the timer.</summary>
    private const double SettleMarginMs = 150;

    /// <summary>The hover outline's strength, on its brush because its opacity is what fades.</summary>
    private const double CueOutlineStrength = 0.35;

    /// <summary>One half turn of the two-arrow mark.</summary>
    private const double HalfTurnDegrees = 180;

    private static readonly double HighlightMs = PanelMotion.Ms(PanelMotion.SwitchHighlightBaseMs);
    private static readonly double FaceOutMs = PanelMotion.Ms(PanelMotion.TileFaceOutBaseMs);
    private static readonly double FaceInMs = PanelMotion.Ms(PanelMotion.TileFaceInBaseMs);
    private static readonly double FaceInDelayMs = PanelMotion.Ms(PanelMotion.TileFaceInDelayBaseMs);
    private static readonly double MarkTurnMs = PanelMotion.Ms(PanelMotion.TileGlyphTurnBaseMs);
    private static readonly double FlipHeightMs = PanelMotion.Ms(PanelMotion.BlockFlipHeightBaseMs);

    private readonly PopupGraph _graph = new(owner, chartHost);
    private readonly PopupTotalsTable _table = new(owner, tableHost, refitForPendingGrowth, redock);
    private readonly TranslateTransform _highlightOffset = (TranslateTransform)highlight.RenderTransform;
    private readonly TranslateTransform _totalsShift = new();
    private readonly TranslateTransform _modelShift = new();
    private readonly SolidColorBrush _tagText = new();

    /// <summary>A flip is one gesture, so its height takes one figure and one curve both ways.</summary>
    private readonly DisclosureAnimation _faceHeight = new(
        owner, faces, chevron: null, FlipHeightMs, FlipHeightMs,
        new DisclosureRowMotion(_ => 0, 0, 0, 0), redock, PanelMotion.Decelerate)
    {
        Settled = () => faces.ClipToBounds = false,
    };

    private IReadOnlyList<PanelMeasureOption> _options = [];
    private PanelLocalChartView? _chart;
    private bool _populating;
    private bool _showingModels;

    /// <summary>Filled from the flip tag's template, which exists only once it is applied.</summary>
    private Border _tagFill = null!;

    /// <summary>
    /// Made here because a transform written in a template is frozen and cannot be animated. Its
    /// centre is the arrows' own drawing: a <c>Path</c>'s box includes its stroke, so 0.5,0.5 would be off.
    /// </summary>
    private readonly RotateTransform _markTurn = new() { CenterX = 4, CenterY = 5 };

    /// <summary>Half turns of the mark from its resting angle: one each for the face and the hover.</summary>
    private int _markTurns;
    private bool _cueOn;

    /// <summary>
    /// Which flip is the current one: a replaced fade can still raise <c>Completed</c> at its
    /// original time, and must not hide a face a later press has brought back.
    /// </summary>
    private int _flip;
    private DispatcherTimer? _flipSettle;

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

        totalsFace.RenderTransform = _totalsShift;
        modelFaceHost.RenderTransform = _modelShift;

        flipTag.ApplyTemplate();
        _tagFill = (Border)flipTag.Template.FindName("HoverFill", flipTag);
        ((UIElement)flipTag.Template.FindName("Mark", flipTag)).RenderTransform = _markTurn;
        flipTag.Foreground = _tagText;

        block.MouseEnter += (_, _) => ShowCue(true);
        block.MouseLeave += (_, _) => ShowCue(false);

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

    /// <summary>
    /// Presses the flip tag by raising the <c>Click</c> the mouse and the keyboard both end in, at
    /// the moment <c>--motion-check</c> chooses.
    /// </summary>
    public void ClickFlipForVerification() => flipTag.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, flipTag));

    /// <summary>
    /// What a flip has put on screen, for <c>--motion-check</c>. A collapsed face is reset to full
    /// opacity when the flip settles, so only a visible face's opacity is worth reading.
    /// </summary>
    public FlipReadout Readout => new(
        _showingModels, _flipSettle is not null,
        totalsFace.Visibility, totalsFace.Opacity,
        modelFaceHost.Visibility, modelFaceHost.Opacity,
        _faceHeight.IsMoving || !double.IsNaN(faces.Height), faces.ClipToBounds);

    /// <summary>See <see cref="Readout"/>.</summary>
    public readonly record struct FlipReadout(
        bool ShowingModels, bool Flipping,
        Visibility Totals, double TotalsOpacity,
        Visibility Models, double ModelsOpacity,
        bool HeightPinned, bool Clipped);

    public void Populate(LocalBlockContent content)
    {
        _highlightOffset.BeginAnimation(TranslateTransform.XProperty, null);
        highlight.BeginAnimation(FrameworkElement.WidthProperty, null);

        // Every open shows the remembered view settled, whatever a closed panel was in the middle of.
        SettleFaces();
        _faceHeight.Apply(expanded: true);
        faces.ClipToBounds = false;
        ResetCue();

        ShowWords(content);
        ShowFace(content);
        PlaceHighlight();
    }

    /// <summary>
    /// The switch was pressed: the words change at once, because a figure is real from its first
    /// frame, while the highlight, the chart and the table each move to the new view from wherever
    /// they are on screen. The flip tag lands here too. Changing the measure on the model face has
    /// nothing to move from, so it happens at once, and so does a flip with Windows' animations off.
    /// </summary>
    public void SwitchTo(LocalBlockContent content)
    {
        var staysOnTotals = !_showingModels && content.Models is null;
        var turning = _showingModels != content.Models is not null;

        ShowWords(content);
        SlideHighlight();

        if (turning && PanelMotion.Runs(WpfMotion.AnimationsOn, FaceInMs))
        {
            Turn(content);
            return;
        }

        // The switch pressed mid-flip: the block keeps turning and lands on the new measure.
        if (!turning && _flipSettle is not null)
        {
            Retarget(content);
            return;
        }

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
        if (_showingModels)
        {
            modelFace.FillIn(PanelMotion.Ms(PanelMotion.DoughnutSweepOpenDelayBaseMs), PanelMotion.ModelRowOpenStartMs);
        }
        else
        {
            _graph.Rise(PanelMotion.ChartBarStartMs);
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

        FillFaces(content);
    }

    private void FillFaces(LocalBlockContent content)
    {
        _table.Populate(content.Totals.Table);
        _chart = content.Totals.Chart;

        if (content.Models is { } models)
        {
            modelFace.Populate(models);
        }
    }

    /// <summary>
    /// Turns the block to the view's face from whatever is on screen: the tile's crossfade, with
    /// the height moving to the new face's and the panel re-docking on every frame of it.
    ///
    /// <para>A face still visible is one a press caught leaving, so it comes back from where it
    /// is, with its contents and arrivals left running. Only a face that has fully left is filled
    /// and fills in again.</para>
    /// </summary>
    private void Turn(LocalBlockContent content)
    {
        var (coming, comingShift, going, goingShift) = content.Models is not null
            ? (modelFaceHost, _modelShift, totalsFace, _totalsShift)
            : (totalsFace, _totalsShift, modelFaceHost, _modelShift);

        // Totals to models: the totals leave upward and the models arrive from below.
        var travel = content.Models is not null ? PanelMotion.TileFaceTravelDip : -PanelMotion.TileFaceTravelDip;

        // Read before Prepare stops the height, which is the one reporting it.
        var from = _faceHeight.Current.Height;
        var fresh = coming.Visibility != Visibility.Visible;

        _showingModels = content.Models is not null;
        _faceHeight.Prepare(from);
        faces.ClipToBounds = true;

        if (fresh)
        {
            Place(coming, UIElement.OpacityProperty, 0);
            Place(comingShift, TranslateTransform.YProperty, travel);
            coming.Visibility = Visibility.Visible;
            FillFaces(content);
        }

        coming.IsHitTestVisible = true;
        going.IsHitTestVisible = false;

        // A face filled here is at rest and may be rebuilt for the density; one coming back has
        // rows mid-fade, and a rebuild would snap them.
        modelFace.HoldRows = !fresh;
        MoveHeightTo(coming, from);
        modelFace.HoldRows = true;

        var flip = ++_flip;

        // Completed MUST be attached before BeginAnimation: WPF animates its own copy of the
        // timeline, so a handler added afterwards is on an object nothing will ever raise.
        var fadeOut = WpfMotion.DelayedFade(going.Opacity, 0, FaceOutMs, 0, PanelMotion.Accelerate);
        fadeOut.Completed += (_, _) => Retire(flip, going);
        going.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        goingShift.BeginAnimation(TranslateTransform.YProperty, WpfMotion.PixelTravel(
            owner, goingShift.Y, -travel, FaceOutMs, 0, PanelMotion.Accelerate));

        coming.BeginAnimation(UIElement.OpacityProperty, WpfMotion.DelayedFade(
            coming.Opacity, 1, FaceInMs, FaceInDelayMs, PanelMotion.Decelerate));
        comingShift.BeginAnimation(TranslateTransform.YProperty, WpfMotion.PixelTravel(
            owner, comingShift.Y, 0, FaceInMs, FaceInDelayMs, PanelMotion.Decelerate));

        TurnMark(content.Models is not null ? 1 : -1);

        var lastMs = Math.Max(FaceInDelayMs + FaceInMs, Math.Max(MarkTurnMs, FlipHeightMs));
        if (fresh)
        {
            lastMs = Math.Max(lastMs, _showingModels
                ? modelFace.FillIn(PanelMotion.Ms(PanelMotion.DoughnutSweepDelayBaseMs), PanelMotion.ModelRowStartMs)
                : _graph.Rise(PanelMotion.ChartBarBackStartMs));
        }

        // A timer rather than a last Completed because which part ends last depends on the
        // direction and on the number of rows. It is also the backstop for the Completed above:
        // without it a missed one leaves a face hit-testable at zero opacity, showing hover cards
        // for rows nobody can see.
        _flipSettle?.Stop();
        _flipSettle = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(lastMs + SettleMarginMs),
        };
        _flipSettle.Tick += (_, _) => SettleFaces();
        _flipSettle.Start();
    }

    /// <summary>
    /// The measure changed while the block is turning: the coming face takes the new figures at
    /// once, because a figure is real from its first frame, and the height aims at its new height.
    /// </summary>
    private void Retarget(LocalBlockContent content)
    {
        var from = _faceHeight.Current.Height;
        _faceHeight.Prepare(from);
        faces.ClipToBounds = true;

        _table.Populate(content.Totals.Table);
        if (content.Models is { } models)
        {
            modelFace.Populate(models);
        }
        else
        {
            _chart = content.Totals.Chart;
            _graph.MoveTo(content.Totals.Chart);
        }

        MoveHeightTo(_showingModels ? modelFaceHost : totalsFace, from);
    }

    /// <summary>
    /// Re-fits the panel for the height the face is about to take, before anything is laid out at
    /// the far end of the move, and then moves the pinned height there.
    /// </summary>
    private void MoveHeightTo(FrameworkElement face, double from)
    {
        refitForPendingGrowth(NaturalHeight(face) - from);
        owner.UpdateLayout();
        redock();

        // Again, because the re-fit may have changed the density the face is built at.
        _faceHeight.Resize(from, NaturalHeight(face));
    }

    private double NaturalHeight(FrameworkElement face)
    {
        face.Measure(new Size(faces.ActualWidth, double.PositiveInfinity));
        return face.DesiredSize.Height;
    }

    /// <summary>Takes the face that has finished leaving off the screen and away from the pointer.</summary>
    private void Retire(int flip, FrameworkElement going)
    {
        if (flip == _flip)
        {
            going.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>The finished faces, with nothing in flight: the end of a flip, and every open.</summary>
    private void SettleFaces()
    {
        _flip++;
        _flipSettle?.Stop();
        _flipSettle = null;

        Place(totalsFace, UIElement.OpacityProperty, 1);
        Place(_totalsShift, TranslateTransform.YProperty, 0);
        Place(modelFaceHost, UIElement.OpacityProperty, 1);
        Place(_modelShift, TranslateTransform.YProperty, 0);

        totalsFace.Visibility = _showingModels ? Visibility.Collapsed : Visibility.Visible;
        totalsFace.IsHitTestVisible = true;
        modelFaceHost.Visibility = _showingModels ? Visibility.Visible : Visibility.Collapsed;
        modelFaceHost.IsHitTestVisible = true;

        modelFace.ReleaseRows();
    }

    /// <summary>Assigns at once. Released first, or a held end value would ignore the assignment.</summary>
    private static void Place(DependencyObject target, DependencyProperty property, double value)
    {
        ((IAnimatable)target).BeginAnimation(property, null);
        target.SetValue(property, value);
    }

    /// <summary>
    /// Draws the chart against the height the fit gave it. Separate from <see cref="Populate"/>
    /// because it needs the canvas's laid-out width.
    /// </summary>
    public void DrawChart()
    {
        if (!_showingModels && _chart is { } chart)
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
        Fade(fill, segment.IsMouseOver && segment.IsChecked != true, animate);
    }

    /// <summary>A hover layer in or out, from where it has got to.</summary>
    private static void Fade(UIElement layer, bool lit, bool animate)
    {
        var from = layer.Opacity;
        var to = lit ? 1.0 : 0.0;
        var ms = PanelMotion.Ms(lit ? PanelMotion.HoverFillInBaseMs : PanelMotion.HoverFillOutBaseMs);

        layer.Opacity = to;

        if (animate && PanelMotion.Runs(WpfMotion.AnimationsOn, ms))
        {
            layer.BeginAnimation(UIElement.OpacityProperty, WpfMotion.Travel(
                from, to, ms, curve: lit ? PanelMotion.Decelerate : PanelMotion.Accelerate));
        }
        else
        {
            layer.BeginAnimation(UIElement.OpacityProperty, null);
        }
    }

    /// <summary>
    /// The pointer over the block says it can be turned: the tag brightens, its mark makes the
    /// flip's half turn, and a faint outline shows the extent of what would turn.
    /// </summary>
    private void ShowCue(bool on)
    {
        if (on == _cueOn)
        {
            return;
        }

        _cueOn = on;
        Fade(_tagFill, on, animate: true);
        Fade(hoverCue, on, animate: true);
        TurnMark(on ? 1 : -1);

        var ms = PanelMotion.Ms(on ? PanelMotion.HoverFillInBaseMs : PanelMotion.HoverFillOutBaseMs);
        var from = _tagText.Color;
        var to = Colour(on ? "TextPrimary" : "TextMuted");
        _tagText.Color = to;

        if (PanelMotion.Runs(WpfMotion.AnimationsOn, ms))
        {
            _tagText.BeginAnimation(SolidColorBrush.ColorProperty, WpfMotion.TravelColor(
                from, to, ms, on ? PanelMotion.Decelerate : PanelMotion.Accelerate));
        }
        else
        {
            _tagText.BeginAnimation(SolidColorBrush.ColorProperty, null);
        }
    }

    /// <summary>The cue at rest in this open's theme, which is what every panel opens with.</summary>
    private void ResetCue()
    {
        _cueOn = false;
        _markTurns = 0;
        Place(_markTurn, RotateTransform.AngleProperty, 0);
        Fade(_tagFill, lit: false, animate: false);
        Fade(hoverCue, lit: false, animate: false);

        _tagText.BeginAnimation(SolidColorBrush.ColorProperty, null);
        _tagText.Color = Colour("TextMuted");

        var outline = ((SolidColorBrush)owner.FindResource("PanelBorder")).Clone();
        outline.Opacity = CueOutlineStrength;
        hoverCue.BorderBrush = outline;
    }

    /// <summary>
    /// A half turn of the mark, from where it has got to. Counted rather than aimed at an angle,
    /// because the hover and the flip each make one and either can arrive during the other.
    /// </summary>
    private void TurnMark(int halfTurns)
    {
        _markTurns += halfTurns;
        var from = _markTurn.Angle;
        var to = _markTurns * HalfTurnDegrees;

        _markTurn.Angle = to;

        if (PanelMotion.Runs(WpfMotion.AnimationsOn, MarkTurnMs))
        {
            _markTurn.BeginAnimation(RotateTransform.AngleProperty, WpfMotion.Travel(from, to, MarkTurnMs));
        }
        else
        {
            _markTurn.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }

    private System.Windows.Media.Color Colour(string key) => ((SolidColorBrush)owner.FindResource(key)).Color;
}
