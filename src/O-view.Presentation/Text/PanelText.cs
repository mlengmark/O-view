using System.Globalization;
using OView.Core.Models;
using OView.Core.Providers.PlanHistory;

namespace OView.Presentation.Text;

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
/// live here. This class keeps the panel's rows — the section labels, freshness, countdowns,
/// resets, hints, the chart's week markers and the off-plan badge.
/// The boost chip and card live in <see cref="BoostText"/>, the token labels and captions in
/// <see cref="TokenText"/>, and the off-plan headings and details in
/// <see cref="OffPlanText"/>.</para>
/// </summary>
public static class PanelText
{
    /// <summary>
    /// The name of the rolling five-hour meter. Both panels put it above the first bar, and
    /// both used to spell it out themselves.
    /// </summary>
    public const string SessionBarLabel = "Current session";

    /// <summary>The name of the weekly meter, above the second bar.</summary>
    public const string WeeklyBarLabel = "Weekly";

    /// <summary>
    /// The figure beside a meter: <c>47% used</c>.
    ///
    /// <para><b>"used", never the bare percentage.</b> A plan meter reading <c>47%</c> says
    /// nothing about which direction it runs — a reader who takes it for headroom has the
    /// number exactly backwards, and the one that matters most is the one nearest 100. Both
    /// panels already spelled the suffix out themselves, in the one place where dropping it
    /// would not look like a mistake.</para>
    ///
    /// <para>Invariant, because the panel states its own figures rather than inheriting the
    /// desktop's digits. When there is no percentage to show the row carries
    /// <see cref="PanelBanner"/>'s placeholder instead, which is a statement about why rather
    /// than a number.</para>
    /// </summary>
    public static string BarPercentUsed(int percent) =>
        string.Create(CultureInfo.InvariantCulture, $"{percent}% used");

    /// <summary>
    /// Stood in for the account name when the plan file has not been read yet, or carries no
    /// name. Lower case, because it is the absence of a name rather than a name
    /// (<see cref="ClaudeAccount"/>): a reader must not take it for what their account is
    /// called.
    /// </summary>
    public const string AccountUnknown = "account unknown";

    /// <summary>The same for the tier badge beside it.</summary>
    public const string TierUnknown = "tier unknown";

    /// <summary>
    /// The daily-usage chart's heading. <b>The day count is the window the figures actually
    /// cover</b> (<see cref="PanelStatistics.WindowDays"/>), not a literal: the heading and the
    /// bars under it have to describe the same span, and a hard-coded number would keep
    /// reading 31 the day the window changes.
    /// </summary>
    public static string UsageWindowHeading(int days) => $"Usage · last {days} days";

    /// <summary>
    /// What a week-boundary gridline on that chart marks when the boundary is the plan's own:
    /// the caption under the reset's time.
    /// </summary>
    public const string WeeklyResetMarker = "Weekly limit reset";

    /// <summary>
    /// What the same gridline marks when no weekly reset has been observed, in place of
    /// <see cref="WeeklyResetMarker"/>.
    ///
    /// <para><b>It disclaims the line it is attached to.</b> A dashed boundary drawn across a
    /// usage chart reads as the plan's week whatever its origin, so the fallback has to say
    /// outright that it is a calendar reference — otherwise the chart asserts a boundary O-view
    /// has no evidence for, which is a fabricated number wearing a gridline (rule 6).</para>
    ///
    /// <para>The Windows panel carried this sentence twice, once for the live chart and once
    /// for the hover cards built for verification, so the rendered proof could have agreed
    /// with itself while disagreeing with what ships.</para>
    /// </summary>
    public const string CalendarWeekMarker =
        "Start of the calendar week (Monday). O-view hasn't observed a weekly reset "
        + "yet, so this is a calendar reference, not your plan's boundary.";

    /// <summary>
    /// The badge on the off-plan section when credit-billed usage was recorded in the window,
    /// beside <see cref="OffPlanText.OffPlanHeading"/>.
    ///
    /// <para>The section's heading and hint already live in <see cref="OffPlanText"/>, which is
    /// where this pair reads as a neighbour; they are here because the slice that moved them out
    /// of the Windows panel was scoped to this class. Worth consolidating, not worth a second
    /// copy in the meantime.</para>
    /// </summary>
    public const string CreditBadgeRecorded = "credit-billed";

    /// <summary>
    /// The same badge when none was. <b>Lower case, like the unknown-account stand-ins</b>
    /// (<see cref="AccountUnknown"/>): it reports an absence in the slot a billing state goes
    /// in, and a capitalised "None recorded" reads as the name of one.
    ///
    /// <para>The row is shown either way rather than hidden when empty. A section that vanishes
    /// leaves the reader unable to tell "no off-plan usage" from "O-view did not look".</para>
    /// </summary>
    public const string CreditBadgeNoneRecorded = "none recorded";

