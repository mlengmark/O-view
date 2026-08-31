namespace OView.Core.Models;

/// <summary>
/// How a model's usage is billed on the tracked plan. Inferred by O-view, not stated by
/// any local data — see <see cref="ModelCatalog"/> for the evidence behind each
/// <see cref="Credit"/> classification.
/// </summary>
public enum BillingClass
{
    /// <summary>Draws from the plan's 5-hour and 7-day windows.</summary>
    Plan,

    /// <summary>Bills as extra usage (credits) instead of drawing from the plan window.</summary>
    Credit,
}

/// <summary>
/// Everything O-view knows about one model, in one row.
/// </summary>
/// <param name="Prefix">
/// Matched against the raw model id with <see cref="StringComparison.OrdinalIgnoreCase"/>.
/// Transcripts carry dated snapshots (<c>claude-opus-5-20260501</c>), so matching is by
/// prefix rather than by equality.
/// </param>
/// <param name="DisplayName">Friendly name for the UI. Never invented — see <see cref="ModelCatalog"/>.</param>
/// <param name="InputPerMTok">Published API rate per million input tokens.</param>
/// <param name="OutputPerMTok">Published API rate per million output tokens.</param>
/// <param name="Billing">Whether this model draws from the plan or bills to credits.</param>
public sealed record ModelEntry(
    string Prefix,
    string DisplayName,
    decimal InputPerMTok,
    decimal OutputPerMTok,
    BillingClass Billing);

/// <summary>
/// The single table of models O-view recognises: friendly name, published rate, and
/// billing class, one row per model.
///
/// <para>These three facts used to live in three separate tables — <c>CostEstimator.Rates</c>,
/// <c>ModelDisplayName.Names</c> and <c>CreditBilledModels.Prefixes</c> — all keyed on the
/// same prefix strings and all maintained by hand. Adding a model meant editing three files,
/// and each omission failed differently and silently: no friendly name (renders as a raw id),
/// no rate (excluded from Est. value), or misclassified as plan-billed. They had already
/// disagreed in a user-visible way: the credit-spend caption was a hand-written literal
/// naming one model while the classifier counted two, so a Mythos user saw their spend
/// summed under a note claiming only Fable was included (GitHub issue #56).</para>
///
/// <para><b>Rates</b> are Anthropic's published API rates as cached 2026-06-24, per million
/// tokens. They price the "Est. value" figures, which are NOT money charged — within plan
/// limits the marginal cost is £0/$0 (CLAUDE.md rule 6).</para>
///
/// <para><b>An unrecognised model gets no row and therefore no guess.</b> It renders as its
/// raw id and is named in the "excludes … (no published rate)" caveat rather than being
/// priced from a pattern. A model Anthropic ships after this table was written is the
/// expected case, not an edge case — it has happened before, for <c>claude-opus-5</c>.</para>
/// </summary>
public static class ModelCatalog
{
    /// <summary>
    /// Credit billing is a classification O-view <em>infers</em>; there is no per-request
    /// billing-tier field (<c>service_tier</c> is uniformly "standard", including on requests
    /// known to have billed to credits). So <see cref="BillingClass.Credit"/> is applied only
    /// on evidence:
    ///
    /// <list type="bullet">
    /// <item><c>claude-fable-5</c> — VERIFIED. On 2026-07-21, ~174K Fable output tokens moved
    /// the plan meter zero points while ~€86 of extra usage was billed; pricing Fable at
    /// published API rates matched that billing. See docs/findings/credit-usage-divergence.md.</item>
    /// <item><c>claude-mythos-5</c> — same tier, pricing and API surface as Fable (per the
    /// model catalog); included by parity, not by direct observation.</item>
    /// </list>
    ///
    /// <para>Consequences of this being a heuristic, stated so the UI can caveat honestly: it
    /// is account- and plan-specific, and it will MISS plan-tier usage (e.g. Opus) that goes
    /// off-plan once the plan limit is reached — that case is caught by the live divergence
    /// detector, not here. Mark a model <see cref="BillingClass.Credit"/> only when there is
    /// evidence it bills to credits on the target plan.</para>
    ///
    /// <para>Order is not significant: <see cref="Find"/> resolves by prefix length, so a new
    /// row cannot be shadowed by where it happens to sit in this list.</para>
    /// </summary>
    private static readonly ModelEntry[] Entries =
    [
        new("claude-fable-5",  "Fable 5",   10.00m, 50.00m, BillingClass.Credit),
        new("claude-mythos-5", "Mythos 5",  10.00m, 50.00m, BillingClass.Credit),
        new("claude-opus-5",   "Opus 5",     5.00m, 25.00m, BillingClass.Plan),
        new("claude-opus-4-8", "Opus 4.8",   5.00m, 25.00m, BillingClass.Plan),
        new("claude-opus-4-7", "Opus 4.7",   5.00m, 25.00m, BillingClass.Plan),
        new("claude-opus-4-6", "Opus 4.6",   5.00m, 25.00m, BillingClass.Plan),
        new("claude-opus-4-5", "Opus 4.5",   5.00m, 25.00m, BillingClass.Plan),
        new("claude-sonnet-5", "Sonnet 5",   2.00m, 10.00m, BillingClass.Plan),
        // Covers Sonnet 4, 4.5 and 4.6 — three models on one row, correct only because
        // all three share $3/$15. Load-bearing: longest-prefix matching will keep
        // resolving all of them here, so if one diverges it needs its own row rather
        // than a rate change here (GitHub issue #256).
        new("claude-sonnet-4", "Sonnet 4",   3.00m, 15.00m, BillingClass.Plan),
        new("claude-haiku-4-5", "Haiku 4.5", 1.00m,  5.00m, BillingClass.Plan),
    ];

    /// <summary>
    /// The entry for a model id, or null when the model is unrecognised.
    ///
    /// <para>Longest prefix wins, decided by prefix <em>length</em> rather than by declaration
    /// order. The three predecessor tables each relied on being written "most-specific first"
    /// and matched with <c>FirstOrDefault</c>, which made ordering load-bearing without saying
    /// so — a row inserted in the wrong place would have silently shadowed a more specific
    /// one.</para>
    /// </summary>
    public static ModelEntry? Find(string model) =>
        Entries
            .Where(e => model.StartsWith(e.Prefix, StringComparison.OrdinalIgnoreCase))
            .MaxBy(e => e.Prefix.Length);

    /// <summary>Every entry in the given billing class, in table order.</summary>
    public static IReadOnlyList<ModelEntry> InClass(BillingClass billing) =>
        Entries.Where(e => e.Billing == billing).ToList();
}
