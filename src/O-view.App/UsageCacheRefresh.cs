using OView.Core.Providers.CachedUsage;

namespace OView.App;

/// <summary>
/// Asks Claude Code to refresh its own usage cache (issue #234): the floor, the freshness
/// gate against Claude Code's own cached block, and the latch that stops it on a suspected
/// charge.
///
/// <para>Split out of <see cref="UsageEngine"/> purely to shorten that file, same members,
/// same behaviour, same logging. The threading stays with the engine: a caller supplies
/// <c>runOffThread</c>, the same off-thread/dispatcher mechanics <see cref="UsageEngine"/>
/// uses for its own polls, so there is one gate-and-dispatch implementation rather than two.
/// </para>
///
/// <para>Never raises <see cref="UsageEngine.SnapshotUpdated"/> or writes
/// <see cref="UsageEngine.Latest"/> itself, a successful refresh's new figures live in a
/// file the engine's own provider chain already reads, so this only tells the engine a
/// refresh succeeded, through <see cref="Succeeded"/>, and leaves scheduling the resulting
/// poll to it.</para>
/// </summary>
public sealed class UsageCacheRefresh
{
    private readonly IUsageCacheRefresher? _refresher;
    private readonly TimeSpan _refreshFloor;
    private readonly IClock _clock;
    private readonly IAppLog? _log;
    private readonly Func<CachedUtilization?> _cachedUtilizationSource;
    private readonly Action<Func<ClaudeCliRefreshResult>, Action<ClaudeCliRefreshResult>> _runOffThread;

    /// <summary>When the last attempt <i>started</i>, so a slow one still spaces the next.</summary>
    private DateTimeOffset? _lastRefreshAttempt;

    /// <summary>
    /// Why refreshing is switched off, or null while it is allowed. Set when an invocation is
    /// found to have been billed, and cleared only by <see cref="ResumeUsageRefresh"/>.
    ///
    /// <para><b>Resettable on purpose, and that is load-bearing rather than a nicety.</b> The
    /// guard behind it errs toward reporting a charge: a Claude Code session the user starts
    /// during the seconds a refresh runs produces a new transcript carrying request ids, and
    /// nothing distinguishes it from a billed refresh. That direction is right, a false stop is
    /// recoverable and a missed charge leaks roughly 50K tokens per poll silently, but only
    /// while it can be undone. A latch with no way out would turn a rare race into a permanent,
    /// unexplained loss of the feature.</para>
    /// </summary>
    private string? _refreshBlocked;

    public UsageCacheRefresh(
        IUsageCacheRefresher? refresher,
        TimeSpan refreshFloor,
        IClock clock,
        IAppLog? log,
        Func<CachedUtilization?> cachedUtilizationSource,
        Action<Func<ClaudeCliRefreshResult>, Action<ClaudeCliRefreshResult>> runOffThread)
    {
        _refresher = refresher;
        _refreshFloor = refreshFloor;
        _clock = clock;
        _log = log;
        _cachedUtilizationSource = cachedUtilizationSource;
        _runOffThread = runOffThread;
    }

    /// <summary>
    /// Why usage refreshing is switched off, or null while it is allowed. Rendered by the head
    /// so a user can see the feature stopped and why, rather than watching it quietly not work.
    /// </summary>
    public string? UsageRefreshBlocked => _refreshBlocked;

    /// <summary>Whether a refresher was supplied at all, the feature being on, not it being allowed.</summary>
    public bool CanRefreshUsageCache => _refresher is not null;

    /// <summary>
    /// How many refresh attempts have actually been started, not how many times
    /// <see cref="RefreshUsageCache"/> was called, since most calls are turned away by the
    /// floor, the freshness gate or the latch.
    ///
    /// <para>Exists so a verification hook can assert the panel-open path <i>reached</i> the
    /// refresher, rather than inferring it from a log line. That distinction is the whole gap
    /// issue #234 stayed open for: the composition behind the click is tested, the click itself
    /// was not.</para>
    /// </summary>
    public int UsageRefreshAttempts { get; private set; }

    /// <summary>Raised when an attempt succeeds, so the engine can schedule a full poll.</summary>
    public event Action? Succeeded;

    /// <summary>
    /// Clears the block set by a suspected charge and allows refreshing again.
    ///
    /// <para>The other half of the trade in <see cref="_refreshBlocked"/>. The guard errs toward
    /// stopping, which is only the right direction while stopping is undoable, so this exists
    /// for the user to say the charge was a concurrent session rather than a real one.</para>
    /// </summary>
    /// <returns>True when something was actually cleared.</returns>
    public bool ResumeUsageRefresh()
    {
        if (_refreshBlocked is null)
        {
            return false;
        }

        _log?.Write($"usage refresh resumed (was blocked: {_refreshBlocked})");
        _refreshBlocked = null;

        // The floor is not re-applied. A user who has just re-enabled this is asking for it now,
        // and making them wait out a 15-minute window they cannot see would read as still broken.
        _lastRefreshAttempt = null;
        return true;
    }

