using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OView.Core.Models;
using OView.Core.Providers.Jsonl;
using OView.Tray.Popup;
using OView.Tray.Tray;
using OView.Tray.Updates;
using Border = System.Windows.Controls.Border;
using Size = System.Windows.Size;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using ToolTip = System.Windows.Controls.ToolTip;

namespace OView.Tray.Diagnostics;

/// <summary>
/// The verification harness: renders every surface offscreen to PNG, and drives the tray
/// menu through repeated open/activate/close cycles.
///
/// <para>All of this used to live in <c>App</c>, where it was roughly 560 of that class's
/// 1,295 lines — tooling compiled into every shipped build and sitting on top of the
/// startup path, the tray wiring, the update flow and the off-plan watcher (GitHub issue
/// #52). None of it runs in normal operation; each entry point is reached only by an
/// explicit <c>--*-samples</c> or <c>--menu-check</c> argument.</para>
///
/// <para>Two pieces of boilerplate were repeated throughout and are now written once:
/// <c>SavePng</c> (five verbatim copies) and <c>ComposeOnBackdrop</c> (two). The backdrop
/// colours were hard-coded literals restating values <see cref="PanelTheme"/> already
/// owns, so a palette change would have silently stopped the verification images matching
/// what ships — which defeats the point of having them. They resolve from the theme now.</para>
/// </summary>
internal static class SampleRenderer
{
    /// <summary>
    /// Renders the detail panel offscreen, in both themes, across the states that change
    /// what it asserts about the weekly window.
    ///
    /// The 31-day graph is the reason this exists. Its week gridlines sit on the derived
    /// weekly reset, and whether they land correctly cannot be judged from unit tests —
    /// those cover the boundary arithmetic, not where a line ends up on a Canvas. Opening
    /// the real panel needs the single-instance mutex and a free display, and it silently
    /// fails when something is running fullscreen.
    ///
    /// The data is constructed rather than read from the store: the interesting contrast is
    /// a plan-derived boundary at 06:28 (which falls INSIDE a day column) against the
    /// Monday-midnight fallback (which lands on a column edge), and one machine's history
    /// shows only whichever it happens to be in.
    /// </summary>
    public static void RenderPopupSamples(string dir)
    {
        Directory.CreateDirectory(dir);
        const double scale = 2.0;

        // A fixed "today" pins the graph — the part being verified — so a rerun that shows
        // different gridlines means the rendering changed, not the date. The header clock
        // and the reset countdowns still read the real time, so the images are not byte-
        // identical between runs; compare the chart, not the file hash.
        var today = new DateOnly(2026, 7, 28);
        var installed = today.AddDays(-9);       // partial history, so pre-install days show

        var series = new List<DayUsage>();
        for (var offset = 30; offset >= 0; offset--)
        {
            var date = today.AddDays(-offset);
            var preInstall = date < installed;
            // A shape with visible within-week variation, so the intensity bands are legible.
            long tokens = preInstall ? 0 : (long)(6_000_000 + 9_000_000 * Math.Abs(Math.Sin(offset * 0.9)));
            series.Add(new DayUsage(date, tokens, preInstall));
        }

        var stats = new PanelStatistics(
            TokensToday: 12_700_000, EstTodayUsd: 9.36m,
            Tokens31Days: 684_600_000, Est31DaysUsd: 492.52m,
            RecordedDays: 10, WindowDays: 31,
            DailySeries: series,
            CreditTokens31Days: 40_000_000, EstCredit31DaysUsd: 92.75m);

        var capturedAt = new DateTimeOffset(today.ToDateTime(new TimeOnly(10, 47)), TimeSpan.Zero);
        var account = new ClaudeAccount("Maximilian", "sample@example.com", "claude_pro", "sample-org");

        // The reset the dev machine actually observed: 06:28:57Z, i.e. a boundary a quarter
        // of the way into its day column rather than on the edge.
        var nextWeekly = new DateTimeOffset(2026, 8, 4, 6, 28, 57, TimeSpan.Zero);

        var live = new UsageSnapshot(DataSource.Live, 25, 3,
            capturedAt.AddHours(3), capturedAt, nextWeekly, TimeSpan.FromHours(10), TimeSpan.FromDays(7));

        // Plan meters showing real usage while NOTHING is recorded locally — the state that
        // triggers the token-scope note. Constructed, because a machine that feeds the
        // transcripts can never produce it, which is exactly why the note went years
        // carrying a hard-coded path and a source list that had been wrong since issue #44:
        // no sample ever rendered it (issue #58).
        var noLocalUsage = stats with
        {
            TokensToday = 0, EstTodayUsd = 0m,
            Tokens31Days = 0, Est31DaysUsd = 0m,
            RecordedDays = 0,
            DailySeries = [.. series.Select(d => d with { TotalTokens = 0, PreInstall = true })],
            CreditTokens31Days = 0, EstCredit31DaysUsd = 0m,
        };

        // Fixed roots rather than the real machine's, so the note is deterministic and the
        // sample does not leak this profile's paths into a committed screenshot.
        var emptyScope = TranscriptScopeReport.Inspect(
            projectsRoot: @"C:\Users\<user>\.claude\projects",
            coworkRoots: [@"C:\Users\<user>\AppData\Roaming\Claude\local-agent-mode-sessions"]);

        var cases = new (string Name, UsageSnapshot Snapshot, PanelStatistics Stats, TranscriptScopeReport? Scope)[]
        {
            ("plan-weeks", live, stats, null),
            // No reset observed: Monday-midnight gridlines, and "Waiting for first reset…".
            ("awaiting-first-reset", new UsageSnapshot(DataSource.Live, 25, 3,
                capturedAt.AddHours(3), capturedAt), stats, null),
            // The token-scope note, which appears in no other state.
            ("no-local-transcripts", live, noLocalUsage, emptyScope),
        };

        foreach (var light in new[] { true, false })
        {
            // The graph's hover cards, rendered standalone — a ToolTip cannot be given a
            // parent, so it appears in no screenshot of the panel and hover is otherwise
            // the only way to see one.
            RenderHoverCards(dir, light,
                [.. new PopupWindow { ThemeOverride = light }.BuildSampleHoverCards(light)],
                "graph-hover-cards");

            foreach (var (name, snapshot, caseStats, scope) in cases)
            {
                var popup = new PopupWindow { ThemeOverride = light, ScopeReport = scope };
                var bitmap = popup.RenderToBitmap(snapshot, caseStats, account, scale);

                SavePng(bitmap, Path.Combine(dir, $"panel-{name}-{(light ? "light" : "dark")}.png"));
            }
        }
    }

