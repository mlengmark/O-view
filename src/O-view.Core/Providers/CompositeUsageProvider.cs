using OView.Core.Models;

namespace OView.Core.Providers;

/// <summary>
/// Resolves the provider chain (ADR-0002 precedence, amended by ADR-0007):
/// PlanHistoryProvider → CachedUtilizationProvider → (OAuth, deferred) → JsonlUsageProvider.
///
/// <para><b>Selection is by information value, not list position.</b> Any Live snapshot beats
/// any Stale one, and stale authoritative percentages beat an estimate that has no percentages
/// at all. The winning snapshot keeps its own Source so the UI labels it honestly.</para>
///
/// <para>That principle used to stop at the tier, with list position quietly deciding the rest.
/// It no longer does: two sources now report the same meters — Claude Desktop's sampled series
/// and Claude Code's cached figures — and within a tier the better answer is whichever describes
/// the account most completely and most recently, not whichever was passed first. Desktop samples
/// every ~5 minutes; Claude Code refreshes whenever it talks to the API, so during active use it
/// is routinely the fresher of the two. Preferring the earlier argument meant showing a reading
/// up to a sampling interval old while a newer one sat in the other source, which is the opposite
/// of what a monitoring tool is for. See <see cref="MoreAccurate"/>.</para>
/// </summary>
public sealed class CompositeUsageProvider : IUsageProvider
{
    private readonly IReadOnlyList<IUsageProvider> _providers;

    /// <summary>
    /// Where a swallowed provider failure is recorded. A delegate rather than a log
    /// interface, for the reason <see cref="PlanHistory.PlanHistoryProvider"/> takes its
    /// activity lookup as one: <c>Core</c> knows nothing about the app's logging, and a
    /// seam that is a single <c>Action</c> stays testable without one.
    ///
    /// <para><b>This exists because the catch below is otherwise perfectly silent.</b> A
    /// provider that throws on every poll is indistinguishable here from one that has no data
    /// — both become <see cref="UsageSnapshot.None"/> and the chain moves on. Measured in the
    /// field: transcript ingestion had been failing on every poll for five days behind this
    /// catch while the panel showed live percentages from a sibling provider and the support
    /// bundle reported <c>status : Ok</c>. Nothing anywhere named the failing provider,
    /// because nothing was asked to.</para>
    /// </summary>
    public Action<string>? Log { get; init; }

    /// <param name="providers">
    /// The sources to consult. Order is a tie-break only — <see cref="MoreAccurate"/> decides
    /// within a tier, and falls back to this order when two snapshots are equally informative
    /// and equally recent.
    /// </param>
    public CompositeUsageProvider(params IUsageProvider[] providers)
    {
        _providers = providers;
    }

    public UsageSnapshot GetSnapshot(DateTimeOffset utcNow)
    {
        var snapshots = _providers.Select(p => SafeGetSnapshot(this, p, utcNow)).ToList();

        foreach (var tier in new[] { DataSource.Live, DataSource.Stale, DataSource.Estimate })
        {
            var candidates = snapshots.Where(s => s.Source == tier).ToList();
            if (candidates.Count > 0)
            {
                return candidates.Aggregate(MoreAccurate);
            }
        }

        return UsageSnapshot.None;
    }

    /// <summary>
    /// The better of two snapshots from the same tier, on two criteria in order.
    ///
    /// <list type="number">
    /// <item><b>How many meters it actually carries.</b> A snapshot reporting both percentages
    /// beats one reporting a single percentage, even when the fuller one is older. The trade is
    /// deliberate: blanking a bar to make the other bar a few minutes fresher costs the user a
    /// whole figure to gain precision they cannot see. Percentages go missing for real reasons —
    /// an aged zero is discarded rather than shown, in both sources — so this is a live case, not
    /// a theoretical one.</item>
    /// <item><b>How recently it was captured.</b> Both surviving sources are caches of the same
    /// upstream meter, so between two equally complete readings the later one is simply closer to
    /// the truth. Utilisation within a window only rises, which makes the older reading a lower
    /// bound on the newer one rather than a competing measurement.</item>
    /// </list>
    ///
    /// <para>Ties keep the incumbent, so a genuinely undecidable pair falls back to the order the
    /// providers were passed in and the result stays deterministic.</para>
    ///
    /// <para>This deliberately does <b>not</b> merge fields across snapshots. Taking the session
    /// figure from one source and the weekly from another would produce a reading that never
    /// existed at any instant, under a single <c>Source</c> label that could only be true of one
    /// of them — a fabrication of exactly the kind rule 6 forbids, and invisible to the user
    /// because every part of it looks real.</para>
    /// </summary>
    private static UsageSnapshot MoreAccurate(UsageSnapshot incumbent, UsageSnapshot challenger)
    {
        var byMeters = MeterCount(challenger).CompareTo(MeterCount(incumbent));
        if (byMeters != 0)
        {
            return byMeters > 0 ? challenger : incumbent;
        }

        // An undated snapshot cannot be shown to be newer, so it does not displace one.
        if (challenger.CapturedAtUtc is not { } challengerAt)
        {
            return incumbent;
        }

        return incumbent.CapturedAtUtc is not { } incumbentAt || challengerAt > incumbentAt
            ? challenger
            : incumbent;
    }

    /// <summary>How many of the two plan meters this snapshot actually reports (0, 1 or 2).</summary>
    private static int MeterCount(UsageSnapshot snapshot) =>
        (snapshot.SessionPercent is null ? 0 : 1) + (snapshot.WeeklyPercent is null ? 0 : 1);

    /// <summary>
    /// A provider that throws (e.g. one backed by a corrupt local store — issue #16)
    /// must not blank the whole display. Treat its failure as "no data" so the chain
    /// falls through to the next source instead of propagating.
    ///
    /// <para><b>Degrading quietly is right; degrading invisibly is not.</b> The swallow stays
    /// — this is a monitoring tool and one bad provider must not take the panel down — but it
    /// now says what it caught and which provider it came from. Without that line a provider
    /// failing on every poll for days looks exactly like a provider with nothing to report,
    /// from inside the app and from a support bundle alike.</para>
    /// </summary>
    private static UsageSnapshot SafeGetSnapshot(
        CompositeUsageProvider owner, IUsageProvider provider, DateTimeOffset utcNow)
    {
        try
        {
            return provider.GetSnapshot(utcNow);
        }
        catch (Exception ex)
        {
            owner.Log?.Invoke(
                $"provider {provider.GetType().Name} FAILED {ex.GetType().Name}: {ex.Message}");
            return UsageSnapshot.None;
        }
    }
}
