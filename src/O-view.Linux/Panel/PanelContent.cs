using Avalonia;
using Avalonia.Controls;
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
/// a usage bar. Each section below them is its own piece, named for what it draws:
/// <see cref="PanelStatTiles"/>, <see cref="PanelTokenBars"/>, <see cref="PanelGraph"/> and
/// <see cref="PanelOffPlan"/>.</para>
/// </summary>
public sealed class PanelContent : Border
{
    private const double PanelWidth = 400;

    /// <summary>Gap between sections at the natural density — this head's own constant.</summary>
    private const double NaturalRootSpacing = 10;

    /// <summary>
    /// How tightly to pack, for a display too short for the natural layout
    /// (<see cref="PanelDensity"/>). Set before <see cref="Populate"/>; defaults to the
    /// shipped layout, so a head that never sets it is unchanged.
    /// </summary>
    public PanelDensity Density { get; set; } = PanelDensity.Normal;

    private readonly LinuxPanelTheme _theme;
    private readonly StackPanel _root;

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
        Child = _root;
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

        _tiles.AddTo(_root, stats, Density);
        _tokenBars.AddTo(_root, stats, scopeReport);
        _graph.AddTo(_root, PanelSections.Graph(stats, snapshot, local), Density);
        _offPlan.AddSectionTo(_root, offPlan);

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
            _root.Children.Add(Muted(_theme, (scopeReport ?? TranscriptScopeReport.Inspect()).Explain()));
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
}
