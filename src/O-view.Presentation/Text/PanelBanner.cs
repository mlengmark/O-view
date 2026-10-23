using OView.Core.Models;
using OView.Core.Providers.Jsonl;
using OView.Core.Providers.PlanHistory;

namespace OView.Presentation.Text;

/// <summary>
/// The banner across the top of the panel, and the placeholder the two percentage gauges
/// show when there is no percentage to show.
///
/// <para><b>Why this joins two reports.</b> "Is anything wrong?" cannot be answered from
/// the plan-history file alone. That file is written by Claude <i>Desktop</i>, so a user who
/// runs only the Claude Code CLI will never have one, while their token tiles are being
/// filled perfectly well from their own transcripts. Reading
/// <see cref="PlanHistoryReport"/> on its own, both heads printed <c>No usage data</c> and
/// told that user to start an application they do not use, directly above two populated
/// tiles (GitHub issue #170). The plan file's absence is only a fault in combination with
/// what the transcript scan found, so the decision needs both reports and lives in one
/// place rather than in a branch inside each head.</para>
///
/// <para>This is issue #58 in a second panel: copy that describes O-view's own architecture
/// back at a user whose setup it never considered. The rule it broke is the same one,
/// never assert something about the user's machine that O-view has not observed
/// (CLAUDE.md rule 6). O-view observed a missing file. It did not observe an absent app, a
/// closed app, or a misconfigured one.</para>
/// </summary>
/// <param name="Title">The banner heading.</param>
/// <param name="Detail">The explanatory paragraph beneath it.</param>
/// <param name="GaugePlaceholder">
/// What the session and weekly gauges read in place of a percentage. <c>unknown</c> is
/// right when O-view cannot tell why the figure is missing; it is wrong when O-view knows
/// exactly why, because a blank gauge with no reason reads as a failure.
/// </param>
public sealed record PanelBanner(string Title, string Detail, string GaugePlaceholder)
{
    /// <summary>The gauge reads this when the reason for a missing percentage is itself unknown.</summary>
    public const string UnknownGauge = "unknown";

    /// <summary>
    /// The gauge reads this when O-view knows which reading is missing rather than merely
    /// having no figure. Naming the cause is the difference between a gap and a bug, and
    /// only one of them is worth a user's time.
    ///
    /// <para>It names the file, not an application. This read "needs Claude Desktop" until
    /// a Claude Code user on a machine with no Desktop saw it beside two gauges Claude Code
    /// itself can fill (issue #234).</para>
    /// </summary>
    public const string NoUsageFileGauge = "usage file not found";

    /// <summary>Heading for the case where only the two percentages are out of reach.</summary>
    public const string ScopeTitle = "Session and weekly % need a usage file";

    /// <summary>Heading for a panel that genuinely has nothing to show.</summary>
    public const string NoDataTitle = "No usage data";

    /// <summary>
    /// Chooses the banner, or <c>null</c> for no banner at all.
    ///
    /// <para>Order matters. The reassuring case is checked first and is deliberately narrow:
    /// the plan file must be genuinely <i>absent</i>
    /// (<see cref="PlanDataStatus.FileMissing"/>, not unreadable or malformed) and the
    /// transcript scan must have found files. Anything else keeps the original banner,
    /// which was written for a real fault and is still right for one.</para>
    /// </summary>
    /// <param name="authoritative">Whether the snapshot's figures can be trusted to describe now.</param>
    /// <param name="planReport">What the plan-history file yielded, if anything.</param>
    /// <param name="scopeReport">What the transcript scan resolved. Null is treated as "no evidence".</param>
    /// <param name="tokens31Days">
    /// The 31-day token total. Zero alongside present transcripts means ingestion, not
    /// absence, a different problem needing a different sentence, and one that would
    /// otherwise hide behind reassuring copy.
    /// </param>
    public static PanelBanner? Resolve(
        bool authoritative,
        PlanHistoryReport? planReport,
        TranscriptScopeReport? scopeReport,
        long tokens31Days)
    {
        if (authoritative || planReport is null)
        {
            return null;
        }

        var explanation = planReport.Explain();
        if (explanation.Length == 0)
        {
            return null;
        }

        if (planReport.Status == PlanDataStatus.FileMissing &&
            scopeReport is { Status: TranscriptScopeStatus.TranscriptsPresent })
        {
            return new PanelBanner(
                ScopeTitle,
                tokens31Days > 0
                    ? ScopeDetail(planReport, scopeReport)
                    : ScopeDetailWithNothingRecorded(planReport, scopeReport),
                NoUsageFileGauge);
        }

        return new PanelBanner(NoDataTitle, explanation, UnknownGauge);
    }