    /// <summary>
    /// Renders the tray-menu flyout in both themes, with the toggles on and off, over a
    /// desktop-ish backdrop so the rounded card and its border are actually visible
    /// (issue #33). Visual verification only — nothing here runs in normal operation.
    /// </summary>
    public static void RenderMenuSamples(string dir)
    {
        Directory.CreateDirectory(dir);
        const double scale = 2.0;
        const double pad = 16;

        // The threshold selector needs its OPEN state in the sheet, not just its closed one:
        // the list is inline in the card (it is not a Popup, precisely so it can be rendered),
        // and that is the frame showing whether the group reads as belonging to the row above
        // it rather than as three more top-level items.
        var cases = new (string Name, bool Startup, bool Notify, int Threshold, bool Open)[]
        {
            ("both-on", true, true, 70, false),
            ("both-off", false, false, 70, false),
            ("threshold-open", true, true, 70, true),
            ("threshold-open-90", true, true, 90, true),
            ("threshold-90", true, true, 90, false),
            // A hand-edited settings.json value that is none of the three offered. It must
            // render honestly rather than snap to the nearest option — TraySettings accepts
            // any 1-100 value, and a pill reading "80%" over a stored 75 would be the menu
            // asserting something untrue about the machine.
            ("threshold-unlisted-75", true, true, 75, true),
        };

        foreach (var light in new[] { true, false })
        {
            foreach (var (name, startup, notify, threshold, open) in cases)
            {
                var menu = new MenuWindow { ThemeOverride = light };
                menu.Populate(startup, notify, threshold, UpdateService.CurrentVersion);
                menu.ExpandThresholdsForVerification(open);
                var card = menu.RenderToBitmap(scale);

                SavePng(
                    ComposeOnBackdrop(card, light, scale, pad),
                    Path.Combine(dir, $"menu-{name}-{(light ? "light" : "dark")}.png"));
            }
        }
    }

