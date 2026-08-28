using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using OView.App.Rendering;
using OView.Core.Models;
using OView.Core.Providers.Jsonl;
using OView.Core.Providers.PlanHistory;

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

    /// <summary>
    /// How tightly to pack, for a display too short for the natural layout
    /// (<see cref="PanelDensity"/>). Set before <see cref="Populate"/>; defaults to the
    /// shipped layout, so a head that never sets it is unchanged.
    /// </summary>
    public PanelDensity Density { get; set; } = PanelDensity.Normal;

    private readonly LinuxPanelTheme _theme;
    private readonly StackPanel _root;

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

    public void Populate(
        UsageSnapshot snapshot,
        PanelStatistics stats,
        ClaudeAccount? account,
        PlanHistoryReport? dataReport,
        TranscriptScopeReport? scopeReport,
        DateTimeOffset utcNow)
    {
        _root.Children.Clear();

        // Applied here rather than in the constructor: the density depends on the screen this
        // open lands on, and a multi-monitor desktop can move between them. Normal reproduces
        // the shipped layout exactly (PanelDensity).
        Padding = new Thickness(Density.RootPadding);
        _root.Spacing = NaturalRootSpacing * Density.SpacingScale;

        var local = TimeZoneInfo.Local;
        var authoritative = snapshot.Source is DataSource.Live or DataSource.Stale;

        AddHeader(snapshot, account, local, utcNow);

        // Explain a blank panel rather than leaving the user to guess (rule 6). Only shown
        // when the figures are genuinely unavailable, and worded from both reports together
        // — a missing plan file beside working transcripts is a CLI-only user, not a fault
        // (issue #170).
        var banner = PanelBanner.Resolve(authoritative, dataReport, scopeReport, stats.Tokens31Days);
        if (banner is not null)
        {
            AddBanner(banner.Title, banner.Detail);
        }

        var placeholder = banner?.GaugePlaceholder ?? PanelBanner.UnknownGauge;

        AddBar("Current session", authoritative ? snapshot.SessionPercent : null,
            PanelText.SessionReset(snapshot.SessionResetAtUtc, utcNow, local, snapshot.SessionResetUncertainty), placeholder);

        AddBar("Weekly", authoritative ? snapshot.WeeklyPercent : null,
            WeeklyResetLine(snapshot, authoritative, utcNow, local), placeholder);

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

    private static string? WeeklyResetLine(
        UsageSnapshot snapshot, bool authoritative, DateTimeOffset utcNow, TimeZoneInfo local)
    {
        if (snapshot.WeeklyResetAtUtc is { } reset)
        {
            return PanelText.WeeklyReset(reset, snapshot.WeeklyResetUncertainty, utcNow, local);
        }

        // Plan data is flowing but no drop has been seen yet. Rendering nothing here is
        // indistinguishable from a bug, so it says what it is waiting for.
        return authoritative ? PanelText.WeeklyResetWaiting : null;
    }

    // ── sections ────────────────────────────────────────────────────────────────────

    private void AddHeader(UsageSnapshot snapshot, ClaudeAccount? account, TimeZoneInfo local, DateTimeOffset utcNow)
    {
        var left = new StackPanel { Spacing = 2 };
        left.Children.Add(Text("O-view", 20, _theme["TextPrimary"], FontWeight.SemiBold));
        left.Children.Add(Text(
            PanelText.Freshness(snapshot, utcNow, local), 12, _theme["TextSecondary"]));

        var right = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(Text(account?.DisplayName ?? "account unknown", 13, _theme["TextPrimary"]));
        if (account?.Email is { Length: > 0 } email)
        {
            right.Children.Add(Text(email, 11, _theme["TextSecondary"]));
        }

        // Tier from organizationType — seatTier is empty and would render blank (rule 8).
        right.Children.Add(new Border
        {
            Background = _theme["BadgeBg"],
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = Text(account?.Tier ?? "tier unknown", 11, _theme["BadgeText"]),
        });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        _root.Children.Add(grid);
    }

    /// <summary>
    /// A usage bar. A null percentage draws the track alone — never a fabricated fill, and
    /// never a zero that would read as "no usage" when the truth is "not known" (rule 6).
    /// </summary>
    private void AddBar(string label, int? percent, string? resetLine, string placeholder)
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(Text(label, 14, _theme["TextPrimary"], FontWeight.SemiBold));
        var value = Text(percent is { } p ? $"{p}% used" : placeholder, 14, _theme["TextSecondary"]);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(value, 1);
        header.Children.Add(value);

        var fill = new Border
        {
            Background = _theme.Level(percent),
            CornerRadius = new CornerRadius(3),
            Height = 6,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = percent is { } pct ? PanelWidth * 0.92 * Math.Clamp(pct, 0, 100) / 100.0 : 0,
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
        if (resetLine is { Length: > 0 })
        {
            block.Children.Add(Text(resetLine, 12, _theme["TextSecondary"]));
        }

        _root.Children.Add(block);
    }

    private void AddTiles(PanelStatistics stats, TranscriptScopeReport? scopeReport)
    {
        var caveat = PanelText.Caveat(stats);
        var offPlan = stats.IsOffPlan;

        _root.Children.Add(TileRow(
            Tile(PanelText.TokensTodayLabel, UsageFormatter.Tokens(stats.TokensToday), PanelText.OffPlanNote(offPlan)),
            Tile(PanelText.EstTodayLabel(offPlan), UsageFormatter.Usd(stats.EstTodayUsd), PanelText.OffPlanNote(offPlan))));

        // A muted line naming the UTC day's boundary stood here for one release, because the
        // tiles were a UTC day and this head has no hover to put the caveat behind (issue
        // #210). The tiles are the reader's own day now, so the line has nothing to say.
        _root.Children.Add(TileRow(
            Tile(PanelText.Tokens31DaysLabel, UsageFormatter.Tokens(stats.Tokens31Days), caveat),
            Tile(PanelText.Est31DaysLabel, UsageFormatter.Usd(stats.Est31DaysUsd), caveat)));

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
        _root.Children.Add(Muted(PanelText.TokenScopeCaveat));

        // What today's total is made of, and why it dwarfs the context figure in Claude's
        // own UI (issue #169). Omitted when there is nothing to break down — a composition
        // of zero explains nothing.
        if (!stats.CompositionToday.HasTokens)
        {
            return;
        }

        _root.Children.Add(Text(
            PanelText.TokenCompositionLine(stats.CompositionToday, PanelText.TokenCompositionTodayScope),
            11, _theme["TextPrimary"]));

        // The figures stay; the prose folds away. Four lines of standing text answering a
        // question only some readers have — but the ones who have it are looking straight at
        // the number that prompted it, so it stays one click from here.
        var body = new StackPanel { Spacing = 3, IsVisible = false };
        body.Children.Add(Text(PanelText.TokenCompositionHint(stats.CompositionToday), 11, _theme["TextSecondary"]));

        // Which surfaces these figures are made of. Empty when nothing was found at all —
        // the scope note owns that state and says considerably more (issue #171).
        if ((scopeReport ?? TranscriptScopeReport.Inspect()).CoverageLine() is { Length: > 0 } coverage)
        {
            body.Children.Add(Muted(coverage));
        }

        _root.Children.Add(Disclosure(PanelText.TokenExplainToggleLabel, body));
        _root.Children.Add(body);
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

    private Grid TileRow(Control left, Control right)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*") };
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    private Border Tile(string label, string value, string caveat)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(Text(label, 12, _theme["TextSecondary"]));
        stack.Children.Add(Text(value, 20, _theme["TextPrimary"], FontWeight.SemiBold));
        if (caveat.Length > 0)
        {
            stack.Children.Add(Text(caveat, 11, _theme["WarnText"]));
        }

        return new Border
        {
            Background = _theme["TileBg"],
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(NaturalTilePadding * Density.SpacingScale),
            Child = stack,
        };
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

        var peak = series.Where(d => !d.PreInstall).Select(d => d.TotalTokens).DefaultIfEmpty(0).Max();
        // Scaled by the density ratio rather than given the Windows chart height: this head
        // ships a 60 px strip, and a display with room must keep exactly that.
        var barArea = NaturalGraphHeight * Density.GraphScale;
        var bars = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Height = barArea };

        foreach (var day in series)
        {
            bars.Children.Add(new Border
            {
                Width = 9,
                Height = day.PreInstall || peak == 0 ? 0 : Math.Max(2, barArea * day.TotalTokens / peak),
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = day.PreInstall ? null : _theme["Series1"],
                CornerRadius = new CornerRadius(1),
            });
        }

        var block = new StackPanel { Spacing = 4 };
        block.Children.Add(Text($"Usage · last {stats.WindowDays} days", 13, _theme["TextPrimary"], FontWeight.SemiBold));
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
