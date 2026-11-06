using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
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
/// <see cref="PanelStatistics"/> handed over when the panel opened, the rollup store
/// keeps its ledger at (date × model) grain and the totals were only ever the last
/// step, so flipping is a re-render of data in hand, which is what the issue's
/// "pre-loaded, no wait" requirement asks for.
///
/// <para>The flip is a crossfade between two faces that are both laid out all the time, so
/// nothing it does can change the tile's size. <see cref="ApplyView"/> is the only place the
/// finished state is written: an instant change goes straight there, and a flip ends there.</para>
/// </summary>
public partial class StatTile : UserControl
{
    private const double SegmentGap = PanelMetrics.TileSegmentGapDip;

    private const double DataEndRadius = PanelMetrics.TileDataEndRadiusDip;

    /// <summary>The glyph's turn on the breakdown. The mark is symmetric, so it rests the same.</summary>
    private const double GlyphOpenAngle = 180;

    /// <summary>How finely a face's travel is stepped: about a frame.</summary>
    private const double TravelStepMs = 16;

    /// <summary>How long after its last animation a flip is settled by the timer.</summary>
    private const double SettleMarginMs = 150;

    // ── hover timing for the per-model figures ────────────────────────────────────
    //
    // The values and the reasoning behind them now live in HoverCard, shared with every
    // other tooltip in the panel. Still applied to every element that carries one:
    // setting them once on the control and relying on property inheritance LOOKS right
    // and silently does not work, the segments resolved to the framework defaults
    // instead, which is invisible in a screenshot and was only caught by reporting the
    // values (--tile-samples writes hover-timing.txt for exactly this reason).

    private static void ApplyHoverTiming(DependencyObject element) => HoverCard.ApplyTiming(element);

    /// <summary>
    /// Both faces as Presentation built them, or an empty tile before the first
    /// <see cref="Populate"/>.
    /// </summary>
    private PanelTileView _view = new("", "", "", [], "", null);
    private bool _expanded;

    /// <summary>The fill the template stacks under the content for hover and keyboard focus.</summary>
    private readonly Border _hoverFill;

    /// <summary>Scales the bar's clip, from nothing to the bar's full width.</summary>
    private readonly ScaleTransform _barReveal = new();

    /// <summary>
    /// Which flip is the current one. A replaced animation can still raise <c>Completed</c> at
    /// its original time, even after being released, so each flip's handler checks it is still
    /// the current one before it hides a face a later click has brought back.
    /// </summary>
    private int _flip;
    private DispatcherTimer? _settleTimer;

    public StatTile()
    {
        InitializeComponent();

        Root.ApplyTemplate();
        _hoverFill = (Border)Root.Template.FindName("HoverFill", Root);

        Root.Click += (_, _) => Toggle();
        Root.MouseEnter += (_, _) => { UpdateGlyph(); UpdateFill(); };
        Root.MouseLeave += (_, _) => { UpdateGlyph(); UpdateFill(); };
        Root.IsKeyboardFocusedChanged += (_, _) => UpdateFill();
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
        // Same caveat, both views, see the XAML for why it is not treated as chrome. One
        // string now, so the two faces cannot be given different caveats.
        BreakdownNote.Text = view.Note;
        BreakdownNote.Visibility = view.Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // A tile with nothing to break down must not pretend to be clickable, an
        // affordance that leads nowhere is worse than none.
        Root.IsEnabled = view.CanFlip;
        Root.Cursor = view.CanFlip ? System.Windows.Input.Cursors.Hand : null;
        ExpandGlyph.Visibility = view.CanFlip ? Visibility.Visible : Visibility.Collapsed;

        // Built now, not on first click. Two reasons, and the first is the one that
        // bites: a Hidden element only reserves the space its CONTENT needs, so an
        // empty breakdown reserves nothing and the tile visibly grows the first time
        // it is opened. Building here gives the Grid both real sizes up front, so the
        // tile is one fixed height and the panel never jumps. It also means a flip has
        // nothing to build, which is what "no wait between clicks" asks for.
        BuildBreakdown();
        ApplyView();
    }

    private void Toggle()
    {
        _expanded = !_expanded;

        if (PanelMotion.Runs(WpfMotion.AnimationsOn, PanelMotion.Ms(PanelMotion.TileFaceInBaseMs)))
        {
            Flip();
        }
        else
        {
            ApplyView();
        }
    }

