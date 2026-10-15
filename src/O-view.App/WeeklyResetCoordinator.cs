using OView.Core.Models;
using OView.Core.Providers;
using OView.Core.Providers.CachedUsage;
using OView.Core.Providers.PlanHistory;
using OView.Core.Storage;

namespace OView.App;

/// <summary>
/// The weekly reset end to end (ADR-0014): the manual override, the instant Claude Code
/// reported, and the conflict between the two.
///
/// <para>Split out of <see cref="UsageEngine"/> purely to shorten that file — same members,
/// same behaviour, same logging. Settings stays with the engine, since every other
/// preference is saved through the same property; this reads and writes it through the
/// <paramref name="getSettings"/>/<paramref name="saveSettings"/> delegates the constructor
/// takes, the same seam <see cref="UsageCacheRefresh"/> uses for its own threading.</para>
/// </summary>
public sealed class WeeklyResetCoordinator
{
    private readonly WeeklyResetAnchor _weeklyAnchor;
    private readonly Func<CachedUtilization?> _cachedUtilizationSource;
    private readonly IUsageProvider _cachedUtilization;
    private readonly string? _orgUuid;
    private readonly IAppLog? _log;
    private readonly Func<TraySettings> _getSettings;
    private readonly Action<TraySettings> _saveSettings;

    /// <summary>Set when a stored anchor disagrees with what the user typed.</summary>
    private DateTimeOffset? _weeklyResetConflict;

    public WeeklyResetCoordinator(
        string? weeklyResetAnchorPath,
        Func<CachedUtilization?> cachedUtilizationSource,
        IUsageProvider cachedUtilization,
        string? orgUuid,
        IAppLog? log,
        Func<TraySettings> getSettings,
        Action<TraySettings> saveSettings)
    {
        _cachedUtilizationSource = cachedUtilizationSource;
        _cachedUtilization = cachedUtilization;
        _orgUuid = orgUuid;
        _log = log;
        _getSettings = getSettings;
        _saveSettings = saveSettings;

        // The weekly reset, since ADR-0014: one instant Claude reported, stored once and
        // projected forward by whole weeks forever. There is no observation log any more and
        // nothing to import from the rollup store — the derivation those existed to feed was
        // removed for producing an answer 11.5 hours wrong while the exact one sat unread.
        var logLine = log is null ? (Action<string>?)null : log.Write;
        _weeklyAnchor = new WeeklyResetAnchor(weeklyResetAnchorPath) { Log = logLine };
    }

    /// <summary>
    /// Pushes the user's entered weekly reset into the provider (GitHub issue #186). Called
    /// on construction and after every change, so the two can never disagree about what is
    /// set — the shape of bug where a preference is saved and does not take effect until a
    /// restart.
    /// </summary>
    public void ApplyWeeklyResetSetting()
    {
        // The entry is read straight from Settings on every poll now (see WithWeeklyReset), so
        // there is no provider-side copy left to keep in step — the class of bug this method
        // existed to prevent is removed rather than maintained. Clearing the recorded conflict
        // stays: a new entry is a fresh claim and deserves a fresh verdict.
        _weeklyResetConflict = null;
    }

    /// <summary>
    /// Applies and persists the user's weekly reset time, returning what is now in effect.
    /// Null clears it and returns to deriving.
    /// </summary>
    public ManualWeeklyReset? SetWeeklyReset(ManualWeeklyReset? reset)
    {
        var settings = _getSettings() with
        {
            WeeklyResetDay = reset?.DayText ?? "",
            WeeklyResetTime = reset?.TimeText ?? "",
            // A new entry is a fresh claim: any conflict already reported was about the old
            // one, so re-arm the notice rather than staying silent about the new value.
            WeeklyResetConflictNoticed = "",
        };
        _saveSettings(settings);
        ApplyWeeklyResetSetting();

        _log?.Write($"weekly reset {(reset is null ? "cleared — deriving" : $"set to {reset.DayText} {reset.TimeText} local")}");
        return settings.WeeklyReset;
    }

