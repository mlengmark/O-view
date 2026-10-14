using OView.App.Platform;
using OView.App.Updates;
using OView.Core.Models;
using OView.Core.Providers;
using OView.Core.Providers.CachedUsage;
using OView.Core.Providers.Jsonl;
using OView.Core.Providers.PlanHistory;
using OView.Core.Storage;

namespace OView.App;

/// <summary>
/// The app, minus its face.
///
/// <para>Owns provider composition, the polling loop and its cadence, the rollup store and
/// weekly-reset log lifecycle, threshold and off-plan notification decisions, settings, and
/// the update-check schedule. None of that is platform-specific, so both heads sit on it
/// rather than each growing their own copy — which is how the polling cadence, the
/// threshold semantics and the reset-log migration would quietly drift apart (ADR-0012).</para>
///
/// <para>A refresh failure keeps the previous state; it never crashes the app. A monitoring
/// tool that dies on a bad poll is worse than one that shows a stale number.</para>
///
/// <para>What it does <b>not</b> do: draw anything, decide how a notification looks, or
/// touch the network. It says <em>what</em> happened and lets a head decide what that looks
/// like.</para>
///
/// <para>Also the shell's implementation of <see cref="ISkinToShell"/> — a skin may depend
/// on that interface instead of this concrete class, with no change in behaviour.</para>
/// </summary>
public sealed class UsageEngine : IDisposable, ISkinToShell
{
    private readonly RollupStore _store;
    private readonly IUsageProvider _provider;
    private readonly PlanHistoryProvider? _planHistory;

    /// <summary>
    /// Claude Code's cached usage figures, held directly as well as through the chain — see
    /// <see cref="WithReportedResets"/> for why the chain alone is not enough.
    /// </summary>
    private readonly IUsageProvider _cachedUtilization;
    private readonly UsageEngineOptions _options;
    private readonly IClock _clock;
    private readonly IAppLog? _log;
    private readonly TimeSpan _normalInterval;
    private readonly TimeSpan _warmupInterval;
    private readonly TimeSpan _planInterval;

    private readonly List<IAppTimer> _timers = [];
    private IAppTimer? _pollTimer;

    /// <summary>The cheap plan-history cadence. Fixed — <see cref="AdjustCadence"/> re-times
    /// only the full poll, whose warm-up is already faster than this.</summary>
    private IAppTimer? _planTimer;

    private DateTimeOffset _startedAt;

    /// <summary>Where a scheduled poll publishes back to. Null means "publish inline".</summary>
    private IUiDispatcher? _dispatcher;

    /// <summary>
    /// One gate per cadence, so a read already in flight cannot stack another behind it.
    /// They are deliberately separate — see <see cref="RunOffThread{T}"/>.
    /// </summary>
    private readonly PollGate _fullGate = new();
    private readonly PollGate _planGate = new();

    /// <summary>
    /// Its own gate, for the same reason the other two have theirs: this one spawns a process
    /// and can take seconds, and sharing a gate would let it swallow the ticks of whichever
    /// cadence it borrowed.
    /// </summary>
    private readonly PollGate _refreshGate = new();

    /// <summary>
    /// The floor, the freshness gate and the latch behind <see cref="RefreshUsageCache"/>
    /// (issue #234) — its own class purely to keep this file shorter. See
    /// <see cref="UsageCacheRefresh"/>.
    /// </summary>
    private readonly UsageCacheRefresh _cacheRefresh;

    /// <summary>
    /// The manual override, the reported instant and the conflict between them (ADR-0014) —
    /// its own class purely to keep this file shorter. See <see cref="WeeklyResetCoordinator"/>.
    /// </summary>
    private readonly WeeklyResetCoordinator _weeklyReset;

    /// <summary>
    /// The plan-history cadence and the panel's statistics — its own class purely to keep this
    /// file shorter. See <see cref="PlanHistoryPoll"/>.
    /// </summary>
    private readonly PlanHistoryPoll _planHistoryPoll;

    /// <summary>
    /// The threshold and off-plan notification decisions — its own class purely to keep this
    /// file shorter. See <see cref="UsageAlerts"/>.
    /// </summary>
    private readonly UsageAlerts _alerts;

