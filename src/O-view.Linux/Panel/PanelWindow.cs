using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using OView.App;
using OView.App.Rendering;
using OView.Core.Models;
using OView.Core.Providers.Jsonl;
using OView.Core.Providers.PlanHistory;

namespace OView.Linux.Panel;

/// <summary>
/// The detail panel as a window, placed in the work area's corner nearest the panel bar.
///
/// <para><b>Still not a docked flyout, and it never can be.</b> StatusNotifierItem exposes no
/// way to ask where its own icon was drawn — there is no <c>Shell_NotifyIconGetRect</c>
/// equivalent — so anchoring the panel <i>under the icon</i> remains impossible and is not
/// attempted. What ADR-0013 decision 3 asks for is "whatever positioning is genuinely
/// achievable", and a fixed corner is: the work area already excludes the panel bar wherever
/// the desktop publishes it, so the same corner the Windows head docks to can be computed
/// from <c>Screens</c> alone (issue #144).</para>
///
/// <para>The corner rule itself is <see cref="WorkAreaPlacement"/>, shared with the Windows
/// head rather than copied — it is a decision a user would expect both platforms to make the
/// same way.</para>
///
/// <para><b>A request, not a guarantee.</b> Setting <c>Position</c> is an X11 request the
/// window manager may ignore, and a native Wayland compositor will not let a client place
/// itself at all. So the requested and actual positions are both logged: the point of
/// <c>--diagnose</c> is that one round trip answers the question, and "did the corner take?"
/// is exactly the question the next hardware report needs to answer. Where the screen
/// geometry cannot be read at all, this falls back to centring rather than guessing at
/// coordinates — ADR-0013's reasoning, unchanged: a panel half off-screen reads as broken
/// where a plainly-centred one does not.</para>
///
/// <para>Deactivation only dismisses once the panel has actually been focused — see
/// <see cref="PanelDismissal"/> for why an unguarded handler makes a refused activation look
/// exactly like a broken tray icon.</para>
/// </summary>
public sealed class PanelWindow : Window
{
    private readonly PanelContent _content;
    private readonly PanelDismissal _dismissal = new();
    private readonly IAppLog? _log;

    /// <summary>The corner we asked for, until the compositor has answered. Null once logged.</summary>
    private PixelPoint? _requestedPosition;