    /// <summary>
    /// Renders the stat tiles across the model counts that change the chart's shape,
    /// in both themes and both views (issue #37). The cases that matter — a folded
    /// "Other", an unpriced model — are ones a given machine's real data may never
    /// produce, so they are constructed rather than waited for.
    /// </summary>
    public static void RenderTileSamples(string dir)
    {
        Directory.CreateDirectory(dir);

        ModelSlice S(string model, long tokens, decimal? usd) =>
            new(model, ModelDisplayName.For(model), tokens, usd);

        // The 31-day window, and the tiles that draw from it. Deliberately shaped like
        // the reported case: "today" contains ONLY Opus 5, which used to make Opus 5 the
        // first segment there and therefore blue, while the 31-day tile ranked Opus 4.8
        // first and painted Opus 5 orange. One colour order, derived from the window,
        // is what stops that — so the samples share one, exactly as the panel does.
        ModelSlice[] window =
        [
            S("claude-opus-4-8", 350_000_000, 250.00m),
            S("claude-opus-5", 180_000_000, 120.00m),
            S("claude-fable-5", 40_000_000, 50.00m),
            S("claude-haiku-4-5", 5_900_000, 5.21m),
        ];
        var colourOrder = ModelBreakdown.ColourOrder(window);

        var cases = new (string Name, ModelSlice[] Slices)[]
        {
            ("today — Opus 5 only (was blue here, orange below)", [S("claude-opus-5", 155_400_000, 91.44m)]),
            ("31 days — all four models", window),
            ("two of the window's models", [window[0], window[2]]),
            ("three models, no remainder", [.. window.Take(3)]),
            ("unpriced model present",
                [S("claude-opus-4-8", 150_000_000, 30.00m), S("claude-brandnew-9", 4_000_000, null)]),
        };

        foreach (var light in new[] { true, false })
        {
            foreach (var expanded in new[] { false, true })
            {
                var rows = new StackPanel { Margin = new Thickness(16) };
                var pending = new List<(StatTile Tile, string Label, string Value, ModelSlice[] Slices, BreakdownMeasure Measure)>();
                foreach (var (name, slices) in cases)
                {
                    rows.Children.Add(new TextBlock
                    {
                        Text = name,
                        FontSize = 11,
                        Margin = new Thickness(0, 6, 0, 3),
                        Foreground = new System.Windows.Media.SolidColorBrush(light
                            ? System.Windows.Media.Color.FromRgb(0x33, 0x33, 0x33)
                            : System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0xCC)),
                    });

                    var pair = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                    var tokensTile = NewSampleTile(BreakdownMeasure.Tokens);
                    var valueTile = NewSampleTile(BreakdownMeasure.EstValue);
                    pair.Children.Add(tokensTile);
                    pair.Children.Add(valueTile);
                    rows.Children.Add(pair);

                    // Carry the coverage caveat on the samples too: it has to survive
                    // into the breakdown view, and a sample that never shows one cannot
                    // catch it going missing (it did).
                    pending.Add((tokensTile, "Tokens · 31 days",
                        UsageFormatter.Tokens(slices.Sum(s => s.Tokens)), slices, BreakdownMeasure.Tokens));
                    pending.Add((valueTile, "Est. value · 31 days",
                        SumSampleUsd(slices), slices, BreakdownMeasure.EstValue));
                }

                // The tiles resolve their colours through DynamicResource, so the palette
                // has to live on a host they are parented to.
                var host = new Border
                {
                    Child = rows,
                    Background = new System.Windows.Media.SolidColorBrush(light
                        ? System.Windows.Media.Color.FromRgb(0xF9, 0xF9, 0xF9)
                        : System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20)),
                };
                PanelTheme.Apply(host.Resources, light);

                // Only now: the breakdown resolves its series colours through the
                // logical tree, so a tile populated before it is parented and themed has
                // nowhere to find them. The real panel gets this ordering for free — its
                // tiles are declared in XAML and the theme is applied before Populate.
                foreach (var (tile, label, value, slices, _) in pending)
                {
                    tile.Populate(label, value, "8 of 31 days recorded", slices, tile.SplitBy, colourOrder);
                    tile.SetExpanded(expanded);
                }

                if (expanded)
                {
                    RenderHoverCards(dir, light,
                    [
                        pending[6].Tile.BuildSampleTooltip(0),   // 5-models: named model, tokens
                        pending[6].Tile.BuildSampleTooltip(2),   // 5-models: folded "Other"
                        pending[7].Tile.BuildSampleTooltip(0),   // 5-models: named model, est. value
                        // An UNRECOGNISED model, where the friendly name is the raw id.
                        // Included because that is the case where dropping the card's
                        // title could have left nothing naming the row at all.
                        pending[8].Tile.BuildSampleTooltip(1),
                    ]);
                }

                SavePng(
                    VisualRenderer.Render(host),
                    Path.Combine(dir,
                        $"tiles-{(expanded ? "breakdown" : "summary")}-{(light ? "light" : "dark")}.png"));

                // Hover timing cannot be seen in a still, and the part that can silently
                // break is the inheritance from the control down to the segments — a
                // segment that missed it would fall back to WPF's defaults and look
                // identical. Report the resolved values so they are checked, not assumed.
                if (expanded && pending.FirstOrDefault().Tile?.ResolvedHoverTiming() is { } timing)
                {
                    // Intended values only — no "framework default" annotations. The
                    // unset baseline measured on the dev machine (1000 / 100 /
                    // int.MaxValue) did not match the documented defaults, so printing
                    // documented figures beside measured ones would assert something
                    // unverified.
                    File.WriteAllText(Path.Combine(dir, "hover-timing.txt"),
                        $"resolved on a bar segment ({(light ? "light" : "dark")}):{Environment.NewLine}" +
                        $"  InitialShowDelay : {timing.InitialShowDelay} ms (intended 400){Environment.NewLine}" +
                        $"  BetweenShowDelay : {timing.BetweenShowDelay} ms (intended 3000){Environment.NewLine}" +
                        $"  ShowDuration     : {timing.ShowDuration} ms (intended 20000){Environment.NewLine}");
                }
            }
        }
    }

    /// <summary>
    /// Renders the update dialog in both themes and both variants — the installed path
    /// ("Update now", with the relaunch note) and the portable one ("Open page").
    /// </summary>
    public static void RenderDialogSamples(string dir)
    {
        Directory.CreateDirectory(dir);
        const double scale = 2.0;
        const double pad = 16;

        var variants = new (string Name, string Message, string Confirm, string Detail)[]
        {
            ("installed", "O-view 0.5.1 is available — you have 0.5.0. Download and install it now?",
                "Update now", "O-view will close briefly and reopen automatically."),
            ("portable", "O-view 0.5.1 is available — you have 0.5.0. Open the download page for the new version?",
                "Open page", ""),
        };

        foreach (var light in new[] { true, false })
        {
            foreach (var (name, message, confirm, detail) in variants)
            {
                var dialog = new DialogWindow { ThemeOverride = light };
                dialog.Populate("Update available", message, confirm, "Not now", detail);
                var card = dialog.RenderToBitmap(scale);

                SavePng(
                    ComposeOnBackdrop(card, light, scale, pad),
                    Path.Combine(dir, $"dialog-{name}-{(light ? "light" : "dark")}.png"));
            }
        }
    }

    /// <summary>
    /// Renders the hover cards to their own PNG. They need their own pass because a
    /// ToolTip cannot be given a parent — it throws — so it can neither be laid out
    /// inside the sample panel nor appear in a screenshot of the panel itself. Measured,
    /// arranged and rendered standalone instead, with the palette written into each
    /// card's own resources so its DynamicResource lookups resolve without a tree above
    /// it to walk.
    /// </summary>
    private static void RenderHoverCards(string dir, bool light, ToolTip?[] tips, string name = "hover-cards")
    {
        const double scale = VisualRenderer.DefaultScale;
        const double gap = 12;

        var rendered = new List<BitmapSource>();
        foreach (var tip in tips)
        {
            if (tip is null)
            {
                continue;
            }

            PanelTheme.Apply(tip.Resources, light);
            rendered.Add(VisualRenderer.Render(tip, scale));
        }

        if (rendered.Count == 0)
        {
            return;
        }

        var totalWidth = rendered.Sum(b => b.PixelWidth / scale) + gap * (rendered.Count + 1);
        var totalHeight = rendered.Max(b => b.PixelHeight / scale) + 2 * gap;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Over the tile surface the cards actually float above, so the elevation
            // step reads the way it will in the app.
            dc.DrawRectangle(PanelTheme.Brush("TileBg", light), null,
                new Rect(0, 0, totalWidth, totalHeight));

            var x = gap;
            foreach (var card in rendered)
            {
                dc.DrawImage(card, new Rect(x, gap, card.PixelWidth / scale, card.PixelHeight / scale));
                x += card.PixelWidth / scale + gap;
            }
        }

        SavePng(
            VisualRenderer.ToBitmap(visual, totalWidth, totalHeight, scale),
            Path.Combine(dir, $"{name}-{(light ? "light" : "dark")}.png"));
    }

    private static StatTile NewSampleTile(BreakdownMeasure measure) =>
        new()
        {
            Width = 180,
            Margin = new Thickness(0, 0, 8, 0),
            SplitBy = measure,
            // The panel's own formatters, so a sample cannot render a figure differently
            // from the tile it is a picture of (issue #55).
            FormatSlice = measure == BreakdownMeasure.Tokens
                ? s => UsageFormatter.Tokens(s.Tokens)
                : s => UsageFormatter.Usd(s.EstUsd),
        };

    private static string SumSampleUsd(ModelSlice[] slices) =>
        UsageFormatter.Usd(slices.Sum(s => s.EstUsd ?? 0m));

    /// <summary>Renders every icon state at 100% and 150% scaling sizes for visual verification.</summary>
    public static void RenderSamples(string dir)
    {
        Directory.CreateDirectory(dir);
        var states = new (string Name, UsageSnapshot Snapshot)[]
        {
            ("live-06", new(DataSource.Live, 6, 1, null, DateTimeOffset.UtcNow)),
            ("live-47", new(DataSource.Live, 47, 20, null, DateTimeOffset.UtcNow)),
            ("live-58", new(DataSource.Live, 58, 30, null, DateTimeOffset.UtcNow)),
            ("live-72", new(DataSource.Live, 72, 40, null, DateTimeOffset.UtcNow)),
            ("live-91", new(DataSource.Live, 91, 60, null, DateTimeOffset.UtcNow)),
            ("live-100", new(DataSource.Live, 100, 80, null, DateTimeOffset.UtcNow)),
            ("estimate", new(DataSource.Estimate, null, null, null, DateTimeOffset.UtcNow)),
            ("none", UsageSnapshot.None),
        };

        foreach (var size in new[] { 16, 24 })  // 100% and 150% scaling
        {
            foreach (var light in new[] { false, true })
            {
                foreach (var (name, snapshot) in states)
                {
                    using var bmp = IconRenderer.Render(size, snapshot, light);
                    bmp.Save(Path.Combine(dir, $"{name}-{size}px-{(light ? "light" : "dark")}.png"));
                }
            }
        }
    }

    // ── shared plumbing ───────────────────────────────────────────────────────────

    /// <summary>
    /// Encodes and writes one PNG. Existed five times verbatim across the renderers.
    /// </summary>
    private static void SavePng(BitmapSource bitmap, string path)
    {
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        png.Save(stream);
    }

    /// <summary>
    /// Composites a transparent-cornered card over a flat backdrop, so its corner radius
    /// and border read the way they will on screen. Existed twice, identically bar the
    /// variable names — the menu and dialog samples.
    ///
    /// <para>The backdrop is a desktop stand-in rather than a panel surface, so it is one
    /// step outside the palette in each theme: light greys down, dark greys up. Taken from
    /// <see cref="PanelTheme"/> rather than restated as literals.</para>
    /// </summary>
    private static BitmapSource ComposeOnBackdrop(
        BitmapSource card, bool light, double scale = VisualRenderer.DefaultScale, double pad = 16)
    {
        var w = card.PixelWidth / scale;
        var h = card.PixelHeight / scale;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(PanelTheme.Brush("Backdrop", light), null,
                new Rect(0, 0, w + 2 * pad, h + 2 * pad));
            dc.DrawImage(card, new Rect(pad, pad, w, h));
        }

        return VisualRenderer.ToBitmap(visual, w + 2 * pad, h + 2 * pad, scale);
    }

    /// <summary>
    /// Exercises every menu row the way a user does, repeatedly: open the flyout, activate a
    /// row, let it close, open it again. A single activation of a single row can never catch
    /// this class of fault — the flyout carries state across opens (`_closing`, `_closedAt`)
    /// and it is the SECOND and third cycles that expose it, exactly as `--toggle-check`
    /// exists because the panel's toggle only broke on the second open.
    ///
    /// Handlers here only record. The point is the plumbing from a row to its event — running
    /// the real actions would exit the app halfway through and download an installer.
    /// </summary>
    public static void RunMenuCheck(string path, Action shutdown)
    {
        var report = new System.Text.StringBuilder();
        var fired = new Dictionary<string, int>
        {
            ["diagnostics"] = 0, ["updates"] = 0, ["exit"] = 0,
        };

        var menu = new MenuWindow
        {
            SetRunAtStartup = enable => enable,          // no registry writes in a check
            SetNotifyOnThreshold = enable => enable,
        };
        menu.CopyDiagnosticsRequested += (_, _) => fired["diagnostics"]++;
        menu.CheckForUpdatesRequested += (_, _) => fired["updates"]++;
        menu.ExitRequested += (_, _) => fired["exit"]++;

        // Every open re-populates from these, so each cycle starts from both ticks ON and
        // a toggle within the cycle must flip its row to OFF.
        void Open() => menu.ShowDocked(true, true, 70, UpdateService.CurrentVersion);

        bool Ticked(MenuWindow.MenuRow row) => menu.RowIsChecked(row);

        void Record(string label) =>
            report.AppendLine($"{label,-42} visible={menu.IsVisible,-5} closing={menu.IsClosing,-5} " +
                              $"ticks=[startup {Ticked(MenuWindow.MenuRow.Startup),-5} " +
                              $"notify {Ticked(MenuWindow.MenuRow.Notify),-5}] " +
                              $"fired=[diag {fired["diagnostics"]}, upd {fired["updates"]}, exit {fired["exit"]}]");

        // A toggle row renders what its setter RETURNS, not what was requested — the whole
        // point being that a failed registry write must not draw a tick that lies about the
        // machine. Nothing verified the tick actually followed, so verify it: with identity
        // setters here, a toggle that leaves its row unchanged means the plumbing from the
        // row through the setter to the glyph is broken.
        //
        // Checked on the FOLLOWING step, not inline. InvokeRow goes through the button's
        // automation peer, which raises Click on a later dispatcher turn — so the tick has
        // not moved by the time InvokeRow returns, and comparing immediately reports every
        // toggle as broken. (It did: the first version of this counter said 0 of 6 while
        // the recorded tick states plainly showed all six flipping.)
        var toggleFlips = 0;
        var toggleAttempts = 0;
        (MenuWindow.MenuRow Row, bool Before)? pendingToggle = null;

        void SettleToggle()
        {
            if (pendingToggle is not { } pending)
            {
                return;
            }

            pendingToggle = null;
            toggleAttempts++;
            if (Ticked(pending.Row) != pending.Before)
            {
                toggleFlips++;
            }
        }

        void CheckedToggle(MenuWindow.MenuRow row)
        {
            SettleToggle();                     // the previous toggle has had a turn by now
            pendingToggle = (row, Ticked(row));
            menu.InvokeRow(row);
        }

        // Each action row, three times over, with a full open/close cycle between — plus the
        // toggle rows, which must NOT close the flyout, and a click-away dismissal.
        var steps = new List<(string Label, Action Do)>();
        for (var cycle = 1; cycle <= 3; cycle++)
        {
            var n = cycle;
            foreach (var row in new[] { MenuWindow.MenuRow.Updates, MenuWindow.MenuRow.Diagnostics })
            {
                steps.Add(($"cycle {n}: open before {row}", Open));
                steps.Add(($"cycle {n}: activate {row}", () => menu.InvokeRow(row)));
                steps.Add(($"cycle {n}: settled after {row}", () => { }));
            }

            steps.Add(($"cycle {n}: open for toggles", Open));
            steps.Add(($"cycle {n}: toggle Run at startup", () => CheckedToggle(MenuWindow.MenuRow.Startup)));
            steps.Add(($"cycle {n}: toggle Notify", () => CheckedToggle(MenuWindow.MenuRow.Notify)));
            steps.Add(($"cycle {n}: toggles left it open", SettleToggle));
            steps.Add(($"cycle {n}: dismiss (click-away)", menu.DismissNow));
            steps.Add(($"cycle {n}: settled after dismiss", () => { }));
        }

        var step = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            if (step > 0)
            {
                Record(steps[step - 1].Label);
            }

            if (step == steps.Count)
            {
                timer.Stop();
                var expected = 3;
                report.AppendLine();
                report.AppendLine($"activations of 'Check for updates…' : {fired["updates"]} of {expected} expected");
                report.AppendLine($"activations of 'Copy diagnostics'   : {fired["diagnostics"]} of {expected} expected");
                report.AppendLine($"toggles that moved their tick       : {toggleFlips} of {toggleAttempts} expected");
                var pass = fired["updates"] == expected
                           && fired["diagnostics"] == expected
                           && toggleFlips == toggleAttempts;
                report.AppendLine($"RESULT: {(pass ? "PASS" : "FAIL")}");
                File.WriteAllText(path, report.ToString());
                shutdown();
                return;
            }

            steps[step++].Do();
        };
        timer.Start();
    }

    /// <summary>
    /// Exercises the tray click the way a user does: open, close, open again — each step
    /// through the same ShowPopup() the icon calls, spaced far enough apart that the
    /// close transition and the toggle's grace window have both elapsed. Writes what the
    /// panel actually is at each point, so a "stopped reacting" report becomes a fact
    /// rather than a guess.
}
