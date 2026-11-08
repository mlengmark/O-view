using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using OView.App.Rendering;
using OView.Core.Models;
using OView.Presentation.Panel;
using OView.Presentation.Samples;
using OView.Tray.Popup;
using Border = System.Windows.Controls.Border;

namespace OView.Tray.Diagnostics;

/// <summary>
/// <c>--motion-check</c>: turns the panel's local block over, flips a stat tile and folds the
/// menu's threshold list on real windows, each to its end, and then clicks a second time while
/// the first transition is still running.
///
/// <para>What a still cannot show, and a single click cannot catch, is a transition that does
/// not finish or finishes in the wrong place. This app has shipped a missed completion once
/// (<see cref="FlyoutAnimation"/>), and the two surfaces here answer a second click by turning
/// round from where they are rather than by snapping to an end, which is a state machine with
/// four ways to end up in the wrong state. So each surface is driven through: open, close, a
/// click reversing an opening, and a click reversing a closing, and the state it rests in is read
/// every time.</para>
///
/// <para>Driven by one-shot timers at <see cref="DispatcherPriority.Send"/> and by raising
/// <c>Click</c> directly. A sampler on <c>CompositionTarget.Rendering</c> and a click through an
/// automation peer both land late and in bursts while an animation runs, which is the opposite
/// of a click meant for the middle of one. Every wait is a figure from
/// <see cref="PanelMotion"/>, so a retuned pace retunes the check with it.</para>
/// </summary>
internal static class MotionChecks
{
    /// <summary>
    /// How long a transition is given to come to rest, as a multiple of its own length. Both
    /// surfaces have a guard that settles them 150 ms after their last animation, so twice the
    /// length is past it at any pace, and a surface still moving then is stuck.
    /// </summary>
    private const double SettleBudget = 2;

    /// <summary>
    /// Where in an opening the second click lands. Half way: the curve has done most of its work
    /// by then and the click still finds everything moving.
    /// </summary>
    private const double ReverseOpeningAt = 0.5;

    /// <summary>
    /// Where in a closing the second click lands. Later than half, because the accelerating
    /// curve barely moves in its first half and a click there would find it where it started.
    /// </summary>
    private const double ReverseClosingAt = 0.75;

    /// <summary>The half turn the tile's glyph and the list's chevron make between their rests.</summary>
    private const double OpenTurnDegrees = 180;

    private const double TurnToleranceDegrees = 0.5;

    /// <summary>
    /// How long after a second click its faces are read again: past the next rendered frame, since
    /// a read in the click's own turn returns the base value rather than what is on screen.
    /// </summary>
    private const double ContinuationProbeMs = 60;

    /// <summary>
    /// How far a face may have moved by <see cref="ContinuationProbeMs"/>: the most the fades can
    /// travel in that time. A face that snapped to an end moves much further.
    /// </summary>
    private const double ContinuationToleranceOpacity = 0.2;

    /// <summary>
    /// One surface the check drives: how to click it, whether it is mid-transition, and how it
    /// reads when it should be at rest open or closed. <c>Misses</c> returns null when it is,
    /// and otherwise what is wrong. <c>Opacities</c> are the faces a second click must carry on
    /// from, for a surface whose claim is that it does.
    /// </summary>
    private sealed record Surface(
        string Name, Action Click, Func<bool> InFlight, Func<bool, string?> Misses,
        double OpenMs, double CloseMs, double SettleMs, Func<double[]>? Opacities = null);

