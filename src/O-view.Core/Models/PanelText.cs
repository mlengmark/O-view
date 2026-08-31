using System.Globalization;
using OView.Core.Pricing;
using OView.Core.Providers.CachedUsage;
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

    /// <summary>Hover text for an approximate weekly reset, naming how wide the bracket is.</summary>
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
    /// The boost chip on a meter's label row: <c>50% Boosted · until 31 Aug · ends in 2w 4d 14h</c>.
    ///
    /// <para><b>The figure leads and the word follows it immediately.</b> The same row carries
    /// utilisation — a <i>level</i> — a hundred pixels to the right, and this is a <i>delta</i>.
    /// A bare <c>50%</c> adrift on that row would read as the same kind of number; <c>50%
    /// Boosted</c> reads as one phrase, so the two are never split.</para>
    ///
    /// <para><b>Every part is optional and drops out silently.</b> With neither figure parsed the
    /// chip is just <c>Boosted</c>, which is the floor this feature never falls below: the
    /// message itself is relayed verbatim in the hover card either way
    /// (<see cref="BoostCard"/>), and that path depends on no parsing at all.</para>
    ///
    /// <para><b>The month is abbreviated, and that is a layout constraint rather than a
    /// preference.</b> Measured at the panel's real width in the face the Windows head paints,
    /// the label row leaves 281px for this chip: <c>50% Boosted · until 31 Aug · ends in 14h</c>
    /// takes 220, the widest the payload could carry takes 263, and the same string with a full
    /// month name takes 289 and overflows. The row must never wrap — a wrap shifts the bar under
    /// it and changes the panel's height.</para>
    /// </summary>
    /// <param name="notice">The notice to describe. Its own bar decides where this is drawn.</param>
    /// <param name="utcNow">Now, for the countdown.</param>
    /// <param name="local">The reader's zone: a promo ends at the end of its last local day.</param>
    public static string BoostChip(BoostNotice notice, DateTimeOffset utcNow, TimeZoneInfo local)
    {
        var chip = notice.Percent is { } pct
            ? string.Create(CultureInfo.InvariantCulture, $"{pct}% Boosted")
            : "Boosted";

        if (notice.EndsOn is not { } last)
        {
            return chip;
        }

        var ends = EndOfDayUtc(last, local);
        return string.Create(CultureInfo.InvariantCulture,
            $"{chip} · until {last:d MMM} · ends in {BoostRemaining(ends - utcNow)}");
    }

    /// <summary>
    /// Time left on a promo, in the weeks/days/hours the chip asks for: <c>2w 4d 14h</c>,
    /// <c>4d 14h</c>, <c>14h</c>.
    ///
    /// <para><b>Empty leading units are dropped</b> — a promo ending tonight reads <c>14h</c>,
    /// not <c>0w 0d 14h</c>. Hours are the floor because the end is a <i>date</i>: the source
    /// says "Aug 31", so the last hour of that day is the finest thing anyone knows, and
    /// counting down in minutes would imply a precision the sentence never carried.</para>
    /// </summary>
    public static string BoostRemaining(TimeSpan left)
    {
        if (left <= TimeSpan.Zero)
        {
            return "under an hour";
        }

        var weeks = left.Days / 7;
        var days = left.Days % 7;
        var hours = left.Hours;

        var parts = new List<string>(3);
        if (weeks > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{weeks}w"));
        }

        if (days > 0 || parts.Count > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{days}d"));
        }

        if (hours > 0 || parts.Count > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{hours}h"));
        }

        return parts.Count > 0 ? string.Join(" ", parts) : "under an hour";
    }

    /// <summary>
    /// The instant a promo's last day ends, in UTC — the next local midnight after it.
    ///
    /// <para>Built from the zone's offset rather than
    /// <see cref="TimeZoneInfo.ConvertTimeToUtc(DateTime, TimeZoneInfo)"/>, which throws when the
    /// wall-clock time it is handed does not exist. Midnight is skipped outright in a handful of
    /// zones on their DST transition day, and a countdown must not be the thing that takes the
    /// panel down.</para>
    /// </summary>
    private static DateTimeOffset EndOfDayUtc(DateOnly lastDay, TimeZoneInfo local)
    {
        var midnight = lastDay.AddDays(1).ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(midnight, local.GetUtcOffset(midnight)).ToUniversalTime();
    }

    /// <summary>
    /// The hover card behind the chip: Claude's sentence, then who said it and when O-view read it.
    ///
    /// <para><b>The message is never edited, summarised or re-worded.</b> Whether this promo
    /// applies to this account is something O-view cannot check — the payload is a feature-flag
    /// cache, evaluated server-side — so the panel relays rather than asserts, and the provenance
    /// line is what makes that the honest reading rather than a hedge (rule 6).</para>
    /// </summary>
    public static string BoostCard(
        BoostNotice notice, DateTimeOffset fetchedAtUtc, TimeZoneInfo local)
    {
        var read = TimeZoneInfo.ConvertTime(fetchedAtUtc, local);
        var ends = notice.EndsOn is { } last
            ? string.Create(CultureInfo.InvariantCulture, $"Ends {last:ddd d MMM} · ")
            : "";

        return string.Create(CultureInfo.InvariantCulture,
            $"{notice.Text}\n\n{ends}reported by Claude Code, read {read:HH:mm}");
    }

    /// <summary>
    /// The caveat under the 31-day tiles: how much of the window is actually recorded, which
    /// models were left out of the money figures, what O-view assumed about cache writes it
    /// could not attribute, and how old the rates behind the figures are.
    ///
    /// <para>All four are the same class of statement — the total is real but qualified — and
    /// omitting any of them lets a figure read as the whole picture. An unpriced model must be
    /// named rather than silently dropped, and must not void the total (a single newly
    /// released Claude once blanked both Est. tiles entirely).</para>
    ///
    /// <para><b>The last two are conditions that clear.</b> The TTL note applies only to rows a
    /// build before GitHub issue #255 ingested and that are out of reach of the re-ingest, so it
    /// disappears as that history ages out of the window. The rate age appears only past
    /// <see cref="RateCard.StaleAfter"/> — a caveat that is always on says nothing, and the
    /// figures are not less true for being priced at a rate that has not changed.</para>
    /// </summary>
    public static string Caveat(PanelStatistics stats)
    {
        var parts = new List<string>(4);

        if (stats.CoverageNote is { Length: > 0 } coverage)
        {
            parts.Add(coverage);
        }

        if (stats.UnpricedModels.Count > 0)
        {
            parts.Add($"excludes {string.Join(", ", stats.UnpricedModels)} (no published rate)");
        }

        if (stats.TtlUnrecordedCacheWrites > 0)
        {
            parts.Add($"{UsageFormatter.Tokens(stats.TtlUnrecordedCacheWrites)} cache writes "
                      + "with no recorded duration, priced at the 5-minute rate");
        }

        if (stats.RatesAreStale)
        {
            parts.Add(RateAge(stats.Rates));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// How old the rates are and where they came from: <c>rates: bundled, as of 24 Jun 2026</c>.
    ///
    /// <para><b>The source is named as well as the date</b>, because the two answer different
    /// halves of "can I check this figure". A date says how likely the table is to have moved;
    /// a source says whose table it is. Only <see cref="RateCardSource.Bundled"/> exists today,
    /// and the line is written to carry the other from the day it does — a user-editable
    /// pricing file with no provenance on screen is a fabricated-number vector (issue #255).</para>
    /// </summary>
    public static string RateAge(RateCard card) => string.Create(
        CultureInfo.InvariantCulture,
        $"rates: {(card.Source == RateCardSource.Bundled ? "bundled" : "user file")}, as of {card.AsOf:d MMM yyyy}");

    /// <summary>
    /// What the token and cost tiles permanently do not cover (issue #235).
    ///
    /// <para><b>Deliberately not part of <see cref="Caveat"/>.</b> That one is a per-tile hint
    /// carried by the two 31-day tiles, and it is right for statements about the 31-day window.
    /// This applies to every token and cost figure on the panel, so riding the same channel would
    /// print it twice on adjacent tiles and never on the "today" pair. It renders once, beneath
    /// the whole tile block.</para>
    ///
    /// <para><b>The plan bars are account-wide; these tiles are not.</b> Cloud-container Cowork
    /// sessions and Claude chat leave no transcript on this machine — measured directly on
    /// 2026-08-28, a cloud Cowork session wrote no registration and no transcript at all — so the
    /// bars include that work and the tiles cannot. The machine that reported
    /// [#218](https://github.com/mlengmark/O-view/issues/218) sat at 100% on the plan meter with a
    /// two-day-old local record, which is that pairing in its most alarming form.</para>
    ///
    /// <para><b>Always present, unlike the caveats it joins.</b> Partial history and an unpriced
    /// model are conditions that pass; this one is the standing shape of the data. A caveat that
    /// appears only sometimes teaches a reader that its absence means full coverage, and here that
    /// would be false every time.</para>
    ///
    /// <para><b>This is not the line issue #232 removed.</b> That one sat under the session
    /// <i>bar</i>, described the session window alone, and carried a disclosure; it was removed as
    /// out of place with the explicit instruction that nothing replace it there. This states the
    /// scope of the <i>tiles</i>, in the caveat line they already carry, which is where the panel
    /// has always admitted a figure is real but incomplete.</para>
    /// </summary>
    public const string TokenScopeCaveat = "chat and cloud sessions not counted";

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
    /// 1000× over-count. #169 answered that by qualifying the heading: <c>incl. cache</c>.</para>
    ///
    /// <para><b>Issue #253 changed the figure instead.</b> The qualifier was correct and
    /// still left the panel leading with a number nobody reads — at a stable ~89% cache-read
    /// share, output never exceeds about 1.5% of the total, so the one quantity that tracks
    /// work done was buried in a figure dominated by conversation re-sends. The tiles now
    /// headline output alone and say so. The composition moved beneath them
    /// (<see cref="TokensUsedTodayLabel"/>), where explaining the rest is its whole job.</para>
    ///
    /// <para><b>"Today" means the reader's today.</b> It said "(UTC)" for one release because
    /// the bucket genuinely was a UTC day and a label that does not match its figure is the
    /// same rule-6 failure as a wrong figure (issue #210). The bucket is now the local day
    /// (issue #211), so the qualifier is gone: leaving it on would be the mislabelling
    /// pointed the other way.</para>
    /// </summary>
    public const string TokensTodayLabel = "Output tokens today";

    /// <inheritdoc cref="TokensTodayLabel"/>
    public const string Tokens31DaysLabel = "Output tokens · 31 days";

    /// <summary>
    /// Headings for the two composition bars.
    ///
    /// <para><b>"Tokens used" rather than "Tokens", and deliberately not the tiles' wording.</b>
    /// A bar totalling 1.4M sitting under a tile reading <c>Output tokens today 14.6K</c> is a
    /// contradiction unless something says the two count different things. These name the
    /// billed whole; the tiles name the work.</para>
    /// </summary>
    /// <summary>
    /// The section heading above both bars and the view switch. Names the section once so the
    /// two bar headings can name only their windows.
    /// </summary>
    public const string TokensUsedHeading = "Tokens used";

    /// <inheritdoc cref="TokensUsedHeading"/>
    public const string TokensUsedTodayLabel = "Tokens used · today";

    /// <inheritdoc cref="TokensUsedTodayLabel"/>
    public const string TokensUsed31DaysLabel = "Tokens used · last 31 days";

    /// <summary>
    /// One token kind's name.
    ///
    /// <para>"Cache write" rather than "cache creation" because the pair reads as a pair — a
    /// reader scanning the legend needs to see at once that two of the four entries are cache
    /// traffic, which is the whole point of showing the split.</para>
    /// </summary>
    public static string TokenKindLabel(TokenKind kind) => kind switch
    {
        TokenKind.Output => "output",
        TokenKind.Input => "input",
        TokenKind.CacheWrite => "cache write",
        TokenKind.CacheRead => "cache read",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a token kind."),
    };

    /// <summary>
    /// A kind's share of its own window, to two decimals.
    ///
    /// <para>Built rather than formatted with <c>"P2"</c>, which renders "1.08 %" — with a
    /// space — under the INVARIANT culture as well as most European ones. The panel states
    /// its own figures rather than inheriting the desktop's, and invariant alone does not get
    /// there here.</para>
    ///
    /// <para><b>Floored at <c>&lt;0.01%</c> rather than rounded to <c>0.00%</c>.</b> Input
    /// runs at 0.003% of a day; printing that as zero says it did not happen, and printing
    /// enough decimals to show it would set the column width for every other row.</para>
    /// </summary>
    public static string TokenShare(double share) => share switch
    {
        >= 1 => "100%",
        <= 0 => "0%",
        < 0.0001 => "<0.01%",
        _ => (share * 100).ToString("0.00", CultureInfo.InvariantCulture) + "%",
    };

    /// <summary>Scope words for a segment's hover card, naming which window it belongs to.</summary>
    public const string TokenWindowToday = "today";

    /// <inheritdoc cref="TokenWindowToday"/>
    public const string TokenWindow31Days = "the last 31 days";

    /// <summary>
    /// The identifying line under a segment card's token figure:
    /// <c>cache write · 10.30% of today · Est. $0.87</c>.
    ///
    /// <para>Built as one caption rather than as a third hover-card shape. The panel has two —
    /// a figure with a caption, and a sentence — and <c>HoverCard</c> says to reuse them; a
    /// per-kind card is a figure with a caption, so it is one.</para>
    ///
    /// <para><b>"Est." is never dropped here either.</b> A hover card is not an exemption from
    /// the rule the tiles follow, and this is the figure a reader is most likely to quote back
    /// as money spent. An unpriced window says so outright rather than showing <c>$0.00</c>,
    /// which would read as "this cost nothing" (rule 6).</para>
    ///
    /// <para>The window is named because <b>the two bars have measurably different shapes</b> —
    /// cache write ran 10.3% of a day against 0.93% of the month on the machine this was
    /// designed against — so a share with no window attached can be read against the wrong bar.</para>
    /// </summary>
    public static string TokenCardCaption(TokenKind kind, double share, decimal? estUsd, string window)
    {
        var value = estUsd is null ? "value unknown" : $"Est. {UsageFormatter.Usd(estUsd)}";
        return $"{TokenKindLabel(kind)} · {TokenShare(share)} of {window} · {value}";
    }

    /// <summary>Column headings for the breakdown view.</summary>
    public const string TokenBreakdownKindHeader = "Kind";

    /// <inheritdoc cref="TokenBreakdownKindHeader"/>
    public const string TokenBreakdownTodayHeader = "Today";

    /// <inheritdoc cref="TokenBreakdownKindHeader"/>
    public const string TokenBreakdown31DaysHeader = "31 days";

    /// <summary>
    /// Heading for a share column. <b>There is one per window, not one shared.</b> The two
    /// windows have measurably different shapes — cache write ran 10.3% of a day against
    /// 0.93% of the month on the machine this was designed against — so a single share column
    /// beside two token columns would be read against whichever one it sat nearer.
    /// </summary>
    public const string TokenBreakdownShareHeader = "Share";

    /// <inheritdoc cref="TokenBreakdownKindHeader"/>
    public const string TokenBreakdownTotalLabel = "Total";

    /// <summary>Labels for the two views of the composition.</summary>
    public const string TokenViewBarsLabel = "Bars";

    /// <inheritdoc cref="TokenViewBarsLabel"/>
    public const string TokenViewBreakdownLabel = "Breakdown";

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