    /// <summary>Raw reader for Claude Code's cached figures, used only to harvest the anchor.</summary>
    private readonly Func<CachedUtilization?> _cachedUtilizationSource;

    /// <summary>Most recent snapshot — what the panel opens with.</summary>
    public UsageSnapshot Latest { get; private set; } = UsageSnapshot.None;

    /// <summary>
    /// The rollup store as this engine sees it, for the support bundle.
    ///
    /// <para>Exposed so a bundle produced by the <i>running</i> app reports the store through
    /// the connection that app is actually using, rather than through one opened for the
    /// occasion. Those two should agree; when they do not, that is the finding.</para>
    /// </summary>
    public RollupStoreReport InspectStore() => _store.Inspect();

    /// <summary>
    /// Which plan-history file this engine resolved to (issue #212).
    ///
    /// <para>Exposed so the resolution can be asserted rather than inferred from the figures:
    /// reaching past an injected provider into the real machine's file only ever showed as an
    /// outcome, which is why it survived a suite that runs on every commit. A test can now say
    /// "not that file" and be right on every machine on every day.</para>
    /// </summary>
    public string PlanHistoryPath => (_planHistory as PlanHistoryProvider)?.Path ?? "";

    /// <summary>
    /// How many <b>plan-history</b> samples the current window holds. Zero when there is no
    /// plan history to read — the state every test with an injected provider chain must be in
    /// (issue #212).
    ///
    /// <para>Deliberately not the size of the series the detector evaluates, which may carry
    /// one more (<see cref="MeterSeries.WithReportedReading"/>). This exists to assert what
    /// was read from <i>this file</i>, so folding the other source in would blunt the
    /// isolation check it is here for.</para>
    /// </summary>
    public int PlanWindowSampleCount => _planHistoryPoll.WindowSampleCount;

    /// <summary>Current persisted settings. Mutated only through <see cref="SetNotifyOnThreshold"/>.</summary>
    public TraySettings Settings { get; private set; }

    /// <summary>Raised after every refresh attempt that produced a snapshot. The head redraws from this.</summary>
    public event Action<UsageSnapshot>? SnapshotUpdated;

    /// <summary>The engine has decided the user should be told something. The head decides how it appears.</summary>
    public event Action<AppNotification>? NotificationRequested;

    /// <summary>Time to check for a newer release (ADR-0009). The head owns the HTTP and the UI.</summary>
    public event Action? UpdateCheckDue;

    /// <summary>
    /// Time to compare the bundled rate card against Anthropic's published table (ADR-0016).
    /// Same shape as <see cref="UpdateCheckDue"/> for the same reason: the engine owns the
    /// <i>when</i> and touches no network.
    /// </summary>
    public event Action? RateCheckDue;

