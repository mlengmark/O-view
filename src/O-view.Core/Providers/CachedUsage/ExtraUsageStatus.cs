using System.Text.Json;

namespace OView.Core.Providers.CachedUsage;

/// <summary>
/// Whether work past the plan allowance bills as extra usage on this account.
///
/// <para>Three values, and <see cref="Unknown"/> is not a rounding of the other two. O-view
/// reads this from another application's cache, so "the file did not say" is a state that
/// happens routinely — a machine with no Claude Code, a block that predates the field, a
/// value of a shape this does not recognise. Collapsing it into <see cref="Disabled"/> would
/// tell a user with auto-billing switched on that they are safe, and into
/// <see cref="Enabled"/> would restore exactly the false alarm issue #259 is about.</para>
/// </summary>
public enum ExtraUsageState
{
    /// <summary>Not readable: no block, no field, or a value this does not understand.</summary>
    Unknown,

    /// <summary>Extra usage is off, so work past the plan window does not bill beyond it.</summary>
    Disabled,

    /// <summary>Extra usage is on, so work past the plan window can bill beyond it.</summary>
    Enabled,
}

/// <summary>
/// The account's extra-usage (auto-billing) setting, as Claude Code cached it in
/// <c>~/.claude.json</c> → <c>cachedUsageUtilization.utilization.extra_usage</c>.
///
/// <para><b>Why this is worth reading.</b> O-view's off-plan banner asserted "usage is billing
/// beyond your plan" at every user whose 5-hour window was exhausted, including the ones who
/// had switched extra usage off and therefore could not be billed a penny (issue #259). The
/// panel's own comments already recorded that Claude Code knows the answer and named the field
/// — <c>extra_usage.user_disabled</c> — while the wording went on hedging around it. It is a
/// local file, read-only, no token and no network: the same terms as every other source
/// (CLAUDE.md rule 3).</para>
///
/// <para><b>Measured 2026-09-01</b> on a <c>claude_pro</c> account, against a block fetched
/// three minutes earlier:</para>
/// <code>
/// "extra_usage": { "is_enabled": false, "monthly_limit": null, "used_credits": null,
///                  "utilization": null, "currency": null, "decimal_places": null,
///                  "disabled_reason": null, "user_disabled": true,
///                  "spend_limit_reached": false, "credits_ever_enabled": true,
///                  "daily": null, "weekly": null }
/// </code>
///
/// <para><b>This contradicts what the findings recorded</b>, and the correction is the point:
/// <c>cli-usage-refresh.md</c> §4 listed <c>extra_usage</c> among the fields that are "empty
/// even when fresh". It is not empty — four of its booleans carry real values. What is null is
/// the <i>money</i> half (limit, credits used, currency), which is consistent with an account
/// that has extra usage switched off and has therefore spent nothing.</para>
///
/// <para><b>Only <c>is_enabled</c> decides the state.</b> It is the resolved answer; the other
/// three are the reason behind it and are carried for diagnostics, not branched on.
/// <c>spend_limit_reached</c> in particular looks like it should qualify an enabled account
/// whose cap is spent — but nothing has been observed with it true, so treating it as a second
/// off switch would be reasoning from a field name (rule 6).</para>
/// </summary>
/// <param name="State">The resolved answer, from <c>is_enabled</c>.</param>
/// <param name="UserDisabled">
/// <c>user_disabled</c> — the person turned it off, as opposed to it being unavailable to them.
/// </param>
/// <param name="SpendLimitReached">
/// <c>spend_limit_reached</c> — read and reported, never branched on. See the remarks above.
/// </param>
/// <param name="DisabledReason">
/// <c>disabled_reason</c> — a string when the account is barred from extra usage for some
/// reason other than the user's own choice. Null on every account observed so far.
/// </param>
public sealed record ExtraUsageStatus(
    ExtraUsageState State,
    bool UserDisabled = false,
    bool SpendLimitReached = false,
    string? DisabledReason = null)
{
    /// <summary>The state of a machine that told us nothing — the default everywhere.</summary>
    public static readonly ExtraUsageStatus Unknown = new(ExtraUsageState.Unknown);

    /// <summary>Property inside <c>utilization</c> that holds the block.</summary>
    public const string PropertyName = "extra_usage";

    /// <summary>
    /// Reads the block out of a <c>utilization</c> object, or <see cref="Unknown"/> when it is
    /// absent, null, or carries no boolean <c>is_enabled</c>.
    ///
    /// <para>Null is the documented normal state of most of this object's siblings
    /// (<c>seven_day_opus</c> and the rest), so it is handled as ordinary rather than as an
    /// error — and a non-boolean <c>is_enabled</c> is treated the same way, because this is
    /// another application's private cache and it has already gained and lost keys.</para>
    /// </summary>
    public static ExtraUsageStatus Read(JsonElement utilization)
    {
        if (utilization.ValueKind != JsonValueKind.Object ||
            !utilization.TryGetProperty(PropertyName, out var extra) ||
            extra.ValueKind != JsonValueKind.Object ||
            !extra.TryGetProperty("is_enabled", out var enabled) ||
            enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return Unknown;
        }

        return new ExtraUsageStatus(
            enabled.ValueKind == JsonValueKind.True ? ExtraUsageState.Enabled : ExtraUsageState.Disabled,
            Flag(extra, "user_disabled"),
            Flag(extra, "spend_limit_reached"),
            Text(extra, "disabled_reason"));
    }

    /// <summary>A boolean that is false unless the file actually says true.</summary>
    private static bool Flag(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String &&
        v.GetString() is { Length: > 0 } s
            ? s
            : null;
}