    /// <summary>
    /// How both scope wordings open: who fills these two percentages.
    ///
    /// <para>This said <i>only</i> Claude Desktop wrote them, which stopped being true when
    /// Claude Code began caching the same two figures (issue #234), so the one sentence a
    /// Claude Code user on a machine with no Desktop would read was the one that sent them
    /// after an application they do not run. Both writers are named, and neither is said to
    /// be absent: all O-view observed is that no reading arrived.</para>
    /// </summary>
    private const string Sources =
        "Claude Desktop's usage file and the figures Claude Code caches both fill these two "
        + "gauges, and O-view has neither.";

    /// <summary>
    /// The CLI-only note. Every clause is an observation: which surfaces were found, how
    /// many locations were searched, and what that costs. It never says either application
    /// is absent, the file goes missing just as readily when a packaged install redirects
    /// it somewhere O-view did not look, which is why the diagnostics offer stays.
    /// </summary>
    private static string ScopeDetail(PlanHistoryReport plan, TranscriptScopeReport scope) =>
        $"{Sources} It found no usage file in the {Locations(plan)} it checked. "
        + $"Your {SourceList(scope)} token figures below are unaffected. "
        // The instruction opens its own sentence: it is a capitalised imperative phrase on
        // Windows ("Right-click the tray icon → …"), which reads as a typo mid-sentence.
        + $"{DiagnosticsHint.Instruction}, O-view may be looking in the wrong place for how "
        + "they are installed here.";

    /// <summary>
    /// The same situation, but with nothing ingested from the transcripts that were found.
    /// The reassuring version would be false here, and a user reading it beside two zeroed
    /// tiles is exactly the reader this whole class exists for.
    /// </summary>
    private static string ScopeDetailWithNothingRecorded(PlanHistoryReport plan, TranscriptScopeReport scope) =>
        $"{Sources} It found no usage file in the {Locations(plan)} it checked. It did find "
        + $"{scope.TotalFiles} {SourceList(scope)} transcript{(scope.TotalFiles == 1 ? "" : "s")} "
        + $"but recorded no usage from them, which is unexpected. "
        + $"{DiagnosticsHint.Instruction} and report this.";

    /// <summary>
    /// "1 location" or "3 locations", pluralised rather than written "location(s)". The
    /// count is evidence for a rule-6 claim, and evidence reads better without a bracket.
    /// </summary>
    private static string Locations(PlanHistoryReport plan) =>
        $"{plan.SearchedCount} location{(plan.SearchedCount == 1 ? "" : "s")}";

    /// <summary>
    /// Names the surfaces the scan actually found, never a literal, a Cowork-only user
    /// must not be told their usage comes from Claude Code, and vice versa (issue #58).
    /// </summary>
    private static string SourceList(TranscriptScopeReport scope) => scope.PresentSources switch
    {
        // TranscriptsPresent guarantees at least one, but the report is another object's
        // to build and a banner must not depend on that invariant to form a sentence.
        [] => "Claude Code and Cowork",
        [var only] => only,
        var many => $"{string.Join(", ", many.SkipLast(1))} and {many[^1]}",
    };
}