    public UsageEngine(UsageEngineOptions? options = null)
    {
        _options = options ?? new UsageEngineOptions();
        _clock = _options.Clock;
        _log = _options.Log;
        _normalInterval = _options.PollInterval;
        _warmupInterval = Min(_options.WarmupInterval, _options.PollInterval);

        // Clamped for the same reason the warm-up is, and it is the same diagnostic that
        // forces it: --interval-ms drives PollInterval alone, so a sub-20 s value would leave
        // the cheap read as the SLOWER of the two — the plan-history cadence running behind
        // the full poll it exists to run ahead of.
        _planInterval = Min(_options.PlanPollInterval, _options.PollInterval);

        _store = new RollupStore(_options.RollupDbPath);

        // What stood beside the database when it opened (issue #213). Logged whenever it is
        // anything other than the ordinary case, because a guard that quarantines a file
        // without saying so is the same class of problem as the silent rollback it prevents —
        // and "the store was open elsewhere, so nothing was checked" has to be as visible as a
        // quarantine, or an unmade check reads as a clean one.
        if (_store.JournalGuard.IsNoteworthy)
        {
            _log?.Write($"rollup journal: {_store.JournalGuard.Describe()}");
        }

        // Observed weekly resets accrue in their own durable file, not in the rollup store
        // (ADR-0011): the store is a rebuildable cache that wipes itself on corruption,
        // whereas a missed reset costs a week. Older builds kept them in the store, so any
        // rows it still holds are carried across on the way past — idempotent, so this is
        // safe to run on every launch.
        var data = _options.ClaudeData;
        var account = ClaudeAccount.TryRead(data?.AccountFile);

        // Every Core seam below takes the log as a delegate rather than an interface, so the
        // accounting layer keeps knowing nothing about how this app logs. What they all now
        // share is that their failures are recorded instead of merely survived.
        var logLine = _log is null ? (Action<string>?)null : _log.Write;

        // Deliberately not the CachedUtilization provider: that one drops a resets_at once it
        // has passed, and a passed instant is precisely what the anchor is made of.
        _cachedUtilizationSource = _options.CachedUtilizationSource
            ?? (_options.Provider is null
                ? () => CachedUtilization.TryRead(data?.CachedUsageFile)
                : () => null);

        _cacheRefresh = new UsageCacheRefresh(
            _options.UsageCacheRefresher,
            _options.UsageRefreshFloor,
            _clock,
            _log,
            _cachedUtilizationSource,
            (read, publish) => RunOffThread(_refreshGate, "usage refresh", read, publish));
        _cacheRefresh.Succeeded += Poll;

        // A caller that supplied its own Provider has described the whole world, so an
        // unnamed plan history reads NOTHING rather than the real machine's file (issue #212).
        //
        // The default used to be PlanHistoryFile.DefaultPath in that case, and the guard
        // against it — PlanHistoryPoll's own addressed flag — covered only *publishing*.
        // UsageAlerts.ReadOffPlan() →
        // BuildStatistics() went through this provider regardless, so the off-plan comparison
        // read the developer's own Claude Desktop usage on every poll in every test that did
        // not name a file. Replayed against that file afterwards: six instants where a test
        // with an empty rollup store would have seen the plan meter pinned at 99% and reported
        // usage billing beyond the plan, all within two hours on 2026-08-25 — which is when
        // eight of them did.
        //
        // Same fix, and the same reasoning, as CachedUtilization gets a few lines below.
        _planHistory = _options.PlanHistory ?? new PlanHistoryProvider(
            path: data?.PlanHistoryFile
                ?? _options.PlanHistoryPath
                ?? (_options.Provider is null ? null : PlanHistoryFile.NoFile),
            orgUuid: account?.OrganizationUuid,
            // Local request times tighten the five-hour window's start bracket (issue #185).
            // The engine owns both halves, so the wiring lives here rather than teaching the
            // plan-history provider about the rollup store.
            earliestActivity: _store.EarliestRequestBetween)
        {
            Log = logLine,
        };

        // Claude Code's own cached figures — real percentages where the JSONL estimate has
        // none, and on a machine without Desktop the only source of the top two bars at all.
        //
        // Position here is a tie-break, not a precedence: the composite picks whichever
        // snapshot reports the most meters, most recently. Desktop samples every ~5 minutes
        // while this refreshes on use, so neither is reliably the fresher one and neither
        // deserves a standing preference.
        //
        // A caller that supplies its own Provider has described the whole world, so the real
        // file is left alone unless it also supplies this. Without that, WithReportedResets
        // would read the developer's own ~/.claude.json during tests and fold live reset times
        // onto fixtures — passing on a CI runner, which has no such file, and failing on the
        // machine of whoever last used Claude Code. Reaching past an injected provider to real
        // user data is the bug, not the test that caught it.
        _cachedUtilization = _options.CachedUtilization ?? (_options.Provider is null
            ? new CachedUtilizationProvider(data?.CachedUsageFile)
            : new CachedUtilizationProvider(() => null));

        var transcripts = data is null
            ? new JsonlUsageProvider(_store) { Log = logLine }
            : new JsonlUsageProvider(_store, data.ProjectsRoot, data.CoworkRoots, data.SessionRegistryRoots) { Log = logLine };

        _provider = _options.Provider ?? new CompositeUsageProvider(
            _planHistory,
            _cachedUtilization,
            transcripts)
        {
            Log = logLine,
        };

        // Whether the plan-history provider just built is one the caller actually asked for,
        // and so may be published from directly — see PlanHistoryPoll's own doc for why this
        // matters. False in exactly one case: a caller injected a whole Provider and said
        // nothing about plan history.
        var planHistoryIsAddressed = _options.Provider is null
            || _options.PlanHistory is not null
            || _options.PlanHistoryPath is not null;

        Settings = TraySettings.Load(_options.SettingsPath);

        _weeklyReset = new WeeklyResetCoordinator(
            _options.WeeklyResetAnchorPath,
            _cachedUtilizationSource,
            _cachedUtilization,
            account?.OrganizationUuid,
            _log,
            () => Settings,
            settings =>
            {
                Settings = settings;
                Settings.Save(_options.SettingsPath);
            });
        _weeklyReset.ApplyWeeklyResetSetting();

        _alerts = new UsageAlerts(
            _planHistory is not null,
            BuildStatistics,
            () => CachedUsage?.ExtraUsage.State ?? ExtraUsageState.Unknown,
            _log,
            () => Settings,
            settings =>
            {
                Settings = settings;
                Settings.Save(_options.SettingsPath);
            },
            notification => NotificationRequested?.Invoke(notification));

        _planHistoryPoll = new PlanHistoryPoll(
            _planHistory,
            planHistoryIsAddressed,
            _store,
            _cachedUtilization,
            _weeklyReset,
            _clock,
            _log,
            _options.DisplayZone,
            _options.SimulateDivergence,
            () => Latest,
            snapshot =>
            {
                Latest = snapshot;
                SnapshotUpdated?.Invoke(snapshot);
                _alerts.NotifyOnThreshold(snapshot);
            },
            (read, publish) => RunOffThread(_planGate, "plan poll", read, publish));
    }

