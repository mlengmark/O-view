using OView.Core.Models;

namespace OView.Core.Providers;

/// <summary>
/// Chooses how soon the tray should poll again. A fresh launch can start *before* Claude
/// Desktop has written any usage samples — Desktop was closed, or is still starting — so
/// the first snapshots carry no authoritative percentages and the plan bars read
/// "unknown" ([ADR-0007](../../../docs/adr/0007-plan-history-primary-provider.md); the
/// bars only fill on <see cref="DataSource.Live"/>/<see cref="DataSource.Stale"/>).
///
/// Rather than wait a full normal interval for that data to appear, the controller polls
/// on a short <em>warm-up</em> cadence until authoritative data arrives, then settles to
/// the normal interval. The warm-up is time-boxed by <see cref="WarmupWindow"/> so a
/// Desktop that stays closed does not mean permanent fast polling — after the window the
/// cadence returns to normal regardless.
/// </summary>
public static class PollingCadence
{
    /// <summary>
    /// How long after start the warm-up cadence may apply while still waiting for
    /// authoritative data. Desktop that is going to start does so well within this;
    /// past it, the fast retry has stopped earning its keep.
    /// </summary>
    public static readonly TimeSpan WarmupWindow = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The interval to use for the NEXT poll. The short <paramref name="warmup"/> applies
    /// only while the latest snapshot still has no authoritative percentages AND we are
    /// inside <see cref="WarmupWindow"/>; otherwise the <paramref name="normal"/> interval.
    /// </summary>
    /// <param name="latest">Source of the most recent snapshot.</param>
    /// <param name="sinceStart">Elapsed time since polling began.</param>
    /// <param name="warmup">Short interval used while warming up.</param>
    /// <param name="normal">Steady-state interval.</param>
    public static TimeSpan Next(DataSource latest, TimeSpan sinceStart, TimeSpan warmup, TimeSpan normal) =>
        IsAuthoritative(latest) || sinceStart >= WarmupWindow ? normal : warmup;

    /// <summary>
    /// Whether a snapshot from this source populates the plan bars — the signal that the
    /// warm-up has done its job. Matches the popup's own Live/Stale gate so the two agree.
    /// </summary>
    public static bool IsAuthoritative(DataSource source) =>
        source is DataSource.Live or DataSource.Stale;
}
