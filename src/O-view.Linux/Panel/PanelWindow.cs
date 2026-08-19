using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using OView.App;
using OView.Core.Models;
using OView.Core.Providers.Jsonl;
using OView.Core.Providers.PlanHistory;

namespace OView.Linux.Panel;

/// <summary>
/// The detail panel as a window.
///
/// <para><b>Not a docked flyout.</b> StatusNotifierItem exposes no way to ask where its own
/// icon was drawn — there is no <c>Shell_NotifyIconGetRect</c> equivalent — and under
/// Wayland a client generally cannot position its own surface at all. ADR-0013 decided not
/// to promise the Windows docking behaviour rather than approximate it badly: a panel that
/// lands in the wrong place every time reads as broken, where a plainly-centred one does
/// not.</para>
///
/// <para>So this centres on the active screen and closes on Esc or deactivation. If a real
/// desktop turns out to allow better placement, that is an improvement to make with
/// evidence rather than a guess to build on now.</para>
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
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = theme["PanelBg"];

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
        Show();
        Activate();
    }

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
