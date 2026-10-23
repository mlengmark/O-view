using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OView.App.Rendering;
using OView.Core.Models;
using OView.Presentation.Panel;
using OView.Presentation.Text;
using Brush = System.Windows.Media.Brush;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using RadioButton = System.Windows.Controls.RadioButton;
using ToolTip = System.Windows.Controls.ToolTip;

namespace OView.Tray.Popup;

/// <summary>
/// Draws the token composition disclosure: the Bars/Breakdown switch, the per-window bars
/// with their legends, and the table they fold open into.
///
/// <para><b>The panel is never laid out at the far end of the fold.</b> The obvious order,
/// apply the final state, lay it out, then animate back from where the fold was, puts the
/// window at its full height for the length of that layout, still docked for the height it
/// had. It does not stay there, but it is presented: <c>--fold-check</c>'s screen grabs
/// caught the frame, a whole panel 72 px down with its bottom inside the taskbar. So the
/// end point is measured off the body alone, the body is pinned at the fold's starting
/// height, and every size the window takes from here is one the fold puts it at and
/// re-docks it for.</para>
/// </summary>
internal sealed class PopupTokenComposition
{
    private readonly FrameworkElement _owner;
    private readonly DisclosureAnimation _disclosure;
    private readonly RadioButton _barsToggle;
    private readonly RadioButton _breakdownToggle;
    private readonly StackPanel _barsView;
    private readonly StackPanel _breakdownView;
    private readonly FrameworkElement _section;
    private readonly TextBlock _heading;
    private readonly TextBlock _coverageLine;
    private readonly Action<double> _refitForPendingGrowth;
    private readonly Action _redock;

    private bool _expanded;

    public PopupTokenComposition(
        FrameworkElement owner, DisclosureAnimation disclosure,
        RadioButton barsToggle, RadioButton breakdownToggle,
        StackPanel barsView, StackPanel breakdownView, FrameworkElement section,
        TextBlock heading, TextBlock coverageLine,
        Action<double> refitForPendingGrowth, Action redock)
    {
        _owner = owner;
        _disclosure = disclosure;
        _barsToggle = barsToggle;
        _breakdownToggle = breakdownToggle;
        _barsView = barsView;
        _breakdownView = breakdownView;
        _section = section;
        _heading = heading;
        _coverageLine = coverageLine;
        _refitForPendingGrowth = refitForPendingGrowth;
        _redock = redock;

        // Checked fires on the segment being turned ON only, so one handler per segment is
        // one handler per state, no need to read IsChecked back off the group.
        barsToggle.Checked += (_, _) => Show(false);
        breakdownToggle.Checked += (_, _) => Show(true);
    }

    /// <summary>
    /// Where the disclosure row sits on the physical screen, so <c>--fold-check</c> can cut
    /// its filmstrip to the band that actually moves rather than to the whole panel.
    /// </summary>
    public double ScreenTop => _barsToggle.PointToScreen(new Point(0, 0)).Y;

    /// <summary>
    /// The folding body's current height, what <c>--fold-check</c> samples.
    ///
    /// <para>Read from the <c>Height</c> property rather than from <c>ActualHeight</c>: a
    /// dependency property returns its animated value, while <c>ActualHeight</c> reported the
    /// settled layout throughout the first trace and made a working fold look instant. Auto
    /// (NaN) means nothing is animating, so the arranged height is the answer.</para>
    /// </summary>
    public double Height
    {
        get
        {
            if (_breakdownView.Visibility != Visibility.Visible)
            {
                return 0;
            }

            var height = (double)_breakdownView.GetValue(FrameworkElement.HeightProperty);
            return double.IsNaN(height) ? _breakdownView.ActualHeight : height;
        }
    }

    /// <summary>
    /// Opens the breakdown for a verification render. The view resets to Bars on every
    /// Populate, so a sample of the expanded state cannot be produced any other way, and an
    /// unrendered state is how the no-data banner spent its whole life saying the wrong
    /// thing (issues #58, #170).
    /// </summary>
    public void ExpandForVerification() => SetExpanded(true);