    /// <summary>
    /// Applies and persists the user's weekly reset time, returning what is now in effect.
    /// Null clears it and returns to deriving.
    /// </summary>
    public ManualWeeklyReset? SetWeeklyReset(ManualWeeklyReset? reset) => _weeklyReset.SetWeeklyReset(reset);

    /// <summary>
    /// The reset instant Claude reported that disproves the entered weekly reset, or null.
    /// Surfaced so the head can tell the user once — silently overriding a wrong entry leaves
    /// them believing the number they typed.
    /// </summary>
    public DateTimeOffset? WeeklyResetConflict => _weeklyReset.WeeklyResetConflict;

    /// <summary>
    /// Records that the user has been told about <paramref name="conflict"/>, so the notice
    /// fires once per conflicting instant rather than on every poll.
    /// </summary>
    public void MarkWeeklyResetConflictNoticed(DateTimeOffset conflict) =>
        _weeklyReset.MarkWeeklyResetConflictNoticed(conflict);

    /// <summary>Whether this conflict is new to the user.</summary>
    public bool IsWeeklyResetConflictUnseen(DateTimeOffset conflict) =>
        _weeklyReset.IsWeeklyResetConflictUnseen(conflict);

    /// <summary>
    /// Starts polling. Refreshes once immediately so the first result sets the cadence,
    /// then schedules the poll timer and the update-check timers.
    /// </summary>
    public void Start(ITimerFactory timers, IUiDispatcher? dispatcher = null)
    {
        _startedAt = _clock.UtcNow;
        _dispatcher = dispatcher;

        _pollTimer = Track(timers.Create(_normalInterval, Poll));
        _log?.Write($"startup interval={_normalInterval.TotalSeconds}s");

        Poll();  // sets the initial cadence from the first result
        _pollTimer.Start();

        // The cheap read, on its own faster cadence (issue #163). Started after the first full
        // poll so the opening snapshot still comes from the composite provider — that one has
        // to be able to fall back to the JSONL estimate, which this path deliberately cannot.
        //
        // Not re-timed by AdjustCadence: the warm-up exists to fill the bars quickly before
        // Desktop has written anything, and at 3 s it is already faster than this. Leaving
        // this one fixed keeps two independently-tuned cadences from fighting over one timer.
        _planTimer = Track(timers.Create(_planInterval, _planHistoryPoll.PollPlanHistory));
        _planTimer.Start();
        _log?.Write($"plan-history interval={_planInterval.TotalSeconds}s");

        // Auto-update (ADR-0009 as amended): one check shortly after launch, then every six
        // hours. The engine owns only the *when*; the head performs the check and surfaces
        // the result.
        IAppTimer? initial = null;
        initial = Track(timers.Create(_options.FirstUpdateCheckDelay, () =>
        {
            initial!.Stop();
            UpdateCheckDue?.Invoke();
        }));
        initial.Start();

        // Jittered once, here, so two instances that start together do not stay in step for
        // as long as they run. The rate limit is per IP, not per user, so synchronised
        // instances behind one address are the one way this cadence could cost anything.
        var recheck = UpdateSchedule.Jittered(_options.UpdateCheckInterval, _options.UpdateJitter);
        Track(timers.Create(recheck, () => UpdateCheckDue?.Invoke())).Start();
        _log?.Write($"update check interval={recheck.TotalMinutes:0} min (base {_options.UpdateCheckInterval.TotalMinutes:0})");

        // The rate-card drift check (ADR-0016), on the same two-timer shape and through the
        // same jitter helper — the endpoint is public and unauthenticated, so the reason to
        // scatter a fleet is the courtesy one rather than a rate limit, and it costs nothing.
        IAppTimer? firstRateCheck = null;
        firstRateCheck = Track(timers.Create(_options.FirstRateCheckDelay, () =>
        {
            firstRateCheck!.Stop();
            RateCheckDue?.Invoke();
        }));
        firstRateCheck.Start();

        var rateRecheck = UpdateSchedule.Jittered(_options.RateCheckInterval, _options.UpdateJitter);
        Track(timers.Create(rateRecheck, () => RateCheckDue?.Invoke())).Start();
        _log?.Write($"rate check interval={rateRecheck.TotalHours:0} h (base {_options.RateCheckInterval.TotalHours:0})");
    }

