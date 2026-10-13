using System.Globalization;
using OView.Core.Models;
using OView.Core.Providers.CachedUsage;

namespace OView.Presentation.Text;

/// <summary>
/// The off-plan banner's heading and detail: what the divergence banner says when local work
/// is running past a meter that is not moving, or past the window entirely (see
/// <see cref="PanelText"/> for why wording like this is shared rather than written per
/// platform).
/// </summary>
public static class OffPlanText
{
    /// <summary>U+00A0, as an escape — the literal character is invisible in source.</summary>
    private const char NonBreakingSpace = ' ';

    /// <summary>
    /// Human-readable list of the credit-billed models, for <see cref="OffPlanHint"/>'s
    /// caption. Built from <see cref="CreditBilledModels.Models"/> — the same entries
    /// <see cref="CreditBilledModels.IsCreditBilled"/> matches, so the caption can never
    /// name a different set than the one being summed (GitHub issue #56).
    ///
    /// <para>Spaces inside a name are non-breaking, so a model is never split across a
    /// line. The caption sits in a wrapping run of prose whose entire job is stating
    /// precisely which models are counted, and "Fable / 5, Mythos 5" broken over two lines
    /// reads as three entries rather than two.</para>
    ///
    /// <para><b>Moved here from Core (OVIAA-39).</b> The join and the non-breaking-space
    /// substitution are panel formatting; <see cref="CreditBilledModels"/> keeps only the
    /// membership test and the entries themselves.</para>
    /// </summary>
    public static string CreditModelsCaption { get; } = string.Join(
        ", ", CreditBilledModels.Models.Select(e => e.DisplayName.Replace(' ', NonBreakingSpace)));