    public static void Run(string path, Action shutdown)
    {
        var report = new StringBuilder();

        // Without animations nothing is ever in flight, so every second click would report that
        // it missed. Say so instead of failing for a reason that is the machine's setting.
        if (!WpfMotion.AnimationsOn)
        {
            report.AppendLine("motion check NOT RUN, Windows' \"animate controls and elements\" setting is off.");
            report.AppendLine("With it off every transition ends at once, so there is no mid-flight to click in.");
            report.AppendLine();
            report.AppendLine("RESULT: NOT RUN");
            File.WriteAllText(path, report.ToString());
            shutdown();
            return;
        }

        var windows = new List<Window>();
        var failures = 0;
        var queue = new Queue<(double DelayMs, Action Do)>();

        void Add(Surface surface)
        {
            foreach (var (label, clicksAtMs, expectOpen) in Script(surface))
            {
                bool? midFlight = null;
                string? jumped = null;
                queue.Enqueue((0, surface.Click));
                if (clicksAtMs is { } secondAt)
                {
                    double[]? before = null;
                    queue.Enqueue((secondAt, () =>
                    {
                        midFlight = surface.InFlight();
                        before = surface.Opacities?.Invoke();
                        surface.Click();
                    }));

                    if (surface.Opacities is { } opacities)
                    {
                        queue.Enqueue((ContinuationProbeMs, () => jumped = ContinuationMiss(before!, opacities())));
                    }
                }

                queue.Enqueue((surface.SettleMs, () =>
                {
                    var miss = jumped ?? surface.Misses(expectOpen);
                    var why = miss
                              ?? (midFlight == false ? "the second click found it already at rest, so it proved nothing" : null);
                    failures += why is null ? 0 : 1;
                    report.AppendLine(
                        $"{surface.Name,-5} {label,-24} second click mid-flight: " +
                        $"{(midFlight is null ? "n/a" : midFlight.Value ? "yes" : "NO"),-3} " +
                        $"expected {(expectOpen ? "open  " : "closed")} " +
                        (why is null ? "ok" : $"FAIL, {why}"));
                }));
            }
        }

        void Finish(Exception? error)
        {
            foreach (var window in windows)
            {
                window.Close();
            }

            if (error is not null)
            {
                failures++;
                report.AppendLine($"threw: {error}");
            }

            report.AppendLine();
            report.AppendLine($"RESULT: {(failures == 0 ? "PASS" : "FAIL")}");
            File.WriteAllText(path, report.ToString());
            shutdown();
        }

        try
        {
            Add(BlockSurface(windows));
            Add(TileSurface(windows));
            // The menu opens only when its turn comes, and is given its own entrance to finish
            // before the list is touched: a fold started under an arriving surface is two
            // transitions, and this check is about one.
            var menu = new MenuWindow { PinForVerification = true };
            queue.Enqueue((0, () => menu.ShowDocked(new MenuWindow.MenuState(
                RunAtStartup: true,
                NotifyOnThreshold: true,
                ThresholdPercent: 70,
                UpdateAutomatically: true,
                CanUpdateAutomatically: true,
                Version: "motion-check"))));
            queue.Enqueue((SettleBudget * PanelMotion.Ms(PanelMotion.SurfaceOpenBaseMs), () => { }));
            Add(ListSurface(menu));
            queue.Enqueue((0, () => menu.DismissNow()));
        }
        catch (Exception ex)
        {
            Finish(ex);
            return;
        }

        void Next()
        {
            if (queue.Count == 0)
            {
                Finish(null);
                return;
            }

            var (delayMs, step) = queue.Dequeue();
            var timer = new DispatcherTimer(DispatcherPriority.Send)
            {
                Interval = TimeSpan.FromMilliseconds(delayMs),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    step();
                }
                catch (Exception ex)
                {
                    Finish(ex);
                    return;
                }

                Next();
            };
            timer.Start();
        }

