using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;

using Avalonia.Layout;
using Avalonia.Media;
using OView.App.Rendering;
using OView.Core.Models;
using OView.Core.Providers.CachedUsage;
using OView.Core.Providers.Jsonl;
using OView.Core.Providers.PlanHistory;
using OView.Presentation.Panel;
using OView.Presentation.Text;
using static OView.Linux.Panel.PanelPrimitives;

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
///
/// <para>This class owns the panel's frame and the two rows it draws itself — the header and
/// a usage bar. The header is also the panel's drag handle, which is why it is the one row
/// built once and refilled rather than rebuilt (<see cref="Header"/>). Each section below them
/// is its own piece, named for what it draws:
/// <see cref="PanelStatTiles"/>, <see cref="PanelTokenBars"/>, <see cref="PanelGraph"/> and
/// <see cref="PanelOffPlan"/>.</para>
///
/// <para>The frame can also put those sections in a scrolling viewport, so a panel the window
/// has had to cap can still be reached to its last section — see <see cref="ScrollContent"/>,
/// <see cref="Viewport"/> and <see cref="ScrollBar"/>. A panel with room never asks for one
/// and is laid out exactly as it was before it could scroll at all. The gestures that move it
/// — the wheel, a thumb drag, a click on the track — are <see cref="PanelScroll"/>.</para>
/// </summary>
public sealed class PanelContent : Border
{
    private const double PanelWidth = PanelMetrics.PanelWidthDip;

    /// <summary>Gap between sections at the natural density — this head's own constant.</summary>
    private const double NaturalRootSpacing = 10;

    /// <summary>
    /// The scroll bar's width and the gap before it, shared with the Windows head through
    /// <see cref="PanelMetrics"/>; the two colours are shared through <see cref="PanelPalette"/>.
    /// </summary>
    private const double ScrollBarWidth = PanelMetrics.ScrollBarWidthDip;

    private const double ScrollBarGap = PanelMetrics.ScrollBarGapDip;

    /// <summary>
    /// How tightly to pack, for a display too short for the natural layout
    /// (<see cref="PanelDensity"/>). Set before <see cref="Populate"/>; defaults to the
    /// shipped layout, so a head that never sets it is unchanged.
    /// </summary>
    public PanelDensity Density { get; set; } = PanelDensity.Normal;

    private readonly LinuxPanelTheme _theme;
    private readonly StackPanel _root;
    private readonly Grid _header;
    private readonly ScrollContentPresenter _viewport;
    private readonly Border _scrollTrack;
    private readonly Border _scrollThumb;
    private readonly Grid _scrollFrame;
    private readonly PanelScroll _scroll;

    private readonly PanelStatTiles _tiles;
    private readonly PanelTokenBars _tokenBars;
    private readonly PanelGraph _graph;
    private readonly PanelOffPlan _offPlan;

    /// <summary>
    /// The flip of every tile that has a second face, in the order the panel places them.
    ///
    /// <para>Avalonia raises no pointer events without a windowing subsystem and CI has none,
    /// so a test cannot click a tile. This is the state a click drives, exposed so the flip can
    /// be asserted — the same reason <see cref="PanelDismissal"/> is its own class rather than
    /// logic inline in an event handler, and it matters more here because no golden image shows
    /// a tile's second face. What no test reaches either way is the three subscriptions in
    /// the tile itself.</para>
    /// </summary>
    public IReadOnlyList<PanelStatTiles.TileFlip> TileFlips => _tiles.Flips;

    /// <summary>
    /// The viewport the sections are scrolled inside, once <see cref="ScrollContent"/> has put
    /// them there. Public for the same reason <see cref="PanelTokenBars.ViewSwitch"/> is: this
    /// is the state a wheel turn and a thumb drag drive, and a test process has no windowing
    /// subsystem to produce either gesture for real. It is also what says how much content is
    /// out of sight — the one number that proves nothing was dropped when the window had to
    /// cap itself.
    /// </summary>
    public ScrollContentPresenter Viewport => _viewport;

    /// <summary>The scroll bar's track, hidden while everything fits inside the viewport.</summary>
    public Control ScrollBar => _scrollTrack;

