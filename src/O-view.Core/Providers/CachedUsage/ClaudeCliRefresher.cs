using System.ComponentModel;
using System.Diagnostics;
using OView.Core.Providers.Jsonl;

namespace OView.Core.Providers.CachedUsage;

/// <summary>What an attempt to refresh Claude Code's usage cache achieved.</summary>
public enum RefreshOutcome
{
    /// <summary>The block's <c>fetchedAtMs</c> advanced. The only success.</summary>
    Refreshed,

    /// <summary>Claude Code ran and exited cleanly, but the block did not move.</summary>
    Unchanged,

    /// <summary>
    /// Claude Code ran and exited cleanly, and there is <b>still no cached block at all</b>.
    ///
    /// <para><b>Distinct from <see cref="Unchanged"/> because it is not the same event.</b>
    /// Unchanged means a block exists and did not advance, which is ordinary — Claude Code keeps
    /// its own freshness window and serves a cached answer to a second ask
    /// ([findings/cli-usage-refresh.md](../../../../docs/findings/cli-usage-refresh.md)). This
    /// means the one thing the refresh exists to produce was not produced, on a machine that has
    /// never had one. Nothing about that is ordinary, and it cannot right itself by being asked
    /// again on the same cadence.</para>
    ///
    /// <para><b>Why it needed its own name.</b> Both states came back as <c>Unchanged</c>, which
    /// the finding above explicitly teaches as "an ordinary outcome, not a failure". So a refresh
    /// that had never once worked logged exactly like a healthy no-op. Observed on a user's
    /// machine reporting v0.9.1: <c>usage refresh unchanged</c> on repeat against a
    /// <c>~/.claude.json</c> carrying no block, weekly reset unknown, and no indication anywhere
    /// that anything was wrong — until they ran <c>/usage</c> by hand and it filled in at once.
    /// </para>
    ///
    /// <para><b>Not fatal, and deliberately not.</b> The cause is not established — an old Claude
    /// Code, a trust prompt on the working directory, and a login that exits quietly all produce
    /// it — so this reports what was seen and lets the caller decide. Stopping the feature on a
    /// state whose cause is unknown would trade a silent no-op for a silent latch.</para>
    /// </summary>
    NoBlockProduced,

    /// <summary>No <c>claude</c> on PATH. Not an error — most machines do not have one.</summary>
    NotFound,

    /// <summary>Killed at the timeout. Usually a login prompt waiting on input nobody will give.</summary>
    TimedOut,

    /// <summary>Ran and failed. <c>Detail</c> carries the exit code or exception type.</summary>
    Failed,

    /// <summary>
    /// <b>The invocation was billed.</b> A transcript carrying a <c>requestId</c> appeared, which
    /// means the argument reached the model instead of being handled as a slash command. The
    /// feature must stop permanently rather than retry — see <see cref="ClaudeCliRefresher"/>.
    /// </summary>
    Billed,
}

/// <param name="Outcome">What happened.</param>
/// <param name="Detail">Exit code, exception type, or the transcript that proved a charge. Never output text.</param>
public sealed record ClaudeCliRefreshResult(RefreshOutcome Outcome, string? Detail = null)
{
    public bool Succeeded => Outcome == RefreshOutcome.Refreshed;

    /// <summary>
    /// Whether this outcome must stop the feature rather than back it off.
    ///
    /// <para><b>The latch this feeds has to be resettable.</b> The guard behind it errs toward
    /// reporting a charge — an unrelated session started during the seconds a refresh runs looks
    /// the same as a billed one — and that trade is only correct while the user can undo it. A
    /// permanent, unexplained disable would make it the wrong way round.</para>
    /// </summary>
    public bool IsFatal => Outcome == RefreshOutcome.Billed;
}

/// <summary>
/// Something that can ask Claude Code to refresh its usage cache.
///
/// <para>A seam rather than a courtesy: it lets <c>UsageEngine</c>'s cadence, latch and gating be
/// tested without spawning a process, which is the half of this feature that decides how often
/// the other half runs.</para>
/// </summary>
public interface IUsageCacheRefresher
{
    ClaudeCliRefreshResult Refresh();
}