    /// <summary>
    /// One poll, start to finish on the calling thread: read a snapshot, publish it, then
    /// evaluate the notification rules.
    ///
    /// <para>Publish-before-notify is deliberate and matches the order the tray has always
    /// used — the icon reflects the new number before any balloon talks about it.</para>
    ///
    /// <para>Synchronous, so a caller that needs <see cref="Latest"/> to be current when it
    /// returns gets that. The scheduled polls do <b>not</b> use this — see
    /// <see cref="Poll"/>.</para>
    /// </summary>
    public void Refresh() => Publish(Read());

    /// <summary>
    /// A scheduled poll: read off the UI thread, publish back onto it.
    ///
    /// <para>The read is file discovery, JSON parsing and SQLite writes, and its cost
    /// scales with total transcript history rather than with new activity on a first run.
    /// Done on the dispatcher, that froze the tray icon and the menu for the whole ingest
    /// on the first machine that had a large enough history (issue #125). Done here, the
    /// UI thread only ever runs <see cref="Publish"/>, which raises events and compares
    /// numbers.</para>
    ///
    /// <para>With no dispatcher the engine has no separate UI thread to protect and runs
    /// inline, which is what the tests rely on.</para>
    /// </summary>
    private void Poll() => RunOffThread(_fullGate, "poll", Read, Publish);

    /// <summary>
    /// Why usage refreshing is switched off, or null while it is allowed. Rendered by the head
    /// so a user can see the feature stopped and why, rather than watching it quietly not work.
    /// </summary>
    public string? UsageRefreshBlocked => _cacheRefresh.UsageRefreshBlocked;

    /// <summary>Whether a refresher was supplied at all — the feature being on, not it being allowed.</summary>
    public bool CanRefreshUsageCache => _cacheRefresh.CanRefreshUsageCache;

