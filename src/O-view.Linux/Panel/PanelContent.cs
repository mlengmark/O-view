using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using OView.App.Rendering;
using OView.Core.Models;
using OView.Core.Providers.CachedUsage;
using OView.Core.Providers.Jsonl;
using OView.Core.Providers.PlanHistory;
using OView.Presentation.Panel;
using OView.Presentation.Text;

namespace OView.Linux.Panel;

/// <summary>
/// The detail panel's content (docs/ui-spec.md), built for Avalonia.
///
/// <para>Every figure and every sentence comes from the shared layer —
/// <see cref="PanelStatistics"/>, <see cref="PanelText"/>, <see cref="UsageFormatter"/>,
/// <see cref="TranscriptScopeReport"/>. Nothing here computes or words anything, so the two
/// panels cannot disagree about what a number means or how a caveat reads.</para>
///
/// <para>It is a plain control rather than a window so the same tree can be shown live or
/// rendered offscreen for verification, exactly as the WPF panel does.</para>
/// </summary>
public sealed class PanelContent : Border
{
    private const double PanelWidth = 400;

    /// <summary>Gap between sections at the natural density — this head's own constant.</summary>
    private const double NaturalRootSpacing = 10;

    /// <summary>Height of the bar strip at the natural density. The Windows chart is 86.</summary>
    private const double NaturalGraphHeight = 60;

    /// <summary>Inset inside a stat tile at the natural density.</summary>
    private const double NaturalTilePadding = 10;

    /// <summary>Gap between a tile's bar segments, in the surface colour.</summary>
    private const double SegmentGap = 2;

    /// <summary>Rounding on the bar's data end; the baseline end stays square.</summary>
    private const double DataEndRadius = 4;

    /// <summary>
    /// How tightly to pack, for a display too short for the natural layout
    /// (<see cref="PanelDensity"/>). Set before <see cref="Populate"/>; defaults to the
    /// shipped layout, so a head that never sets it is unchanged.
    /// </summary>
    public PanelDensity Density { get; set; } = PanelDensity.Normal;

    private readonly LinuxPanelTheme _theme;
    private readonly StackPanel _root;

    /// <summary>
    /// The flip of every tile that has a second face, in the order the panel places them.
    ///
    /// <para>Rebuilt by each <see cref="Populate"/>, like the tiles themselves — a flip left
    /// over from a previous open would be holding controls no longer on screen.</para>
    /// </summary>
    private readonly List<TileFlip> _tileFlips = [];

    /// <summary>
    /// The tiles that can flip, for tests.
    ///
    /// <para>Avalonia raises no pointer events without a windowing subsystem, and CI has none,
    /// so a test cannot click a tile. This exposes the state a click drives instead — the same
    /// reason <see cref="PanelDismissal"/> exists as its own class rather than living inline in
    /// an event handler. What is left untested either way is the three event subscriptions.</para>
    /// </summary>
    internal IReadOnlyList<TileFlip> TileFlips => _tileFlips;

    public PanelContent(LinuxPanelTheme theme)
    {
        _theme = theme;
        Width = PanelWidth;
        Background = theme["PanelBg"];
        BorderBrush = theme["PanelBorder"];
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(16);

        _root = new StackPanel { Spacing = 10 };
        Child = _root;
    }

    /// <summary>
    /// One tile's flip: which face is showing, and how loud the affordance is.
    ///
    /// <para>The opacities are the Windows tile's — faint at rest, clearer under the pointer,
    /// clearest while the breakdown is open. The affordance should be findable and never louder
    /// than the number it sits beside.</para>
    /// </summary>
    internal sealed class TileFlip
    {
        private readonly Control _summary;
        private readonly Control _breakdown;
        private readonly Control _glyph;
        private bool _pointerOver;

        internal TileFlip(Control summary, Control breakdown, Control glyph)
        {
            _summary = summary;
            _breakdown = breakdown;
            _glyph = glyph;
            Apply();
        }