    /// <summary>
    /// Asks Claude Code to refresh its usage cache, if it is allowed and due.
    ///
    /// <para><b>Call this when the panel opens.</b> That is the moment freshness is observable,
    /// and it is what makes this event-driven rather than another timer, a tray gauge only has
    /// to be right when someone looks at it. <paramref name="force"/> bypasses the floor for
    /// exactly that case: a person opening the panel is not a poll.</para>
    ///
    /// <para><b>Ten panel opens in quick succession are still ten attempts here</b>, nothing in
    /// this cadence collapses them, and nothing should: that is what lets a person who opens the
    /// panel ten times in a row get ten honest looks at Claude Code's own freshness window rather
    /// than nine silently skipped. They stop piling up ten separate session files one layer
    /// down instead, where <c>ClaudeCliRefresher</c> continues one Claude Code session across
    /// repeat invocations rather than starting a new one each time.</para>
    ///
    /// <para>Silently does nothing when no refresher was supplied, when a suspected charge has
    /// blocked it, when the floor has not elapsed, or when one is already running. All four are
    /// ordinary, and none is an error worth surfacing.</para>
    /// </summary>
    public void RefreshUsageCache(bool force = false)
    {
        if (_refresher is null || _refreshBlocked is not null)
        {
            return;
        }

        var utcNow = _clock.UtcNow;

        if (!force && _lastRefreshAttempt is { } last && utcNow - last < _refreshFloor)
        {
            return;
        }

        // Nothing to gain from spawning a process to refresh something already fresher than the
        // floor, the user may have run /usage themselves, which is the one other thing that
        // moves this block.
        //
        // Note what this deliberately does NOT do: skip while a Claude Code session is running.
        // That was the plan until it was measured. A running session does not maintain this
        // block, the development machine held a 4.43-day-old one while Claude Code had been
        // running all morning, so "a session is open" says nothing about freshness.
        if (!force && CachedBlockAge(utcNow) is { } age && age < _refreshFloor)
        {
            return;
        }

        // Stamped before the run rather than after, so a slow or hung attempt still spaces the
        // next one. Stamping on completion would let a run that takes the full timeout be
        // followed immediately by another.
        _lastRefreshAttempt = utcNow;
        UsageRefreshAttempts++;

        _runOffThread(RunRefresh, ApplyRefresh);
    }

    /// <summary>
    /// Runs off the UI thread. It spawns a process and waits up to
    /// <see cref="ClaudeCliRefresher.DefaultTimeout"/> for it, done on the dispatcher that is a
    /// frozen tray icon for the duration, which is issue #125's lesson applied to a new caller.
    /// </summary>
    private ClaudeCliRefreshResult RunRefresh() =>
        _refresher!.Refresh();

    /// <summary>
    /// How old Claude Code's cached block is, or null when that cannot be established, no file,
    /// unreadable, or a test that described its own world and left this source returning null.
    ///
    /// <para>Null means "do not gate on this", not "stale". An unknown age must not block a
    /// refresh, or the feature would switch itself off on exactly the machines whose file it
    /// cannot read.</para>
    /// </summary>
    private TimeSpan? CachedBlockAge(DateTimeOffset utcNow)
    {
        try
        {
            return _cachedUtilizationSource() is { } cached
                ? utcNow - cached.FetchedAtUtc
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Back on the UI thread with the outcome.
    ///
    /// <para>A successful refresh raises <see cref="Succeeded"/> rather than reading here: the
    /// new figures live in a file the engine's own composite chain already reads, and re-reading
    /// it here would put the transcript walk back on the dispatcher. The engine's own poll only
    /// schedules.</para>
    /// </summary>
    private void ApplyRefresh(ClaudeCliRefreshResult result)
    {
        if (result.IsFatal)
        {
            // The one outcome that stops rather than backs off. Logged in full because the next
            // thing that happens is a feature going quiet, and a user asking why deserves an
            // answer that names the transcript.
            _refreshBlocked = result.Detail is { Length: > 0 } detail
                ? $"an invocation appears to have been billed ({detail})"
                : "an invocation appears to have been billed";

            _log?.Write($"usage refresh BLOCKED, {_refreshBlocked}");
            return;
        }

        _log?.Write($"usage refresh {Describe(result.Outcome)}" +
                    (result.Detail is { Length: > 0 } d ? $" ({d})" : ""));

        if (result.Succeeded)
        {
            Succeeded?.Invoke();
        }
    }

    /// <summary>
    /// The outcome as the log should carry it.
    ///
    /// <para>Every other outcome is legible as its own name. <see cref="RefreshOutcome.NoBlockProduced"/>
    /// is not, and it is the one a reader most needs to understand: it is the state that used to
    /// be indistinguishable from a healthy no-op, so the line has to say what it means rather
    /// than name an enum. The log is what a bug report carries, one round trip should be enough
    /// to see this (CLAUDE.md).</para>
    /// </summary>
    private static string Describe(RefreshOutcome outcome) => outcome switch
    {
        RefreshOutcome.NoBlockProduced =>
            "ran but wrote no usage block, /usage is not refreshing on this machine",
        _ => outcome.ToString().ToLowerInvariant(),
    };
}