    /// <summary>Whether the sections are currently in the viewport rather than straight in
    /// the frame.</summary>
    public bool Scrolls => ReferenceEquals(Child, _scrollFrame);

    /// <summary>
    /// The title row, which is also the band the panel is dragged by — the one part of the
    /// panel with nothing on it to click, so a press here can mean "move me" without taking a
    /// click away from anything.
    ///
    /// <para>Built once in the constructor and refilled by each <see cref="Populate"/> rather
    /// than rebuilt with the rest of the tree. A re-populate happens on every open and again
    /// when the density changes, and a fresh control each time would silently drop whatever
    /// handlers the window had attached to the last one.</para>
    ///
    /// <para><b>Its transparent background is load-bearing.</b> A control with no background is
    /// not hit-testable in the gaps between its children, so without one the handle would only
    /// answer on the words themselves. It paints nothing, so the verification renders are
    /// unchanged.</para>
    /// </summary>
    public Control Header => _header;

    public PanelContent(LinuxPanelTheme theme)
    {
        _theme = theme;
        _tiles = new PanelStatTiles(theme);
        _tokenBars = new PanelTokenBars(theme);
        _graph = new PanelGraph(theme);
        _offPlan = new PanelOffPlan(theme);

        Width = PanelWidth;
        Background = theme["PanelBg"];
        BorderBrush = theme["PanelBorder"];
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(16);

        _root = new StackPanel { Spacing = 10 };

        // See Header: built here, filled by AddHeader, so the window's drag handlers survive a
        // re-populate. Transparent for hit-testing, which paints nothing.
        _header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Background = Brushes.Transparent,
        };

        // What a screen reader says the band is for. Set here, with the rest of what the header
        // is, rather than in AddHeader: the words never change between opens, and the one thing
        // the name must survive is the re-populate that refills the children. The move cursor
        // says the same thing to everyone who can see it, and the wording is Presentation's so
        // both heads say one name — PanelHeaderView.MoveName.
        AutomationProperties.SetName(_header, PanelHeaderView.MoveName);

        _scrollThumb = new Border
        {
            Background = theme["ScrollThumb"],
            CornerRadius = new CornerRadius(ScrollBarWidth / 2),
            Width = ScrollBarWidth,
            Height = PanelScroll.MinThumbHeight,
            VerticalAlignment = VerticalAlignment.Top,
        };

        _scrollTrack = new Border
        {
            Background = theme["ScrollTrack"],
            CornerRadius = new CornerRadius(ScrollBarWidth / 2),
            Width = ScrollBarWidth,
            Margin = new Thickness(ScrollBarGap, 0, 0, 0),
            ClipToBounds = true,
            Child = _scrollThumb,

            // Hidden until there is something out of sight, and it occupies an Auto column, so
            // a panel that fits is laid out at exactly the width it was before this existed.
            IsVisible = false,
        };

        _viewport = new ScrollContentPresenter
        {
            CanVerticallyScroll = true,
            CanHorizontallyScroll = false,
        };

        // Every gesture that moves the content, and the thumb that follows it, live together
        // in PanelScroll — including the wheel, which this file used to leave to the viewport.
        _scroll = new PanelScroll(this, _viewport, _scrollTrack, _scrollThumb);

