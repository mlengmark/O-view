using System.Text.Json;
using OView.Core.Models;

namespace OView.Core.Providers.CachedUsage;

/// <summary>
/// One promo notice as Claude Code caches it, with whatever the sentence yielded.
/// </summary>
/// <param name="Bar">
/// The meter the notice belongs to — the same key space as
/// <c>cachedUsageUtilization.utilization</c>, so <c>seven_day</c> is the weekly gauge.
/// <b>A notice is never shown against a bar it did not name.</b>
/// </param>
/// <param name="Text">The message, exactly as written. Always displayed unaltered.</param>
/// <param name="Percent">How much bigger the limit is, or null — see <see cref="PromoText.Percent"/>.</param>
/// <param name="EndsOn">The promo's last day, or null — see <see cref="PromoText.EndDate"/>.</param>
public sealed record BoostNotice(string Bar, string Text, int? Percent, DateOnly? EndsOn)
{
    /// <summary>The weekly meter's key, and the only bar any notice has ever named.</summary>
    public const string WeeklyBar = "seven_day";

    /// <summary>The five-hour meter's key.</summary>
    public const string SessionBar = "five_hour";

    /// <summary>
    /// Whether the promo's last day is behind us. False when no date resolved — an unknown
    /// expiry is not an expiry, and that case is handled by the freshness backstop instead
    /// (<see cref="BoostNotices.UndatedFreshness"/>).
    /// </summary>
    public bool HasExpired(DateOnly today) => EndsOn is { } last && today > last;
}

/// <summary>
/// The promo notices Claude Code caches in <c>~/.claude.json</c> →
/// <c>cachedGrowthBookFeatures.tengu_rate_limit_promo_notices</c>, and the timestamp that says
/// how fresh they are.
///
/// <para>This is what Claude Code renders beside its own usage bars. Reading it costs nothing new:
/// the same file already supplies the account badge (<see cref="ClaudeAccount"/>) and the
/// percentages (<see cref="CachedUtilization"/>), it is read-only, and no credential or network
/// call is involved (CLAUDE.md rule 3).</para>
///
/// <para><b>Freshness comes from its own key, and must not be taken from the usage block's.</b>
/// <c>cachedGrowthBookFeaturesAt</c> is refreshed when Claude Code <i>starts</i>;
/// <c>cachedUsageUtilization.fetchedAtMs</c> only when <c>/usage</c> runs. Measured 2026-08-31 on
/// the development machine, 08:24:24Z against 08:19:30Z — close that morning, but driven by
/// different triggers, so a notice can be current beside a percentage that is days old. Labelling
/// either from the other's timestamp would be a fabricated claim about age.</para>
///
/// <para><b>This is a feature-flag cache, not an entitlement.</b> Whether the payload is targeted
/// at this account is unproven — the evaluation happens server-side and "consistent with
/// targeting" is not an observation. So the panel relays it, attributed and dated, and never
/// states that the user's limits are boosted in O-view's own voice.</para>
///
/// <para><b>Do not write to this file.</b> It belongs to Claude Code, and rule 3's read-only rule
/// covers it exactly as it covers Claude Desktop's.</para>
/// </summary>
/// <param name="FetchedAtUtc">When Claude Code last refreshed the flag cache.</param>
/// <param name="Items">Every notice in the array, in file order.</param>
public sealed record BoostNotices(DateTimeOffset FetchedAtUtc, IReadOnlyList<BoostNotice> Items)
{
    /// <summary>The feature-flag cache, a top-level object.</summary>
    public const string FeaturesProperty = "cachedGrowthBookFeatures";

    /// <summary>The notice array inside it.</summary>
    public const string NoticesProperty = "tengu_rate_limit_promo_notices";

    /// <summary>The flag cache's own fetch time, a sibling of the cache rather than a member.</summary>
    public const string FetchedAtProperty = "cachedGrowthBookFeaturesAt";

    /// <summary>
    /// How old the flag cache may be before a notice <b>with no end date</b> stops being shown.
    ///
    /// <para><b>Not measured, and deliberately loose.</b> It applies only to the case the date
    /// check cannot cover: a sentence with no readable date, where the sole remaining evidence
    /// that a promo is still running is that Claude Code recently agreed it was. The flag cache
    /// refreshes on Claude Code startup, so a threshold of minutes would hide the notice on any
    /// machine where nobody had opened Claude Code that morning — which may be most of them.
    /// A day is long enough not to do that and short enough that a machine which has stopped
    /// running Claude Code altogether stops making the claim.</para>
    ///
    /// <para>A <i>dated</i> notice ignores this entirely: it is checked against the clock, which
    /// is evidence about the promo rather than about our reader. Logging real
    /// <see cref="FetchedAtUtc"/> ages across ordinary use is what should replace this number
    /// (issue #254).</para>
    /// </summary>
    public static readonly TimeSpan UndatedFreshness = TimeSpan.FromDays(1);