/// <summary>
/// Asks Claude Code to refresh its own usage cache, by running the command that is the only thing
/// known to do so (GitHub issue #234).
///
/// <para><b>Why this exists.</b> <see cref="CachedUtilizationProvider"/> was added so a machine
/// with no Claude Desktop could still fill the two plan bars. It cannot, because Claude Code
/// refreshes <c>cachedUsageUtilization</c> only when <c>/usage</c> runs — not on startup, not on
/// <c>--version</c>, <c>--help</c> or <c>doctor</c>, and not on an ordinary <c>-p</c> prompt.
/// Measured on the development machine on 2026-08-28: the block was <b>4.43 days old</b> while
/// <c>~/.claude.json</c> had been written twelve minutes earlier, both <c>resets_at</c> had
/// passed, and the panel therefore showed <i>unknown</i> — which is the normal state for exactly
/// the population that provider exists to serve.
/// ([findings/cli-usage-refresh.md](../../../../docs/findings/cli-usage-refresh.md))</para>
///
/// <para><b>No credential is handled, and that is the whole design.</b> Claude Code authenticates
/// itself, as the client Anthropic approves; O-view spawns it and then reads the file it already
/// reads. Nothing is copied, stored or replayed. This is the second permitted source category in
/// [ADR-0015](../../../../docs/adr/0015-no-credential-based-usage-sources.md), and it is what
/// makes server-fresh figures reachable without touching CLAUDE.md rule 3.</para>
///
/// <para><b>The output is discarded on purpose.</b> <c>/usage</c> prints more than the cached
/// block carries, and parsing it would couple O-view to another application's terminal
/// formatting. The structured block is already parsed by <see cref="CachedUtilization"/>, so this
/// spawns to <i>refresh</i> and reads the <i>file</i> — one parser, and no dependency on text
/// layout that changes without notice.</para>
///
/// <para><b>The cost guard is not optional.</b> <c>/usage</c> is handled locally and costs
/// nothing: the invocation produced a six-line transcript holding only <c>queue-operation</c>,
/// <c>user</c>, <c>system</c> and <c>last-prompt</c> records, with no <c>requestId</c> and no
/// usage record. An <i>unrecognised</i> argument through the same entry point cost <b>49,094
/// cache-write + 97,456 cache-read + 470 output</b> tokens for one trivial exchange, because
/// Claude Code rebuilds its entire context per invocation. If a future release stops treating
/// <c>/usage</c> as a slash command, the string reaches the model and every refresh costs roughly
/// 50K tokens — several million a day on any sane cadence, spent to report usage. So the charge
/// is <i>detected</i> rather than assumed absent, and <see cref="RefreshOutcome.Billed"/> stops
/// the feature rather than backing it off.</para>
///
/// <para><b>The argument is passed as an argument, never through a shell.</b>
/// <see cref="ProcessStartInfo.ArgumentList"/> with <see cref="ProcessStartInfo.UseShellExecute"/>
/// false. Found the hard way: run through Git Bash, MSYS path-translates <c>/usage</c> into
/// <c>C:/Program Files/Git/usage</c>, which is not a slash command, reaches the model and is
/// billed. That is the failure this class exists to prevent, and it is one string away.</para>
/// </summary>
public sealed class ClaudeCliRefresher : IUsageCacheRefresher
{
    /// <summary>The executable, resolved through PATH by the OS rather than searched for here.</summary>
    public const string ExecutableName = "claude";

    /// <summary>
    /// The only argument. Exactly this string and nothing else — anything Claude Code does not
    /// recognise as a slash command becomes a billed prompt.
    /// </summary>
    public const string UsageArgument = "/usage";

    /// <summary>
    /// How long to wait before killing it.
    ///
    /// <para>Generous rather than tight, because the cost of being wrong is asymmetric: a slow
    /// machine that gets killed mid-refresh keeps its stale block and the panel keeps saying
    /// <i>unknown</i>, while waiting a few extra seconds off the UI thread costs nothing. The
    /// case this is really for is a Claude Code that wants a login and sits on stdin forever;
    /// stdin is closed, but a prompt that ignores EOF would otherwise hang the poll for good.</para>
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Working directory for the spawned process, chosen rather than inherited.
    ///
    /// <para><b>Claude Code files its transcript under a slug derived from the process's working
    /// directory</b>, so whatever O-view happens to be running in becomes a folder in the user's
    /// project list. Measured: a run from a temp path produced
    /// <c>C--Users-…-Temp-claude-…-spawncheck</c> — a directory that means nothing to anyone who
    /// finds it, in another application's data.</para>
    ///
    /// <para>Inheriting is therefore not neutral. The installed app's working directory is
    /// wherever the shell launched it, which for a startup-registered tray app is not something
    /// this code controls or can predict, and it would vary between a Start Menu launch, an
    /// autostart entry and a post-update relaunch through Explorer (ADR-0010).</para>
    ///
    /// <para>O-view's own data directory is used instead: it already exists, it is the same place
    /// on both platforms via <see cref="Environment.SpecialFolder.LocalApplicationData"/>, and
    /// the slug it produces <i>names O-view</i> — so a user who finds the folder can tell what
    /// made it. Deliberately not the user profile, which would scatter these into the project
    /// slug their real home-directory sessions already use.</para>
    /// </summary>
    public static string WorkingDirectory => ResolveWorkingDirectory(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "O-view"),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// The preferred directory when it exists, otherwise the fallback.
    ///
    /// <para>A working directory that does not exist makes <see cref="Process.Start(ProcessStartInfo)"/>
    /// throw, and that would surface as <see cref="RefreshOutcome.NotFound"/> — reporting "no
    /// Claude Code on this machine" because of a missing folder of our own. O-view creates its
    /// data directory when the rollup store opens, but this must not depend on that ordering.</para>
    /// </summary>
    public static string ResolveWorkingDirectory(string preferred, string fallback) =>
        Directory.Exists(preferred) ? preferred : fallback;