    public PanelWindow(LinuxPanelTheme theme, IAppLog? log = null)
    {
        _content = new PanelContent(theme);
        _log = log;

        Title = "O-view";
        Content = _content;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowDecorations = WindowDecorations.BorderOnly;
        // Overridden per-open by ShowWith, which centres instead when there is no usable
        // screen geometry to compute a corner from. Manual with no position set lands at
        // (0,0), which is worse than either.
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = theme["PanelBg"];

        // Says whether the corner actually took. A window manager is free to ignore a
        // position request and a native Wayland compositor always will, so the difference
        // between "we asked" and "it happened" has to reach a bug report — no test here can
        // reach a compositor.
        PositionChanged += (_, e) =>
        {
            if (_requestedPosition is not { } requested)
            {
                return;
            }

            _requestedPosition = null;   // once per placement, not once per nudge
            var honoured = e.Point == requested;
            _log?.Write(
                $"panel position -> {e.Point.X},{e.Point.Y} (requested {requested.X},{requested.Y}; "
                + $"{(honoured ? "honoured" : "OVERRIDDEN by the window manager")})");
        };

        // Dismissal mirrors the Windows panel: Esc, or clicking away.
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Hide();
            }
        };
        Activated += (_, _) => _dismissal.Activated();
        Deactivated += (_, _) =>
        {
            if (_dismissal.ShouldHideOnDeactivated())
            {
                Hide();
                return;
            }

            // Not a click-away — the compositor refused the activation. Named in the log
            // because "panel opened" alone cannot distinguish this from the #124 deadlock,
            // and the two need completely different fixes.
            _log?.Write(
                "panel deactivated before it was ever focused — dismissal suppressed "
                + $"(x{_dismissal.SuppressedDeactivations}); the window manager declined to activate it");
        };
    }

    public void ShowWith(
        UsageSnapshot snapshot,
        PanelStatistics stats,
        ClaudeAccount? account,
        PlanHistoryReport? dataReport,
        TranscriptScopeReport? scopeReport,
        DateTimeOffset utcNow)
    {
        _content.Populate(snapshot, stats, account, dataReport, scopeReport, utcNow);
        _dismissal.Opening();

        // Decided before Show, applied after it: the corner needs the panel's size, and
        // SizeToContent does not produce one until the window has been laid out. Choosing
        // the startup location up front means a screen we cannot read falls back to Avalonia's
        // own centring rather than to Manual-with-no-position, which is the top-left corner.
        var placeable = TargetScreen() is not null;
        WindowStartupLocation = placeable
            ? WindowStartupLocation.Manual
            : WindowStartupLocation.CenterScreen;

        Show();

        if (placeable)
        {
            FitToScreen(snapshot, stats, account, dataReport, scopeReport, utcNow);
            PlaceAtWorkAreaCorner();
        }

        Activate();
    }

    /// <summary>
    /// Re-lays the panel out compactly when this screen is too short for the natural layout
    /// (<see cref="PanelDensity"/>), so the bottom sections stay above the desktop's panel bar
    /// rather than under it.
    ///
    /// <para>Measured after <c>Show</c>, because <see cref="Window.SizeToContent"/> gives no
    /// height until the first layout pass — the same reason placement happens there. A
    /// threshold on the screen alone would be a guess: the panel's height depends on whether a
    /// banner, an off-plan section or an expanded explanation is present.</para>
    ///
    /// <para>Re-populating rebuilds the whole tree, which is why this only runs when the panel
    /// actually overflows. A display with room does one pass, exactly as before.</para>
    /// </summary>
    private void FitToScreen(
        UsageSnapshot snapshot,
        PanelStatistics stats,
        ClaudeAccount? account,
        PlanHistoryReport? dataReport,
        TranscriptScopeReport? scopeReport,
        DateTimeOffset utcNow)
    {
        if (TargetScreen() is not { } screen)
        {
            return;
        }

        var scaling = screen.Scaling > 0 ? screen.Scaling : 1.0;
        var available = WorkAreaPlacement.AvailableHeightPx(ToBox(screen.WorkingArea)) / scaling;
        var natural = (FrameSize ?? ClientSize).Height;

        var density = PanelDensity.For(natural, available);
        if (!density.IsCompact)
        {
            return;
        }

        _content.Density = density;
        _content.Populate(snapshot, stats, account, dataReport, scopeReport, utcNow);
        _log?.Write(
            $"panel density: compact (natural {natural:0} dip > available {available:0} dip)");
    }

    /// <summary>
    /// Moves the panel to the work-area corner nearest the desktop's panel bar.
    ///
    /// <para>Called after <c>Show</c> because <see cref="Window.SizeToContent"/> leaves the
    /// size unknown until the first layout pass, and the corner is derived from it.</para>
    /// </summary>
    private void PlaceAtWorkAreaCorner()
    {
        if (TargetScreen() is not { } screen)
        {
            _log?.Write("panel placement skipped — no screen geometry available");
            return;
        }

        // FrameSize includes the border this window draws; ClientSize is the fallback for a
        // compositor that has not reported a frame yet. Both are DIPs, and the work area is
        // device pixels, so the scale has to be applied — the classic HiDPI mistake is to
        // compare the two directly and land a quarter of the way up the screen.
        var size = FrameSize ?? ClientSize;
        var scaling = screen.Scaling > 0 ? screen.Scaling : 1.0;

        var (left, top, corner) = WorkAreaPlacement.Place(
            ToBox(screen.Bounds),
            ToBox(screen.WorkingArea),
            (int)Math.Round(size.Width * scaling),
            (int)Math.Round(size.Height * scaling));

        _requestedPosition = new PixelPoint(left, top);
        Position = _requestedPosition.Value;

        _log?.Write(
            $"panel placement: {corner} at {left},{top} on \"{screen.DisplayName}\" "
            + $"(work {Describe(screen.WorkingArea)}, screen {Describe(screen.Bounds)}, "
            + $"scale {scaling:0.##}, panel {size.Width:0}x{size.Height:0} dip)");
    }

    /// <summary>
    /// The screen to place on: the one the window is already on where that is knowable, so a
    /// multi-monitor desktop does not always throw the panel onto monitor 1. Neither platform
    /// reports where the tray icon was drawn, so "where the window manager just put it" is the
    /// closest available proxy for where the user is looking.
    /// </summary>
    private Screen? TargetScreen() =>
        Screens.ScreenFromWindow(this) ?? Screens.Primary ?? Screens.All.FirstOrDefault();

    private static PixelBox ToBox(PixelRect r) => new(r.X, r.Y, r.Width, r.Height);

    private static string Describe(PixelRect r) => $"{r.Width}x{r.Height}+{r.X}+{r.Y}";

    /// <summary>Populates without showing — for the offscreen verification renders.</summary>
    internal PanelContent PopulateOnly(
        UsageSnapshot snapshot,
        PanelStatistics stats,
        ClaudeAccount? account,
        PlanHistoryReport? dataReport,
        TranscriptScopeReport? scopeReport,
        DateTimeOffset utcNow)
    {
        _content.Populate(snapshot, stats, account, dataReport, scopeReport, utcNow);
        return _content;
    }
}
