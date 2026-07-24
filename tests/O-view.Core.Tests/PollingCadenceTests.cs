using OView.Core.Models;
using OView.Core.Providers;

namespace OView.Core.Tests;

public class PollingCadenceTests
{
    private static readonly TimeSpan Warmup = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Normal = TimeSpan.FromSeconds(60);

    [Theory]
    [InlineData(DataSource.Live)]
    [InlineData(DataSource.Stale)]
    public void Authoritative_data_uses_normal_interval_immediately(DataSource source)
    {
        // Once the plan bars can be filled, there is nothing to warm up for.
        Assert.Equal(Normal, PollingCadence.Next(source, TimeSpan.Zero, Warmup, Normal));
    }

    [Theory]
    [InlineData(DataSource.None)]      // nothing yet — Desktop not sampling
    [InlineData(DataSource.Estimate)]  // only the JSONL fallback; plan bars still blank
    public void Without_authoritative_data_inside_the_window_polls_fast(DataSource source)
    {
        Assert.Equal(Warmup, PollingCadence.Next(source, TimeSpan.Zero, Warmup, Normal));
        Assert.Equal(Warmup, PollingCadence.Next(source, TimeSpan.FromSeconds(30), Warmup, Normal));
    }

    [Theory]
    [InlineData(DataSource.None)]
    [InlineData(DataSource.Estimate)]
    public void Warmup_gives_up_once_the_window_elapses(DataSource source)
    {
        // A Desktop that stays closed must not mean permanent fast polling.
        Assert.Equal(Normal, PollingCadence.Next(source, PollingCadence.WarmupWindow, Warmup, Normal));
        Assert.Equal(Normal, PollingCadence.Next(source, PollingCadence.WarmupWindow + TimeSpan.FromMinutes(5), Warmup, Normal));
    }

    [Fact]
    public void Transition_to_authoritative_mid_warmup_switches_to_normal()
    {
        var early = TimeSpan.FromSeconds(9);
        Assert.Equal(Warmup, PollingCadence.Next(DataSource.None, early, Warmup, Normal));
        Assert.Equal(Normal, PollingCadence.Next(DataSource.Live, early, Warmup, Normal));
    }

    [Theory]
    [InlineData(DataSource.Live, true)]
    [InlineData(DataSource.Stale, true)]
    [InlineData(DataSource.Estimate, false)]
    [InlineData(DataSource.None, false)]
    public void IsAuthoritative_matches_the_plan_bar_gate(DataSource source, bool expected)
    {
        Assert.Equal(expected, PollingCadence.IsAuthoritative(source));
    }
}
