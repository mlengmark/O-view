using OView.Core.Providers.CachedUsage;

namespace OView.Core.Tests;

/// <summary>
/// Asking Claude Code to refresh its own usage cache (issue #234).
///
/// <para>Most of these tests are about the <b>cost guard</b> rather than the refresh, and
/// deliberately so. Getting the refresh wrong leaves the panel where it already is — saying
/// <i>unknown</i>, which is today's behaviour. Getting the guard wrong spends the user's plan:
/// <c>/usage</c> is handled locally and costs nothing, but an argument Claude Code does not
/// recognise reaches the model and cost <b>49,094 cache-write + 97,456 cache-read + 470
/// output</b> tokens for one trivial exchange when it was measured. The two outcomes are one
/// string apart, and the string is not ours to control forever.</para>
/// </summary>
public class ClaudeCliRefresherTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    private static ClaudeCliRefresher.ProcessRun Ok => new(Started: true, Exited: true, ExitCode: 0);

    /// <summary>A block stamped at <paramref name="fetched"/>, or nothing at all when null.</summary>
    private static Func<CachedUtilization?> Block(DateTimeOffset? fetched) =>
        () => fetched is { } at
            ? new CachedUtilization(at, AccountUuid: null, FiveHour: null, SevenDay: null)
            : null;

    /// <summary>Records whether the guard was consulted, so "never asked" is assertable.</summary>
    private sealed class FakeGuard(string? billed = null) : IBilledTranscriptGuard
    {
        public bool Asked { get; private set; }

        public IReadOnlySet<string> Snapshot() => new HashSet<string>();

        public string? FindBilled(IReadOnlySet<string> before)
        {
            Asked = true;
            return billed;
        }
    }

    private sealed class ThrowingGuard(bool onSnapshot) : IBilledTranscriptGuard
    {
        public IReadOnlySet<string> Snapshot() =>
            onSnapshot ? throw new IOException("tree unreadable") : new HashSet<string>();

        public string? FindBilled(IReadOnlySet<string> before) =>
            throw new IOException("tree unreadable");
    }

    private static ClaudeCliRefresher Refresher(
        Func<CachedUtilization?> read,
        ClaudeCliRefresher.ProcessRun run,
        IBilledTranscriptGuard? guard = null) =>
        new(read, _ => run, guard ?? new FakeGuard());

    // ── the refresh itself ──────────────────────────────────────────────────

    [Fact]
    public void AnAdvancedFetchTimeIsARefresh()
    {
        var reads = new Queue<DateTimeOffset?>([Now.AddDays(-4), Now]);
        var refresher = Refresher(() => Block(reads.Dequeue())(), Ok);

        Assert.Equal(RefreshOutcome.Refreshed, refresher.Refresh().Outcome);
    }

    /// <summary>
    /// The state this feature exists for. On the development machine the block was 4.43 days old
    /// while <c>~/.claude.json</c> had been written twelve minutes earlier — the file moves
    /// constantly, the block does not, so "the process ran" is not evidence of anything.
    /// </summary>
    [Fact]
    public void AnUnmovedFetchTimeIsNotARefresh_EvenOnACleanExit()
    {
        var refresher = Refresher(Block(Now.AddDays(-4.43)), Ok);

        Assert.Equal(RefreshOutcome.Unchanged, refresher.Refresh().Outcome);
    }

    /// <summary>
    /// A block that exists now and did not before is the strongest form of a refresh, and an
    /// earlier draft reported it as <c>Unchanged</c> because null compares as "not greater".
    /// </summary>
    [Fact]
    public void ABlockAppearingWhereThereWasNoneIsARefresh()
    {
        var reads = new Queue<DateTimeOffset?>([null, Now]);
        var refresher = Refresher(() => Block(reads.Dequeue())(), Ok);

        Assert.Equal(RefreshOutcome.Refreshed, refresher.Refresh().Outcome);
    }

    /// <summary>
    /// The state that hid the defect: Claude Code ran, exited 0, and there is still no block
    /// to read. Reported as Unchanged until v0.9.3 — which findings/cli-usage-refresh.md
    /// explicitly teaches as ordinary, so a refresh that had never once worked logged exactly
    /// like a healthy no-op. Observed on a v0.9.1 machine whose weekly reset stayed unknown
    /// while the log said "usage refresh unchanged" on repeat.
    /// </summary>
    [Fact]
    public void ACleanRunThatWroteNoBlockIsNotUnchanged()
    {
        var refresher = Refresher(Block(null), Ok);

        Assert.Equal(RefreshOutcome.NoBlockProduced, refresher.Refresh().Outcome);
    }

    // ── the cost guard ──────────────────────────────────────────────────────

    /// <summary>
    /// <b>The test this class is for.</b> Every <c>claude -p</c> invocation writes a transcript,
    /// including the free one, so the file appearing proves nothing — what separates them is a
    /// request id, which only a run that reached the model carries.
    /// </summary>
    [Fact]
    public void ATranscriptCarryingARequestIdIsAChargeAndStopsTheFeature()
    {
        var refresher = Refresher(Block(Now.AddDays(-4)), Ok, new FakeGuard("b81aba98.jsonl"));

        var result = refresher.Refresh();

        Assert.Equal(RefreshOutcome.Billed, result.Outcome);
        Assert.True(result.IsFatal);
        Assert.False(result.Succeeded);
    }

    /// <summary>
    /// A run can refresh the block <i>and</i> be billed — that is exactly what a future Claude
    /// Code would do if it kept serving usage but stopped handling the argument locally. Reporting
    /// the refresh would let the feature keep running at ~50K tokens a poll, which is the whole
    /// failure this guards. The charge outranks the success.
    /// </summary>
    [Fact]
    public void AChargeOutranksASuccessfulRefresh()
    {
        var reads = new Queue<DateTimeOffset?>([Now.AddDays(-4), Now]);
        var refresher = Refresher(
            () => Block(reads.Dequeue())(), Ok, new FakeGuard("billed.jsonl"));

        Assert.Equal(RefreshOutcome.Billed, refresher.Refresh().Outcome);
    }

    /// <summary>
    /// A run that was billed and then timed out is still a run that was billed. The charge decides
    /// whether this may ever run again, so it is checked before the outcome is reported rather
    /// than instead of it.
    /// </summary>
    [Fact]
    public void AChargeIsReportedEvenWhenTheProcessTimedOut()
    {
        var timedOut = new ClaudeCliRefresher.ProcessRun(Started: true, Exited: false, ExitCode: 0);
        var refresher = Refresher(Block(Now), timedOut, new FakeGuard("billed.jsonl"));

        Assert.Equal(RefreshOutcome.Billed, refresher.Refresh().Outcome);
    }

    /// <summary>
    /// The one direction this check may not fail in. An unreadable transcript tree read as
    /// "nothing was billed" is a silent 50K-token-per-poll leak behind a swallowed IOException,
    /// so a throwing guard is treated as a charge and stops the feature until someone looks.
    /// </summary>
    [Fact]
    public void AThrowingCostGuardIsTreatedAsACharge()
    {
        var refresher = Refresher(Block(Now), Ok, new ThrowingGuard(onSnapshot: false));

        var result = refresher.Refresh();

        Assert.Equal(RefreshOutcome.Billed, result.Outcome);
        Assert.Contains("IOException", result.Detail);
    }

    /// <summary>
    /// A guard that cannot take a baseline cannot judge the result either. Spawning anyway would
    /// run the one operation this class exists to supervise with no way to see what it cost, so
    /// the refresh is abandoned before the process starts.
    /// </summary>
    [Fact]
    public void AGuardThatCannotTakeABaselineStopsBeforeSpawning()
    {
        var spawned = false;
        var refresher = new ClaudeCliRefresher(
            Block(Now),
            _ => { spawned = true; return Ok; },
            new ThrowingGuard(onSnapshot: true));

        var result = refresher.Refresh();

        Assert.Equal(RefreshOutcome.Failed, result.Outcome);
        Assert.Contains("guard baseline", result.Detail);
        Assert.False(spawned);
    }

    /// <summary>
    /// A process that never started cannot have been billed, and the guard must not be consulted
    /// — otherwise an unrelated transcript from a concurrent session would disable the feature on
    /// a machine that has no <c>claude</c> at all.
    /// </summary>
    [Fact]
    public void NoChargeIsLookedForWhenTheProcessNeverStarted()
    {
        var guard = new FakeGuard("billed.jsonl");
        var refresher = Refresher(
            Block(Now), new ClaudeCliRefresher.ProcessRun(false, false, 0, "Win32Exception"), guard);

        Assert.Equal(RefreshOutcome.NotFound, refresher.Refresh().Outcome);
        Assert.False(guard.Asked);
    }

    // ── failure is an outcome, never an exception ───────────────────────────

    /// <summary>
    /// Not an error. Most machines have no <c>claude</c>, and saying so is what lets the caller
    /// tell "not installed" from "installed and broken" — ADR-0010's rule that O-view never
    /// asserts something about the machine it has not observed.
    /// </summary>
    [Fact]
    public void AMissingExecutableIsNotFound_NotAFailure()
    {
        var refresher = Refresher(
            Block(Now), new ClaudeCliRefresher.ProcessRun(false, false, 0, "Win32Exception"));

        Assert.Equal(RefreshOutcome.NotFound, refresher.Refresh().Outcome);
    }

    [Fact]
    public void ATimeoutIsItsOwnOutcome()
    {
        var refresher = Refresher(Block(Now), new ClaudeCliRefresher.ProcessRun(true, false, 0));

        Assert.Equal(RefreshOutcome.TimedOut, refresher.Refresh().Outcome);
    }

    [Fact]
    public void ANonZeroExitCarriesTheCodeWithoutOutputText()
    {
        var refresher = Refresher(Block(Now), new ClaudeCliRefresher.ProcessRun(true, true, 3));

        var result = refresher.Refresh();

        Assert.Equal(RefreshOutcome.Failed, result.Outcome);
        Assert.Equal("exit 3", result.Detail);
    }

    /// <summary>
    /// This runs inside the poll loop, where an escaped exception leaves a gate held for the life
    /// of the process and every later tick is silently dropped — the failure
    /// <c>UsageEngine.RunOffThread</c> documents as observed in the field.
    /// </summary>
    [Fact]
    public void AThrowingRunnerIsAFailure_NotAnEscapedException()
    {
        var refresher = new ClaudeCliRefresher(
            Block(Now),
            _ => throw new InvalidOperationException("boom"),
            new FakeGuard());

        var result = refresher.Refresh();

        Assert.Equal(RefreshOutcome.Failed, result.Outcome);
        Assert.Equal("InvalidOperationException", result.Detail);
    }

    /// <summary>
    /// A reader that throws must not blank the outcome, only the comparison — and must not be
    /// read as NoBlockProduced either. An unreadable file is unknown (locked, mid-write,
    /// permissions), not evidence that nothing was written, so it stays Unchanged and claims
    /// nothing about the machine (rule 6).
    /// </summary>
    [Fact]
    public void AThrowingReaderDoesNotEscape()
    {
        var refresher = new ClaudeCliRefresher(
            () => throw new IOException("locked"), _ => Ok, new FakeGuard());

        Assert.Equal(RefreshOutcome.Unchanged, refresher.Refresh().Outcome);
    }

    // ── the contract that costs money to break ──────────────────────────────

    /// <summary>
    /// Pinned deliberately, tautological as it looks. This exact string is what Claude Code
    /// handles locally; anything else it does not recognise becomes a billed prompt. It was
    /// measured going wrong: run through a shell, MSYS path-translates <c>/usage</c> into
    /// <c>C:/Program Files/Git/usage</c>, which reached the model and was charged. A future edit
    /// that "tidies" this constant — a space, a flag, a prefix — has a five-figure token cost per
    /// poll and no other test would notice.
    /// </summary>
    [Fact]
    public void TheArgumentIsExactlyTheSlashCommand()
    {
        Assert.Equal("/usage", ClaudeCliRefresher.UsageArgument);
        Assert.Equal("claude", ClaudeCliRefresher.ExecutableName);
    }

    /// <summary>
    /// Claude Code names the transcript's folder after the process's working directory, so an
    /// inherited one puts a meaningless slug in the user's project list — measured, a run from a
    /// temp path produced <c>C--Users-…-Temp-claude-…-spawncheck</c>. O-view's own data directory
    /// is used instead, because the slug it produces names O-view.
    /// </summary>
    [Fact]
    public void TheWorkingDirectoryIsOViewsOwnWhenItExists()
    {
        var preferred = Directory.CreateTempSubdirectory("oview-wd-").FullName;
        try
        {
            Assert.Equal(preferred, ClaudeCliRefresher.ResolveWorkingDirectory(preferred, "fallback"));
        }
        finally
        {
            Directory.Delete(preferred);
        }
    }

    /// <summary>
    /// A working directory that does not exist makes the spawn throw, which would be reported as
    /// <see cref="RefreshOutcome.NotFound"/> — "no Claude Code on this machine", because of a
    /// missing folder of our own. The data directory is created when the rollup store opens, and
    /// this must not depend on that ordering.
    /// </summary>
    [Fact]
    public void AMissingDataDirectoryFallsBackRatherThanBreakingTheSpawn()
    {
        var absent = Path.Combine(Path.GetTempPath(), $"oview-absent-{Guid.NewGuid():N}");

        Assert.Equal("fallback", ClaudeCliRefresher.ResolveWorkingDirectory(absent, "fallback"));
    }

    /// <summary>The real one must resolve to somewhere that exists, or every spawn fails.</summary>
    [Fact]
    public void TheResolvedWorkingDirectoryExists()
    {
        Assert.True(Directory.Exists(ClaudeCliRefresher.WorkingDirectory));
    }
}
