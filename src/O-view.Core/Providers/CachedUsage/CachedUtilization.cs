using System.Globalization;
using System.Text.Json;
using OView.Core.Models;

namespace OView.Core.Providers.CachedUsage;

/// <summary>One meter: a percentage, and when the window behind it rolls over.</summary>
/// <param name="Percent">Utilisation 0–100, as Claude Code received it.</param>
/// <param name="ResetsAtUtc">
/// Exact reset instant, or null when this bar carries none. <b>Reported, not derived</b> —
/// which is what makes it worth more than everything else O-view knows about reset times.
/// </param>
public sealed record UtilizationBar(int Percent, DateTimeOffset? ResetsAtUtc);

/// <summary>
/// The usage figures Claude Code caches in <c>~/.claude.json</c> → <c>cachedUsageUtilization</c>.
///
/// <para>This is the same data <c>/status</c> → Usage renders, written to disk as a side effect
/// of fetching it: session and weekly percentages <b>and exact reset timestamps</b>, for anyone
/// who has run Claude Code — CLI or hosted in Desktop. No token, no network, no credential
/// handling (CLAUDE.md rule 3): a local JSON file, read-only, like the plan-history file.</para>
///
/// <para><b>Why this matters more than the percentages alone.</b> Every other reset time in
/// O-view is <i>inferred</i> from a drop in a sampled series, so its precision is capped by the
/// sampling gap — about half an interval for the five-hour window, and roughly ten hours for the
/// weekly one, whose resets land overnight while Desktop is closed (ADR-0011). These timestamps
/// are reported by the source. They are exact, and they are the only exact reset times O-view
/// has ever had.</para>
///
/// <para>Verified against a user's <c>/status</c> screen: <c>five_hour.resets_at</c> and
/// <c>seven_day.resets_at</c> converted to the local zone matched the two times it displayed.</para>
///
/// <para><b>Do not write to this file.</b> It belongs to Claude Code, it sits beside
/// <c>.credentials.json</c>, and rule 3's read-only rule covers it exactly as it covers Claude
/// Desktop's.</para>
/// </summary>
/// <param name="FetchedAtUtc">
/// When Claude Code last refreshed the figures. Drives the staleness label, and is the only
/// honest basis for one: this is a cache refreshed when Claude Code runs, not a sampler.
/// </param>
/// <param name="AccountUuid">
/// Account the figures belong to. Note this is the <i>account</i> uuid, which is not the
/// organization uuid the plan-history file keys on — they are different identifiers on the same
/// machine, so do not match one against the other.
/// </param>
/// <param name="FiveHour">The rolling five-hour session meter.</param>
/// <param name="SevenDay">The seven-day weekly meter.</param>
public sealed record CachedUtilization(
    DateTimeOffset FetchedAtUtc,
    string? AccountUuid,
    UtilizationBar? FiveHour,
    UtilizationBar? SevenDay)
{
    /// <summary>Top-level property holding the block. Not inside <c>clientDataCacheSlots</c>.</summary>
    public const string PropertyName = "cachedUsageUtilization";

    /// <summary>
    /// Whether work past the plan allowance bills as extra usage on this account
    /// (issue #259). <see cref="ExtraUsageState.Unknown"/> when the block does not say.
    ///
    /// <para>An <c>init</c> property rather than a sixth positional parameter, deliberately.
    /// The four above are a fixed positional list that a new entry in the middle would
    /// silently re-point — the hazard issue #248 hit on <see cref="UsageSnapshot"/>, whose
    /// trailing arguments are named from that point on for the same reason.</para>
    ///
    /// <para><b>Not gated on freshness here.</b> A percentage stops describing anything once
    /// its window rolls over, which is why <see cref="CachedUtilizationProvider"/> discards
    /// one; an account setting does not roll over, it is simply older or newer. It is dated
    /// by <see cref="FetchedAtUtc"/> and the panel says when it was read rather than deciding
    /// on the reader's behalf that an hour-old answer is no answer.</para>
    /// </summary>
    public ExtraUsageStatus ExtraUsage { get; init; } = ExtraUsageStatus.Unknown;

    /// <summary>
    /// Where to look — the same candidates as the account badge, so the two can never disagree
    /// about which <c>.claude.json</c> is in effect, and so this follows
    /// <c>CLAUDE_CONFIG_DIR</c> for free (<see cref="ClaudeAccount.Candidates"/>).
    /// </summary>
    public static IReadOnlyList<string> Candidates() => ClaudeAccount.Candidates();

    /// <summary>
    /// The <b>most recently fetched</b> block across the candidates; null when no file has one.
    /// Never throws.
    ///
    /// <para>Not merely the first file that <i>exists</i>: a relocated configuration can leave a
    /// stub behind, and a file without the block is no better than a missing one here.</para>
    ///
    /// <para><b>And not merely the first that has one.</b> When Claude Code migrated its state
    /// into <c>~/.claude/.claude.json</c> it stopped writing the old file, which kept a block
    /// that was hours old and looked exactly as valid as a live one. List order decides nothing
    /// here for the same reason it decides nothing between providers (issue #191): the
    /// candidates are two locations for one file, so the freshest reading is the reading.</para>
    /// </summary>
    public static CachedUtilization? TryRead(string? path = null) =>
        TryReadAny(path is null ? Candidates() : [path]);

    /// <summary>
    /// The selection rule over an explicit candidate list, testable without a real profile —
    /// see <see cref="ClaudeAccount.TryReadAny"/> for why that separation exists.
    /// </summary>
    public static CachedUtilization? TryReadAny(IReadOnlyList<string> candidates)
    {
        CachedUtilization? newest = null;

        foreach (var candidate in candidates)
        {
            try
            {
                if (Parse(File.ReadAllText(candidate)) is { } parsed &&
                    (newest is null || parsed.FetchedAtUtc > newest.FetchedAtUtc))
                {
                    newest = parsed;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Unreadable or malformed: try the next candidate, and report nothing rather
                // than a number this cannot stand behind.
            }
        }

        return newest;
    }

    /// <summary>
    /// Parses the block out of a <c>.claude.json</c> document, or null when it is absent or
    /// carries no usable timestamp. Every field is treated as optional (repo convention): this
    /// is another application's private cache, and it has already gained and lost keys.
    /// </summary>
    public static CachedUtilization? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty(PropertyName, out var block) ||
            block.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // No fetch time means no way to say how old the figures are, and an unlabelled
        // percentage of unknown age is precisely the confident-but-wrong reading rule 6
        // exists to prevent. Refuse the whole block rather than show it undated.
        if (!block.TryGetProperty("fetchedAtMs", out var fetched) ||
            !fetched.TryGetInt64(out var fetchedMs))
        {
            return null;
        }

        block.TryGetProperty("utilization", out var bars);

        return new CachedUtilization(
            DateTimeOffset.FromUnixTimeMilliseconds(fetchedMs),
            ReadString(block, "accountUuid"),
            ReadBar(bars, "five_hour"),
            ReadBar(bars, "seven_day"))
        {
            ExtraUsage = ExtraUsageStatus.Read(bars),
        };
    }

    /// <summary>
    /// One bar, or null when absent or explicitly null — which is the normal state of most of
    /// them (<c>seven_day_opus</c>, <c>seven_day_cowork</c> and the rest read null on a plan
    /// that has no separate meter for them).
    /// </summary>
    private static UtilizationBar? ReadBar(JsonElement bars, string name)
    {
        if (bars.ValueKind != JsonValueKind.Object ||
            !bars.TryGetProperty(name, out var bar) ||
            bar.ValueKind != JsonValueKind.Object ||
            !bar.TryGetProperty("utilization", out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out var percent))
        {
            return null;
        }

        return new UtilizationBar((int)Math.Round(percent), ReadResetAt(bar));
    }

    /// <summary>
    /// The reset instant, normalised to UTC. The source writes an offset-qualified timestamp
    /// (<c>2026-08-24T00:00:00.046735+00:00</c>), so parse it as one and convert — never
    /// <see cref="DateTime"/>, whose Kind would be lost on the way through.
    /// </summary>
    private static DateTimeOffset? ReadResetAt(JsonElement bar) =>
        bar.TryGetProperty("resets_at", out var resets) &&
        resets.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(resets.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String &&
        v.GetString() is { Length: > 0 } s
            ? s
            : null;
}
