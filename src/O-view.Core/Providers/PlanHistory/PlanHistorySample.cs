namespace OView.Core.Providers.PlanHistory;

/// <summary>
/// One validated sample from plan-usage-history.json. Only samples with all four
/// fields present and in range survive parsing; anything partial is skipped.
/// </summary>
/// <param name="AtUtc">Sample time (file stores Unix epoch milliseconds).</param>
/// <param name="OrgUuid">Organization the sample belongs to. Multi-org accounts interleave.</param>
/// <param name="FiveHourPercent">`u.fh` — five-hour window utilisation, 0–100.</param>
/// <param name="SevenDayPercent">`u.sd` — seven-day window utilisation, 0–100.</param>
public sealed record PlanHistorySample(
    DateTimeOffset AtUtc,
    string OrgUuid,
    int FiveHourPercent,
    int SevenDayPercent);