    /// <summary>
    /// The label on the credit figure. <b>"Est." stays on both branches</b> — O-view prices
    /// credit usage from the same public rates as everything else, so it is a valuation of what
    /// was billed rather than the charge itself, and the zero case is no less an estimate for
    /// being zero.
    /// </summary>
    public const string CreditSpendLabel = "Est. credit spend";

    /// <summary>
    /// The header's freshness line: <c>As of now</c>, <c>As of 11:34</c>,
    /// <c>Local estimate · as of 11:34</c>, <c>No data</c>.
    ///
    /// <para><b>It describes the reading, not the repaint.</b> The line it replaces read
    /// <c>Updated 11:34 · live</c>, where the clock time was the moment the panel was drawn
    /// and "live" was a claim about the pipeline rather than about the number beside it. Both
    /// halves overstated: O-view holds whatever the last poll returned, and the source itself
    /// samples on its own schedule, so the figures on screen are always a log of a past
    /// moment (GitHub issue #192). The time shown is now
    /// <see cref="UsageSnapshot.CapturedAtUtcValue"/> — when the sample was actually taken.</para>
    ///
    /// <para>"now" is claimed only for a reading captured within the current clock minute;
    /// once that minute has passed the reading is a past log and is stamped with its own
    /// time. That also keeps the stamp from ever reading as the current minute, which would
    /// invite exactly the "this is happening now" reading the issue is about.</para>
    ///
    /// <para>The <see cref="DataSource"/> tiers collapse here on purpose: a capture time says
    /// how old a reading is far more precisely than the Live/Stale split it replaces, and
    /// both tiers are authoritative figures either way. What does <b>not</b> collapse is
    /// <see cref="DataSource.Estimate"/> — a JSONL-derived figure must still say it is a local
    /// estimate (rule 6, ADR-0002), with its age beside it rather than instead of it.</para>
    /// </summary>
    public static string Freshness(UsageSnapshot snapshot, DateTimeOffset utcNow, TimeZoneInfo local)
    {
        var age = AsOf(snapshot.CapturedAtUtcValue, utcNow, local);

        return snapshot.Source switch
        {
            DataSource.Live or DataSource.Stale => age is null ? CaptureTimeUnknown : $"As of {age}",
            DataSource.Estimate => age is null ? "Local estimate" : $"Local estimate · as of {age}",
            _ => "No data",
        };
    }

    /// <summary>
    /// Said when an authoritative figure arrived without a capture time. Rare — every shipped
    /// provider stamps one — but the alternative is stamping it with the repaint clock, which
    /// is the bug this line was rewritten to remove.
    /// </summary>
    public const string CaptureTimeUnknown = "Reading time unknown";

    /// <summary>
    /// <c>now</c> for a sample taken in the current clock minute, otherwise its local
    /// <c>HH:mm</c>. Null when there is no capture time to report.
    /// </summary>
    private static string? AsOf(UsageInstant capturedAt, DateTimeOffset utcNow, TimeZoneInfo local)
    {
        if (capturedAt is not { Status: not UsageValueStatus.Unavailable, Value: { } at })
        {
            return null;
        }

        var captured = TimeZoneInfo.ConvertTime(at, local);
        var now = TimeZoneInfo.ConvertTime(utcNow, local);

        // Both conditions are needed. The elapsed check alone would call a 40-second-old
        // reading "now" across a minute boundary, which is the past log the issue asks to be
        // stamped; the clock-minute check alone misreads the DST fall-back hour, where an
        // instant 40 minutes ago carries a *later* local minute than now. A capture in the
        // future is a clock adjustment, not a prediction — report it as now rather than
        // stamping the panel with a time that has not happened.
        var elapsed = utcNow - at;
        var withinThisMinute = elapsed < TimeSpan.FromMinutes(1)
            && (elapsed < TimeSpan.Zero || Minute(captured) == Minute(now));

        return withinThisMinute
            ? "now"
            : string.Create(CultureInfo.InvariantCulture, $"{captured:HH:mm}");
    }