        _viewport.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollContentPresenter.OffsetProperty
                || e.Property == ScrollContentPresenter.ExtentProperty
                || e.Property == ScrollContentPresenter.ViewportProperty)
            {
                _scroll.UpdateBar();
            }
        };

        _scrollFrame = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_scrollTrack, 1);
        _scrollFrame.Children.Add(_viewport);
        _scrollFrame.Children.Add(_scrollTrack);

        Child = _root;
    }

    /// <summary>
    /// Puts the sections in a scrolling viewport, or takes them straight back into the frame.
    ///
    /// <para>The window asks for one exactly when <see cref="PanelFit"/> has had to cap it, so
    /// everything past the cap stays reachable instead of being clipped off the bottom of the
    /// screen.</para>
    ///
    /// <para><b>Why a panel with room does not simply always have one.</b> A viewport clips to
    /// its own bounds, and this panel has a usage bar whose fill overruns the content column by
    /// 2 DIP at 100 % — an overrun that is in 8 of the 22 verification renders as they stand.
    /// A viewport on every display would quietly trim it, which is a change to how the panel
    /// looks on every display that already fits, arriving inside a change about the ones that
    /// do not. So a panel with room keeps exactly the tree it has always had.</para>
    /// </summary>
    public void ScrollContent(bool scrolls)
    {
        if (scrolls == Scrolls)
        {
            return;
        }

        // Detached from the old parent first: Avalonia refuses a control that still has one.
        if (scrolls)
        {
            Child = null;
            _viewport.Content = _root;

            // A presenter fills itself from its content only once its tree reaches a window,
            // and the verification renders come from a tree that never does — the same trap
            // that left both halves of the Bars/Breakdown switch blank in every sample image.
            // Asking for the child here means the sections are built either way.
            _viewport.UpdateChild();
            Child = _scrollFrame;
        }
        else
        {
            Child = null;
            _viewport.Content = null;
            _viewport.UpdateChild();
            _scrollTrack.IsVisible = false;
            Child = _root;
        }
    }

    /// <summary>
    /// The height this panel's content wants, measured against an unbounded height — the
    /// content's own answer, not the window's.
    ///
    /// <para><b>Why it cannot be read off the window.</b> The panel is <c>SizeToContent</c>, so
    /// its frame is whatever the compositor granted, and a compositor will not grant more than
    /// the screen: on Maximilian's 1024×768 desktop the frame came back as exactly 768 and the
    /// panel therefore concluded it was not too tall for a 712 DIP work area, while four
    /// sections sat off the bottom edge with no way to reach them (his report of 2026-10-15).
    /// Measuring the tree is how the verification renders have always sized themselves, and it
    /// is the only number that is about the content rather than about the display.</para>
    ///
    /// <para>The measure is left invalidated afterwards so the next layout pass measures the
    /// tree against the constraint it is actually being laid out in, rather than keeping this
    /// unbounded answer.</para>
    /// </summary>
    public double NaturalHeightDip()
    {
        Measure(Size.Infinity);
        var wanted = DesiredSize.Height;
        InvalidateMeasure();
        return wanted;
    }

    public void Populate(
        UsageSnapshot snapshot,
        PanelStatistics stats,
        ClaudeAccount? account,
        PlanHistoryReport? dataReport,
        TranscriptScopeReport? scopeReport,
        DateTimeOffset utcNow,
        BoostNotices? boostNotices = null,
        TimeZoneInfo? zone = null,
        CachedUtilization? cachedUsage = null)
    {
        _root.Children.Clear();

        // Back to the top. A re-populate is a fresh open or a re-layout at a new density, and
        // either way the header is where the panel should start.
        _viewport.Offset = default;

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
        // Both off-plan registers at once: the live banner below, and the standing 31-day
        // section at the foot of the panel. Built here rather than at each use so the two
        // halves of one claim are read from one object.
        var offPlan = PanelSections.OffPlan(stats, cachedUsage, utcNow, local);

        // What the plan meters cannot show: work running past a meter that is not moving, or
        // past the window entirely. Above the no-data banner, as on the Windows panel — this
        // one describes live work, that one describes figures that are missing, and the live
        // signal is the more urgent of the two.
        if (offPlan.Banner is { } offPlanBanner)
        {
            _offPlan.AddBannerTo(_root, offPlanBanner, Density);
        }

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

        var report = scopeReport ?? TranscriptScopeReport.Inspect();
        _tiles.AddTo(_root, stats, Density);
        _tokenBars.AddTo(_root, PanelSections.TokenBars(stats, report));
        _graph.AddTo(_root, PanelSections.Graph(stats, snapshot, local), Density);
        _offPlan.AddSectionTo(_root, offPlan);

        if (PanelSections.ScopeNote(stats, snapshot, report) is { } note)
        {
            _root.Children.Add(Muted(_theme, note));
        }
    }

    // ── sections ────────────────────────────────────────────────────────────────────

    private void AddHeader(PanelHeaderView header)
    {
        _header.Children.Clear();

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

        Grid.SetColumn(right, 1);
        _header.Children.Add(left);
        _header.Children.Add(right);
        _root.Children.Add(_header);
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
}
