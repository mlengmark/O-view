namespace OView.Core.Models;

/// <summary>
/// Models believed to bill as **extra usage (credits)** rather than drawing from the
/// plan's 5-hour window, on this account's plan.
///
/// <para>The set itself lives in <see cref="ModelCatalog"/> as
/// <see cref="BillingClass.Credit"/>, which is also where the evidence for each entry and
/// the limits of the heuristic are recorded. This class is the query over it.</para>
///
/// <para>Both the membership test and the caption the UI shows now derive from that one
/// table. They did not: the caption was a hand-written <c>"Fable"</c> while the classifier
/// matched Fable <em>and</em> Mythos, so a Mythos user's spend was summed into the credit
/// total under a note claiming only Fable was included — and a Mythos-only user saw a
/// non-zero "Est. credit spend" beneath a sentence naming a model they had never run
/// (GitHub issue #56). A list stated twice is a list that eventually disagrees with
/// itself.</para>
/// </summary>
public static class CreditBilledModels
{
    /// <summary>U+00A0, as an escape — the literal character is invisible in source.</summary>
    private const char NonBreakingSpace = '\u00A0';

    private static readonly IReadOnlyList<ModelEntry> Entries =
        ModelCatalog.InClass(BillingClass.Credit);

    /// <summary>
    /// Human-readable list of the covered models, for the UI caption. Derived from the
    /// same entries <see cref="IsCreditBilled"/> matches, so the caption can never name a
    /// different set than the one being summed.
    ///
    /// <para>Spaces inside a name are non-breaking, so a model is never split across a
    /// line. The caption sits in a wrapping run of prose whose entire job is stating
    /// precisely which models are counted, and "Fable / 5, Mythos 5" broken over two lines
    /// reads as three entries rather than two.</para>
    /// </summary>
    public static string DisplayList { get; } =
        string.Join(", ", Entries.Select(e => e.DisplayName.Replace(' ', NonBreakingSpace)));

    public static bool IsCreditBilled(string model) =>
        ModelCatalog.Find(model)?.Billing == BillingClass.Credit;
}
