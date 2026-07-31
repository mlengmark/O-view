using System.Globalization;
using OView.Core.Providers.PlanHistory;

namespace OView.Core.Models;

/// <summary>
/// The words the detail panel puts on screen.
///
/// <para><b>Why this is shared rather than written per platform.</b> Every string here is a
/// <i>statement about the user's usage</i>, and several exist specifically to stop a number
/// being misread — the coverage caveat that keeps a short history from looking like low
/// usage (ADR-0006), the <c>~</c> that marks a reset time O-view only knows to within
/// hours (ADR-0011), the label that flips when spend stops being hypothetical. Two panels
/// wording those differently is the same failure as two panels computing them differently,
/// and issues #55 and #56 were both exactly that.</para>
///
/// <para>Layout, colour and interaction stay with each platform's panel. Only the sentences
/// live here.</para>
/// </summary>
public static class PanelText
{
    /// <summary>
    /// A duration as the panel says it: <c>3d 4h</c>, <c>2h 14m</c>, <c>14m</c>, or
    /// <c>under a minute</c>.
    ///
    /// <para>The units step down with the magnitude so the line stays short and never
    /// implies precision it does not have — a reset six days away is not usefully described
    /// to the minute.</para>
    /// </summary>
    public static string Countdown(TimeSpan t) => t.TotalMinutes < 1
        ? "under a minute"
        : t.TotalDays >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{t.Days}d {t.Hours}h")
            : t.TotalHours >= 1
                ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}h {t.Minutes}m")
                : string.Create(CultureInfo.InvariantCulture, $"{t.Minutes}m");

    /// <summary>
    /// The session-reset line. Before any drop has been observed the reset time is
    /// genuinely unknown, and says so rather than guessing (ADR-0011, rule 6).
    /// </summary>
    public static string SessionReset(DateTimeOffset? resetAtUtc, DateTimeOffset utcNow, TimeZoneInfo local) =>
        resetAtUtc is { } reset
            ? $"Resets in {Countdown(reset - utcNow)} · {TimeZoneInfo.ConvertTime(reset, local):HH:mm}"
            : "Reset time unknown (no reset observed yet)";

    /// <summary>
    /// The weekly-reset line. Carries the weekday, because a reset a week out needs one,
    /// and a <c>~</c> when the observation is only bracketed to within hours — showing an
    /// exact minute O-view does not have would be a fabricated number.
    /// </summary>
    public static string WeeklyReset(
        DateTimeOffset resetAtUtc, TimeSpan? uncertainty, DateTimeOffset utcNow, TimeZoneInfo local)
    {
        var at = TimeZoneInfo.ConvertTime(resetAtUtc, local);
        return $"Resets in {Countdown(resetAtUtc - utcNow)} · {(IsApproximate(uncertainty) ? "~" : "")}{at:ddd HH:mm}";
    }

    /// <summary>
    /// Whether a weekly observation is wide enough to need the <c>~</c>. A reset caught
    /// while Claude Desktop was sampling is precise; one caught across a gap is not.
    /// </summary>
    public static bool IsApproximate(TimeSpan? uncertainty) =>
        (uncertainty ?? TimeSpan.Zero) > WeeklyResetDetector.PreciseBracket;

    /// <summary>Hover text for an approximate weekly reset, naming how wide the bracket is.</summary>
    public static string WeeklyResetApproximateHint(TimeSpan uncertainty) =>
        $"The weekly reset was observed while Claude Desktop wasn't sampling, so it is "
        + $"known to within {Countdown(uncertainty)}. O-view keeps watching and will "
        + "sharpen this if it sees a reset while Desktop is running.";

    /// <summary>
    /// Shown while plan data is flowing but no weekly drop has been seen yet. This used to
    /// render as nothing at all, which is indistinguishable from a bug.
    /// </summary>
    public const string WeeklyResetWaiting = "Waiting for first reset…";

    /// <summary>Why the wait is normal, and roughly how long it lasts.</summary>
    public const string WeeklyResetWaitingHint =
        "Claude Desktop reports weekly usage as a percentage and never reports when the "
        + "window resets, so O-view derives it by watching that percentage fall. It checks "
        + "on every refresh and fills this in on the first reset it sees — within a week.";

    /// <summary>
    /// The caveat under the 31-day tiles: how much of the window is actually recorded, and
    /// which models were left out of the money figures.
    ///
    /// <para>Both are the same class of statement — the total is real but incomplete — and
    /// omitting either lets a figure read as the whole picture. An unpriced model must be
    /// named rather than silently dropped, and must not void the total (a single newly
    /// released Claude once blanked both Est. tiles entirely).</para>
    /// </summary>
    public static string Caveat(PanelStatistics stats)
    {
        var coverage = stats.CoverageNote;
        if (stats.UnpricedModels.Count == 0)
        {
            return coverage;
        }

        var excluded = $"excludes {string.Join(", ", stats.UnpricedModels)} (no published rate)";
        return coverage.Length > 0 ? $"{coverage} · {excluded}" : excluded;
    }

    /// <summary>
    /// Tile headings for estimated money. <b>"Est." is never dropped</b> — within plan
    /// limits the marginal cost is £0 and these price tokens at public API rates, so the
    /// figure is a valuation, not a charge.
    ///
    /// <para>The framing flips when usage goes off-plan, because then it genuinely is
    /// spend.</para>
    /// </summary>
    public static string EstTodayLabel(bool offPlan) => offPlan ? "Est. spend today" : "Est. value today";

    /// <summary>
    /// The 31-day heading, which deliberately does <b>not</b> flip with off-plan state.
    /// Divergence is detected for the <i>current session window</i> only, so relabelling a
    /// 31-day total as "spend" would extend a claim over 30 days it was never measured
    /// against.
    /// </summary>
    public const string Est31DaysLabel = "Est. value · 31 days";

    public const string TokensTodayLabel = "Tokens today";

    public const string Tokens31DaysLabel = "Tokens · 31 days";

    /// <summary>Sub-label noting that an off-plan figure includes work billed outside the plan.</summary>
    public static string OffPlanNote(bool offPlan) => offPlan ? "incl. off-plan usage" : "";
}