    /// <summary>
    /// The reset instant Claude reported that disproves the entered weekly reset, or null.
    /// Surfaced so the head can tell the user once — silently overriding a wrong entry leaves
    /// them believing the number they typed.
    /// </summary>
    public DateTimeOffset? WeeklyResetConflict => _weeklyResetConflict;

    /// <summary>
    /// Records that the user has been told about <paramref name="conflict"/>, so the notice
    /// fires once per conflicting instant rather than on every poll.
    /// </summary>
    public void MarkWeeklyResetConflictNoticed(DateTimeOffset conflict) =>
        _saveSettings(_getSettings() with { WeeklyResetConflictNoticed = conflict.ToString("o") });

    /// <summary>Whether this conflict is new to the user.</summary>
    public bool IsWeeklyResetConflictUnseen(DateTimeOffset conflict) =>
        _getSettings().WeeklyResetConflictNoticed != conflict.ToString("o");

    /// <summary>
    /// Carries the user's entered weekly reset onto a snapshot that has none.
    ///
    /// <para><b>The case this exists for is the one the feature was built for.</b>
    /// <c>PlanHistoryProvider</c> resolves the entry itself — including checking it against
    /// observed resets — but it returns <c>None</c> before reaching that code when there is
    /// no plan-history file at all, and the composite then falls through to the JSONL
    /// estimate, whose weekly reset is null. So a user with no Claude Desktop entered their
    /// reset time and saw nothing change: exactly the population issue #186 was written for,
    /// and the only one that cannot derive the value instead.</para>
    ///
    /// <para>Applied here rather than by widening the provider, because the entry is a user
    /// setting rather than a reading of the plan-history file, and it should survive whichever
    /// provider ends up winning the composite's tier resolution.</para>
    ///
    /// <para><b>Only fills a gap; never overrides.</b> A snapshot that already carries a
    /// weekly reset has been through the provider's entry-versus-evidence rule, which is the
    /// one place allowed to decide between them. Overwriting it here would reinstate an entry
    /// that an observation had just disproved.</para>
    /// </summary>
    public UsageSnapshot WithWeeklyReset(UsageSnapshot snapshot, DateTimeOffset utcNow)
    {
        // Nothing to attach a reset to: a panel with no data at all should stay blank rather
        // than grow one lonely populated line.
        if (snapshot.Source == DataSource.None)
        {
            return snapshot;
        }

        // Harvest first. Whenever Claude Code has reported an instant, store it — including
        // one already in the past, which is the case that matters: the cache refreshes only
        // when Claude Code fetches usage, measured at 43 hours stale on a machine whose file
        // was being rewritten every few minutes. Read once, it is correct forever after.
        var reported = ReportedWeeklyResetInstant();
        if (reported is { } instant)
        {
            _weeklyAnchor.Save(instant, _orgUuid);
        }

        var anchor = _weeklyAnchor.Read(_orgUuid);
        _weeklyResetConflict = null;

        var settings = _getSettings();

        // A discovered anchor outranks a typed entry, because it comes from the source rather
        // than from transcription — but it must never silently replace one. Disagreement is
        // recorded so the head can say so once; a wrong entry quietly overridden leaves the
        // user believing the number they typed.
        if (anchor is { } stored && settings.WeeklyReset is { } entered &&
            entered.IsContradictedBy(stored, TimeZoneInfo.Local))
        {
            _weeklyResetConflict = stored;
        }

        var next = anchor is { } a
            ? WeeklyWindow.NextAfter(a, utcNow)
            : settings.WeeklyReset?.NextAfter(utcNow, TimeZoneInfo.Local);

        // Genuinely unknown: no reported instant has ever been seen and nothing was entered.
        // The panel says so and points at the entry dialog rather than showing a guess — which
        // is what the derivation used to do, wrongly and without saying so (rule 6).
        if (next is null)
        {
            return snapshot;
        }

        return snapshot with
        {
            // Both the stored anchor and the user's own entry are exact — same standing as any
            // other source read directly, so the instant carries Real with it. Replacing the
            // whole pair is what keeps the two from parting company: a figure assigned on its
            // own used to leave the old status beside it, which is how a snapshot ended up
            // holding a weekly reset while still flagged Unavailable.
            WeeklyResetAtUtcValue = UsageSnapshot.Instant(next),
            // No uncertainty field to set: both remaining sources are exact, so the weekly row
            // never wore the "~" and the field carrying it was removed (ADR-0014, issue #248).
            WeeklyResetPeriod = WeeklyWindow.Length,
        };
    }

