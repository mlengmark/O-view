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
    /// The header's freshness line: <c>As of now</c>, <c>As of 11:34</c>,
    /// <c>Local estimate · as of 11:34</c>, <c>No data</c>.
    ///
    /// <para><b>It describes the reading, not the repaint.</b> The line it replaces read
    /// <c>Updated 11:34 · live</c>, where the clock time was the moment the panel was drawn
    /// and "live" was a claim about the pipeline rather than about the number beside it. Both
    /// halves overstated: O-view holds whatever the last poll returned, and the source itself
    /// samples on its own schedule, so the figures on screen are always a log of a past
    /// moment (GitHub issue #192). The time shown is now
    /// <see cref="UsageSnapshot.CapturedAtUtc"/> — when the sample was actually taken.</para>
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
        var age = AsOf(snapshot.CapturedAtUtc, utcNow, local);

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
    private static string? AsOf(DateTimeOffset? capturedAtUtc, DateTimeOffset utcNow, TimeZoneInfo local)
    {
        if (capturedAtUtc is not { } at)
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
    ///
    /// <para>It read "(UTC)" for one release, because the figure was bucketed by UTC date and
    /// had to say so (issue #210). The suffix is off again now the bucket is the reader's own
    /// day — a qualifier that is no longer true is not a safe thing to leave on.</para>
    /// </summary>
    public static string EstTodayLabel(bool offPlan) => offPlan ? "Est. spend today" : "Est. value today";

    /// <summary>
    /// The 31-day heading, which deliberately does <b>not</b> flip with off-plan state.
    /// Divergence is detected for the <i>current session window</i> only, so relabelling a
    /// 31-day total as "spend" would extend a claim over 30 days it was never measured
    /// against.
    /// </summary>
    public const string Est31DaysLabel = "Est. value · 31 days";

    /// <summary>
    /// The token tile headings, which name what they count.
    ///
    /// <para>They read "Tokens today" alone until a user reported the figure as inflated:
    /// <c>235.6M</c> beside the thousands Claude's own UI shows (issue #169). The sum was
    /// right — roughly 90% of it is cached prompt re-reads, which are billed and counted —
    /// but an unqualified "Tokens today" invites the comparison that makes it look like a
    /// 1000× over-count. The qualifier is the headline's share of rule 6: a figure states
    /// what it measures, and the detail follows in <see cref="TokenCompositionLine"/>.</para>
    ///
    /// <para><b>"Today" means the reader's today.</b> It said "(UTC)" for one release because
    /// the bucket genuinely was a UTC day and a label that does not match its figure is the
    /// same rule-6 failure as a wrong figure (issue #210). The bucket is now the local day
    /// (issue #211), so the qualifier is gone: leaving it on would be the mislabelling
    /// pointed the other way.</para>
    /// </summary>
    public const string TokensTodayLabel = "Tokens today · incl. cache";

    /// <inheritdoc cref="TokensTodayLabel"/>
    public const string Tokens31DaysLabel = "Tokens · 31 days · incl. cache";

    /// <summary>
    /// The four-way split behind a token total, as one line:
    /// <c>Today: input 14 · cache write 44.3K · cache read 398.1K · output 3.7K</c>.
    ///
    /// <para>Named "cache write" rather than "cache creation" because the pair reads as a
    /// pair — a user scanning the line needs to see at once that two of the four entries
    /// are cache traffic, which is the whole point of showing it.</para>
    ///
    /// <para><paramref name="scope"/> names which total is being explained, so the line can
    /// never be read against the wrong tile. Only the daily one is rendered today; the
    /// parameter is here because a line that says "input 14" beside two different totals
    /// with no scope word is exactly the ambiguity this whole issue was about.</para>
    /// </summary>
    public static string TokenCompositionLine(TokenComposition c, string scope) =>
        $"{scope}: input {UsageFormatter.Tokens(c.Input)} · cache write {UsageFormatter.Tokens(c.CacheCreation)}"
        + $" · cache read {UsageFormatter.Tokens(c.CacheRead)} · output {UsageFormatter.Tokens(c.Output)}";

    /// <summary>Scope word for the daily composition line.</summary>
    public const string TokenCompositionTodayScope = "Today";

    /// <summary>
    /// The tokens behind the session bar, rendered directly beneath it (GitHub issue #218).
    ///
    /// <para><b>The scope is named, not implied.</b> "This session window" is the same window
    /// the percentage above is a percentage of, and saying so is the whole point — the panel's
    /// other token figures are a calendar day and 31 of them, so a figure placed here without a
    /// scope word would simply move the ambiguity rather than remove it. That is the lesson of
    /// #210 and #169 applied before the fact.</para>
    ///
    /// <para><b>"local sessions only" is not a hedge.</b> The bar is account-wide: it counts
    /// chat, which keeps no local usage record at all, and any work done on another machine
    /// (rule 9). This figure is what was written to this machine. The qualifier is what stops
    /// the two being read as the same measurement and the difference as a fault — which is
    /// exactly how issue #218 was reported.</para>
    ///
    /// <para>Empty when no window has been established, because a zero would then be a claim
    /// about usage rather than about the absence of a window.</para>
    /// </summary>
    public static string SessionUsageLine(PanelStatistics stats)
    {
        if (!stats.HasSessionWindow)
        {
            return "";
        }

        if (stats.TokensSession == 0)
        {
            return "This session window: no local session activity recorded";
        }

        var line = $"This session window: {UsageFormatter.Tokens(stats.TokensSession)} tokens";

        if (stats.EstSessionUsd is { } usd)
        {
            // Flips with off-plan state, and this is the one figure where that is unambiguously
            // right: divergence is detected for THIS window, so unlike the 31-day heading the
            // label is not extending a claim past what was measured.
            line += $" · {(stats.IsOffPlan ? "Est. spend" : "Est.")} {UsageFormatter.Usd(usd)}";
        }

        if (stats.UnpricedModelsSession.Count > 0)
        {
            line += $" · excludes {string.Join(", ", stats.UnpricedModelsSession)} (no published rate)";
        }

        return line + " — local sessions only";
    }

    /// <summary>
    /// Why the line above can read zero while the bar above <i>it</i> reads high.
    ///
    /// <para>Shown only in that case, because it is the only case that needs it — a figure that
    /// agrees with its bar explains itself. Without it, "no local session activity" beside
    /// "87%" is precisely the pairing that gets reported as tokens going uncounted, and the
    /// panel has the facts to answer it (the same failure as #44, #58 and #170, where the panel
    /// knew and did not say).</para>
    ///
    /// <para>It states what the two sources are, and asserts nothing about this machine that
    /// O-view has not observed — it does not claim the user was in chat, or on another device,
    /// only that neither would appear here.</para>
    ///
    /// <para><b>It leads with how stale the local record is, because that is the fact.</b> "No
    /// local session activity" beside a bar reading 100% invites the reading that something has
    /// just broken. On the machine in issue #218 nothing had been written for two days while the
    /// meters ran at 100% — a figure that says so is both more informative and the one a support
    /// report needs, and it turns an alarming absence into a dated observation.</para>
    ///
    /// <para><b>The last clause is deliberately open.</b> It said "a session on another device",
    /// which was too narrow: a session can leave no transcript on the machine it is running on —
    /// issue #218 is exactly that, and the mechanism was never established. Naming a cause O-view
    /// cannot see would be the fabrication rule 6 forbids; naming the consequence is what the
    /// panel actually knows.</para>
    ///
    /// <para><b>Silent when the machine has recorded nothing at all.</b> That case already has
    /// an explanation — the token-scope note, which names the surfaces and the locations
    /// searched (#58, #170) — and it is the better one, because "none in this window" is a
    /// weaker statement than "none anywhere". Two paragraphs saying overlapping things is how a
    /// panel stops being read.</para>
    /// </summary>
    public static string SessionUsageNote(
        PanelStatistics stats, DateTimeOffset utcNow, TimeZoneInfo local)
    {
        if (stats is not { HasSessionWindow: true, TokensSession: 0, Tokens31Days: > 0 })
        {
            return "";
        }

        var staleness = stats.LatestLocalActivityUtc is { } at
            ? $"Newest local record: {Countdown(Elapsed(at, utcNow))} old"
              + $" ({TimeZoneInfo.ConvertTime(at, local):ddd HH:mm}). "
            : "";

        return staleness
            + "The bar above comes from Claude's own meters and covers your whole account. "
            + "Chat keeps no local usage record, and neither does a session that writes no "
            + "transcript on this machine, so neither can be counted here.";
    }

    /// <summary>
    /// Age, clamped at zero. A record stamped slightly ahead of the clock is a clock
    /// correction, not a reading from the future, and "-0m old" reads as a rendering fault.
    /// </summary>
    private static TimeSpan Elapsed(DateTimeOffset at, DateTimeOffset utcNow) =>
        utcNow - at is { Ticks: > 0 } age ? age : TimeSpan.Zero;

    /// <summary>
    /// Why that total is so much larger than the number Claude's own UI shows — the
    /// sentence the whole of issue #169 comes down to.
    ///
    /// <para>It states the share rather than asserting a fixed ratio: the proportion moves
    /// with how long conversations run, and a hard-coded "about 90%" would be a fabricated
    /// number on a machine where it is 60% (rule 6). It also names the comparison being
    /// warned against explicitly, because a user who has already made that comparison is
    /// the reader this line exists for.</para>
    /// </summary>
    public static string TokenCompositionHint(TokenComposition c) =>
        // Built rather than formatted with "P0", which renders "89 %" — with a space — under
        // the INVARIANT culture as well as most European ones. The panel states its own
        // figures rather than inheriting the desktop's, and invariant alone does not get
        // there here.
        $"Cache reads are {(c.CacheReadShare * 100).ToString("0", CultureInfo.InvariantCulture)}% of this: "
        + "every turn re-sends the conversation and caching bills the re-send. It adds up every "
        + "request, so it is not the context figure Claude's UI shows — that is one conversation "
        + $"at one moment. Without cache reads: {UsageFormatter.Tokens(c.ExcludingCacheReads)}.";

    /// <summary>
    /// The disclosure that reveals <see cref="TokenCompositionHint"/>. Phrased as the
    /// question the reader is actually asking — the one that opened issue #169 — rather
    /// than as a label for what is behind it.
    /// </summary>
    public const string TokenExplainToggleLabel = "Why so large?";

    /// <summary>
    /// What the divergence banner says when local work is running past a meter that is not
    /// moving.
    ///
    /// <para><b>It states the observation and stops there.</b> The wording it replaces ended
    /// "that work is billing elsewhere — most likely extra-usage credits", which is a claim
    /// about the user's billing that O-view cannot see and, on the machine that reported it,
    /// was false: their account had extra usage switched off, and Claude Code's own cache says
    /// so in <c>extra_usage.user_disabled</c>. Naming a cause is not the panel's job — the two
    /// numbers are, and they are what the reader can check (rule 6).</para>
    ///
    /// <para>The wording also names the meter as the thing that did not move, rather than the
    /// usage as the thing that went astray. Those describe the same observation and read very
    /// differently when the meter is the part at fault.</para>
    /// </summary>
    public static string DivergenceDetail(long outputTokens, int risePoints) =>
        $"About {UsageFormatter.Tokens(outputTokens)} output tokens ran in this window while the "
        + $"plan meter moved {risePoints} point{(risePoints == 1 ? "" : "s")}. Usage that a plan "
        + "meter does not account for is billed some other way — O-view cannot see your billing, "
        + "so check Settings → Usage in Claude for what it was.";

    /// <inheritdoc cref="DivergenceDetail"/>
    public const string PlanLimitReachedDetail =
        "The 5-hour window is exhausted, so continued work is not drawing from it. Whether that "
        + "bills as extra usage depends on your account settings, which O-view cannot read.";

    /// <summary>Sub-label noting that an off-plan figure includes work billed outside the plan.</summary>
    public static string OffPlanNote(bool offPlan) => offPlan ? "incl. off-plan usage" : "";

    /// <summary>
    /// What the off-plan section means, shown on hover rather than as standing text
    /// (GitHub issue #181).
    ///
    /// <para><b>Moving it must not lose it.</b> Both wordings carry a rule-6 caveat that the
    /// figure is not what was charged — "Estimated at published API rates" and "check your
    /// billing page for exact figures" in one, "isn't captured" in the other — and relocating
    /// a caveat behind a hover is the obvious way to quietly drop it. It lives here so it has
    /// one definition and can be asserted, which is why it moved out of the head rather than
    /// simply being reassigned to a tooltip in place.</para>
    /// </summary>
    public static string OffPlanHint(bool hasCreditUsage) => hasCreditUsage
        ? $"Estimated at published API rates for models billed as extra usage ({CreditBilledModels.DisplayList}). "
          + "O-view cannot read your credit balance; check your billing page for exact figures."
        : $"No credit-billed usage ({CreditBilledModels.DisplayList}) recorded in the last 31 days. "
          + "Off-plan usage while O-view wasn't running isn't captured.";

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
