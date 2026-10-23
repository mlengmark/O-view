using System.Windows;
using System.Windows.Controls;
using OView.App.Rendering;
using OView.Core.Models;
using Size = System.Windows.Size;

namespace OView.Tray.Popup;

/// <summary>
/// Picks the density the display can actually fit, caps the panel at the work area if even
/// that is not enough, and scrolls the sections that still do not fit inside the cap.
///
/// <para><b>Measured, not guessed.</b> The natural layout is applied first and laid out,
/// because the panel's height depends on what is in it — a no-data banner, an off-plan
/// section, an expanded explanation — and a threshold on the work area alone would be wrong
/// for most of those combinations. The extra layout pass costs nothing next to the two the
/// flyout already runs.</para>
///
/// <para><b>And measured from the content, not from the window.</b> This used to ask both
/// questions about <c>ActualHeight</c>, which is the height of a window that has already been
/// fitted to the display — so a panel too tall for the screen reads back the cap and concludes
/// it fits. That is what the Linux panel did when four sections sat off the bottom of a
/// 1024×768 desktop with nothing to scroll them back (Maximilian's B13 report of
/// 2026-10-15). <see cref="NaturalHeightDip"/> asks the tree instead, and asks again after the
/// compact re-layout, because the tree that answered first is not the one the cap and the
/// scroll decision are about.</para>
///
/// <para>Separate from <see cref="PopupWindow"/> only because that file is held under 600
/// lines (<see cref="PopupScroll"/> is the other piece split out for the same reason); the
/// window still owns the layout pass, the density and the chart, and hands them here.</para>
/// </summary>
internal sealed class PopupFit
{
    private readonly Window _window;
    private readonly Border _panelRoot;
    private readonly PopupScroll _scroll;
    private readonly Action<PanelDensity> _applyDensity;
    private readonly Action<PanelStatistics, UsageSnapshot> _drawGraph;

    /// <summary>
    /// What the panel was last fit for, so a later growth or shrink (<see cref="RefitForPendingGrowth"/>)
    /// can redo the fit without the caller handing the figures back.
    /// </summary>
    private PanelStatistics? _lastStats;

    private UsageSnapshot? _lastSnapshot;

    /// <param name="window">The flyout being fit — its layout pass, width and height cap.</param>
    /// <param name="panelRoot">The sections' outer border, measured for its natural height.</param>
    /// <param name="scroll">Where the sections go if the fit has to cap them short.</param>
    /// <param name="applyDensity">Lays the panel out at a given <see cref="PanelDensity"/>.</param>
    /// <param name="drawGraph">Redraws the chart against whatever height the fit just gave it.</param>
    public PopupFit(
        Window window, Border panelRoot, PopupScroll scroll,
        Action<PanelDensity> applyDensity, Action<PanelStatistics, UsageSnapshot> drawGraph)
    {
        _window = window;
        _panelRoot = panelRoot;
        _scroll = scroll;
        _applyDensity = applyDensity;
        _drawGraph = drawGraph;
    }

    /// <param name="pendingGrowth">
    /// Height the panel is about to gain or lose but has not yet — the disclosure fold, which is
    /// measured before it plays so that the window is never laid out at the far end of it
    /// (<see cref="PopupTokenComposition"/>). Zero everywhere else, where what is on screen and
    /// what is being measured are the same panel.
    /// </param>
    public void FitToWorkArea(PanelStatistics stats, UsageSnapshot snapshot, double pendingGrowth = 0)
    {
        _lastStats = stats;
        _lastSnapshot = snapshot;

        _applyDensity(PanelDensity.Normal);
        _window.UpdateLayout();

        var available = PopupPositioner.AvailableHeightDip();
        var outcome = PanelFit.Fit(available, NaturalHeightDip, density =>
        {
            _applyDensity(density);
            _window.UpdateLayout();
        }, pendingGrowth);

        ApplyFit(outcome.Fit, available);
        _window.UpdateLayout();

        _drawGraph(stats, snapshot);
    }

    /// <summary>
    /// The height the panel's content wants, measured against an unbounded height — the
    /// content's own answer rather than the window's.
    ///
    /// <para>Any cap already on the root is cleared before measuring. Leaving one would hand
    /// back the cap itself, which is the same mistake as measuring <c>ActualHeight</c>: a panel
    /// that scrolled the last time it opened would decide it now fits, and stop scrolling.</para>
    ///
    /// <para>The measure is left invalidated afterwards, so the next layout pass measures the
    /// tree against the constraint it is actually being laid out in rather than keeping this
    /// unbounded answer.</para>
    /// </summary>
    public double NaturalHeightDip()
    {
        _panelRoot.Height = double.NaN;
        _panelRoot.Measure(new Size(_window.Width, double.PositiveInfinity));
        var wanted = _panelRoot.DesiredSize.Height;
        _panelRoot.InvalidateMeasure();
        return wanted;
    }

    /// <summary>
    /// Makes the panel the height the fit decided, and puts the sections in a viewport if that
    /// height had to cut them short.
    ///
    /// <para>The cap goes on the content rather than on the window, because the window is
    /// <c>SizeToContent</c> and follows it — and because the verification renders lay the same
    /// content out with no window at all, so a cap stated there is one both paths obey.
    /// <c>MaxHeight</c> holds it on the window as well, against a later re-layout this method
    /// never sees. A panel that fits goes back to sizing itself, so moving between a short
    /// display and a tall one in one session leaves no cap behind.</para>
    /// </summary>
    public void ApplyFit(PanelFit.Result fit, double availableHeightDip)
    {
        _window.MaxHeight = availableHeightDip;
        _scroll.ScrollContent(fit.MustScroll);
        _panelRoot.Height = fit.MustScroll ? fit.WindowHeightDip : double.NaN;
    }

    /// <summary>
    /// Re-fits the panel for height the composition disclosure is about to gain or lose, if the
    /// panel has been fit for something yet. The disclosure measures before it plays, so the
    /// window is never laid out at the far end of the fold (<see cref="PopupTokenComposition"/>).
    /// </summary>
    public void RefitForPendingGrowth(double pendingGrowth)
    {
        if (_lastStats is { } stats && _lastSnapshot is { } snapshot)
        {
            FitToWorkArea(stats, snapshot, pendingGrowth);
        }
    }
}
