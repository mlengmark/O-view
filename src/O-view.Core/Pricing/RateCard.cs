using OView.Core.Models;

namespace OView.Core.Pricing;

/// <summary>
/// Where the rates in force came from. Named on screen beside the age, because a figure
/// derived from a rate the reader cannot trace is a figure they cannot check.
/// </summary>
public enum RateCardSource
{
    /// <summary>Compiled into this build. The only source that exists today.</summary>
    Bundled,

    /// <summary>
    /// Read from a file the user can edit. Not implemented — recorded here because the moment
    /// it is, the panel must say so: a user-editable pricing file is a fabricated-number vector
    /// unless its provenance is on screen (GitHub issue #255).
    /// </summary>
    UserFile,
}

/// <summary>
/// Anthropic's published prices for one model, per million tokens.
///
/// <para><b>Every column is read from the rate card, never computed from another column.</b>
/// Anthropic publishes all five — base input, 5-minute cache write, 1-hour cache write, cache
/// hit, output — so storing one and a multiplier throws away a published fact in order to
/// re-derive it. That is exactly how GitHub issue #255 happened: a single
/// <c>CacheWriteMultiplier = 1.25m</c> stood in for two different published prices, and the
/// comment above it stayed accurate about the constant while being wrong about the data.</para>
/// </summary>
/// <param name="Fast">
/// Fast mode's own rate row, or null when this model has no published fast-mode price.
///
/// <para>A row rather than a multiplier, for the reason above. Null is <b>not</b> a fallback to
/// the standard row: a request that reports <c>speed: "fast"</c> against a model with no fast
/// row is priced as unknown and labelled, because silently charging the cheaper rate is the
/// failure this whole design is against.</para>
/// </param>
public sealed record ModelRates(
    decimal InputPerMTok,
    decimal OutputPerMTok,
    decimal CacheWrite5mPerMTok,
    decimal CacheWrite1hPerMTok,
    decimal CacheReadPerMTok,
    ModelRates? Fast = null);

/// <summary>
/// The published prices O-view is pricing from, and the date they were read.
///
/// <para><b>The date is data, not a comment.</b> It used to be prose above the table —
/// "Anthropic's published API rates as cached 2026-06-24" — invisible to every figure derived
/// from it, so nothing in the app knew the table was ageing and nothing told the user
/// (issue #255). Past <see cref="StaleAfter"/> the Est. tiles carry it.</para>
///
/// <para><b>Age is necessary and not sufficient, which is why the drift check exists.</b> The
/// Sonnet 5 row was wrong on the day it was written — it recorded a price increase that was
/// later cancelled (issue #256) — so a mechanism that only asks how old this table is would
/// never have found it. <see cref="RateCardDrift"/> compares values.</para>
/// </summary>
public sealed record RateCard(
    DateOnly AsOf,
    RateCardSource Source,
    IReadOnlyList<ModelEntry> Models,
    decimal UsInferenceMultiplier = 1.10m)
{
    /// <summary>
    /// How old the table may get before the panel says so. Ninety days, because published
    /// rates change on the order of months: shorter would put a permanent caveat under a
    /// figure that is fine, and a caveat that is always on says nothing.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(90);

    /// <summary>Whether this table is old enough to be worth naming beside a figure.</summary>
    public bool IsStaleOn(DateOnly today) => today.DayNumber - AsOf.DayNumber > StaleAfter.TotalDays;

    /// <summary>
    /// What to call this card's provenance on screen. Here rather than at each display site so
    /// the panel's caveat and the diagnostics bundle cannot come to name the same source two
    /// ways — the second reader is the one that drifts.
    /// </summary>
    public string SourceLabel => Source == RateCardSource.Bundled ? "bundled" : "user file";

    /// <summary>
    /// The entry for a model id, or null when the model is unrecognised.
    ///
    /// <para>Longest prefix wins, decided by prefix <em>length</em> rather than by declaration
    /// order — transcripts carry dated snapshots (<c>claude-opus-5-20260501</c>), so matching
    /// is by prefix rather than by equality, and a row inserted in the wrong place must not be
    /// able to shadow a more specific one.</para>
    /// </summary>
    public ModelEntry? Find(string model) =>
        Models
            .Where(e => model.StartsWith(e.Prefix, StringComparison.OrdinalIgnoreCase))
            .MaxBy(e => e.Prefix.Length);

    /// <summary>
    /// The rates to price a request at, or null when they cannot be established: an
    /// unrecognised model, an unrecognised modifier value, or fast mode on a model with no
    /// published fast row. All three are the same answer — an honest unknown that the caller
    /// labels (CLAUDE.md rule 6).
    /// </summary>
    public ModelRates? RatesFor(string model, UsageModifiers modifiers)
    {
        if (modifiers.IsUnpriceable || Find(model) is not { } entry)
        {
            return null;
        }

        return modifiers.Fast ? entry.Rates.Fast : entry.Rates;
    }
}