    /// <summary>
    /// Folds the breakdown from outside, for <c>--fold-check</c>. The real transition,
    /// not the instant state: an animation's completion is the thing being checked, and this
    /// app has shipped a missed one before (<see cref="FlyoutAnimation"/>).
    /// </summary>
    public void ToggleForVerification() => Show(!_expanded);

    /// <summary>
    /// Freezes the disclosure partway open for a verification render. On a real desktop this
    /// state exists for about a tenth of a second, which is long enough to look wrong and far
    /// too short to inspect, and the failure it guards against, content drawn outside the
    /// height it has been given, is invisible at both ends of the fold.
    /// </summary>
    /// <param name="fraction">How far open, 0–1.</param>
    public void RevealForVerification(double fraction)
    {
        // Laid out fully open first: the fold's geometry is a fraction of the body's natural
        // height, which is only known once it has been measured at that height.
        SetExpanded(true);
        _owner.UpdateLayout();
        _disclosure.ApplyPartial(fraction, _breakdownView.ActualHeight);
    }

    /// <summary>
    /// Switches between the two views of what was billed, as a fold rather than as a jump.
    /// </summary>
    private void Show(bool expanded)
    {
        if (_expanded == expanded)
        {
            return;
        }

        _expanded = expanded;
        RunFold(expanded, _barsView.ActualWidth);
    }

    /// <summary>
    /// The fold itself.
    ///
    /// <para><b>Kept as one method, because what makes it correct is easy to leave out.</b> The
    /// re-fit below is one line and looks optional; without it a panel that fitted closed and
    /// does not fit open is clipped by the taskbar, which is the reported failure this whole
    /// path exists to prevent. A hand-written copy of this method would work on the developer's
    /// tall display and fail on a short one.</para>
    /// </summary>
    /// <param name="availableWidth">
    /// The text column the body wraps within, a disclosure measures against the line it sits
    /// under, not against the panel.
    /// </param>
    private void RunFold(bool expanded, double availableWidth)
    {
        // Captured before anything moves, so a click landing mid-fold continues from what is
        // on screen rather than snapping to one end and travelling the whole way again.
        var (fromHeight, fromAngle) = _disclosure.Current;
        var toHeight = expanded ? _disclosure.MeasureNatural(availableWidth) : 0;

        _disclosure.Prepare(fromHeight);

        // Expanding is exactly when the panel crosses the work area, so the density is
        // re-decided here rather than only at open, a panel that fitted closed and does
        // not fit open is the whole reported case.
        _refitForPendingGrowth(toHeight - fromHeight);

        _owner.UpdateLayout();
        _redock();

        _disclosure.Run(expanded, fromHeight, fromAngle, toHeight);
    }

    /// <summary>
    /// The four-way split behind today's token total, and why it dwarfs the context figure
    /// in Claude's own UI (issue #169).
    ///
    /// <para>Hidden rather than shown empty when there is nothing to break down: a
    /// composition of zero explains nothing, and the scope note that appears in that state
    /// is already saying the useful thing.</para>
    ///
    /// <para>The figures stay on screen; the prose folds behind a disclosure. It was four
    /// lines of standing text that every user read on every open, and it answers a question
    /// only some of them have, but the ones who do have it are looking straight at the
    /// number that prompted it, so the answer stays one click away rather than moving to a
    /// document nobody opens.</para>
    ///
    /// <para>Collapsed on every Populate, deliberately: the panel is a transient view, not a
    /// setting, and StatTile resets its own expansion for the same reason.</para>
    /// </summary>
    public void Populate(PanelTokenBarsView view)
    {
        var show = view.Show;
        _section.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        _barsView.Children.Clear();
        _breakdownView.Children.Clear();

        if (!show)
        {
            _breakdownView.Visibility = Visibility.Collapsed;
            return;
        }

        _heading.Text = view.Heading;
        _barsToggle.Content = view.BarsLabel;
        _breakdownToggle.Content = view.BreakdownLabel;

        AddBar(view.Today, first: true);
        AddBar(view.Window31, first: false);

        _breakdownView.Children.Add(BuildBreakdown(view.Breakdown));

        PopupText.SetLine(_coverageLine, view.CoverageLine);

        // Back to Bars on every open. The panel is a transient view, not a setting, the same
        // reason StatTile resets its own expansion.
        SetExpanded(false);
    }