        /// <summary>Whether the per-model face is the one showing. False on every open: the
        /// panel is a transient view rather than a setting, which is why the Windows tile
        /// resets its own expansion too.</summary>
        internal bool IsExpanded { get; private set; }

        internal void Toggle()
        {
            IsExpanded = !IsExpanded;
            Apply();
        }

        internal void PointerOver(bool over)
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
            _glyph.Opacity = IsExpanded ? 0.9 : _pointerOver ? 0.75 : 0.35;
        }
    }

    public void Populate(
        UsageSnapshot snapshot,
        PanelStatistics stats,
        ClaudeAccount? account,
        PlanHistoryReport? dataReport,
        TranscriptScopeReport? scopeReport,
        DateTimeOffset utcNow,
        BoostNotices? boostNotices = null,
        TimeZoneInfo? zone = null)
    {
        _root.Children.Clear();
        _tileFlips.Clear();

        // Applied here rather than in the constructor: the density depends on the screen this
        // open lands on, and a multi-monitor desktop can move between them. Normal reproduces
        // the shipped layout exactly (PanelDensity).
        Padding = new Thickness(Density.RootPadding);
        _root.Spacing = NaturalRootSpacing * Density.SpacingScale;

        // A pinned zone is for verification renders, so a UTC CI runner and a CET desktop
        // draw the same reset times; the live panel always follows the machine.
        var local = zone ?? TimeZoneInfo.Local;
        var authoritative = PanelSections.IsAuthoritative(snapshot);

        AddHeader(PanelSections.Header(snapshot, account, utcNow, local));

        // Explain a blank panel rather than leaving the user to guess (rule 6). Only shown
        // when the figures are genuinely unavailable, and worded from both reports together
        // — a missing plan file beside working transcripts is a CLI-only user, not a fault
        // (issue #170).
        var banner = PanelBanner.Resolve(authoritative, dataReport, scopeReport, stats.Tokens31Days);
        if (banner is not null)
        {
            AddBanner(banner.Title, banner.Detail);
        }

        AddBar(PanelSections.SessionMeter(snapshot, banner, utcNow, local));

        // No settings: this head does not receive them, which reads the same as not having
        // entered a weekly reset — so the "you set this" label the Windows panel can show
        // never appears here. Plumbing them through is a change to this head's shell.
        AddBar(PanelSections.WeeklyMeter(snapshot, banner, null, boostNotices, utcNow, local));

        AddTiles(stats, scopeReport);
        AddGraph(stats);

        // Nothing recorded at all while the plan meters show real usage: the tiles are
        // measuring a source this user does not feed, not measuring zero. Say which source,
        // so the 0 is interpretable rather than looking broken. Derived from what the scan
        // actually resolved, never a literal (issue #58).
        //
        // Keyed on the token total, not on RecordedDays: those meant the same thing while
        // RecordedDays counted days with usage, but it now counts days observed (issue #142),
        // so a store older than the window reports full coverage while the tiles still read
        // zero — the exact case this note is for.
        if (stats.Tokens31Days == 0 && authoritative && snapshot.SessionPercent is > 0)
        {
            _root.Children.Add(Muted((scopeReport ?? TranscriptScopeReport.Inspect()).Explain()));
        }
    }

    // ── sections ────────────────────────────────────────────────────────────────────

    private void AddHeader(PanelHeaderView header)
    {
        var left = new StackPanel { Spacing = 2 };
        left.Children.Add(Text(header.Title, 20, _theme["TextPrimary"], FontWeight.SemiBold));
        left.Children.Add(Text(header.Freshness, 12, _theme["TextSecondary"]));

        var right = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(Text(header.AccountName, 13, _theme["TextPrimary"]));
        if (header.Email is { } email)
        {
            right.Children.Add(Text(email, 11, _theme["TextSecondary"]));
        }

        right.Children.Add(new Border
        {
            Background = _theme["BadgeBg"],
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = Text(header.Tier, 11, _theme["BadgeText"]),
        });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        _root.Children.Add(grid);
    }

    /// <summary>
    /// A usage bar. No fill percentage draws the track alone — never a fabricated fill, and
    /// never a zero that would read as "no usage" when the truth is "not known" (rule 6).
    ///
    /// <para>The reset line is the one with the action spliced in: this head's rows carry a
    /// single line of text and a compositor may never deliver hover at all, so the step that
    /// ends an unknown weekly reset rides on the row or never appears
    /// (<see cref="PanelMeterView.ResetLineWithAction"/>).</para>
    /// </summary>
    private void AddBar(PanelMeterView meter)
    {
        // Clipped, so a chip wider than its cell is cut off at the boundary rather than drawn
        // over the figure on the right. The Windows head's first render of this row did exactly
        // that, and no test could have caught it — only the picture did.
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ClipToBounds = true,
        };

        // Label and chip share the left cell. The chip reuses the tier badge's colour pair
        // rather than introducing one, so it reads as the same class of object — a factual
        // label about the account, not a warning.
        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        left.Children.Add(Text(meter.Label, 14, _theme["TextPrimary"], FontWeight.SemiBold));
        if (meter.Chip is { } chip)
        {
            left.Children.Add(new Border
            {
                Background = _theme["BadgeBg"],
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = Text(chip.Text, 11, _theme["BadgeText"]),
            });
        }

        header.Children.Add(left);
        var value = Text(meter.Value, 14, _theme["TextSecondary"]);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(value, 1);
        header.Children.Add(value);

        var fill = new Border
        {
            // Keyed on the percentage rather than the band, because LinuxPanelTheme is where
            // this head shares the band classification with the tray icon — the same colours,
            // light or dark, that the ring gauge uses.
            Background = _theme.Level(meter.Percent),
            CornerRadius = new CornerRadius(3),
            Height = 6,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = meter.FillPercent is { } pct ? PanelWidth * 0.92 * pct / 100.0 : 0,
        };

        var track = new Border
        {
            Background = _theme["BarTrack"],
            CornerRadius = new CornerRadius(3),
            Height = 6,
            Child = fill,
        };

        var block = new StackPanel { Spacing = 4 };
        block.Children.Add(header);
        block.Children.Add(track);
        if (meter.ResetLineWithAction is { Length: > 0 } resetLine)
        {
            block.Children.Add(Text(resetLine, 12, _theme["TextSecondary"]));
        }

        _root.Children.Add(block);
    }

    private void AddTiles(PanelStatistics stats, TranscriptScopeReport? scopeReport)
    {
        // Both faces of all four tiles, including which caveat each carries. The off-plan note
        // used to ride on this head's tokens-today tile and never on the Windows one; the
        // mixture is what the value tiles are about, and Presentation now says so once.
        var tiles = PanelSections.Tiles(stats);

        _root.Children.Add(TileRow(Tile(tiles.TokensToday), Tile(tiles.EstToday)));

        // A muted line naming the UTC day's boundary stood here for one release, because the
        // tiles were a UTC day and this head has no hover to put the caveat behind (issue
        // #210). The tiles are the reader's own day now, so the line has nothing to say.
        _root.Children.Add(TileRow(Tile(tiles.Tokens31Days), Tile(tiles.Est31Days)));

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
        _root.Children.Add(Muted(TokenText.TokenScopeCaveat));

        // What was billed, beside tiles that headline only output (issue #253). Omitted when
        // there is nothing to break down — a composition of zero explains nothing.
        var today = stats.CompositionToday;
        var window31 = stats.Composition31Days;
        if (!today.HasTokens && !window31.HasTokens)
        {
            return;
        }

        var breakdown = BuildBreakdown(today, window31);

        var header = new Grid
        {
            Margin = new Thickness(0, 12, 0, 0),
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };
        header.Children.Add(Text(TokenText.TokensUsedHeading, 11, _theme["TextSecondary"]));

        var switcher = ViewSwitch(breakdown);
        Grid.SetColumn(switcher, 1);
        header.Children.Add(switcher);
        _root.Children.Add(header);

        _root.Children.Add(TokenBar(today, TokenText.TokensUsedTodayLabel, TokenText.TokenWindowToday, 6));
        _root.Children.Add(TokenBar(window31, TokenText.TokensUsed31DaysLabel, TokenText.TokenWindow31Days, 9));
        _root.Children.Add(breakdown);

        // Which surfaces these figures are made of. Empty when nothing was found at all —
        // the scope note owns that state and says considerably more (issue #171).
        if ((scopeReport ?? TranscriptScopeReport.Inspect()).CoverageLine() is { Length: > 0 } coverage)
        {
            _root.Children.Add(Muted(coverage));
        }
    }

    /// <summary>
    /// The Bars/Breakdown switch. Two buttons rather than a disclosure, so the Linux panel
    /// offers the same two views the Windows one does — and, on a head where a compositor may
    /// never deliver hover at all, the breakdown is the only route to a segment's exact share
    /// and estimated value that needs no pointer.
    /// </summary>
    private Control ViewSwitch(Control breakdown)
    {
        var bars = SwitchButton(TokenText.TokenViewBarsLabel, active: true);
        var table = SwitchButton(TokenText.TokenViewBreakdownLabel, active: false);

        void Select(bool showBreakdown)
        {
            breakdown.IsVisible = showBreakdown;
            bars.Background = showBreakdown ? Brushes.Transparent : _theme["BarTrack"];
            table.Background = showBreakdown ? _theme["BarTrack"] : Brushes.Transparent;
        }

        bars.Click += (_, _) => Select(false);
        table.Click += (_, _) => Select(true);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        row.Children.Add(bars);
        row.Children.Add(table);

        return new Border
        {
            Background = _theme["TileBg"],
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(2),
            Child = row,
        };
    }

    private Button SwitchButton(string label, bool active) => new()
    {
        Content = Text(label, 10.5, _theme[active ? "TextPrimary" : "TextMuted"]),
        Background = active ? _theme["BarTrack"] : Brushes.Transparent,
        BorderThickness = new Thickness(0),
        CornerRadius = new CornerRadius(3),
        Padding = new Thickness(8, 3),
        Cursor = new Cursor(StandardCursorType.Hand),
    };

    /// <summary>
    /// One composition bar: heading and billed total, the segmented track, and the legend
    /// carrying every exact figure the track's floors do not.
    ///
    /// <para>Widths come from <see cref="TokenBarGeometry"/> — shared with the Windows head,
    /// because the segment order and the minimum width are measured decisions and two copies
    /// of a measured decision is how they drift.</para>
    /// </summary>
    private Control TokenBar(TokenComposition composition, string heading, string window, double topMargin)
    {
        var block = new StackPanel { Spacing = 4, Margin = new Thickness(0, topMargin, 0, 0) };

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(Text(heading, 10.5, _theme["TextSecondary"]));
        var total = Text(UsageFormatter.Tokens(composition.Total), 12, _theme["TextPrimary"], FontWeight.SemiBold);
        Grid.SetColumn(total, 1);
        head.Children.Add(total);
        block.Children.Add(head);

        var slices = composition.InDisplayOrder.Where(s => s.Tokens > 0).ToList();

        // Avalonia gives no laid-out width before the first arrange pass either, so the
        // widths are applied on size change against the track the bar actually got.
        var track = new Grid { Height = 12 };
        var pieces = new List<Border>(slices.Count);
        for (var i = 0; i < slices.Count; i++)
        {
            track.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(0, GridUnitType.Pixel)));

            var slice = slices[i];
            var brush = KindBrush(slice.Kind);
            var piece = new Border
            {
                Background = brush,
                // Rounded only at the ends — a Border does not clip children to its corner
                // radius, so a rounded container over square segments shows through.
                CornerRadius = new CornerRadius(
                    i == 0 ? 3 : 0, i == slices.Count - 1 ? 3 : 0,
                    i == slices.Count - 1 ? 3 : 0, i == 0 ? 3 : 0),
            };
            ToolTip.SetTip(piece, SegmentTip(slice, window));
            Grid.SetColumn(piece, i);
            track.Children.Add(piece);
            pieces.Add(piece);
        }

        track.SizeChanged += (_, _) =>
        {
            var segments = TokenBarGeometry.Segments(composition, track.Bounds.Width);
            if (segments.Count != track.ColumnDefinitions.Count)
            {
                return;
            }

            for (var i = 0; i < segments.Count; i++)
            {
                track.ColumnDefinitions[i].Width = new GridLength(segments[i].Width, GridUnitType.Pixel);
            }
        };
        block.Children.Add(track);

        // Every figure the bar cannot draw honestly. Not decoration — see TokenBarGeometry.
        var legend = new WrapPanel();
        foreach (var slice in slices)
        {
            var entry = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Margin = new Thickness(0, 0, 10, 0),
            };
            entry.Children.Add(new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(2),
                Background = KindBrush(slice.Kind),
                VerticalAlignment = VerticalAlignment.Center,
            });
            entry.Children.Add(Text(
                $"{TokenText.TokenKindLabel(slice.Kind)} {UsageFormatter.Tokens(slice.Tokens)}",
                10.5, _theme["TextSecondary"]));
            ToolTip.SetTip(entry, SegmentTip(slice, window));
            legend.Children.Add(entry);
        }

        block.Children.Add(legend);
        return block;
    }

    private string SegmentTip(TokenKindSlice slice, string window) =>
        $"{UsageFormatter.Tokens(slice.Tokens)} · "
        + TokenText.TokenCardCaption(slice.Kind, slice.Share, slice.EstUsd, window);

    private IBrush KindBrush(TokenKind kind) => _theme[TokenBarGeometry.PaletteKey(kind)];

    /// <summary>
    /// The breakdown table, with <b>a share column per window</b> — the two windows have
    /// measurably different shapes, so one shared column would be read against whichever
    /// token column it sat nearer.
    /// </summary>
    private Control BuildBreakdown(TokenComposition today, TokenComposition window31)
    {
        var table = new Grid
        {
            IsVisible = false,
            Margin = new Thickness(0, 6, 0, 0),
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"),
        };

        AddBreakdownRow(table, 0, null,
            TokenText.TokenBreakdownKindHeader, TokenText.TokenBreakdownTodayHeader,
            TokenText.TokenBreakdownShareHeader, TokenText.TokenBreakdown31DaysHeader,
            TokenText.TokenBreakdownShareHeader, _theme["TextMuted"], 9.5);

        var row = 1;
        foreach (var slice in (today.HasTokens ? today : window31).InDisplayOrder)
        {
            var mine = today.InDisplayOrder.First(s => s.Kind == slice.Kind);
            var other = window31.InDisplayOrder.First(s => s.Kind == slice.Kind);

            AddBreakdownRow(table, row++, KindBrush(slice.Kind),
                TokenText.TokenKindLabel(slice.Kind),
                UsageFormatter.Tokens(mine.Tokens), TokenText.TokenShare(mine.Share),
                UsageFormatter.Tokens(other.Tokens), TokenText.TokenShare(other.Share),
                _theme["TextSecondary"], 10.5);
        }

        AddBreakdownRow(table, row, null,
            TokenText.TokenBreakdownTotalLabel,
            UsageFormatter.Tokens(today.Total), TokenText.TokenShare(today.HasTokens ? 1 : 0),
            UsageFormatter.Tokens(window31.Total), TokenText.TokenShare(window31.HasTokens ? 1 : 0),
            _theme["TextPrimary"], 10.5, FontWeight.SemiBold);

        return table;
    }

    private void AddBreakdownRow(
        Grid table, int row, IBrush? chip, string kind, string todayTokens, string todayShare,
        string windowTokens, string windowShare, IBrush foreground, double size,
        FontWeight weight = FontWeight.Normal)
    {
        table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var label = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 2),
        };
        if (chip is not null)
        {
            label.Children.Add(new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(2),
                Background = chip,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        label.Children.Add(Text(kind, size, foreground, weight));
        Grid.SetRow(label, row);
        table.Children.Add(label);

        var cells = new[] { todayTokens, todayShare, windowTokens, windowShare };
        for (var i = 0; i < cells.Length; i++)
        {
            var cell = Text(cells[i], size, foreground, weight);
            cell.TextAlignment = TextAlignment.Right;
            cell.Margin = new Thickness(10, 2, 0, 2);
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, i + 1);
            table.Children.Add(cell);
        }
    }

    /// <summary>
    /// A text disclosure that shows or hides <paramref name="body"/>. Avalonia has no
    /// borderless text button by default, so this is a Button with its chrome stripped —
    /// a Button rather than a clickable TextBlock so it keeps keyboard focus and Space/Enter
    /// activation, matching the Windows head (which learned the same lesson in StatTile).
    /// </summary>
    private Button Disclosure(string label, Control body)
    {
        var chevron = Text("⌄", 11, _theme["TextMuted"]);
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        content.Children.Add(Text(label, 11, _theme["TextMuted"]));
        content.Children.Add(chevron);

        var button = new Button
        {
            Content = content,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        button.Click += (_, _) =>
        {
            body.IsVisible = !body.IsVisible;
            chevron.Text = body.IsVisible ? "⌃" : "⌄";
        };

        return button;
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
        _tileFlips.Add(flip);

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
        Padding = new Thickness(NaturalTilePadding * Density.SpacingScale),
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

    /// <summary>
    /// The 31-day graph. <b>Days before install draw nothing at all</b> — they have no data,
    /// not zero data, and a zero-height bar would claim a day of no usage that was never
    /// measured (rule 6).
    /// </summary>
    private void AddGraph(PanelStatistics stats)
    {
        var series = stats.DailySeries;
        if (series.Count == 0)
        {
            return;
        }

        var peak = series.Where(d => !d.PreInstall).Select(d => d.OutputTokens).DefaultIfEmpty(0).Max();
        // Scaled by the density ratio rather than given the Windows chart height: this head
        // ships a 60 px strip, and a display with room must keep exactly that.
        var barArea = NaturalGraphHeight * Density.GraphScale;
        var bars = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Height = barArea };

        foreach (var day in series)
        {
            bars.Children.Add(new Border
            {
                Width = 9,
                Height = day.PreInstall || peak == 0 ? 0 : Math.Max(2, barArea * day.OutputTokens / peak),
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = day.PreInstall ? null : _theme["Series1"],
                CornerRadius = new CornerRadius(1),
            });
        }

        var block = new StackPanel { Spacing = 4 };
        block.Children.Add(Text(
            PanelText.UsageWindowHeading(stats.WindowDays), 13, _theme["TextPrimary"], FontWeight.SemiBold));
        block.Children.Add(bars);
        _root.Children.Add(block);
    }

    private void AddBanner(string title, string detail)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(Text(title, 13, _theme["WarnText"], FontWeight.SemiBold));
        stack.Children.Add(Text(detail, 11, _theme["WarnText"]));

        _root.Children.Add(new Border
        {
            Background = _theme["WarnBg"],
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(NaturalTilePadding * Density.SpacingScale),
            Child = stack,
        });
    }

    // ── primitives ──────────────────────────────────────────────────────────────────

    private TextBlock Muted(string text) => Text(text, 11, _theme["TextMuted"]);

    private static TextBlock Text(string text, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new()
        {
            Text = text,
            FontSize = size,
            Foreground = brush,
            FontWeight = weight,
            TextWrapping = TextWrapping.Wrap,
        };
}
