using OView.Core.Models;
using OView.Core.Providers.CachedUsage;

namespace OView.App.Tests;

/// <summary>
/// What the off-plan balloon claims, against what Claude Code says about extra usage on the
/// account (issue #259).
///
/// <para>The balloon read "Usage is billing beyond your plan" at everyone whose session window
/// ran out — including the population that had extra usage switched off and could not be billed
/// anything. It is the worse of the two surfaces to be wrong on: the panel has room to qualify
/// a claim, and a notification is three lines with nothing beside them.</para>
///
/// <para>The setting is read through <c>CachedUtilizationSource</c>, which is null whenever a
/// test supplies its own <c>Provider</c> — so a test that does not describe this gets Unknown
/// rather than the developer's own <c>~/.claude.json</c>. That guard is the reason these
/// fixtures have to be explicit, and it is the right way round.</para>
/// </summary>
public class OffPlanNotificationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Runs one poll of an engine whose window is exhausted, and returns the off-plan balloon.
    /// The threshold notifier is silenced so only the one under test is left.
    /// </summary>
    private static AppNotification OffPlanBalloon(TempDir dir, ExtraUsageState? extraUsage)
    {
        var options = new UsageEngineOptions
        {
            Clock = new FakeClock(T0),
            Provider = new FakeProvider(),
            RollupDbPath = dir.File("usage.db"),
            WeeklyResetAnchorPath = dir.File("weekly-resets.json"),
            SettingsPath = dir.File("settings.json"),
            PlanHistoryPath = WritePlanHistory(dir),
            SimulateDivergence = "limit",
            CachedUtilizationSource = extraUsage is null ? null : () => Block(extraUsage.Value),
        };

        using var engine = new UsageEngine(options);
        var sent = new List<AppNotification>();
        engine.NotificationRequested += sent.Add;

        engine.SetThresholdPercent(100);
        engine.Start(new FakeTimerFactory());

        return Assert.Single(sent);
    }

    /// <summary>A cached block carrying nothing but the setting and the time it was read.</summary>
    private static CachedUtilization Block(ExtraUsageState state) =>
        new(T0.AddMinutes(-3), null, null, null)
        {
            ExtraUsage = new ExtraUsageStatus(state, UserDisabled: state == ExtraUsageState.Disabled),
        };

    /// <summary>
    /// The reported case. An account with extra usage off is not billing beyond its plan, and
    /// saying otherwise was not a hedge but a false statement about the reader's money.
    /// </summary>
    [Fact]
    public void AnAccountWithExtraUsageOffIsNotToldItIsBeingBilled()
    {
        using var dir = new TempDir();
        var balloon = OffPlanBalloon(dir, ExtraUsageState.Disabled);

        Assert.Equal("Usage is not drawing from your plan", balloon.Title);
        Assert.Contains("Extra usage is switched off", balloon.Message, StringComparison.Ordinal);

        // The negated form, not the absence of the phrase: the sentence has to reach the
        // reader, and "should not be billing beyond your plan" contains "billing beyond".
        Assert.Contains("should not be billing beyond your plan",
            balloon.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("is billing beyond", balloon.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The Est. figure is the API-rate VALUE of the work, not a charge — a distinction the panel
    /// carries in a label and a hover, and a balloon cannot. Beside "extra usage is switched
    /// off" it contradicts the sentence next to it, and the half a reader carries away from a
    /// contradiction is the money.
    /// </summary>
    [Fact]
    public void TheEstimatedValueIsWithheldWhereNothingCanBeBilled()
    {
        using var dir = new TempDir();

        Assert.DoesNotContain("Est.",
            OffPlanBalloon(dir, ExtraUsageState.Disabled).Message, StringComparison.Ordinal);
    }

    /// <summary>Where the account can be billed, the original claim stands — and now it is evidenced.</summary>
    [Fact]
    public void AnAccountWithExtraUsageOnKeepsTheBillingClaim()
    {
        using var dir = new TempDir();
        var balloon = OffPlanBalloon(dir, ExtraUsageState.Enabled);

        Assert.Equal("Usage is billing beyond your plan", balloon.Title);
        Assert.Contains("Extra usage is switched on", balloon.Message, StringComparison.Ordinal);
        Assert.Contains("Est. $92.75", balloon.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// No block to read — a machine with no Claude Code, or one whose cache predates the field.
    /// It states what O-view observed and claims nothing about billing either way, which is the
    /// only honest position when the answer is genuinely absent (rule 6).
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(ExtraUsageState.Unknown)]
    public void WithNothingReadNoBillingClaimIsMade(ExtraUsageState? state)
    {
        using var dir = new TempDir();
        var balloon = OffPlanBalloon(dir, state);

        Assert.Equal("Usage is not drawing from your plan", balloon.Title);
        Assert.DoesNotContain("Extra usage", balloon.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("billing beyond", balloon.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whatever the wording, this is what the yellow triangle is for.</summary>
    [Fact]
    public void ItIsStillAWarningInEveryState()
    {
        using var dir = new TempDir();

        Assert.Equal(NotificationKind.Warning, OffPlanBalloon(dir, ExtraUsageState.Disabled).Kind);
        Assert.Equal(NotificationKind.Warning, OffPlanBalloon(dir, ExtraUsageState.Enabled).Kind);
    }

    private static string WritePlanHistory(TempDir dir)
    {
        var path = dir.File("plan-usage-history.json");
        var at = T0.AddMinutes(-1).ToUnixTimeMilliseconds();
        File.WriteAllText(path,
            $"{{\"version\":2,\"samples\":[{{\"t\":{at},\"org\":\"org-1\",\"u\":{{\"fh\":40,\"sd\":12}}}}]}}");
        return path;
    }
}