    private static DateTime Minute(DateTimeOffset t) =>
        new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0);

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
    /// The session-reset line. Before a window start has been observed the reset time is
    /// genuinely unknown, and says so rather than guessing (ADR-0011, rule 6).
    ///
    /// <para>Carries the same <c>~</c> as the weekly line when the start was only bracketed
    /// — which is every start inferred across a sampling gap, because Desktop was closed when
    /// the window actually began. Printing <c>22:47</c> to the minute for a boundary known to
    /// a quarter of an hour is the shape of the bug this line was reported for (issue
    /// #180).</para>
    /// </summary>
    public static string SessionReset(
        DateTimeOffset? resetAtUtc, DateTimeOffset utcNow, TimeZoneInfo local,
        TimeSpan? uncertainty = null)
    {
        if (resetAtUtc is not { } reset)
        {
            return "Reset time unknown (no reset observed yet)";
        }

        var at = TimeZoneInfo.ConvertTime(reset, local);
        return $"Resets in {Countdown(reset - utcNow)} · {(IsApproximate(uncertainty) ? "~" : "")}{at:HH:mm}";
    }

    /// <summary>
    /// The weekly-reset line. Carries the weekday, because a reset a week out needs one,
    /// and a <c>~</c> when the observation is only bracketed to within hours — showing an
    /// exact minute O-view does not have would be a fabricated number.
    /// </summary>
    /// <summary>
    /// The weekly reset line. <b>Never approximate</b> — see below.
    ///
    /// <para>This took an <c>uncertainty</c> and could render a <c>~</c>, from when the weekly
    /// reset was inferred from a drop in Claude Desktop's sampled series and could only be
    /// bracketed. [ADR-0014](../../../docs/adr/0014-weekly-reset-is-a-reported-constant.md)
    /// replaced that with a reported instant, stored as an anchor and projected forward by whole
    /// weeks, so the value carries zero uncertainty and the marker became unreachable. It stayed
    /// in the code for one release to keep that behavioural change reviewable on its own; issue
    /// #248 is the removal it promised.</para>
    ///
    /// <para><b>The five-hour window is the opposite and keeps its <c>~</c></b>
    /// (<see cref="SessionReset"/>). It rolls from first use rather than sitting on a grid, so its
    /// reset is still derived and still bracketed. Applying one rule to both windows is the
    /// mistake ADR-0014 exists to prevent, so <see cref="IsApproximate"/> stays for that caller.</para>
    /// </summary>
    public static string WeeklyReset(
        DateTimeOffset resetAtUtc, DateTimeOffset utcNow, TimeZoneInfo local)
    {
        var at = TimeZoneInfo.ConvertTime(resetAtUtc, local);
        return $"Resets in {Countdown(resetAtUtc - utcNow)} · {at:ddd HH:mm}";
    }

    /// <summary>
    /// Whether a weekly observation is wide enough to need the <c>~</c>. A reset caught
    /// while Claude Desktop was sampling is precise; one caught across a gap is not.
    /// </summary>
    public static bool IsApproximate(TimeSpan? uncertainty) =>
        (uncertainty ?? TimeSpan.Zero) > WeeklyWindow.PreciseBracket;

    /// <summary>
    /// Marks a weekly reset the user entered rather than one O-view inferred (issue #186).
    /// Short, because it sits beside the time rather than under it — the reasoning belongs in
    /// <see cref="WeeklyResetUserSuppliedHint"/>.
    /// </summary>
    public const string WeeklyResetUserSupplied = "you set this";

    /// <summary>Hover text explaining why an entered reset outranks a derived one.</summary>
    public const string WeeklyResetUserSuppliedHint =
        "You entered this from Claude's own Settings → Usage, so O-view shows it exactly "
        + "rather than the time it derives by watching the weekly percentage fall. It keeps "
        + "deriving in the background and will tell you if an observed reset ever disagrees.";

    /// <summary>
    /// Told to the user when an observed reset disproves what they entered (issue #186).
    ///
    /// <para>Says what was observed and what to do, and does not decide for them which is
    /// wrong: a plan change, a typo and a genuine schedule change all look identical from
    /// here. The entry has already been set aside in favour of the observation — a number
    /// O-view has evidence against must not stay on screen (rule 6) — so this explains a
    /// change that has already happened rather than asking permission for one.</para>
    /// </summary>
    public static string WeeklyResetConflict(DateTimeOffset reportedUtc, TimeZoneInfo local)
    {
        // One instant now, not a bracket. The old copy said "between X and Y" because the
        // reset was inferred from a gap in Claude Desktop's sampling and could only ever be
        // narrowed to a range. Claude reports it exactly (ADR-0014), so quoting a range would
        // understate what O-view knows and invite the user to think it is still guessing.
        var at = TimeZoneInfo.ConvertTime(reportedUtc, local);

        return $"Claude reports your weekly limit resetting {at:ddd HH:mm}, which does not "
            + "match the time you entered. O-view is using the reported time. Re-enter yours "
            + "if your plan changed.";
    }

    /// <summary>
    /// Shown when no weekly reset is known: Claude Code has never reported one and nothing was
    /// entered (ADR-0014).
    ///
    /// <para><b>This replaces "Waiting for first reset…", which described a mechanism that no
    /// longer exists.</b> That copy is ADR-0011's, from when the reset was derived by watching
    /// the weekly percentage fall; ADR-0014 deleted the derivation, so nothing was watching and
    /// the promised fill-in could never arrive. A row that names a wait nobody is serving is the
    /// blank row's defect — indistinguishable from a bug — wearing an explanation.</para>
    ///
    /// <para>It states the fact instead, and the hint carries the one action that ends it. The
    /// action only has to work once: the reset is a fixed weekly grid and O-view keeps the
    /// anchor, so this row is a one-time setup step rather than a standing gap.</para>
    /// </summary>
    public const string WeeklyResetUnknown = "Weekly reset time not known";

    /// <summary>
    /// What to do about it — both routes, in one string.
    ///
    /// <para>Claude Code writes the exact instant only when <c>/usage</c> runs: not on startup,
    /// not on an ordinary prompt
    /// ([findings/cli-usage-refresh.md](../../../../docs/findings/cli-usage-refresh.md)). O-view
    /// asks it to on its own cadence (issue #234), so this row usually fills itself in; the hint
    /// is for the machines where it has not. It names the command rather than describing it,
    /// because the user has to type that exact string.</para>
    ///
    /// <para><b>Both routes, deliberately, rather than one chosen for the reader.</b> Choosing
    /// would mean deciding whether this machine has a usable Claude Code — and on the machine
    /// that prompted this, O-view's own refresher was running, reporting <c>Unchanged</c>, and
    /// producing no block at all. Until that is understood, a branch would be the app asserting
    /// a cause it has not established, which is rule 6 pointed at ourselves. Two sentences cost
    /// a hover card nothing; a confident wrong one costs the user the fix.</para>
    /// </summary>
    public const string WeeklyResetUnknownHint =
        "Claude reports the exact weekly reset only when /usage runs in Claude Code. O-view asks "
        + "it to periodically; if this stays blank, run /usage in a Claude Code session yourself "
        + "— once is enough, because the reset is a fixed weekly time O-view then remembers. "
        + "With no Claude Code, enter it from Menu → Weekly reset…: Claude shows the time under "
        + "Settings → Usage.";

    /// <summary>
    /// The same action in a few words, for a surface with no room for the full hint: the Linux
    /// panel's bar rows carry a single line and no hover card, so the action rides on the row
    /// itself or does not appear at all.
    /// </summary>
    public const string WeeklyResetUnknownAction = "run /usage in Claude Code";

    /// <summary>
    /// The menu row offered when usage refreshing has stopped itself (issue #234).
    ///
    /// <para>Shown only while <c>UsageEngine.UsageRefreshBlocked</c> is set. It is an action
    /// rather than a status line because the state is undoable and the user is the only one who
    /// can judge it: the guard behind the block errs toward reporting a charge, and a Claude Code
    /// session started while a refresh ran looks exactly like a billed one.</para>
    ///
    /// <para><b>The row has to exist at all, or the trade is wrong.</b> Erring toward stopping is
    /// only correct while stopping can be reversed — a latch with no way out turns a rare race
    /// into a permanent, unexplained loss of the plan bars on machines with no Claude Desktop.</para>
    /// </summary>
    public const string UsageRefreshBlockedRow = "Resume usage refresh";

    /// <summary>
    /// Why it stopped and what resuming means. Names the evidence, because the alternative is a
    /// feature that went quiet for reasons the user cannot inspect.
    /// </summary>
    public static string UsageRefreshBlockedHint(string reason) =>
        $"O-view stopped refreshing Claude Code's usage figures because {reason}. "
        + "That check is deliberately cautious — a Claude Code session started while the refresh "
        + "ran looks the same as a billed one. Resume if you think that is what happened.";

    /// <summary>
    /// Why an update check came back empty when GitHub throttled it (issue #176).
    ///
    /// <para>It says the limit is shared, because for most people hitting it that is the
    /// whole explanation: 60 requests an hour is counted <b>per IP address</b> for an
    /// unauthenticated caller, so an office or a VPN exit node reaches it without this user
    /// having done anything. The previous wording blamed their connection and sent them to
    /// debug a network that was working.</para>
    ///
    /// <para>The retry time is stated only when GitHub sent one. Inventing "try again in an
    /// hour" would be a fabricated number (rule 6), and the honest version still tells the
    /// reader more than the message it replaces.</para>
    /// </summary>
    public static string RateLimitedNotice(DateTimeOffset? retryAfterUtc, TimeZoneInfo local) =>
        "GitHub limits how often it answers without an account, and that limit is shared by "
        + "everyone on your network. O-view will try again "
        + (retryAfterUtc is { } at
            ? $"after {TimeZoneInfo.ConvertTime(at, local):HH:mm}."
            : "on its next check.")
        + " Nothing is wrong with your connection or your install.";
}
