namespace OView.Core.Models;

/// <summary>
/// Models believed to bill as **extra usage (credits)** rather than drawing from the
/// plan's 5-hour window, on this account's plan.
///
/// This is a classification O-view *infers*, not one the data states — there is no
/// per-request billing-tier field (service_tier is uniformly "standard", including on
/// requests known to have billed to credits). The set is therefore explicit and
/// evidence-based rather than a universal rule:
///
///   • claude-fable-5 — VERIFIED. On 2026-07-21, ~174K Fable output tokens moved the
///     plan meter zero points while ~€86 of extra usage was billed; pricing Fable at
///     published API rates matched that billing. See docs/findings/credit-usage-divergence.md.
///   • claude-mythos-5 — same tier, pricing, and API surface as Fable (per the model
///     catalog); included by parity, not by direct observation.
///
/// Consequences of this being a heuristic, stated so the UI can caveat honestly:
///   • It is account- and plan-specific. A different plan may bill these differently.
///   • It will MISS plan-tier usage (e.g. Opus) that goes off-plan once the plan limit
///     is reached — that case is caught by the live divergence detector, not here.
///   • Add a model only when there is evidence it bills to credits on the target plan.
/// </summary>
public static class CreditBilledModels
{
    private static readonly string[] Prefixes =
    [
        "claude-fable-5",
        "claude-mythos-5",
    ];

    /// <summary>Human-readable list of the covered models, for the UI caption.</summary>
    public static string DisplayList => "Fable";

    public static bool IsCreditBilled(string model) =>
        Prefixes.Any(p => model.StartsWith(p, StringComparison.OrdinalIgnoreCase));
}
