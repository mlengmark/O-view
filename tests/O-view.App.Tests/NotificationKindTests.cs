using OView.Core.Models;
using OView.Core.Providers.PlanHistory;

namespace OView.App.Tests;

/// <summary>
/// What severity a notification claims to be.
///
/// <para>Every Windows balloon used to carry <c>ToolTipIcon.Warning</c>, because that was
/// hard-coded at the single point they all funnel through. So "O-view is up to date" arrived
/// under a yellow warning triangle, identical to "Usage is billing beyond your plan". A tool
/// that draws the same alarm for good news and for money being spent has taught the user to
/// ignore the alarm — and the alarm it then loses is the one that mattered.</para>
///
/// <para>The Windows head has no test project, so the mapping from kind to
/// <c>ToolTipIcon</c> and the choice made at each of its eleven call sites are verified by
/// reading, not here. What <b>is</b> covered here is the half that lives in <c>App</c>: the
/// two notifications the engine itself raises, and the default a caller gets for
/// forgetting.</para>
/// </summary>
public class NotificationKindTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private static (UsageEngine Engine, FakeProvider Provider, FakeTimerFactory Timers, List<AppNotification> Sent)
        Build(TempDir dir, Func<UsageEngineOptions, UsageEngineOptions>? tweak = null)
    {
        var provider = new FakeProvider();
        var options = new UsageEngineOptions
        {
            Clock = new FakeClock(T0),
            Provider = provider,
            RollupDbPath = dir.File("usage.db"),
            WeeklyResetAnchorPath = dir.File("weekly-resets.json"),
            SettingsPath = dir.File("settings.json"),
        };

        var engine = new UsageEngine(tweak is null ? options : tweak(options));
        var sent = new List<AppNotification>();
        engine.NotificationRequested += sent.Add;
        return (engine, provider, new FakeTimerFactory(), sent);
    }

    /// <summary>
    /// Information, so a caller that forgets under-states rather than over-states. A false
    /// alarm devalues every real one; an under-stated icon costs nothing the words do not
    /// already say.
    /// </summary>
    [Fact]
    public void TheDefaultKindIsInformation()
    {
        Assert.Equal(NotificationKind.Information, new AppNotification("t", "m").Kind);
    }

    /// <summary>
    /// A usage threshold crossing is what the warning glyph is actually for. This is one of
    /// only two the engine raises, and it kept the triangle for a reason.
    /// </summary>
    [Fact]
    public void AThresholdCrossingIsAWarning()
    {
        using var dir = new TempDir();
        var (engine, provider, timers, sent) = Build(dir);
        using var _e = engine;

        provider.SetSession(10);
        engine.Start(timers);
        Assert.Empty(sent);

        provider.SetSession(95);
        timers.Poll.Tick();

        Assert.Equal(NotificationKind.Warning, Assert.Single(sent).Kind);
    }

    /// <summary>
    /// Spend leaving the plan is the most consequential thing O-view says, and the one the
    /// hard-coded triangle was devaluing by sharing it with "up to date".
    /// </summary>
    [Fact]
    public void OffPlanBillingIsAWarning()
    {
        using var dir = new TempDir();
        var (engine, provider, timers, sent) = Build(dir, o => o with
        {
            SimulateDivergence = "limit",
            PlanHistoryPath = WritePlanHistory(dir),
        });
        using var _e = engine;

        engine.SetThresholdPercent(100);   // silence the threshold notifier, leaving only off-plan
        provider.SetSession(40);
        engine.Start(timers);

        var offPlan = Assert.Single(sent, n => n.Title.Contains("plan", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(NotificationKind.Warning, offPlan.Kind);
    }

    /// <summary>
    /// The kind travels with the notification rather than being re-derived by each head from
    /// the wording. Two heads guessing from strings is how they drift, and the words are the
    /// part most likely to be reworded.
    /// </summary>
    [Fact]
    public void TheKindSurvivesOnTheRecordRatherThanBeingInferred()
    {
        var warning = new AppNotification("Claude usage", "Session usage is at 95%.", NotificationKind.Warning);

        Assert.Equal(NotificationKind.Warning, warning.Kind);
        Assert.Equal(NotificationKind.Warning, (warning with { Message = "reworded entirely" }).Kind);
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
