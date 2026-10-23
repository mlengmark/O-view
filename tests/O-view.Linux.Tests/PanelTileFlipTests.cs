using OView.Core.Models;
using OView.Core.Providers.CachedUsage;
using OView.Linux.Panel;

namespace OView.Linux.Tests;

/// <summary>
/// The Linux tiles' flip between a total and the per-model split (GitHub issue #37), which the
/// Windows panel has always had.
///
/// <para><b>This is the feature's only proof.</b> No shared sample state carries per-model
/// rows, so every tile in every golden render has nothing to break down, no golden on either
/// head shows a tile's second face, and giving a state that data would change the Windows
/// picture as well, which Phase D puts out of scope. The flip is therefore asserted here
/// instead of looked at.</para>
///
/// <para>Avalonia raises no pointer events without a windowing subsystem and CI has none, so
/// these drive the state a click drives rather than clicking, the same reason
/// <see cref="PanelDismissal"/> is its own class. The controls under assertion are the ones the
/// panel really built; what no test here covers is the three event subscriptions that call
/// <c>Toggle</c>.</para>
/// </summary>
public class PanelTileFlipTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static ModelSlice Slice(string model, long tokens, decimal? usd, string name) =>
        new(model, name, tokens) { EstUsdValue = Priced(usd) };

    /// <summary>An Est. figure as the real builder pairs it: estimated, or unavailable.</summary>
    private static EstimatedUsd Priced(decimal? usd) =>
        new(usd, usd is null ? UsageValueStatus.Unavailable : UsageValueStatus.Estimated);

    /// <summary>Four models in the window, so the fourth folds into the remainder.</summary>
    private static readonly ModelSlice[] Window =
    [
        Slice("claude-opus-4-8", 350_000_000, 250.00m, "Opus 4.8"),
        Slice("claude-opus-5", 180_000_000, 120.00m, "Opus 5"),
        Slice("claude-fable-5", 40_000_000, 50.00m, "Fable 5"),
        Slice("claude-haiku-4-5", 5_900_000, 5.21m, "Haiku 4.5"),
    ];

    /// <summary>
    /// A panel with per-model rows behind the 31-day tiles and nothing behind today's.
    ///
    /// <para>No token composition, deliberately: that section is the one piece of this panel
    /// that needs a platform cursor, which a test process has none of.</para>
    /// </summary>
    private static PanelStatistics Stats(IReadOnlyList<ModelSlice>? window = null)
    {
        var models31 = window ?? Window;

        return new PanelStatistics(
            TokensToday: 0,
            Tokens31Days: models31.Sum(s => s.Tokens),
            RecordedDays: 31,
            WindowDays: 31,
            DailySeries: [],
            CreditTokens31Days: 0)
        {
            EstTodayValue = Priced(0m),
            Est31DaysValue = Priced(models31.Sum(s => s.EstUsdValue.Value ?? 0m)),
            ModelsToday = [],
            Models31Days = models31,
            ModelColourOrder = ModelBreakdown.ColourOrder(models31),
        };
    }

    private static PanelContent Panel(IReadOnlyList<ModelSlice>? window = null)
    {
        var content = new PanelContent(new LinuxPanelTheme(light: true));

        // Live, so the meters are the account's own figures and no banner stands in for them.
        content.Populate(
            new UsageSnapshot(
                DataSource.Live,
                UsageSnapshot.Percent(25),
                UsageSnapshot.Percent(3),
                UsageSnapshot.Instant(Now.AddHours(3)),
                UsageSnapshot.Instant(Now),
                UsageSnapshot.Instant(null)),
            Stats(window),
            account: null,
            dataReport: null,
            scopeReport: null,
            utcNow: Now,
            zone: TimeZoneInfo.Utc);

        return content;
    }

    /// <summary>
    /// Only the two tiles with something to break down gain a flip. Today's pair has no usage
    /// behind it, so it stays the plain tile, an affordance that leads nowhere is worse than
    /// none.
    /// </summary>
    [Fact]
    public void OnlyTheTilesWithSomethingToBreakDownCanFlip()
    {
        Assert.Equal(2, Panel().TileFlips.Count);
    }

    /// <summary>A tile with nothing to chart on either window offers no flip at all.</summary>
    [Fact]
    public void ATileWithNothingToBreakDownOffersNothing()
    {
        // One model, priced, so the token tile charts it; the value tile does too. Dropping
        // the price leaves the value tile with rows and no chart.
        Assert.Equal(2, Panel([Slice("claude-opus-5", 180_000_000, 120.00m, "Opus 5")]).TileFlips.Count);
        Assert.Single(Panel([Slice("claude-brandnew-9", 4_000_000, null, "Brand New 9")]).TileFlips);
    }

    /// <summary>
    /// Every tile opens on its total. The panel is a transient view rather than a setting, so a
    /// panel reopened after a flip must not still be showing the breakdown.
    /// </summary>
    [Fact]
    public void ATileOpensOnItsTotal()
    {
        Assert.All(Panel().TileFlips, flip => Assert.False(flip.IsExpanded));
    }

    /// <summary>
    /// The feature: a click flips the tile, and another click flips it back, as often as you
    /// like. Nothing is fetched either way, the split is already in hand.
    /// </summary>
    [Fact]
    public void ATileFlipsAndFlipsBackAsOftenAsYouLike()
    {
        var flip = Panel().TileFlips[0];

        for (var i = 0; i < 5; i++)
        {
            flip.Toggle();
            Assert.True(flip.IsExpanded);

            flip.Toggle();
            Assert.False(flip.IsExpanded);
        }
    }

    /// <summary>
    /// One face is showing at a time, and the other is held out of sight without leaving the
    /// layout, the tile keeps one height so the sections below it never jump.
    /// </summary>
    [Fact]
    public void OneFaceShowsAndTheOtherKeepsItsSpace()
    {
        var flip = Panel().TileFlips[0];

        Assert.Equal(1, flip.Summary.Opacity);
        Assert.Equal(0, flip.Breakdown.Opacity);
        Assert.True(flip.Summary.IsHitTestVisible);
        Assert.False(flip.Breakdown.IsHitTestVisible);

        flip.Toggle();

        Assert.Equal(0, flip.Summary.Opacity);
        Assert.Equal(1, flip.Breakdown.Opacity);
        Assert.False(flip.Summary.IsHitTestVisible);
        Assert.True(flip.Breakdown.IsHitTestVisible);

        // Both faces are still in the layout either way. A collapsed one would hand the tile a
        // different height on each flip, which is the jump this avoids.
        Assert.True(flip.Summary.IsVisible);
        Assert.True(flip.Breakdown.IsVisible);
    }

    /// <summary>
    /// The affordance is faint at rest, clearer under the pointer, clearest while the breakdown
    /// is open, findable, and never louder than the number it sits beside. Same three values
    /// as the Windows tile.
    /// </summary>
    [Fact]
    public void TheGlyphIsFaintUntilItIsWanted()
    {
        var flip = Panel().TileFlips[0];

        Assert.Equal(0.35, flip.Glyph.Opacity, 3);

        flip.PointerOver(true);
        Assert.Equal(0.75, flip.Glyph.Opacity, 3);

        flip.Toggle();
        Assert.Equal(0.9, flip.Glyph.Opacity, 3);

        // Still open after the pointer leaves, the breakdown does not close itself.
        flip.PointerOver(false);
        Assert.Equal(0.9, flip.Glyph.Opacity, 3);
        Assert.True(flip.IsExpanded);
    }

    /// <summary>
    /// A second open rebuilds the tiles, so no flip is left holding controls that are no longer
    /// on screen, and the new tiles open on their totals like any others.
    /// </summary>
    [Fact]
    public void ASecondOpenStartsFromFreshTiles()
    {
        var content = Panel();
        var first = content.TileFlips[0];
        first.Toggle();

        content.Populate(
            new UsageSnapshot(
                DataSource.Live,
                UsageSnapshot.Percent(25),
                UsageSnapshot.Percent(3),
                UsageSnapshot.Instant(Now.AddHours(3)),
                UsageSnapshot.Instant(Now),
                UsageSnapshot.Instant(null)),
            Stats(),
            account: null,
            dataReport: null,
            scopeReport: null,
            utcNow: Now,
            zone: TimeZoneInfo.Utc);

        Assert.Equal(2, content.TileFlips.Count);
        Assert.DoesNotContain(first, content.TileFlips);
        Assert.All(content.TileFlips, flip => Assert.False(flip.IsExpanded));
    }
}
