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
    /// <summary>
    /// The covered models, as data — the same entries <see cref="IsCreditBilled"/> matches,
    /// so a caption built from them can never name a different set than the one being
    /// summed.
    ///
    /// <para><b>The caption itself is Presentation's job (OVIAA-39).</b> This class used to
    /// also join the names into a UI string, non-breaking spaces and all; that join is
    /// panel formatting and now lives at
    /// <see cref="OView.Presentation.Text.OffPlanText.CreditModelsCaption"/>, built from the
    /// entries exposed here.</para>
    /// </summary>
    public static IReadOnlyList<ModelEntry> Models { get; } =
        ModelCatalog.InClass(BillingClass.Credit);

    public static bool IsCreditBilled(string model) =>
        ModelCatalog.Find(model)?.Billing == BillingClass.Credit;
}
