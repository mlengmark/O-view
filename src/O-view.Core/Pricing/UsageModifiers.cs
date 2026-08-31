namespace OView.Core.Pricing;

/// <summary>
/// What a transcript said about one pricing modifier.
///
/// <para><b>Three states, because two are not enough.</b> A value this build does not
/// recognise is neither "no modifier" nor a known one, and pricing it as
/// <see cref="Standard"/> is precisely the silent downgrade to the cheaper number that
/// GitHub issue #257 is about — a rate O-view cannot look up must fail to a labelled
/// unknown, never to a default.</para>
/// </summary>
public enum ModifierValue
{
    /// <summary>Absent, null, or a published value that leaves the price unmodified.</summary>
    Standard,

    /// <summary>The published value that changes the price. The rate card has to be able to price it.</summary>
    Applied,

    /// <summary>A value this build has never seen. Priced as unknown.</summary>
    Unrecognised,
}

/// <summary>
/// The two per-request pricing modifiers Anthropic publishes, as the transcript reported them.
///
/// <para>Both were listed in <c>docs/findings/jsonl-schema.md</c> under "also present on
/// <c>usage</c>, not currently needed" — judged against what the panel <i>displayed</i> rather
/// than against what it <i>computes</i>. A field that appears in a pricing formula is
/// load-bearing whether or not today's build reads it.</para>
///
/// <para><b>Both are inactive on every record measured here</b> — <c>speed</c> is
/// <c>"standard"</c> on 15,817 of 15,817 Claude Code assistant records and
/// <c>inference_geo</c> is <c>"not_available"</c> on 15,851 of 15,851 (measured 2026-08-31,
/// this machine). Inactive is not absent: the fields are there, they are read, and a value
/// that ever changes is priced or refused rather than ignored.</para>
/// </summary>
public readonly record struct UsageModifiers(ModifierValue Speed, ModifierValue InferenceGeo)
{
    /// <summary>Neither modifier in force — what every measured record carries.</summary>
    public static readonly UsageModifiers Standard = default;

    /// <summary>Fast mode, which is its own published rate row rather than a multiplier.</summary>
    public bool Fast => Speed == ModifierValue.Applied;

    /// <summary>US-pinned inference, which is a published multiplier over every category.</summary>
    public bool UsInference => InferenceGeo == ModifierValue.Applied;

    /// <summary>True when either field carried something this build cannot price.</summary>
    public bool IsUnpriceable =>
        Speed == ModifierValue.Unrecognised || InferenceGeo == ModifierValue.Unrecognised;

    /// <summary>
    /// What an unrecognised value is stored as. The exact string is not kept — the finding is
    /// that <i>something</i> unrecognised was reported, and the transcript still holds the
    /// value itself.
    /// </summary>
    public const string UnrecognisedText = "unrecognised";

    /// <summary>
    /// The value to persist, or null for <see cref="ModifierValue.Standard"/>.
    ///
    /// <para>Null on purpose for the common case: it costs nothing to store, and it shares its
    /// NULL with every row written before this build tracked the field. Both say the price was
    /// not modified — for a legacy row that is an assumption, and it is the same assumption the
    /// TTL-unrecorded bucket names in the Est. caveat.</para>
    ///
    /// <para>Read back through <see cref="From"/>, which is why these are the published tokens
    /// rather than enum names: one function decides what a stored value means and what a
    /// transcript's value means, so the two cannot drift.</para>
    /// </summary>
    public string? SpeedText => Speed switch
    {
        ModifierValue.Applied => "fast",
        ModifierValue.Unrecognised => UnrecognisedText,
        _ => null,
    };

    /// <inheritdoc cref="SpeedText"/>
    public string? InferenceGeoText => InferenceGeo switch
    {
        ModifierValue.Applied => "us",
        ModifierValue.Unrecognised => UnrecognisedText,
        _ => null,
    };

    /// <summary>
    /// Classifies the raw <c>usage.speed</c> and <c>usage.inference_geo</c> strings.
    ///
    /// <para>Null covers three cases that are one case here: the field was absent, it was
    /// JSON <c>null</c> (observed on a Cowork audit record), or the row predates this build
    /// tracking it. All three mean "nothing said the price was modified".</para>
    /// </summary>
    public static UsageModifiers From(string? speed, string? inferenceGeo) => new(
        speed switch
        {
            null or "" or "standard" => ModifierValue.Standard,
            "fast" => ModifierValue.Applied,
            _ => ModifierValue.Unrecognised,
        },
        inferenceGeo switch
        {
            // "not_available" is what the consumer plans report; "global" is the API default.
            // Both are standard pricing.
            null or "" or "not_available" or "global" => ModifierValue.Standard,
            "us" => ModifierValue.Applied,
            _ => ModifierValue.Unrecognised,
        });
}