        Next();
    }

    /// <summary>
    /// The sequence each surface is put through, from closed. Each entry is a click and, for the
    /// two reversals, a second click that long after the first; then the state it must rest in.
    /// </summary>
    private static IEnumerable<(string Label, double? SecondClickAtMs, bool ExpectOpen)> Script(Surface s) =>
    [
        ("opens", null, true),
        ("closes", null, false),
        ("reversed while opening", s.OpenMs * ReverseOpeningAt, false),
        ("opens again", null, true),
        ("reversed while closing", s.CloseMs * ReverseClosingAt, true),
    ];

    /// <summary>
    /// The local block's flip on a real panel, from the totals face. Both reversals land while the
    /// two faces are on screen together, which is what "reverses the crossfade" is a claim about.
    /// </summary>
    private static Surface BlockSurface(List<Window> windows)
    {
        var state = SampleStates.All.Single(s => s.Name == "offplan-on-models");
        var panel = new PopupWindow
        {
            PinForVerification = true,
            ThemeOverride = false,
            FixedNow = SampleStates.Now,
            FixedZone = SampleStates.Zone,
            ScopeReport = state.Scope,
            DataReport = state.Plan,
            CachedUsage = state.CachedUsage,
            BoostNotices = state.Boost,
            ViewChoice = state.View with { Face = PanelFace.Totals },
        };
        panel.ShowNearTrayIcon(state.Snapshot, state.Stats, state.Account);
        windows.Add(panel);

        // Half way through the only time both faces show: after the new one starts, before the
        // old one has gone.
        var bothShowingMs = PanelMotion.Ms(PanelMotion.TileFaceInDelayBaseMs + PanelMotion.TileFaceOutBaseMs) / 2;

        // The chart's bars rising on the way back are the longest part of either direction.
        var flipMs = PanelMotion.ChartBarBackStartMs(state.Stats.DailySeries.Count)
                     + PanelMotion.Ms(PanelMotion.ChartBarRiseBaseMs);

        return new Surface(
            "block", panel.ClickFlipForVerification, () => panel.FlipReadout.Flipping,
            open => BlockMiss(panel.FlipReadout, open),
            OpenMs: bothShowingMs / ReverseOpeningAt, CloseMs: bothShowingMs / ReverseClosingAt,
            SettleMs: SettleBudget * flipMs,
            Opacities: () => [panel.FlipReadout.TotalsOpacity, panel.FlipReadout.ModelsOpacity]);
    }

    private static string? ContinuationMiss(double[] before, double[] after) =>
        before.Zip(after).Any(pair => Math.Abs(pair.First - pair.Second) > ContinuationToleranceOpacity)
            ? $"the faces jumped: opacity {before[0]:0.00}, {before[1]:0.00} became {after[0]:0.00}, {after[1]:0.00}"
            : null;

    private static string? BlockMiss(PopupLocalBlock.FlipReadout r, bool open)
    {
        if (r.Flipping)
        {
            return "a flip is still running";
        }

        if (r.ShowingModels != open)
        {
            return $"the block is showing the {(r.ShowingModels ? "model" : "totals")} face";
        }

        var (shown, shownOpacity, gone) = open
            ? (r.Models, r.ModelsOpacity, r.Totals)
            : (r.Totals, r.TotalsOpacity, r.Models);

        if (shown != Visibility.Visible || shownOpacity < 1)
        {
            return $"the {(open ? "model" : "totals")} face is {shown} at opacity {shownOpacity:0.00}";
        }

        if (gone != Visibility.Collapsed)
        {
            return $"the {(open ? "totals" : "model")} face is still {gone}";
        }

        if (r.HeightPinned)
        {
            return "the height is still pinned";
        }

        return r.Clipped ? "the faces are still clipped" : null;
    }

    private static Surface TileSurface(List<Window> windows)
    {
        ModelSlice Slice(string model, long tokens) => new(model, ModelDisplayName.For(model), tokens);

        ModelSlice[] slices =
        [
            Slice("claude-opus-4-8", 350_000_000),
            Slice("claude-opus-5", 180_000_000),
            Slice("claude-fable-5", 40_000_000),
        ];
        var view = PanelSections.Tile(
            "Tokens", "570M", "", slices, BreakdownMeasure.Tokens, ModelBreakdown.ColourOrder(slices));

        // Themed before it is populated, as the sample renders do: the breakdown resolves its
        // colours through the tree.
        var tile = new StatTile { Width = 180 };
        var host = new Border
        {
            Child = tile,
            Padding = new Thickness(16),
            Background = PanelTheme.Brush("PanelBg", false),
        };
        PanelTheme.Apply(host.Resources, false);

        var window = new Window
        {
            Content = host,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Left = 40,
            Top = 40,
        };
        tile.Populate(view);
        window.Show();
        window.UpdateLayout();
        windows.Add(window);

        // The face going out, the face coming in behind it, the bar and legend drawing in, and
        // the glyph turning: the flip is over when the last of them is.
        var arrivesMs = PanelMotion.Ms(PanelMotion.TileFaceInDelayBaseMs + PanelMotion.TileFaceInBaseMs);
        var barMs = PanelMotion.Ms(PanelMotion.TileModelBarDelayBaseMs + PanelMotion.TileModelBarBaseMs);
        var legendMs = PanelMotion.LegendStartMs(view.Segments.Count - 1) + PanelMotion.Ms(PanelMotion.TileLegendFadeBaseMs);
        var turnMs = PanelMotion.Ms(PanelMotion.TileGlyphTurnBaseMs);
        var flipMs = Math.Max(Math.Max(arrivesMs, turnMs), Math.Max(barMs, legendMs));

        return new Surface(
            "tile", tile.ClickForVerification, () => tile.Readout.Flipping,
            open => TileMiss(tile.Readout, open),
            OpenMs: arrivesMs, CloseMs: arrivesMs, SettleMs: SettleBudget * flipMs);
    }

    private static Surface ListSurface(MenuWindow menu)
    {
        var openMs = PanelMotion.Ms(PanelMotion.ThresholdOpenBaseMs);
        var closeMs = PanelMotion.Ms(PanelMotion.ThresholdCloseBaseMs);
        var rowsMs = PanelMotion.OptionStartMs(menu.OptionRowCount - 1) + PanelMotion.Ms(PanelMotion.OptionFadeBaseMs);

        return new Surface(
            "list", menu.ClickThresholdPill, () => !menu.ThresholdList.Released,
            open => menu.IsVisible && !menu.IsClosing ? ListMiss(menu.ThresholdList, open) : "the flyout is not open",
            OpenMs: openMs, CloseMs: closeMs, SettleMs: SettleBudget * Math.Max(Math.Max(openMs, closeMs), rowsMs));
    }

    private static string? TileMiss(StatTile.FlipReadout r, bool open)
    {
        if (r.Flipping)
        {
            return "a flip is still running";
        }

        if (r.Expanded != open)
        {
            return $"the tile is asking for the {(r.Expanded ? "breakdown" : "total")}";
        }

        var (shown, shownOpacity, gone) = open
            ? (r.Breakdown, r.BreakdownOpacity, r.Summary)
            : (r.Summary, r.SummaryOpacity, r.Breakdown);

        // Only the visible face's opacity is read: a hidden one is reset to full on settling.
        if (shown != Visibility.Visible || shownOpacity < 1)
        {
            return $"the {(open ? "breakdown" : "total")} is {shown} at opacity {shownOpacity:0.00}";
        }

        if (gone != Visibility.Hidden)
        {
            return $"the {(open ? "total" : "breakdown")} is still {gone}";
        }

        if (r.BarClipped)
        {
            return "the bar is still clipped";
        }

        return TurnMiss(r.GlyphAngle, open, "glyph");
    }

    /// <summary>
    /// What is wrong with the threshold list when it should be at rest, open or closed; null when
    /// nothing is. <c>--menu-check</c> asks the same question after its own open.
    /// </summary>
    internal static string? ListMiss(MenuWindow.ThresholdListState s, bool open)
    {
        if (s.Open != open)
        {
            return $"the list is logically {(s.Open ? "open" : "closed")}";
        }

        if (!s.Released)
        {
            return "the fold is still holding the list at a pinned height";
        }

        if (s.Shown != open)
        {
            return $"the list is {(s.Shown ? "on screen" : "gone")}";
        }

        if (open && s.LeastRowOpacity < 1)
        {
            return $"a row is at opacity {s.LeastRowOpacity:0.00}";
        }

        return TurnMiss(s.ChevronAngle, open, "chevron");
    }

    private static string? TurnMiss(double angle, bool open, string what) =>
        Math.Abs(angle - (open ? OpenTurnDegrees : 0)) > TurnToleranceDegrees
            ? $"the {what} rests at {angle:0}°"
            : null;
}
