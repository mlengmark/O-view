using OView.Core.Providers.PlanHistory;

namespace OView.App.Tests;

/// <summary>
/// Nothing an injected provider chain stands for may reach past it into the real machine's
/// files (<a href="https://github.com/mlengmark/O-view/issues/212">issue #212</a>).
///
/// <para><b>These assert on the resolved path, deliberately, not on the figures.</b> The bug
/// was silent for exactly that reason: it only ever showed as an outcome, so eight tests failed
/// for hours and then passed unchanged, and the difference was how much Claude the developer
/// had used in between. An outcome-shaped test would have been green on a CI runner — which has
/// no plan history at all — and green on this machine most of the day. A path-shaped one is
/// true or false regardless of the weather.</para>
///
/// <para>Measured afterwards by replaying the developer's own file through the real provider
/// and the real detector with an empty rollup store: <b>six</b> instants out of 1,180 samples
/// would have reported usage billing beyond the plan, all inside two hours on 2026-08-25, all
/// with the session meter pinned at 99%. That is a small enough target to miss for weeks and a
/// large enough one to hit on the day you are shipping.</para>
/// </summary>
public class RealUserDataIsolationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    private static UsageEngineOptions Options(TempDir dir) => new()
    {
        Clock = new FakeClock(T0),
        Provider = new FakeProvider(),
        RollupDbPath = dir.File("usage.db"),
        WeeklyResetAnchorPath = dir.File("weekly-resets.json"),
        SettingsPath = dir.File("settings.json"),
    };

    /// <summary>
    /// The rule, stated once: a caller that supplied its own <see cref="UsageEngineOptions.Provider"/>
    /// and said nothing about plan history gets a provider that reads nothing.
    /// </summary>
    [Fact]
    public void AnInjectedProviderChain_ResolvesNoPlanHistoryFile()
    {
        using var dir = new TempDir();
        using var engine = new UsageEngine(Options(dir));

        Assert.Equal(PlanHistoryFile.NoFile, engine.PlanHistoryPath);
    }

    /// <summary>
    /// And specifically not a file under the real user profile — the assertion the issue asks
    /// for by name. Stated separately from the one above because it is the property that
    /// matters: the sentinel is today's way of achieving it, and this survives changing it.
    /// </summary>
    [Fact]
    public void AnInjectedProviderChain_ResolvesNothingUnderTheRealUserProfile()
    {
        using var dir = new TempDir();
        using var engine = new UsageEngine(Options(dir));

        AssertNotUnderTheUserProfile(engine.PlanHistoryPath);
    }

    /// <summary>
    /// Naming a file is what asks for one. The guard must not be so eager that a test which
    /// legitimately describes a plan history stops getting it.
    /// </summary>
    [Fact]
    public void NamingAPlanHistoryPath_StillGetsThatFile()
    {
        using var dir = new TempDir();
        var named = dir.File("plan-usage-history.json");

        using var engine = new UsageEngine(Options(dir) with { PlanHistoryPath = named });

        Assert.Equal(named, engine.PlanHistoryPath);
    }

    /// <summary>
    /// Production is the case that must still read the real file: no injected provider means
    /// the engine is the whole chain, and the whole chain is what reads Claude Desktop.
    /// </summary>
    [Fact]
    public void WithNoInjectedProvider_TheRealFileIsStillWhatIsRead()
    {
        using var dir = new TempDir();

        using var engine = new UsageEngine(new UsageEngineOptions
        {
            Clock = new FakeClock(T0),
            RollupDbPath = dir.File("usage.db"),
            WeeklyResetAnchorPath = dir.File("weekly-resets.json"),
            SettingsPath = dir.File("settings.json"),
        });

        Assert.Equal(PlanHistoryFile.DefaultPath, engine.PlanHistoryPath);
    }

    /// <summary>
    /// The consequence, asserted where it bit: the off-plan comparison cannot report anything,
    /// because it has no meter to compare against. This is the eight failing tests' condition,
    /// now true on any machine on any day.
    /// </summary>
    [Fact]
    public void TheOffPlanComparisonHasNoMeterToReadFrom()
    {
        using var dir = new TempDir();
        using var engine = new UsageEngine(Options(dir));

        var stats = engine.BuildStatistics();

        Assert.False(stats.IsOffPlan);
        Assert.Equal(0, engine.PlanWindowSampleCount);
    }

    /// <summary>
    /// Fails with the path in the message rather than as a bare false, because the thing a
    /// reader needs to see is <i>which</i> file leaked.
    /// </summary>
    private static void AssertNotUnderTheUserProfile(string path)
    {
        if (path.Length == 0)
        {
            return;   // names no file at all
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        foreach (var root in new[] { profile, appData })
        {
            if (root.Length > 0 &&
                Path.GetFullPath(path).StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            {
                Assert.Fail(
                    $"a test resolved '{path}', which is under the real user profile ('{root}'). " +
                    "Whatever reads it will give this suite a result that depends on whose machine " +
                    "is running it — see issue #212.");
            }
        }
    }
}