    /// <summary>
    /// How many refresh attempts have actually been started — not how many times
    /// <see cref="RefreshUsageCache"/> was called, since most calls are turned away by the
    /// floor, the freshness gate or the latch.
    ///
    /// <para>Exists so a verification hook can assert the panel-open path <i>reached</i> the
    /// refresher, rather than inferring it from a log line. That distinction is the whole gap
    /// issue #234 stayed open for: the composition behind the click is tested, the click itself
    /// was not.</para>
    /// </summary>
    public int UsageRefreshAttempts => _cacheRefresh.UsageRefreshAttempts;

    /// <summary>
    /// Clears the block set by a suspected charge and allows refreshing again.
    ///
    /// <para>The other half of the trade behind the latch. The guard errs toward stopping, which
    /// is only the right direction while stopping is undoable — so this exists for the user to
    /// say the charge was a concurrent session rather than a real one.</para>
    /// </summary>
    /// <returns>True when something was actually cleared.</returns>
    public bool ResumeUsageRefresh() => _cacheRefresh.ResumeUsageRefresh();

    /// <summary>
    /// Asks Claude Code to refresh its usage cache, if it is allowed and due.
    ///
    /// <para><b>Call this when the panel opens.</b> That is the moment freshness is observable,
    /// and it is what makes this event-driven rather than another timer — a tray gauge only has
    /// to be right when someone looks at it. <paramref name="force"/> bypasses the floor for
    /// exactly that case: a person opening the panel is not a poll.</para>
    ///
    /// <para>Silently does nothing when no refresher was supplied, when a suspected charge has
    /// blocked it, when the floor has not elapsed, or when one is already running. All four are
    /// ordinary, and none is an error worth surfacing.</para>
    /// </summary>
    public void RefreshUsageCache(bool force = false) => _cacheRefresh.RefreshUsageCache(force);