    /// <summary>
    /// Crossfades to the face <see cref="_expanded"/> asks for. Every animation starts from
    /// the value on screen, so a click mid-flip turns the tile round where it is. That is also
    /// why the old flip is simply replaced here rather than settled first.
    /// </summary>
    private void Flip()
    {
        var (going, goingShift, coming, comingShift) = _expanded
            ? (SummaryView, SummaryShift, BreakdownView, BreakdownShift)
            : (BreakdownView, BreakdownShift, SummaryView, SummaryShift);

        // Total to breakdown: the total leaves upward and the breakdown arrives from below.
        // The return is the reverse, which is the same as each face retracing its way.
        var travel = _expanded ? PanelMotion.TileFaceTravelDip : -PanelMotion.TileFaceTravelDip;

        // Hidden means the face left the screen completely, so this one starts from scratch.
        // Still visible means a click caught it leaving, and it comes back from where it is.
        var fresh = coming.Visibility != Visibility.Visible;
        if (fresh)
        {
            Place(coming, OpacityProperty, 0);
            Place(comingShift, TranslateTransform.YProperty, travel);
            coming.Visibility = Visibility.Visible;
        }

        coming.IsHitTestVisible = true;

        var flip = ++_flip;
        var outMs = PanelMotion.Ms(PanelMotion.TileFaceOutBaseMs);
        var inMs = PanelMotion.Ms(PanelMotion.TileFaceInBaseMs);
        var inDelayMs = PanelMotion.Ms(PanelMotion.TileFaceInDelayBaseMs);
        var turnMs = PanelMotion.Ms(PanelMotion.TileGlyphTurnBaseMs);

        // Completed MUST be attached before BeginAnimation: WPF animates its own copy of the
        // timeline, so a handler added afterwards is on an object nothing will ever raise.
        var fadeOut = WpfMotion.DelayedFade(going.Opacity, 0, outMs, 0, PanelMotion.Accelerate);
        fadeOut.Completed += (_, _) => Retire(flip, going);
        going.BeginAnimation(OpacityProperty, fadeOut);
        goingShift.BeginAnimation(
            TranslateTransform.YProperty, Travel(goingShift.Y, -travel, outMs, 0, PanelMotion.Accelerate));

        Run(coming, OpacityProperty, 1, inMs, inDelayMs, PanelMotion.Decelerate);
        comingShift.BeginAnimation(
            TranslateTransform.YProperty, Travel(comingShift.Y, 0, inMs, inDelayMs, PanelMotion.Decelerate));

        Run(GlyphTurn, RotateTransform.AngleProperty, _expanded ? GlyphOpenAngle : 0,
            turnMs, 0, PanelMotion.Decelerate);
        UpdateGlyph();

        var lastMs = Math.Max(inDelayMs + inMs, turnMs);
        if (_expanded)
        {
            lastMs = Math.Max(lastMs, RevealBreakdown(fresh));
        }

        // Settled by a timer rather than by a last Completed because which part ends last
        // depends on the direction and on the number of legend entries. It is also the
        // backstop for the Completed above: if that one is ever missed, the face that left
        // stays hit-testable at zero opacity, showing hover cards for a bar nobody can see.
        _settleTimer?.Stop();
        _settleTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(lastMs + SettleMarginMs),
        };
        _settleTimer.Tick += (_, _) => SettleFaces();
        _settleTimer.Start();
    }

    /// <summary>
    /// Draws the bar in and fades the legend in behind it. Returns when the last of it ends.
    /// </summary>
    /// <param name="fresh">
    /// True when the breakdown is arriving from nothing. False when a click caught it leaving,
    /// and whatever was on screen carries on: a bar already drawn is not drawn again.
    /// </param>
    private double RevealBreakdown(bool fresh)
    {
        var entries = LegendHost.Children.Cast<UIElement>().ToList();

        if (fresh)
        {
            // On the clip, never the star columns: those are what size the segments, and
            // animating them would change the shares the bar is there to show.
            BarHost.Clip = new RectangleGeometry(new Rect(0, 0, BarHost.ActualWidth, BarHost.ActualHeight))
            {
                Transform = _barReveal,
            };
            Place(_barReveal, ScaleTransform.ScaleXProperty, 0);
            entries.ForEach(entry => Place(entry, OpacityProperty, 0));
        }

        var barMs = PanelMotion.Ms(PanelMotion.TileModelBarBaseMs);
        var barDelayMs = PanelMotion.Ms(PanelMotion.TileModelBarDelayBaseMs);
        var legendMs = PanelMotion.Ms(PanelMotion.TileLegendFadeBaseMs);

        if (BarHost.Clip is not null)
        {
            Run(_barReveal, ScaleTransform.ScaleXProperty, 1, barMs, barDelayMs, PanelMotion.Decelerate);
        }

        for (var i = 0; i < entries.Count; i++)
        {
            Run(entries[i], OpacityProperty, 1, legendMs, PanelMotion.LegendStartMs(i), PanelMotion.Decelerate);
        }

        return Math.Max(barDelayMs + barMs, PanelMotion.LegendStartMs(entries.Count - 1) + legendMs);
    }

    /// <summary>Takes the face that has finished leaving out of the way of the pointer.</summary>
    private void Retire(int flip, FrameworkElement going)
    {
        if (flip != _flip)
        {
            return;
        }

        // Hidden, not Collapsed, see the XAML: it keeps the tile one fixed size.
        going.Visibility = Visibility.Hidden;
        going.IsHitTestVisible = false;
    }

    /// <summary>
    /// A face's travel on whole device pixels. Both windows draw Display-mode text, which
    /// shimmers when it is carried at a fractional offset, and the slow end of the curve is
    /// where that has time to show.
    /// </summary>
    private DoubleAnimationUsingKeyFrames Travel(
        double from, double to, double ms, double delayMs, PanelMotion.Curve curve)
    {
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        var animation = new DoubleAnimationUsingKeyFrames { Duration = WpfMotion.Duration(delayMs + ms) };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));

        for (var at = 0.0; at < ms; at += TravelStepMs)
        {
            var here = from + (to - from) * curve.ValueAt(at / ms);
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(
                Math.Round(here * scale) / scale,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMs + at))));
        }

        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(
            to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMs + ms))));
        return animation;
    }

    /// <summary>Animates from the value on screen, which is what a replaced animation hands on.</summary>
    private static void Run(
        DependencyObject target, DependencyProperty property, double to, double ms, double delayMs,
        PanelMotion.Curve curve) =>
        ((IAnimatable)target).BeginAnimation(
            property, WpfMotion.DelayedFade((double)target.GetValue(property), to, ms, delayMs, curve));

    /// <summary>
    /// Assigns a value at once. Released first because an animation holds its end value over
    /// the local one for as long as it is attached, so the assignment alone would be ignored.
    /// </summary>
    private static void Place(DependencyObject target, DependencyProperty property, double value)
    {
        ((IAnimatable)target).BeginAnimation(property, null);
        target.SetValue(property, value);
    }

    /// <summary>
    /// The hover timing as it actually resolves on a bar segment. The values are set on
    /// the control and reach the segments by property inheritance, which is the part that
    /// can silently fail, a segment built in code and parented at the wrong moment would
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
    /// it. Built through the same path the real cards use, a hand-made copy would prove
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

    /// <summary>
    /// Clicks the tile by raising its button's <c>Click</c>, the event the mouse and the
    /// keyboard both end in, at the moment the caller chooses. An automation peer would deliver
    /// it on a later dispatcher turn.
    /// </summary>
    internal void ClickForVerification() =>
        Root.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, Root));

    /// <summary>
    /// What a flip has put on screen, for <c>--motion-check</c>: the face the tile has been
    /// asked for, whether a flip is still running, and each face's visibility and opacity. A face
    /// that is <c>Hidden</c> is reset to full opacity when the flip settles, so its opacity says
    /// nothing and only a visible face's is worth reading.
    /// </summary>
    internal FlipReadout Readout => new(
        _expanded, _settleTimer is not null,
        SummaryView.Visibility, SummaryView.Opacity,
        BreakdownView.Visibility, BreakdownView.Opacity,
        GlyphTurn.Angle, BarHost.Clip is not null);

    /// <summary>See <see cref="Readout"/>.</summary>
    internal readonly record struct FlipReadout(
        bool Expanded, bool Flipping,
        Visibility Summary, double SummaryOpacity,
        Visibility Breakdown, double BreakdownOpacity,
        double GlyphAngle, bool BarClipped);

    /// <summary>
    /// Puts the tile in its finished state at once with nothing in flight: the path for
    /// <see cref="Populate"/>, the verification renders, and Windows' animations being off.
    /// </summary>
    private void ApplyView()
    {
        SettleFaces();
        UpdateGlyph(animate: false);
        UpdateFill(animate: false);
    }

    /// <summary>
    /// The finished faces, bar, legend and turn. A flip ends here too, so this leaves the hover
    /// state alone: the pointer may have moved, and that fade has its own timing.
    /// </summary>
    private void SettleFaces()
    {
        _flip++;
        _settleTimer?.Stop();
        _settleTimer = null;

        Place(SummaryView, OpacityProperty, 1);
        Place(SummaryShift, TranslateTransform.YProperty, 0);
        Place(BreakdownView, OpacityProperty, 1);
        Place(BreakdownShift, TranslateTransform.YProperty, 0);
        Place(GlyphTurn, RotateTransform.AngleProperty, _expanded ? GlyphOpenAngle : 0);

        BarHost.Clip = null;
        Place(_barReveal, ScaleTransform.ScaleXProperty, 1);
        foreach (UIElement entry in LegendHost.Children)
        {
            Place(entry, OpacityProperty, 1);
        }

        // Hidden, not Collapsed, see the XAML: it keeps the tile one fixed size.
        SummaryView.Visibility = _expanded ? Visibility.Hidden : Visibility.Visible;
        SummaryView.IsHitTestVisible = !_expanded;
        BreakdownView.Visibility = _expanded ? Visibility.Visible : Visibility.Hidden;
        BreakdownView.IsHitTestVisible = _expanded;
    }

    private void UpdateGlyph(bool animate = true)
    {
        var opacity = _expanded
            ? PanelMetrics.TileGlyphOpenOpacity
            : Root.IsMouseOver ? PanelMetrics.TileGlyphHoverOpacity : PanelMetrics.TileGlyphIdleOpacity;

        Ease(ExpandGlyph, opacity, PanelMotion.Ms(PanelMotion.TileGlyphFadeBaseMs), PanelMotion.Decelerate, animate);
    }

    /// <summary>
    /// Fades the hover and keyboard-focus fill. IsEnabled is false when there is nothing to
    /// break down, so a tile that cannot expand never offers the affordance.
    /// </summary>
    private void UpdateFill(bool animate = true)
    {
        var lit = (Root.IsEnabled && Root.IsMouseOver) || Root.IsKeyboardFocused;

        if (lit)
        {
            Ease(_hoverFill, 1, PanelMotion.Ms(PanelMotion.HoverFillInBaseMs), PanelMotion.Decelerate, animate);
        }
        else
        {
            Ease(_hoverFill, 0, PanelMotion.Ms(PanelMotion.HoverFillOutBaseMs), PanelMotion.Accelerate, animate);
        }
    }

    /// <summary>Moves an opacity to <paramref name="to"/>, at once when animations are off.</summary>
    private static void Ease(UIElement target, double to, double ms, PanelMotion.Curve curve, bool animate)
    {
        if (animate && PanelMotion.Runs(WpfMotion.AnimationsOn, ms))
        {
            Run(target, OpacityProperty, to, ms, 0, curve);
        }
        else
        {
            Place(target, OpacityProperty, to);
        }
    }

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

            // Square at the baseline, 4px rounded at the data end, the bar grows from
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

        // Unpriced models cannot be placed on a value chart, their worth is unknown,
        // not zero, so they are named rather than quietly missing from a total the
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
        // Text still wears text tokens, the swatch beside it carries identity, and a
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
    /// the raw model id underneath. This is the tile's table view, the relief the
    /// colour rules require where a series sits below 3:1 on the light surface, so it
    /// is built as a small piece of the panel's own design rather than left as a line of
    /// system-chrome text.
    ///
    /// The swatch matters beyond decoration: it ties the card to the exact colour being
    /// pointed at, which is the one thing a floating card otherwise loses.
    /// </summary>
    private ToolTip SliceTooltip(PanelTileSegmentView slice, Brush swatch) =>
        // Figure first, model id beneath, swatch alongside, the shared card shape, which
        // this one defined before the graph needed it too. Both lines are the view model's;
        // see PanelTileSegmentView.Identity for why the second is never suppressed.
        HoverCard.Figure(this, slice.Figure, slice.Identity, swatch);

    /// <summary>A styled hover card carrying a sentence, used for the caption's caveat.</summary>
    private ToolTip TextTooltip(string text) => HoverCard.Text(this, text);

    /// <summary>
    /// The brush for each segment's slot. Which slot a segment takes is the view model's,
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
