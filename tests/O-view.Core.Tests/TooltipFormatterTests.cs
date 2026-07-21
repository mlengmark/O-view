using OView.Core.Models;

namespace OView.Core.Tests;

public class TooltipFormatterTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Reset = new(2026, 7, 21, 11, 14, 55, TimeSpan.Zero);
    private static readonly DateTimeOffset Sampled = new(2026, 7, 21, 6, 49, 54, TimeSpan.Zero);

    [Fact]
    public void Live_ShowsPercentagesAndReset()
    {
        var s = new UsageSnapshot(DataSource.Live, 6, 1, Reset, Sampled);

        Assert.Equal("5h: 6% · resets 11:14 · 7d: 1%", TooltipFormatter.Format(s, Utc));
    }

    [Fact]
    public void Live_UnknownReset_OmitsResetSegment()
    {
        var s = new UsageSnapshot(DataSource.Live, 47, 20, null, Sampled);

        Assert.Equal("5h: 47% · 7d: 20%", TooltipFormatter.Format(s, Utc));
    }

    [Fact]
    public void Stale_CarriesAsOfLabel()
    {
        var s = new UsageSnapshot(DataSource.Stale, 31, 6, Reset, Sampled);

        Assert.Equal("5h: 31% (as of 06:49) · resets 11:14 · 7d: 6%", TooltipFormatter.Format(s, Utc));
    }

    [Fact]
    public void Estimate_AdmitsUnknownPercentages()
    {
        var s = new UsageSnapshot(DataSource.Estimate, null, null, null, Sampled);

        Assert.Equal("O-view · local estimate · usage % unknown", TooltipFormatter.Format(s, Utc));
    }

    [Fact]
    public void None_SaysNoData()
    {
        Assert.Equal("O-view · no usage data", TooltipFormatter.Format(UsageSnapshot.None, Utc));
    }

    [Fact]
    public void Output_NeverExceedsNotifyIconCap()
    {
        // 127, not 128 — measured (docs/findings/tray-icon-rendering.md).
        var s = new UsageSnapshot(DataSource.Live, 100, 100, Reset, Sampled);

        Assert.True(TooltipFormatter.Format(s, Utc).Length <= 127);
    }
}