    /// <summary>
    /// Reads off the UI thread and publishes back onto it, under a gate so a slow read cannot
    /// stack another behind it.
    ///
    /// <para>Shared by both cadences rather than written twice. The full poll and the
    /// plan-only poll differ in <i>what</i> they read and publish; the threading, the
    /// drop-don't-queue rule and the shutdown handling are identical, and this file's history
    /// is one of behaviour written twice and fixed once.</para>
    ///
    /// <para>Each cadence carries its OWN gate. Sharing one would let a first-run ingest —
    /// seconds long — swallow every plan-history tick underneath it, which is precisely the
    /// coupling being removed.</para>
    ///
    /// <para><b>The gate is released on every path out of the read, including a throw.</b>
    /// It has to be: nothing else on this path releases it, so one escaped exception left
    /// <c>_busy</c> set for the life of the process and every later tick was dropped by
    /// <see cref="PollGate.TryEnter"/>. That failure is silent and total — the panel goes on
    /// drawing its last snapshot, no transcript is ingested, no observed weekly reset is
    /// written, and because <c>_log</c> is null unless <c>--log</c> was passed there is
    /// nothing on disk to say why. Observed in the field: a tray up for 35 minutes with a
    /// 60 s cadence, 409 KB of unread transcript, and a rollup store whose newest row was
    /// five days old while the store itself passed every health check.</para>
    /// </summary>
    private void RunOffThread<T>(PollGate gate, string what, Func<T> read, Action<T> publish)
    {
        if (_dispatcher is not { } dispatcher)
        {
            // No separate UI thread to protect — run inline, which is what the tests rely on.
            publish(read());
            return;
        }

        // A poll slower than the interval must not stack another behind it. Dropping the
        // tick is right rather than queueing it: the next one reads the same files and
        // reports the same state, so a queue would only replay stale work.
        if (!gate.TryEnter())
        {
            _log?.Write($"{what} skipped — previous still running");
            return;
        }

        // Three lines per poll, and the gaps between them are the diagnosis. A tick that
        // never logs "read begin" means the timer is not firing; "read begin" with no "read
        // done" means the read is hung or threw; "read done" with no "published" means the
        // dispatcher is not running the callback — and each of those is a different bug in a
        // different layer. Without them a stalled poll and a healthy idle one are the same
        // silence, which is how five days of no ingestion went unnoticed.
        var startedAt = Environment.TickCount64;
        _log?.Write($"{what} read begin");

        _ = Task.Run(() =>
        {
            T result;
            try
            {
                result = read();
                _log?.Write($"{what} read done in {Environment.TickCount64 - startedAt} ms");
            }
            catch (Exception ex)
            {
                // Both reads already guard themselves and return a failure result rather
                // than throwing — but each only guards what sits inside its own try, and
                // neither covers what it touches before entering one. This is the backstop
                // for that gap, and for whatever a future read reaches for first.
                //
                // It is not defensive padding: the gate is released nowhere else on this
                // path, so without this the cadence is dead for the life of the process
                // after a single throw, and Task.Run discards the exception so nothing can
                // even report it. Releasing here costs one dropped tick instead.
                gate.Exit();
                _log?.Write($"{what} read FAILED {ex.GetType().Name}: {ex.Message}");
                return;
            }

            try
            {
                dispatcher.Post(() =>
                {
                    try
                    {
                        publish(result);
                        _log?.Write($"{what} published after {Environment.TickCount64 - startedAt} ms");
                    }
                    finally
                    {
                        gate.Exit();
                    }
                });
            }
            catch (Exception ex)
            {
                // The dispatcher is gone — the app is shutting down. Release the gate so
                // nothing is wedged, and drop the result: there is no longer anything to
                // draw it on.
                gate.Exit();
                _log?.Write($"{what} publish skipped {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// One cadence's "a read is already in flight" flag. A type rather than an <c>int</c>
    /// field because there are now two of them and <c>Interlocked</c> on a field cannot be
    /// passed to the shared runner.
    /// </summary>
    private sealed class PollGate
    {
        private int _busy;

        public bool TryEnter() => Interlocked.Exchange(ref _busy, 1) == 0;

        public void Exit() => Volatile.Write(ref _busy, 0);
    }

    /// <summary>
    /// Everything a poll needs from disk, gathered on whatever thread is doing the reading.
    ///
    /// <para>The off-plan figures are built here rather than in <see cref="Publish"/>
    /// because they are a 31-day SQLite aggregate plus a plan-history parse — the second
    /// most expensive thing a poll does, and it used to run on the UI thread too.</para>
    ///
    /// <para>Never throws: a monitoring tool must not die on a bad poll, and a failure has
    /// to reach <see cref="Publish"/> intact so the cadence still gets re-timed.</para>
    /// </summary>
    private PollResult Read()
    {
        var utcNow = _clock.UtcNow;

        try
        {
            // Order matters. The entry fills a gap; the reported timestamps then override
            // whatever is there, entry included — see WithReportedResets.
            var snapshot = _weeklyReset.WithWeeklyReset(_provider.GetSnapshot(utcNow), utcNow);
            snapshot = _weeklyReset.WithReportedResets(snapshot, utcNow);
            return new PollResult(snapshot, _alerts.ReadOffPlan(), utcNow, Failed: false);
        }
        catch (Exception ex)
        {
            _log?.Write($"refresh FAILED {ex.GetType().Name}: {ex.Message}");
            return new PollResult(UsageSnapshot.None, null, utcNow, Failed: true);
        }
    }

    /// <summary>
    /// Claude Code's cached block as it stands now, or null when there is none to read —
    /// which is also what a caller that injected its own <c>Provider</c> gets, because the
    /// source is wired to return null for those (see the constructor).
    ///
    /// <para>Exposed so the panel can read the extra-usage setting and the instant it was
    /// fetched from the same place the engine does. Reading the file again in the head would
    /// be a second answer to one question, and the head has no way to honour the
    /// injected-provider guard that keeps the tests off the developer's own profile.</para>
    /// </summary>
    public CachedUtilization? CachedUsage
    {
        get
        {
            try
            {
                return _cachedUtilizationSource();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// What a poll changes, on the head's own thread: the published snapshot, the
    /// notification decisions, and the cadence.
    ///
    /// <para><b>The ordering guard applies in this direction too.</b> The full poll is the
    /// slow one, so it is the one most likely to lose the race: it can start reading, spend
    /// seconds ingesting, and post a snapshot it took <i>before</i> a plan tick that has
    /// already published. Guarding only the fast path leaves exactly the jump backwards the
    /// guard exists to prevent — measured, the session percentage went 77 → 30.</para>
    ///
    /// <para><b>The cadence and the off-plan check run either way.</b> Both belong to this
    /// poll alone — nothing else evaluates them — so skipping them when the snapshot happens
    /// to be superseded would leave the engine stuck on its warm-up interval with nothing to
    /// re-time it, and the divergence edge trigger unvisited.</para>
    /// </summary>
    private void Publish(PollResult result)
    {
        if (result.Failed)
        {
            // Keep the previous state, and keep warming up if we still can.
            AdjustCadence(DataSource.None, result.UtcNow);
            return;
        }

        if (!_planHistoryPoll.IsOlderThanLatest(result.Snapshot))
        {
            Latest = result.Snapshot;

            _log?.Write($"refresh source={result.Snapshot.Source} " +
                        $"session={result.Snapshot.SessionPercent?.ToString() ?? "null"}");
            SnapshotUpdated?.Invoke(result.Snapshot);

            _alerts.NotifyOnThreshold(result.Snapshot);
        }
        else
        {
            _log?.Write($"refresh superseded — read a sample older than the one displayed " +
                        $"({result.Snapshot.CapturedAtUtc:u} < {Latest.CapturedAtUtc:u})");
        }

        _alerts.CheckOffPlan(result.OffPlan);

        AdjustCadence(result.Snapshot.Source, result.UtcNow);

        // The slow background beat, hung off the poll rather than given a fourth timer. The
        // floor inside decides whether anything actually runs, so the effective cadence is the
        // floor and not this — and a successful refresh scheduling a Poll cannot recur, because
        // the attempt was stamped before it started.
        RefreshUsageCache();
    }

    /// <summary>One poll's readings, carried from the reading thread to the UI thread.</summary>
    private readonly record struct PollResult(
        UsageSnapshot Snapshot,
        PanelStatistics? OffPlan,
        DateTimeOffset UtcNow,
        bool Failed);

    /// <summary>
    /// The panel's figures: 31-day rollups plus off-plan divergence for the current window.
    /// </summary>
    public PanelStatistics BuildStatistics() => _planHistoryPoll.BuildStatistics();

    /// <summary>
    /// Applies and persists the threshold-notification preference, returning the state as it
    /// actually stands afterwards — a failed write must not be reported as success
    /// (CLAUDE.md rule 6).
    /// </summary>
    public bool SetNotifyOnThreshold(bool enable) => _alerts.SetNotifyOnThreshold(enable);

    /// <summary>
    /// Applies and persists the automatic-update preference, returning the state now in
    /// effect (ADR-0009 as amended, issue #140).
    /// </summary>
    public bool SetUpdateAutomatically(bool enable) => _alerts.SetUpdateAutomatically(enable);

    /// <summary>
    /// Applies and persists the notification threshold, returning the percentage now in
    /// effect (issue #141).
    /// </summary>
    public int SetThresholdPercent(int percent) => _alerts.SetThresholdPercent(percent);

    /// <summary>
    /// Re-times the poll timer for the current data state: fast while still waiting for
    /// authoritative data early in the run, normal once it arrives or the warm-up window
    /// has passed. Only touches the timer when the interval actually changes.
    /// </summary>
    private void AdjustCadence(DataSource source, DateTimeOffset utcNow)
    {
        if (_pollTimer is null)
        {
            return;   // Refresh() called outside a Start()ed engine — nothing to re-time.
        }

        var next = PollingCadence.Next(source, utcNow - _startedAt, _warmupInterval, _normalInterval);
        if (_pollTimer.Interval != next)
        {
            _pollTimer.Interval = next;
            _log?.Write($"cadence -> {next.TotalSeconds:0.##}s (source={source})");
        }
    }

    private IAppTimer Track(IAppTimer timer)
    {
        _timers.Add(timer);
        return timer;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a <= b ? a : b;

    public void Dispose()
    {
        foreach (var timer in _timers)
        {
            timer.Stop();
            timer.Dispose();
        }
        _timers.Clear();
        _store.Dispose();
    }
}
