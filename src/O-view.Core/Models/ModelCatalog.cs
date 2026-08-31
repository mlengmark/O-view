using OView.Core.Pricing;

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
/// <param name="Rates">
/// Anthropic's published prices for this model, one field per published column. Dated by
/// <see cref="ModelCatalog.AsOf"/> rather than per row: the whole table is read from one page
/// on one day, and a per-row date would invite the reader to trust an old row that happened
/// not to be re-checked.
/// </param>
/// <param name="Billing">Whether this model draws from the plan or bills to credits.</param>
public sealed record ModelEntry(
    string Prefix,
    string DisplayName,
    ModelRates Rates,
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
/// <para><b>Rates</b> are Anthropic's published API rates, per million tokens, read on
/// <see cref="AsOf"/> — which is a <see cref="DateOnly"/> on <see cref="Bundled"/> rather than
/// a date in this comment, because nothing derived from a prose date can know the table is
/// ageing (GitHub issue #255). Every published column is stored; none is derived from another.
/// They price the "Est. value" figures, which are NOT money charged — within plan limits the
/// marginal cost is £0/$0 (CLAUDE.md rule 6). The structure is written down once, in
/// docs/reference/pricing.md.</para>
///
/// <para><b>An unrecognised model gets no row and therefore no guess.</b> It renders as its
/// raw id and is named in the "excludes … (no published rate)" caveat rather than being
/// priced from a pattern. A model Anthropic ships after this table was written is the
/// expected case, not an edge case — it has happened before, for <c>claude-opus-5</c>.</para>
/// </summary>
public static class ModelCatalog
{
    // Every column as Anthropic publishes it, in ModelRates' own order: base input, output,
    // 5-minute cache write, 1-hour cache write, cache hit. Named because ten models share five
    // rate rows, and a shared row is one place to correct rather than five places to correct
    // consistently. RecordShapeTests pins the order; the published table lists the columns in a
    // different one, and reading a row off the page positionally would swap the two writes.
    //
    // Declared BEFORE Entries deliberately: static field initialisers run in textual order, so
    // a rate row written below the table it feeds would initialise the table with nulls.
    private static readonly ModelRates Fable = new(10.00m, 50.00m, 12.50m, 20.00m, 1.00m);
    private static readonly ModelRates Opus = new(5.00m, 25.00m, 6.25m, 10.00m, 0.50m);
    private static readonly ModelRates Sonnet5 = new(2.00m, 10.00m, 2.50m, 4.00m, 0.20m);
    private static readonly ModelRates Sonnet4 = new(3.00m, 15.00m, 3.75m, 6.00m, 0.30m);
    private static readonly ModelRates Haiku = new(1.00m, 5.00m, 1.25m, 2.00m, 0.10m);

    /// <summary>
    /// Fast mode, a research preview available on Opus 5 and Opus 4.8 only.
    ///
    /// <para>Anthropic publishes its input and output prices in their own table and states that
    /// the prompt-caching multipliers apply on top of them, so the three cache columns here are
    /// that published rule applied to a published input rate — not a multiplier re-derived from
    /// a price we already hold. They come out identical to the Fable row, which shares the $10
    /// base input.</para>
    /// </summary>
    private static readonly ModelRates OpusFast = new(10.00m, 50.00m, 12.50m, 20.00m, 1.00m);

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
        new("claude-fable-5",  "Fable 5",   Fable,  BillingClass.Credit),
        new("claude-mythos-5", "Mythos 5",  Fable,  BillingClass.Credit),
        new("claude-opus-5",   "Opus 5",    Opus with { Fast = OpusFast }, BillingClass.Plan),
        new("claude-opus-4-8", "Opus 4.8",  Opus with { Fast = OpusFast }, BillingClass.Plan),
        // Fast mode is not available on Opus 4.7 — the request errors — so there is nothing
        // to price and no Fast row. Opus 4.6 accepts it, runs at standard speed and is
        // billed at standard rates, which is a published price rather than a fallback.
        new("claude-opus-4-7", "Opus 4.7",  Opus,   BillingClass.Plan),
        new("claude-opus-4-6", "Opus 4.6",  Opus with { Fast = Opus }, BillingClass.Plan),
        new("claude-opus-4-5", "Opus 4.5",  Opus,   BillingClass.Plan),
        new("claude-sonnet-5", "Sonnet 5",  Sonnet5, BillingClass.Plan),
        // Covers Sonnet 4, 4.5 and 4.6 — three models on one row, correct only because
        // all three share $3/$15. Load-bearing: longest-prefix matching will keep
        // resolving all of them here, so if one diverges it needs its own row rather
        // than a rate change here (GitHub issue #256).
        new("claude-sonnet-4", "Sonnet 4",  Sonnet4, BillingClass.Plan),
        new("claude-haiku-4-5", "Haiku 4.5", Haiku,  BillingClass.Plan),
    ];

    /// <summary>
    /// The day the table above was read from Anthropic's published pricing page.
    ///
    /// <para><b>Move this only when every row has actually been re-checked</b>, and in the same
    /// commit as any correction. A date advanced on its own says the table was verified when it
    /// was not, which is worse than an old date honestly stated.</para>
    /// </summary>
    public static readonly DateOnly AsOf = new(2026, 8, 31);

    /// <summary>
    /// The rate card in force: this table, its date, and its provenance, as one value that can
    /// be passed to <see cref="Pricing.CostEstimator"/> and named on screen.
    /// </summary>
    public static readonly RateCard Bundled = new(AsOf, RateCardSource.Bundled, Entries);

    /// <summary>
    /// The entry for a model id, or null when the model is unrecognised. Resolves through
    /// <see cref="Bundled"/> so there is exactly one prefix matcher — see
    /// <see cref="RateCard.Find"/> for why matching is by prefix length rather than by
    /// declaration order.
    /// </summary>
    public static ModelEntry? Find(string model) => Bundled.Find(model);

    /// <summary>Every entry in the given billing class, in table order.</summary>
    public static IReadOnlyList<ModelEntry> InClass(BillingClass billing) =>
        Entries.Where(e => e.Billing == billing).ToList();
}