    /// <summary>
    /// Where to look — the same candidates as the account badge and the usage cache, so the
    /// three can never disagree about which <c>.claude.json</c> is in effect, and so this
    /// follows <c>CLAUDE_CONFIG_DIR</c> for free.
    /// </summary>
    public static IReadOnlyList<string> Candidates() => ClaudeAccount.Candidates();

    /// <summary>
    /// The <b>most recently fetched</b> set across the candidates; null when no file carries one.
    /// Never throws.
    ///
    /// <para>Not the first file that <i>exists</i>, for the reason
    /// <see cref="CachedUtilization.TryReadAny"/> sets out at length: Claude Code's state
    /// migration leaves a stub behind, and a stub resolved by existence beats a populated file.
    /// Measured 2026-08-31 — <c>~/.claude.json</c> carried the array and
    /// <c>~/.claude/.claude.json</c> carried neither it nor the timestamp.</para>
    /// </summary>
    public static BoostNotices? TryRead(string? path = null) =>
        TryReadAny(path is null ? Candidates() : [path]);

    /// <summary>
    /// The selection rule over an explicit candidate list, testable without a real profile.
    /// </summary>
    public static BoostNotices? TryReadAny(IReadOnlyList<string> candidates)
    {
        BoostNotices? newest = null;

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
                // than a claim this cannot stand behind.
            }
        }

        return newest;
    }

    /// <summary>
    /// Parses the notices out of a <c>.claude.json</c> document, or null when the block or its
    /// timestamp is absent.
    ///
    /// <para>The timestamp is required for the same reason
    /// <see cref="CachedUtilization.Parse"/> requires its own: an undated claim of unknown age is
    /// exactly the confidently-wrong statement rule 6 exists to prevent. Every other field is
    /// optional — this is another application's private feature-flag cache, it carries no
    /// stability contract, and it has already gained and lost keys.</para>
    /// </summary>
    public static BoostNotices? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(FetchedAtProperty, out var fetched) ||
            !fetched.TryGetInt64(out var fetchedMs))
        {
            return null;
        }

        var at = DateTimeOffset.FromUnixTimeMilliseconds(fetchedMs);
        var notices = new List<BoostNotice>();

        if (root.TryGetProperty(FeaturesProperty, out var features) &&
            features.ValueKind == JsonValueKind.Object &&
            features.TryGetProperty(NoticesProperty, out var array) &&
            array.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in array.EnumerateArray())
            {
                if (ReadNotice(element, at) is { } notice)
                {
                    notices.Add(notice);
                }
            }
        }

        return new BoostNotices(at, notices);
    }

    /// <summary>
    /// One array entry. Both <c>bar</c> and <c>text</c> are required — a notice with no message
    /// has nothing to relay, and one with no bar has nowhere to go, and guessing "weekly"
    /// would attach a claim to a meter the source never named.
    /// </summary>
    private static BoostNotice? ReadNotice(JsonElement element, DateTimeOffset anchorUtc)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            ReadString(element, "bar") is not { } bar ||
            ReadString(element, "text") is not { } text)
        {
            return null;
        }

        return new BoostNotice(bar, text, PromoText.Percent(text), PromoText.EndDate(text, anchorUtc));
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String &&
        v.GetString() is { Length: > 0 } s
            ? s
            : null;

    /// <summary>
    /// The notice to show against one meter, or null when there is none to show.
    ///
    /// <para>Three ways there is nothing, and each is a deliberate silence rather than an
    /// oversight:</para>
    ///
    /// <list type="number">
    /// <item><b>No notice names this bar.</b> Includes every bar key not recognised here —
    /// model-scoped forms such as <c>seven_day_opus</c> among them. None has ever been observed
    /// populated, and rendering one against the weekly gauge would attach a model's promo to an
    /// account-wide meter.</item>
    /// <item><b>The promo's last day has passed</b>, by the machine's own calendar.</item>
    /// <item><b>No date resolved and the flag cache has gone stale.</b> Nothing left says the
    /// promo is still running — see <see cref="UndatedFreshness"/>.</item>
    /// </list>
    /// </summary>
    /// <param name="bar">The meter's key, e.g. <see cref="BoostNotice.WeeklyBar"/>.</param>
    /// <param name="today">The reader's local date, which is what "has it ended" means to them.</param>
    /// <param name="utcNow">Now, for the staleness backstop.</param>
    public BoostNotice? For(string bar, DateOnly today, DateTimeOffset utcNow)
    {
        foreach (var notice in Items)
        {
            if (!string.Equals(notice.Bar, bar, StringComparison.Ordinal) ||
                notice.HasExpired(today))
            {
                continue;
            }

            if (notice.EndsOn is null && utcNow - FetchedAtUtc > UndatedFreshness)
            {
                continue;
            }

            return notice;
        }

        return null;
    }
}