    /// <summary>
    /// The weekly reset instant Claude Code last reported, past or future, or null.
    /// Never throws: a failure to harvest must not disturb the reading it decorates.
    /// </summary>
    private DateTimeOffset? ReportedWeeklyResetInstant()
    {
        try
        {
            return _cachedUtilizationSource()?.SevenDay?.ResetsAtUtc;
        }
        catch (Exception ex)
        {
            _log?.Write($"weekly anchor read FAILED {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Replaces derived reset times with the ones Claude actually reported, wherever Claude
    /// Code has cached them (<see cref="CachedUtilization"/>).
    ///
    /// <para><b>These are the only exact reset times O-view has.</b> Every other one is inferred
    /// from a drop in a sampled series, so its precision is bounded by the gap between samples:
    /// about half an interval for the five-hour window even after local transcripts narrow the
    /// bracket (issue #185), and roughly ten hours for the weekly one, whose resets land
    /// overnight while Desktop is closed (ADR-0011) — the imprecision that made the manual entry
    /// of issue #186 worth building in the first place. A reported timestamp needs none of that
    /// machinery. It is simply correct.</para>
    ///
    /// <para><b>So this overrides rather than fills a gap</b> — the opposite of
    /// <see cref="WithEnteredWeeklyReset"/>, and the difference is the point. A derived bracket
    /// and a typed-in time are both attempts to recover a number that this source states
    /// outright; leaving either in place ahead of it would be preferring a guess to the
    /// answer.</para>
    ///
    /// <para>Applied to the winning snapshot rather than left to the provider chain, because
    /// the chain resolves on <em>percentages</em>: a machine running Claude Desktop takes its
    /// percentages from plan history and would otherwise keep plan history's derived reset
    /// times too, discarding exact ones that were sitting on disk the whole time. The two
    /// questions have different best answers, so they are answered separately.</para>
    ///
    /// <para>Silent when there is nothing to say: no cache, a window that has already rolled
    /// past its cached boundary, or a reader that throws all leave the snapshot untouched
    /// (<see cref="CachedUtilizationProvider"/> returns those as nulls). This only ever
    /// replaces an approximation with a fact, so it must never be able to remove one.</para>
    /// </summary>
    public UsageSnapshot WithReportedResets(UsageSnapshot snapshot, DateTimeOffset utcNow)
    {
        // Nothing to attach a reset to: a panel with no data at all should stay blank rather
        // than grow one lonely populated line (same rule as the entered reset).
        if (snapshot.Source == DataSource.None)
        {
            return snapshot;
        }

        UsageSnapshot reported;
        try
        {
            reported = _cachedUtilization.GetSnapshot(utcNow);
        }
        catch (Exception ex)
        {
            // A precision refinement must never take down the reading it refines.
            _log?.Write($"reported resets skipped: {ex.GetType().Name}: {ex.Message}");
            return snapshot;
        }

        if (reported.SessionResetAtUtcValue.Value is { } sessionReset)
        {
            snapshot = snapshot with
            {
                // An instant Claude Code reported, same standing as any other source read
                // directly. Figure and status are replaced together, so neither can be left
                // behind describing the other one's reading.
                SessionResetAtUtcValue = UsageSnapshot.Instant(sessionReset),
                // Exact, so it renders without the "~" that marks a derived bracket.
                SessionResetUncertainty = TimeSpan.Zero,
            };
        }

        // The weekly reset is deliberately not touched here. WithWeeklyReset owns it end to
        // end now, working from the stored anchor rather than from this snapshot — which
        // drops a resets_at the moment it passes, the exact case the anchor exists to survive.
        return snapshot;
    }
}
