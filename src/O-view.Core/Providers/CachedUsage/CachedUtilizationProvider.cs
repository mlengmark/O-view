using OView.Core.Models;
using OView.Core.Providers.PlanHistory;

namespace OView.Core.Providers.CachedUsage;

/// <summary>
/// Serves session and weekly usage from Claude Code's own cached figures
/// (<see cref="CachedUtilization"/>) — the source that finally gives the top two bars to
/// people who have no Claude Desktop.
///
/// <para>Until this existed, a Claude Code user got token counts and nothing else: the
/// percentages came only from Claude Desktop's plan-history file, so the two plan bars read
/// "unknown" on a machine that was generating usage all day. Deriving them from local tokens
/// was investigated and rejected — even at window granularity, cost-per-percentage-point spread
/// 2.6× across the middle 80% of windows, which would have put a true 50% anywhere between 31%
/// and 81% (rule 6).</para>
///
/// <para>These figures need no deriving. They are what Claude reported.</para>
/// </summary>
public sealed class CachedUtilizationProvider : IUsageProvider
{
    /// <summary>
    /// Maximum age at which the cached percentages are still labelled
    /// <see cref="DataSource.Live"/>.
    ///
    /// <para><b>Not a measured sampling interval, and deliberately not presented as one.</b>
    /// Claude Desktop samples on a timer, so its 11-minute threshold came from 1,828 observed
    /// gaps. This is a <i>cache</i>, refreshed when Claude Code talks to the API — there is no
    /// cadence to measure, only usage. One refresh gap was observed here, at 10.1 minutes, and
    /// n=1 is an anecdote rather than a distribution.</para>
    ///
    /// <para>So the design does not lean on this number. It is a labelling threshold only —
    /// past it the reading says "Stale" and wears its age — while the two rules that decide
    /// whether a figure may be shown at all are evidence-based and independent of it:
    /// <see cref="CurrentWindowPercent"/> discards a percentage whose window has demonstrably
    /// rolled over, and a zero is discarded far sooner than a non-zero for the reason
    /// <see cref="PlanHistoryProvider.ZeroReadingFreshness"/> sets out.</para>
    /// </summary>
    public static readonly TimeSpan DefaultFreshness = TimeSpan.FromMinutes(15);

    private readonly Func<CachedUtilization?> _read;
    private readonly TimeSpan _freshness;

    /// <param name="path">
    /// File to read; null probes the documented candidates, which follow
    /// <c>CLAUDE_CONFIG_DIR</c>.
    /// </param>
    /// <param name="freshness">Maximum age still labelled <see cref="DataSource.Live"/>.</param>
    public CachedUtilizationProvider(string? path = null, TimeSpan? freshness = null)
        : this(() => CachedUtilization.TryRead(path), freshness)
    {
    }

    /// <param name="read">Injected reader, so the rules are testable without a file.</param>
    /// <param name="freshness">Maximum age still labelled <see cref="DataSource.Live"/>.</param>
    public CachedUtilizationProvider(Func<CachedUtilization?> read, TimeSpan? freshness = null)
    {
        _read = read;
        _freshness = freshness ?? DefaultFreshness;
    }

    public UsageSnapshot GetSnapshot(DateTimeOffset utcNow)
    {
        var cached = SafeRead();
        if (cached is null)
        {
            return UsageSnapshot.None;
        }

        var age = utcNow - cached.FetchedAtUtc;
        var sessionReset = FutureReset(cached.FiveHour, utcNow);
        var weeklyReset = FutureReset(cached.SevenDay, utcNow);
        var session = CurrentWindowPercent(cached.FiveHour, utcNow, age);
        var weekly = CurrentWindowPercent(cached.SevenDay, utcNow, age);

        // Everything aged out or rolled over. Report nothing so the composite falls through to
        // a source that may still know something, rather than winning a tier with a blank.
        if (session is null && weekly is null && sessionReset is null && weeklyReset is null)
        {
            return UsageSnapshot.None;
        }

        return new UsageSnapshot(
            age <= _freshness ? DataSource.Live : DataSource.Stale,
            session,
            weekly,
            sessionReset,
            cached.FetchedAtUtc,
            weeklyReset,
            // Named from here on. These trail a long positional list, and removing the weekly
            // uncertainty field in issue #248 would otherwise have re-pointed them silently —
            // the compiler caught it here, and naming them means it would not have to.
            WeeklyResetPeriod: weeklyReset is null ? null : WeeklyWindow.Length,
            // Reported rather than inferred, so this carries no bracket. The weekly one no
            // longer exists at all (ADR-0014); the session window still rolls from first use,
            // so it keeps the field even when this source can fill it exactly.
            SessionResetUncertainty: sessionReset is null ? null : TimeSpan.Zero);
    }

    /// <summary>
    /// A reset instant only while it is still ahead of us.
    ///
    /// <para><b>A passed reset is not stepped forward to the next one</b>, for either window.
    /// For the five-hour window that would rebuild the exact bug issue #180 removed: the window
    /// starts on first use, so a grid stepped forward from an old boundary describes a window
    /// that never existed. For the weekly window the arithmetic would be sound, but the value
    /// would no longer be a reported one — it would be an inference wearing a reported value's
    /// zero uncertainty. Handing back null instead lets the derivation that already exists take
    /// the question, correctly labelled as derived.</para>
    /// </summary>
    private static DateTimeOffset? FutureReset(UtilizationBar? bar, DateTimeOffset utcNow) =>
        bar?.ResetsAtUtc is { } resets && resets > utcNow ? resets : null;

    /// <summary>
    /// The percentage, if it still describes the window running <i>now</i>.
    ///
    /// <para>Two ways it may not, and the first is specific to a cache that can sit untouched
    /// for hours:</para>
    ///
    /// <list type="number">
    /// <item><b>The window rolled over.</b> The bar carries the instant its own window ends, so
    /// a reset time already in the past is proof the figure describes a window that has since
    /// been replaced. Claude Code caches while it runs; leave it closed across a boundary and
    /// the file still reads 91% for a window that reset to nothing hours ago. This is the one
    /// failure mode unique to this source, it is silent, and it is checkable — so it is
    /// checked.</item>
    /// <item><b>The reading is a stale zero.</b> Within a window utilisation only rises, so an
    /// aged figure is a lower bound rather than a measurement. A non-zero bound degrades
    /// gracefully; zero degrades to nothing at all while looking like a precise finding that
    /// the window is empty — see <see cref="PlanHistoryProvider.ZeroReadingFreshness"/>, whose
    /// argument and threshold apply here unchanged and are shared rather than restated.</item>
    /// </list>
    /// </summary>
    private static int? CurrentWindowPercent(UtilizationBar? bar, DateTimeOffset utcNow, TimeSpan age)
    {
        if (bar is null || bar.ResetsAtUtc is { } resets && resets <= utcNow)
        {
            return null;
        }

        return bar.Percent == 0 && age > PlanHistoryProvider.ZeroReadingFreshness ? null : bar.Percent;
    }

    /// <summary>
    /// A reader that throws must not blank the display — same contract as every other provider
    /// (<see cref="IUsageProvider"/>): unavailable data is <see cref="UsageSnapshot.None"/>, not
    /// an exception escaping into the poll loop.
    /// </summary>
    private CachedUtilization? SafeRead()
    {
        try
        {
            return _read();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