    // Token formatting lives in UsageFormatter, right beside this class. The sentence below
    // that embeds a figure still duplicates its one-line rule rather than call it directly.
    private static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{tokens / 1_000_000.0:0.0}M"),
        >= 1_000 => string.Create(CultureInfo.InvariantCulture, $"{tokens / 1_000.0:0.0}K"),
        _ => tokens.ToString(CultureInfo.InvariantCulture),
    };

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
        $"About {FormatTokens(outputTokens)} output tokens ran in this window while the "
        + $"plan meter moved {risePoints} point{(risePoints == 1 ? "" : "s")}. Usage that a plan "
        + "meter does not account for is billed some other way — O-view cannot see your billing, "
        + "so check Settings → Usage in Claude for what it was.";

    /// <inheritdoc cref="DivergenceDetail"/>
    ///
    /// <para>What is left once the extra-usage sentence is removed: the observation on its own,
    /// used when Claude Code's cache does not say which way the account is set.</para>
    public const string PlanLimitReachedDetail =
        "The 5-hour window is exhausted, so continued work is not drawing from it. Whether that "
        + "bills as extra usage depends on your account settings, which O-view could not read.";

    /// <summary>
    /// Claude's own usage settings page, which is where every wording below sends the reader
    /// for the figure O-view will not state. Taken from Claude Code's own binary rather than
    /// guessed — it is the link Claude Code itself uses for this screen.
    /// </summary>
    public const string UsageSettingsUrl = "https://claude.ai/settings/usage";

    /// <summary>Label on the link to <see cref="UsageSettingsUrl"/>.</summary>
    public const string UsageSettingsLinkLabel = "Open usage settings in Claude";

    /// <summary>
    /// The off-plan banner's heading.
    ///
    /// <para><b>The exhausted-window heading used to assert a charge</b> — "Plan limit reached
    /// — usage is billing beyond your plan" — at every reader whose 5-hour window had run out,
    /// including the ones who had switched extra usage off and could not be billed anything
    /// (issue #259). It read as an emergency, and for that population it was not merely
    /// alarming but false.</para>
    ///
    /// <para>So the claim is now made only where the account setting supports it, and the
    /// three states are three headings rather than one hedge. Unknown gets the observation
    /// alone: the window is spent, which is true regardless, and the detail line underneath
    /// says why O-view will not go further.</para>
    ///
    /// <para>The diverging heading never asserted a charge and is unchanged. It describes a
    /// meter that did not move, and the cause may not be billing at all — a Cowork session
    /// running in a cloud container leaves no local record either (rule 9).</para>
    /// </summary>
    public static string OffPlanTitle(DivergenceState state, ExtraUsageState extraUsage) =>
        state == DivergenceState.PlanLimitReached
            ? extraUsage switch
            {
                ExtraUsageState.Enabled => "Plan limit reached — further work bills as extra usage",
                ExtraUsageState.Disabled => "Plan limit reached — extra usage is switched off",
                _ => "Plan limit reached",
            }
            : "This session's usage is not drawing from your plan";

    /// <summary>
    /// The line under <see cref="OffPlanTitle"/>: the observation, then what Claude Code says
    /// about extra usage on this account, then when it said it.
    ///
    /// <para><b>Relayed and stamped, never asserted in O-view's own voice.</b> This is a cached
    /// answer from another application, and Claude Code refreshes that cache only when
    /// <c>/usage</c> runs — so it can be days old while looking exactly as current as a fresh
    /// one. The provenance clause is what makes reporting it honest rather than a guess wearing
    /// a fact's clothes, and it is the same treatment, for the same reason, that
    /// <see cref="BoostText.BoostCard"/> gives the promo notice (rule 6).</para>
    ///
    /// <para><b>The stamp carries a date whenever the reading is not from today.</b> A bare
    /// <c>09:07</c> on a four-day-old block reads as this morning, which would hide the one
    /// thing the clause exists to disclose.</para>
    /// </summary>
    /// <param name="fetchedAtUtc">
    /// When Claude Code last refreshed the block. Null when <paramref name="extraUsage"/> came
    /// from nowhere — in which case there is nothing to stamp and nothing is claimed.
    /// </param>
    public static string OffPlanDetail(
        DivergenceResult divergence,
        ExtraUsageState extraUsage,
        DateTimeOffset? fetchedAtUtc,
        DateTimeOffset utcNow,
        TimeZoneInfo local)
    {
        var observation = divergence.State == DivergenceState.PlanLimitReached
            ? "The 5-hour window is exhausted, so continued work is not drawing from it."
            : DivergenceDetail(divergence.OutputTokensInWindow, divergence.PlanRisePoints);

        if (extraUsage == ExtraUsageState.Unknown || fetchedAtUtc is not { } fetched)
        {
            return divergence.State == DivergenceState.PlanLimitReached
                ? PlanLimitReachedDetail
                : observation;
        }

        var setting = extraUsage == ExtraUsageState.Enabled
            ? "Extra usage is switched on for this account, so work past the plan allowance "
              + "can bill beyond it."
            : "Extra usage is switched off for this account, so work past the plan allowance "
              + "should not bill beyond it.";

        return $"{observation} {setting} ({ReadStamp(fetched, utcNow, local)})";
    }

    /// <summary>
    /// <c>reported by Claude Code, read 09:07</c>, or <c>… read 28 Aug 12:50</c> once the
    /// reading is no longer from the reader's own today. See <see cref="OffPlanDetail"/>.
    /// </summary>
    internal static string ReadStamp(
        DateTimeOffset fetchedAtUtc, DateTimeOffset utcNow, TimeZoneInfo local)
    {
        var read = TimeZoneInfo.ConvertTime(fetchedAtUtc, local);
        var today = TimeZoneInfo.ConvertTime(utcNow, local).Date;

        return read.Date == today
            ? string.Create(CultureInfo.InvariantCulture, $"reported by Claude Code, read {read:HH:mm}")
            : string.Create(CultureInfo.InvariantCulture, $"reported by Claude Code, read {read:d MMM HH:mm}");
    }

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
        ? $"Estimated at published API rates for models billed as extra usage ({CreditModelsCaption}). "
          + "O-view cannot read your credit balance; check your billing page for exact figures."
        : $"No credit-billed usage ({CreditModelsCaption}) recorded in the last 31 days. "
          + "Off-plan usage while O-view wasn't running isn't captured.";
}
