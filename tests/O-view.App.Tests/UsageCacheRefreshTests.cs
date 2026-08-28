using OView.Core.Providers.CachedUsage;

namespace OView.App.Tests;

/// <summary>
/// When the engine asks Claude Code to refresh its usage cache, and when it refuses to (issue
/// #234).
///
/// <para><see cref="Core.Tests"/> owns whether an invocation was billed; this owns the half that
/// decides how often the other half runs — the floor, the gate, and the latch that stops it
/// altogether. The latch is the part worth testing hardest, because it is the one that can
/// silently cost the user a feature.</para>
/// </summary>
public class UsageCacheRefreshTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeRefresher : IUsageCacheRefresher
    {
        public int Calls { get; private set; }

        public ClaudeCliRefreshResult Next { get; set; } =
            new(RefreshOutcome.Unchanged);

        public ClaudeCliRefreshResult Refresh()
        {
            Calls++;
            return Next;
        }
    }

    /// <summary>A cached block stamped at <paramref name="fetched"/>.</summary>
    private static CachedUtilization Block(DateTimeOffset fetched) =>
        new(fetched, AccountUuid: null, FiveHour: null, SevenDay: null);

    private static UsageEngine NewEngine(
        TempDir dir,
        IUsageCacheRefresher? refresher,
        FakeClock clock,
        TimeSpan? floor = null,
        Func<CachedUtilization?>? block = null)
    {
        var provider = new FakeProvider();
        provider.SetSession(42);

        return new UsageEngine(new UsageEngineOptions
        {
            Clock = clock,
            Provider = provider,
            UsageCacheRefresher = refresher,
            UsageRefreshFloor = floor ?? TimeSpan.FromMinutes(15),

            // Null by default, which is what an injected Provider already forces: a test that
            // describes its own world must not reach past it into the developer's own
            // ~/.claude.json. The staleness tests below opt in explicitly.
            CachedUtilizationSource = block,
            RollupDbPath = dir.File("usage.db"),
            WeeklyResetAnchorPath = dir.File("weekly-resets.json"),
            SettingsPath = dir.File("settings.json"),
        });
    }

    // ── the floor ───────────────────────────────────────────────────────────

    /// <summary>
    /// Opening the panel is a person looking, not a timer, so it bypasses the floor. Making them
    /// wait out a fifteen-minute window they cannot see would read as the feature not working.
    /// </summary>
    [Fact]
    public void OpeningThePanelBypassesTheFloor()
    {
        using var dir = new TempDir();
        var refresher = new FakeRefresher();
        using var engine = NewEngine(dir, refresher, new FakeClock(T0));

        engine.RefreshUsageCache(force: true);
        engine.RefreshUsageCache(force: true);

        Assert.Equal(2, refresher.Calls);
    }

    /// <summary>
    /// The background beat does not. Cost is not the constraint — the refresh is free — but it
    /// spends Claude Code's own rate-limit budget, and a 429 would land on the user's CLI work.
    /// </summary>
    [Fact]
    public void TheBackgroundBeatIsHeldToTheFloor()
    {
        using var dir = new TempDir();
        var refresher = new FakeRefresher();
        using var engine = NewEngine(dir, refresher, new FakeClock(T0));

        engine.RefreshUsageCache();
        engine.RefreshUsageCache();
        engine.RefreshUsageCache();

        Assert.Equal(1, refresher.Calls);
    }

    [Fact]
    public void PastTheFloorTheBackgroundBeatRunsAgain()
    {
        using var dir = new TempDir();
        var clock = new FakeClock(T0);
        var refresher = new FakeRefresher();
        using var engine = NewEngine(dir, refresher, clock, floor: TimeSpan.FromMinutes(15));

        engine.RefreshUsageCache();
        clock.Advance(TimeSpan.FromMinutes(16));
        engine.RefreshUsageCache();

        Assert.Equal(2, refresher.Calls);
    }

    /// <summary>
    /// Stamped before the run rather than after, so a slow or hung attempt still spaces the next
    /// one. Stamping on completion would let a run that takes the full twenty-second timeout be
    /// followed immediately by another.
    /// </summary>
    [Fact]
    public void TheFloorIsMeasuredFromTheStartOfAnAttempt()
    {
        using var dir = new TempDir();
        var clock = new FakeClock(T0);
        var slow = new FakeRefresher();
        using var engine = NewEngine(dir, slow, clock, floor: TimeSpan.FromMinutes(15));

        engine.RefreshUsageCache();

        // Time passes during the attempt, as it would for a real spawn.
        clock.Advance(TimeSpan.FromSeconds(20));
        engine.RefreshUsageCache();

        Assert.Equal(1, slow.Calls);
    }

    /// <summary>
    /// The counter records attempts that <i>started</i>, not calls that were made — most calls
    /// are turned away by the floor, the freshness gate or the latch.
    ///
    /// <para>It exists so the <c>--popup-check</c> hook can assert the panel-open path reached
    /// the refresher rather than inferring it from a log line (issue #249). A counter that
    /// incremented on every call would report success for a click that was gated out.</para>
    /// </summary>
    [Fact]
    public void TheAttemptCounterCountsStartsNotCalls()
    {
        using var dir = new TempDir();
        var refresher = new FakeRefresher();
        using var engine = NewEngine(dir, refresher, new FakeClock(T0));

        Assert.Equal(0, engine.UsageRefreshAttempts);

        engine.RefreshUsageCache(force: true);
        Assert.Equal(1, engine.UsageRefreshAttempts);

        // Turned away by the floor — a call, but not an attempt.
        engine.RefreshUsageCache();
        Assert.Equal(1, engine.UsageRefreshAttempts);
    }

    /// <summary>A blocked engine starts nothing, so the counter must not move either.</summary>
    [Fact]
    public void ABlockedEngineStartsNoAttempts()
    {
        using var dir = new TempDir();
        var refresher = new FakeRefresher
        {
            Next = new ClaudeCliRefreshResult(RefreshOutcome.Billed, "x.jsonl"),
        };
        using var engine = NewEngine(dir, refresher, new FakeClock(T0));

        engine.RefreshUsageCache(force: true);
        var afterBlock = engine.UsageRefreshAttempts;

        engine.RefreshUsageCache(force: true);

        Assert.Equal(afterBlock, engine.UsageRefreshAttempts);
    }

    // ── the staleness gate ──────────────────────────────────────────────────

    /// <summary>
    /// A block already fresher than the floor needs no process spawned at it. The user may have
    /// run <c>/usage</c> themselves, which is the one other thing that moves it.
    /// </summary>
    [Fact]
    public void AFreshBlockIsNotRefreshedInTheBackground()
    {
        using var dir = new TempDir();
        var clock = new FakeClock(T0);
        var refresher = new FakeRefresher();
        using var engine = NewEngine(
            dir, refresher, clock, block: () => Block(T0.AddMinutes(-1)));

        engine.RefreshUsageCache();

        Assert.Equal(0, refresher.Calls);
    }

    [Fact]
    public void AStaleBlockIsRefreshedInTheBackground()
    {
        using var dir = new TempDir();
        var clock = new FakeClock(T0);
        var refresher = new FakeRefresher();
        using var engine = NewEngine(
            dir, refresher, clock, block: () => Block(T0.AddDays(-4.43)));

        engine.RefreshUsageCache();

        Assert.Equal(1, refresher.Calls);
    }

    /// <summary>
    /// The panel is a person looking. Freshness is not theirs to be told about, so opening it
    /// refreshes regardless of how new the block is.
    /// </summary>
    [Fact]
    public void OpeningThePanelRefreshesEvenAFreshBlock()
    {
        using var dir = new TempDir();
        var refresher = new FakeRefresher();
        using var engine = NewEngine(
            dir, refresher, new FakeClock(T0), block: () => Block(T0.AddSeconds(-1)));

        engine.RefreshUsageCache(force: true);

        Assert.Equal(1, refresher.Calls);
    }

    /// <summary>
    /// An unknown age must not gate. A machine whose <c>.claude.json</c> cannot be read is
    /// exactly one that needs the refresh, and treating "cannot tell" as "fresh" would switch
    /// the feature off there.
    /// </summary>
    [Fact]
    public void AnUnreadableBlockDoesNotBlockTheRefresh()
    {
        using var dir = new TempDir();
        var refresher = new FakeRefresher();
        using var engine = NewEngine(
            dir, refresher, new FakeClock(T0),
            block: () => throw new IOException("locked"));

        engine.RefreshUsageCache();

        Assert.Equal(1, refresher.Calls);
    }

    // ── the latch ───────────────────────────────────────────────────────────

    /// <summary>
    /// A suspected charge stops the feature rather than backing it off. Backing off would keep
    /// spending roughly 50K tokens per attempt while looking like ordinary retry behaviour.
    /// </summary>
    [Fact]
    public void ASuspectedChargeStopsFurtherAttempts()
    {
        using var dir = new TempDir();
        var refresher = new FakeRefresher
        {
            Next = new ClaudeCliRefreshResult(RefreshOutcome.Billed, "refresh.jsonl"),
        };
        using var engine = NewEngine(dir, refresher, new FakeClock(T0));

        engine.RefreshUsageCache(force: true);
        engine.RefreshUsageCache(force: true);
        engine.RefreshUsageCache(force: true);

        Assert.Equal(1, refresher.Calls);
    }

    /// <summary>
    /// The next thing a user sees is a feature going quiet, so the reason has to be answerable
    /// and has to name the evidence.
    /// </summary>
    [Fact]
    public void TheBlockSaysWhyAndNamesTheTranscript()
    {
        using var dir = new TempDir();
        var refresher = new FakeRefresher
        {
            Next = new ClaudeCliRefreshResult(RefreshOutcome.Billed, "refresh.jsonl"),
        };
        using var engine = NewEngine(dir, refresher, new FakeClock(T0));

        Assert.Null(engine.UsageRefreshBlocked);

        engine.RefreshUsageCache(force: true);

        Assert.Contains("billed", engine.UsageRefreshBlocked);
        Assert.Contains("refresh.jsonl", engine.UsageRefreshBlocked);
    }

    /// <summary>
    /// <b>The latch must be undoable.</b> The guard behind it errs toward reporting a charge — a
    /// Claude Code session started during the seconds a refresh runs is indistinguishable from a
    /// billed one — and that direction is only correct while the user can reverse it. Without
    /// this the trade is the wrong way round: a rare race would permanently remove a feature.
    /// </summary>
    [Fact]
    public void ResumingClearsTheBlockAndAllowsAnotherAttempt()
    {
        using var dir = new TempDir();
        var refresher = new FakeRefresher
        {
            Next = new ClaudeCliRefreshResult(RefreshOutcome.Billed, "refresh.jsonl"),
        };
        using var engine = NewEngine(dir, refresher, new FakeClock(T0));

        engine.RefreshUsageCache(force: true);
        Assert.Equal(1, refresher.Calls);

        refresher.Next = new ClaudeCliRefreshResult(RefreshOutcome.Unchanged);

        Assert.True(engine.ResumeUsageRefresh());
        Assert.Null(engine.UsageRefreshBlocked);

        engine.RefreshUsageCache(force: true);
        Assert.Equal(2, refresher.Calls);
    }

    /// <summary>
    /// Resuming does not leave the user waiting out a floor they cannot see. Someone who has just
    /// re-enabled this is asking for it now.
    /// </summary>
    [Fact]
    public void ResumingDoesNotReapplyTheFloor()
    {
        using var dir = new TempDir();
        var refresher = new FakeRefresher
        {
            Next = new ClaudeCliRefreshResult(RefreshOutcome.Billed, "x.jsonl"),
        };
        using var engine = NewEngine(dir, refresher, new FakeClock(T0), floor: TimeSpan.FromHours(1));

        engine.RefreshUsageCache();
        engine.ResumeUsageRefresh();
        engine.RefreshUsageCache();   // not forced, and the floor has not elapsed

        Assert.Equal(2, refresher.Calls);
    }

    [Fact]
    public void ResumingReportsWhetherAnythingWasCleared()
    {
        using var dir = new TempDir();
        using var engine = NewEngine(dir, new FakeRefresher(), new FakeClock(T0));

        Assert.False(engine.ResumeUsageRefresh());
    }

    // ── the feature being absent is not an error ────────────────────────────

    /// <summary>
    /// No refresher is the ordinary case for a machine with no Claude Code, and for every test
    /// that does not opt in. It must be silent rather than throwing or logging a failure.
    /// </summary>
    [Fact]
    public void WithNoRefresherTheCallIsSilentAndTheFeatureReportsItselfOff()
    {
        using var dir = new TempDir();
        using var engine = NewEngine(dir, refresher: null, new FakeClock(T0));

        Assert.False(engine.CanRefreshUsageCache);

        engine.RefreshUsageCache(force: true);
        engine.RefreshUsageCache();

        Assert.Null(engine.UsageRefreshBlocked);
    }

    /// <summary>
    /// Outcomes short of a charge are ordinary and must not latch: a machine where Claude Code is
    /// absent, or logged out, or simply slow, has to keep trying — those states resolve
    /// themselves and are the common case.
    /// </summary>
    [Theory]
    [InlineData(RefreshOutcome.NotFound)]
    [InlineData(RefreshOutcome.TimedOut)]
    [InlineData(RefreshOutcome.Failed)]
    [InlineData(RefreshOutcome.Unchanged)]
    public void OnlyAChargeLatches(RefreshOutcome outcome)
    {
        using var dir = new TempDir();
        var refresher = new FakeRefresher { Next = new ClaudeCliRefreshResult(outcome) };
        using var engine = NewEngine(dir, refresher, new FakeClock(T0));

        engine.RefreshUsageCache(force: true);
        engine.RefreshUsageCache(force: true);

        Assert.Null(engine.UsageRefreshBlocked);
        Assert.Equal(2, refresher.Calls);
    }
}