    /// <summary>
    /// One composition bar: heading and billed total, the segmented track, and the legend
    /// that carries every exact figure the track's floors do not.
    /// </summary>
    private void AddBar(PanelTokenBarView bar, bool first)
    {
        var block = new StackPanel { Margin = new Thickness(0, first ? 0 : 9, 0, 0) };

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = bar.Heading,
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Bottom,
            Foreground = (Brush)_owner.FindResource("TextSecondary"),
        };
        Grid.SetColumn(title, 0);
        head.Children.Add(title);

        var total = new TextBlock
        {
            Text = bar.Total,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)_owner.FindResource("TextPrimary"),
        };
        Grid.SetColumn(total, 1);
        head.Children.Add(total);
        block.Children.Add(head);

        var segments = bar.Segments;

        // The track is measured, not assumed: the panel's content width moves with density
        // and DPI, and TokenBarGeometry's floor is expressed in pixels. Widths are therefore
        // applied on SizeChanged rather than at build time, when ActualWidth is still zero.
        var track = new Grid { Height = 12, Margin = new Thickness(0, 4, 0, 0) };
        for (var i = 0; i < segments.Count; i++)
        {
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });

            var slice = segments[i];
            var swatch = KindBrush(slice.Kind);
            var piece = new Border
            {
                Background = swatch,
                // Rounded only at the ends. A Border does not clip its children to a corner
                // radius, so a rounded container over square segments shows the corners
                // through, this gets the pill without a hand-maintained Clip geometry.
                CornerRadius = new CornerRadius(
                    i == 0 ? 3 : 0, i == segments.Count - 1 ? 3 : 0,
                    i == segments.Count - 1 ? 3 : 0, i == 0 ? 3 : 0),
                ToolTip = SegmentCard(slice, bar.Window, swatch),
            };
            HoverCard.ApplyTiming(piece);
            Grid.SetColumn(piece, i);
            track.Children.Add(piece);
        }

        track.SizeChanged += (_, _) => LayOutSegments(track, bar.Composition);
        block.Children.Add(track);

        // Every figure the bar cannot draw honestly. This is what makes the 3 px floor safe,
        // so it is not optional decoration, see TokenBarGeometry.
        var legend = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        for (var i = 0; i < segments.Count; i++)
        {
            var slice = segments[i];
            var swatch = KindBrush(slice.Kind);
            var entry = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 10, 0),
                ToolTip = SegmentCard(slice, bar.Window, swatch),
            };
            HoverCard.ApplyTiming(entry);

            entry.Children.Add(new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(2),
                Background = swatch,
                VerticalAlignment = VerticalAlignment.Center,
            });
            entry.Children.Add(new TextBlock
            {
                Text = bar.Legend[i].Text,
                FontSize = 10.5,
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)_owner.FindResource("TextSecondary"),
            });
            legend.Children.Add(entry);
        }

        block.Children.Add(legend);
        _barsView.Children.Add(block);
    }

    /// <summary>
    /// Applies the shared geometry to a laid-out track. Called on every size change, so the
    /// floors are re-decided against the width the bar actually got rather than a guess.
    /// </summary>
    private static void LayOutSegments(Grid track, TokenComposition composition)
    {
        var segments = TokenBarGeometry.Segments(composition, track.ActualWidth);
        if (segments.Count != track.ColumnDefinitions.Count)
        {
            return;
        }

        for (var i = 0; i < segments.Count; i++)
        {
            track.ColumnDefinitions[i].Width = new GridLength(segments[i].Width);
        }
    }

    /// <summary>
    /// The card behind one segment. Built through <see cref="HoverCard.Figure"/> rather than
    /// as a third card shape, a kind's figure with a line naming it is exactly what that
    /// shape is for, and the panel has shipped two unrelated tooltip designs before.
    /// </summary>
    private ToolTip SegmentCard(TokenKindSlice slice, string window, Brush swatch) =>
        HoverCard.Figure(
            _owner,
            FormatTokens(slice.Tokens),
            TokenText.TokenCardCaption(slice.Kind, slice.Share, slice.EstUsdValue.Value, window),
            swatch);

    private Brush KindBrush(TokenKind kind) =>
        (Brush)_owner.FindResource(TokenBarGeometry.PaletteKey(kind));

    /// <summary>
    /// The breakdown table, laid out from the rows <see cref="PanelSections.TokenBars"/> built:
    /// the header row muted and small, each kind's row with its colour chip, the total row bold.
    /// </summary>
    private Grid BuildBreakdown(IReadOnlyList<PanelTokenBreakdownRow> rows)
    {
        var table = new Grid();
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var c = 0; c < 4; c++)
        {
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        var muted = (Brush)_owner.FindResource("TextMuted");
        var secondary = (Brush)_owner.FindResource("TextSecondary");
        var primary = (Brush)_owner.FindResource("TextPrimary");

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var (foreground, size, bold) = row.RowKind switch
            {
                PanelTokenRowKind.Header => (muted, 9.5, false),
                PanelTokenRowKind.Total => (primary, 10.5, true),
                _ => (secondary, 10.5, false),
            };

            AddRow(table, i, row.Kind is { } kind ? KindBrush(kind) : null,
                row.Label, row.TodayTokens, row.TodayShare, row.WindowTokens, row.WindowShare,
                foreground, size, bold);
        }

        return table;
    }

    private static void AddRow(
        Grid table, int row, Brush? chip, string kind, string todayTokens, string todayShare,
        string windowTokens, string windowShare, Brush foreground, double fontSize, bool bold = false)
    {
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var label = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        if (chip is not null)
        {
            label.Children.Add(new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(2),
                Background = chip,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            });
        }

        label.Children.Add(new TextBlock
        {
            Text = kind,
            FontSize = fontSize,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = foreground,
        });
        Grid.SetRow(label, row);
        Grid.SetColumn(label, 0);
        table.Children.Add(label);

        var cells = new[] { todayTokens, todayShare, windowTokens, windowShare };
        for (var i = 0; i < cells.Length; i++)
        {
            var cell = new TextBlock
            {
                Text = cells[i],
                FontSize = fontSize,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                TextAlignment = TextAlignment.Right,
                Margin = new Thickness(10, 2, 0, 2),
                Foreground = foreground,
            };
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, i + 1);
            table.Children.Add(cell);
        }
    }

    /// <summary>
    /// Puts the explanation in a finished state at once, open or closed, nothing in flight.
    /// The interactive path goes through <see cref="Show"/>, which folds; this
    /// is what <see cref="Populate"/> and the verification renders use.
    /// </summary>
    private void SetExpanded(bool expanded)
    {
        _expanded = expanded;

        // The switch is the state's only visible indicator, so it has to move with it,
        // including on the Populate reset, where nothing was clicked. Set before Apply and
        // guarded by the equality check in Show, so the Checked handler this
        // raises finds the state already correct and does not fold a second time.
        _barsToggle.IsChecked = !expanded;
        _breakdownToggle.IsChecked = expanded;

        _disclosure.Apply(expanded);
    }

    // Token formatting lives in OView.Presentation.Text.UsageFormatter, shared with the
    // off-plan notification and the verification renders so one amount cannot be written two
    // ways (issue #55). Kept as a local alias because it reads better unqualified at the dozen
    // call sites above.
    private static string FormatTokens(long tokens) => UsageFormatter.Tokens(tokens);
}