    private readonly Func<CachedUtilization?> _read;
    private readonly Func<TimeSpan, ProcessRun> _run;
    private readonly IBilledTranscriptGuard _guard;
    private readonly TimeSpan _timeout;

    /// <summary>How a run ended, independent of what it cost or changed.</summary>
    /// <param name="Started">False when the executable could not be launched at all.</param>
    /// <param name="Exited">False when it was killed at the timeout.</param>
    /// <param name="ExitCode">Meaningful only when <paramref name="Exited"/>.</param>
    /// <param name="Failure">Exception type name when the launch threw. Never a message.</param>
    public readonly record struct ProcessRun(
        bool Started, bool Exited, int ExitCode, string? Failure = null);

    /// <summary>Production wiring: the real process, the real file, the real transcripts.</summary>
    public ClaudeCliRefresher(TimeSpan? timeout = null)
        : this(() => CachedUtilization.TryRead(), Spawn, new TranscriptCostGuard(), timeout)
    {
    }

    /// <param name="read">Reads the cached block, so freshness can be compared before and after.</param>
    /// <param name="run">Runs the process. Injected so every outcome is testable without spawning.</param>
    /// <param name="guard">Decides whether the invocation was billed. Injected for the same reason.</param>
    /// <param name="timeout">Overrides <see cref="DefaultTimeout"/>.</param>
    public ClaudeCliRefresher(
        Func<CachedUtilization?> read,
        Func<TimeSpan, ProcessRun> run,
        IBilledTranscriptGuard guard,
        TimeSpan? timeout = null)
    {
        _read = read;
        _run = run;
        _guard = guard;
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>
    /// Runs one refresh. Never throws: every failure is an outcome, because this is called from
    /// the poll loop and an escaped exception there leaves a gate held for the life of the
    /// process (<c>UsageEngine.RunOffThread</c>).
    ///
    /// <para><b>The cost guard runs on every path out of a started process</b>, including a
    /// timeout and a non-zero exit. A run that was billed and then failed is still a run that
    /// was billed, and the charge is what decides whether this may ever run again — so it is
    /// checked before the outcome is reported, not instead of it.</para>
    /// </summary>
    public ClaudeCliRefreshResult Refresh()
    {
        var before = SafeFetchedAt();

        // Taken before the process starts, and by identity rather than by timestamp. A session
        // already running writes its transcript continuously and carries request ids on every
        // line, so any "changed since" filter reports a charge that never happened — see
        // TranscriptCostGuard.
        IReadOnlySet<string> existing;
        try
        {
            existing = _guard.Snapshot();
        }
        catch (Exception ex)
        {
            // Nothing has run yet, so nothing can have been billed. A guard that cannot take a
            // baseline cannot judge the result either, so the refresh is abandoned rather than
            // run unguarded — the one thing that must never happen is spawning with no way to
            // detect a charge.
            return new ClaudeCliRefreshResult(RefreshOutcome.Failed, $"guard baseline ({ex.GetType().Name})");
        }

        ProcessRun run;
        try
        {
            run = _run(_timeout);
        }
        catch (Exception ex)
        {
            return new ClaudeCliRefreshResult(RefreshOutcome.Failed, ex.GetType().Name);
        }

        if (!run.Started)
        {
            // No claude on PATH. Not a failure — most machines do not have one, and saying so
            // is what lets the caller distinguish "not installed" from "installed and broken"
            // (ADR-0010: never assert something about the machine O-view has not observed).
            return new ClaudeCliRefreshResult(RefreshOutcome.NotFound, run.Failure);
        }

        if (SafeFindBilled(existing) is { } billed)
        {
            return new ClaudeCliRefreshResult(RefreshOutcome.Billed, billed);
        }

        if (!run.Exited)
        {
            return new ClaudeCliRefreshResult(RefreshOutcome.TimedOut);
        }

        if (run.ExitCode != 0)
        {
            return new ClaudeCliRefreshResult(RefreshOutcome.Failed, $"exit {run.ExitCode}");
        }

        var after = SafeFetch();

        // Advanced, or appeared where there was nothing. Both are a refresh; a block that
        // exists now and did not before is the strongest possible version of one.
        if (after.FetchedAt is { } now && (before is not { } was || now > was))
        {
            return new ClaudeCliRefreshResult(RefreshOutcome.Refreshed);
        }

        // Ran clean and wrote nothing to find. Separated from Unchanged because the two are
        // different events wearing one name: Unchanged is a block that did not move, which is
        // ordinary, and this is the absence of the thing the run exists to create.
        //
        // Gated on the read having SUCCEEDED, not merely on it returning nothing. A file that
        // could not be opened — locked, mid-write, permissions — is unknown, and reporting it
        // as "nothing was produced" would turn a transient into a standing accusation about the
        // machine (rule 6). Unknown stays Unchanged, which claims nothing either way.
        return new ClaudeCliRefreshResult(
            after is { Readable: true, FetchedAt: null }
                ? RefreshOutcome.NoBlockProduced
                : RefreshOutcome.Unchanged);
    }

    private DateTimeOffset? SafeFetchedAt() => SafeFetch().FetchedAt;

    /// <summary>
    /// The block's fetch time, and whether the file could be read at all.
    ///
    /// <para>The second half is the whole point: "read it, there is no block" and "could not
    /// read it" both produce a null time and mean opposite things. Collapsing them is what let
    /// <see cref="RefreshOutcome.NoBlockProduced"/> be indistinguishable from a locked file.</para>
    /// </summary>
    private (bool Readable, DateTimeOffset? FetchedAt) SafeFetch()
    {
        try
        {
            return (true, _read()?.FetchedAtUtc);
        }
        catch (Exception)
        {
            return (false, null);
        }
    }

    /// <summary>
    /// A guard that throws must not be read as "nothing was billed" — that is the one direction
    /// this check may not fail in. An unreadable transcript tree is reported as a charge, which
    /// stops the feature until someone looks; the alternative is a silent 50K-token-per-poll leak
    /// behind a swallowed <see cref="IOException"/>.
    /// </summary>
    private string? SafeFindBilled(IReadOnlySet<string> before)
    {
        try
        {
            return _guard.FindBilled(before);
        }
        catch (Exception ex)
        {
            return $"cost guard unreadable ({ex.GetType().Name})";
        }
    }

    /// <summary>
    /// Runs <c>claude /usage</c> with no window, no shell and no stdin.
    ///
    /// <para>Output is redirected and drained rather than ignored: a child that fills its pipe
    /// buffer blocks on the write and never exits, so the timeout would fire on a process that
    /// had actually finished its work. It is read and discarded, never parsed.</para>
    /// </summary>
    private static ProcessRun Spawn(TimeSpan timeout)
    {
        var info = new ProcessStartInfo(ExecutableName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,

            // Chosen, not inherited — Claude Code names the transcript's folder after it.
            WorkingDirectory = WorkingDirectory,
        };

        info.ArgumentList.Add(UsageArgument);

        Process? process = null;
        try
        {
            process = Process.Start(info);
            if (process is null)
            {
                return new ProcessRun(false, false, 0, "no process");
            }

            // Nothing will be typed at it. Closing stdin turns a login prompt into a fast exit
            // rather than a wait for the full timeout.
            process.StandardInput.Close();

            // Drained on background threads so neither pipe can fill and deadlock the child.
            var drain = Task.WhenAll(
                process.StandardOutput.ReadToEndAsync(),
                process.StandardError.ReadToEndAsync());

            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                TryKill(process);
                return new ProcessRun(true, false, 0);
            }

            // Bounded: the process is gone, so the pipes are closed and this completes. A faulted
            // read is swallowed deliberately — the output is discarded either way, and letting it
            // surface as an AggregateException would report a failure for a run that succeeded.
            try
            {
                drain.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }

            return new ProcessRun(true, true, process.ExitCode);
        }
        catch (Win32Exception ex)
        {
            // The documented shape of "executable not found" on both platforms.
            return new ProcessRun(false, false, 0, ex.GetType().Name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            return new ProcessRun(false, false, 0, ex.GetType().Name);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already gone, or not ours to kill. Either way the outcome is the timeout.
        }
    }
}
