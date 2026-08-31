using OView.Core.Models;

namespace OView.Core.Pricing;

/// <summary>
/// Prices tokens at published API rates to produce the "Est. value" figures. This is
/// NOT money charged — within plan limits the marginal cost is £0/$0 (CLAUDE.md
/// rule 6; ui-spec.md). The UI must always prefix these figures "Est."
///
/// <para><b>Every price is read from a <see cref="RateCard"/>; none is computed from
/// another.</b> This class used to hold two <c>private const</c> multipliers, and one of
/// them — <c>CacheWriteMultiplier = 1.25m</c> — stood in for two different published prices.
/// Anthropic bills a 5-minute cache write at 1.25× base input and a 1-hour write at 2×, and
/// the transcripts on the machine this was measured on were almost entirely 1-hour, so
/// cache-write value understated by 37.5% of its true amount for as long as the constant
/// existed (GitHub issue #255). The rate structure is written down in
/// docs/reference/pricing.md; the rows live in <see cref="ModelCatalog"/>.</para>
///
/// <para><b>Unknown fails to unknown, never to a default.</b> An unrecognised model, an
/// unrecognised modifier value, and fast mode on a model with no published fast row all
/// return null, and the caller labels it (<see cref="PanelStatistics.UnpricedModels"/>).
/// Falling back to standard rates in any of those cases would put a confident cheaper number
/// on screen, which is the failure this whole design is against.</para>
/// </summary>
public static class CostEstimator
{
    private const decimal MTok = 1_000_000m;

    /// <summary>
    /// Estimated USD value of the given tokens; null when the rates cannot be established
    /// (the caller labels it, never guessing a rate).
    ///
    /// <para>There is deliberately no <c>&lt;synthetic&gt;</c> special case here. Claude
    /// Code's marker for locally generated messages is dropped at parse time by
    /// <see cref="Providers.Jsonl.TranscriptReader"/>, so it never reaches the store and
    /// therefore never reaches this method. The branch that used to sit here was
    /// unreachable, and disagreed with the reader on case sensitivity (issue #57).</para>
    /// </summary>
    /// <param name="card">
    /// The rates to price against. Defaults to the bundled table, which is the only source
    /// that exists today; passed explicitly so a test can price against a card it wrote
    /// rather than against whatever this build happens to ship.
    /// </param>
    public static decimal? EstimateUsd(
        string model,
        TokenSplit tokens,
        UsageModifiers modifiers = default,
        RateCard? card = null)
    {
        card ??= ModelCatalog.Bundled;

        if (card.RatesFor(model, modifiers) is not { } rates)
        {
            return null;
        }

        var usd = Value(rates, tokens);

        // US-pinned inference is genuinely a multiplier rather than a rate row: Anthropic
        // publishes it as 1.1× applied to every category, so it is stored that way.
        return modifiers.UsInference ? usd * card.UsInferenceMultiplier : usd;
    }

    /// <summary>
    /// One rate row applied to one token split. The single place the six columns are
    /// multiplied out — <see cref="RelativeError"/> has to price exactly what
    /// <see cref="EstimateUsd"/> prices, or the calibration measures the difference between
    /// two implementations rather than between O-view and a reported figure.
    ///
    /// <para>The TTL-unrecorded bucket takes the 5-minute rate. It is the cheaper of the two
    /// write prices, so this understates rather than overstates — and the panel names the
    /// assumption rather than letting the figure stand on its own (issue #255).</para>
    /// </summary>
    private static decimal Value(ModelRates rates, TokenSplit tokens) =>
        (tokens.Input * rates.InputPerMTok
         + tokens.CacheWrite5m * rates.CacheWrite5mPerMTok
         + tokens.CacheWriteTtlUnrecorded * rates.CacheWrite5mPerMTok
         + tokens.CacheWrite1h * rates.CacheWrite1hPerMTok
         + tokens.CacheRead * rates.CacheReadPerMTok
         + tokens.Output * rates.OutputPerMTok) / MTok;

    /// <summary>
    /// How far O-view's estimate sits from a figure Claude Code reported for the same tokens,
    /// as a signed fraction: <c>+0.5</c> means O-view is 50% high.
    ///
    /// <para><b>This is the only check on the rates that needs no network, no credential and no
    /// policy change</b>, and it is how issue #256 was found. Claude Code's usage summary prints
    /// token counts <i>and</i> a dollar total, so the rates are the only unknown in the
    /// comparison. It catches every class of estimator error — a wrong rate, a wrong cache
    /// column, an unread modifier, a de-duplication fault — where a drift check against the
    /// published page only catches rates.</para>
    ///
    /// <para><b>It compares against a candidate <see cref="ModelRates"/> rather than solving for
    /// a rate</b>, so it assumes nothing about the ratio between columns. Solving for a single
    /// input rate is what the issue did by hand, and it only works because the ratios happened
    /// to be right; a wrong cache column would have been invisible to it.</para>
    ///
    /// <para>Returns zero when <paramref name="reportedUsd"/> is zero, which is the honest
    /// answer for a comparison with nothing on the other side rather than a division by it.</para>
    /// </summary>
    public static decimal RelativeError(ModelRates rates, TokenSplit tokens, decimal reportedUsd)
    {
        if (reportedUsd == 0)
        {
            return 0;
        }

        return (Value(rates, tokens) - reportedUsd) / reportedUsd;
    }
}
