using OView.Core.Models;
using OView.Core.Providers.Jsonl;
using OView.Core.Storage;

namespace OView.Core.Tests;

public class PanelStatisticsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Directory.CreateTempSubdirectory("oview-tests-").FullName;
    private readonly RollupStore _store;

    public PanelStatisticsTests()
    {
        _store = new RollupStore(Path.Combine(_dir, "usage.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private void Seed(string id, string date, long output, string model = "claude-opus-4-8") =>
        _store.Ingest([new TranscriptRecord(id, DateTimeOffset.Parse(date + "T10:00:00Z"), model, 0, 0, 0, output)]);

    [Fact]
    public void PreInstallDays_AreMarkedNoData_NotZero()
    {
        Seed("r1", "2026-07-19", 100);  // first recorded day

        var stats = PanelStatistics.Build(_store, Now);

        Assert.Equal(31, stats.DailySeries.Count);
        // Days before 2026-07-19 have NO data; days from it onward are the recorded era.
        Assert.All(stats.DailySeries.Where(d => d.DateUtc < new DateOnly(2026, 7, 19)), d => Assert.True(d.PreInstall));
        Assert.All(stats.DailySeries.Where(d => d.DateUtc >= new DateOnly(2026, 7, 19)), d => Assert.False(d.PreInstall));
        // 2026-07-20 is inside the recorded era with no usage — a genuine zero.
        var idle = stats.DailySeries.Single(d => d.DateUtc == new DateOnly(2026, 7, 20));
        Assert.False(idle.PreInstall);
        Assert.Equal(0, idle.TotalTokens);
    }

    [Fact]
    public void EmptyStore_MarksEveryDayPreInstall()
    {
        var stats = PanelStatistics.Build(_store, Now);

        Assert.All(stats.DailySeries, d => Assert.True(d.PreInstall));
        Assert.Equal(0, stats.RecordedDays);
        Assert.True(stats.HasPartialHistory);
    }

    [Fact]
    public void Coverage_CountsRecordedDaysAgainstWindow()
    {
        Seed("r1", "2026-07-18", 10);
        Seed("r2", "2026-07-20", 20);
        Seed("r3", "2026-07-21", 30);

        var stats = PanelStatistics.Build(_store, Now);

        Assert.Equal(3, stats.RecordedDays);
        Assert.Equal(31, stats.WindowDays);
        Assert.True(stats.HasPartialHistory);
    }

    [Fact]
    public void TodayAndWindowTotals_AreSeparate()
    {
        Seed("r1", "2026-07-20", 100);
        Seed("r2", "2026-07-21", 40);

        var stats = PanelStatistics.Build(_store, Now);

        Assert.Equal(40, stats.TokensToday);
        Assert.Equal(140, stats.Tokens31Days);
    }

    [Fact]
    public void HistoryOlderThanWindow_MeansNoPreInstallDays()
    {
        Seed("r0", "2026-05-01", 5);  // well before the 31-day window
        Seed("r1", "2026-07-21", 40);

        var stats = PanelStatistics.Build(_store, Now);

        Assert.All(stats.DailySeries, d => Assert.False(d.PreInstall));
    }

    [Fact]
    public void UnpricedModel_YieldsLabelledPartial_NotABlankTile()
    {
        // Supersedes the original "any unpriced model ⇒ null" rule. That rule blanked
        // both Est. tiles the moment one unrecognised model id appeared (claude-opus-5,
        // in the real report) — the user saw "unknown" with no way to know why. The
        // principle behind rule 6 is "don't mislead", which a partial sum satisfies as
        // long as the exclusion is stated: UnpricedModels drives that caption.
        Seed("r1", "2026-07-21", 1_000_000);
        Seed("r2", "2026-07-21", 1_000_000, model: "claude-hypothetical-9");

        var stats = PanelStatistics.Build(_store, Now);

        Assert.Equal(25.00m, stats.EstTodayUsd);       // the priced portion, reported
        Assert.Contains("claude-hypothetical-9", stats.UnpricedModels);  // and labelled
        Assert.Equal(2_000_000, stats.TokensToday);    // token counts still honest
    }

    [Fact]
    public void EstimateUsesPublishedRates()
    {
        Seed("r1", "2026-07-21", 1_000_000);  // 1M output on opus: $25

        var stats = PanelStatistics.Build(_store, Now);

        Assert.Equal(25.00m, stats.EstTodayUsd);
    }

    // ── 31-day off-plan credit spend (issue #3) ────────────────────────────────
    [Fact]
    public void Credit31Days_CountsOnlyCreditBilledModels()
    {
        Seed("r1", "2026-07-21", 1_000_000, "claude-opus-4-8");   // plan model — excluded
        Seed("r2", "2026-07-21", 1_000_000, "claude-fable-5");    // credit-billed — counted
        Seed("r3", "2026-07-15", 500_000, "claude-fable-5");      // earlier in window — counted

        var stats = PanelStatistics.Build(_store, Now);

        // Fable output only: (1M + 0.5M) tokens.
        Assert.Equal(1_500_000, stats.CreditTokens31Days);
        // Priced at Fable's $50/MTok output: 1.5M -> $75.
        Assert.Equal(75.00m, stats.EstCredit31DaysUsd);
        Assert.True(stats.HasCreditUsage);
    }

    [Fact]
    public void Credit31Days_ZeroWhenNoCreditModels()
    {
        Seed("r1", "2026-07-21", 1_000_000, "claude-opus-4-8");

        var stats = PanelStatistics.Build(_store, Now);

        Assert.Equal(0, stats.CreditTokens31Days);
        Assert.False(stats.HasCreditUsage);
    }

    [Fact]
    public void Credit31Days_ExcludesUsageOutsideWindow()
    {
        Seed("old", "2026-06-01", 1_000_000, "claude-fable-5");   // before the 31-day window
        Seed("new", "2026-07-21", 400_000, "claude-fable-5");

        var stats = PanelStatistics.Build(_store, Now);

        Assert.Equal(400_000, stats.CreditTokens31Days);
    }

    [Fact]
    public void OneUnpricedModel_NoLongerBlanksTheWholeEstimate()
    {
        // The reported bug: tokens showed but both "Est. value" tiles read "unknown",
        // because a single model with no published rate voided the entire total.
        Seed("a", "2026-07-21", 1_000_000, "claude-opus-4-8");
        Seed("b", "2026-07-21", 1_000_000, "claude-brand-new-9");

        var stats = PanelStatistics.Build(_store, Now);

        Assert.NotNull(stats.Est31DaysUsd);              // priced portion still reported
        Assert.Equal(25.00m, stats.Est31DaysUsd);        // 1M Opus output @ $25/MTok
        Assert.Contains("claude-brand-new-9", stats.UnpricedModels);
    }

    // A "<synthetic> does not count as unpriced" test used to sit here. It seeded the
    // store with that model id directly — a state ingestion cannot produce, because
    // TranscriptReader drops those records at parse time — so it pinned the behaviour of
    // a branch no user could reach. The real guarantee is end-to-end and now lives in
    // JsonlIngestionTests.SyntheticRecords_NeverReachTheStore_... (GitHub issue #57).

    [Fact]
    public void NothingPriceable_IsStillUnknown_NotZero()
    {
        // If not one row can be priced there is no basis for a figure — show unknown
        // rather than a $0.00 that reads as "you spent nothing" (rule 6).
        Seed("a", "2026-07-21", 1_000_000, "claude-brand-new-9");

        var stats = PanelStatistics.Build(_store, Now);

        Assert.Null(stats.Est31DaysUsd);
        Assert.Contains("claude-brand-new-9", stats.UnpricedModels);
    }
}
