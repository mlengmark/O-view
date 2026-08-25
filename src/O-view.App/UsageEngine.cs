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
/// </summary>
public sealed class UsageEngine : IDisposable
{
    private readonly RollupStore _store;
    private readonly IUsageProvider _provider;
    private readonly PlanHistoryProvider? _planHistory;

    /// <summary>
    /// Claude Code's cached usage figures, held directly as well as through the chain — see
    /// <see cref="WithReportedResets"/> for why the chain alone is not enough.
    /// </summary>
    private readonly IUsageProvider _cachedUtilization;
    /// <summary>
    /// Not readonly: the threshold is user-settable from the menu (issue #141), and the
    /// watcher carries the edge-trigger state that has to be rebuilt with it. See
    /// <see cref="SetThresholdPercent"/>.
    /// </summary>
    private ThresholdWatcher _watcher;
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
    private bool _offPlanNotified;

    /// <summary>Where a scheduled poll publishes back to. Null means "publish inline".</summary>
    private IUiDispatcher? _dispatcher;

    /// <summary>
    /// One gate per cadence, so a read already in flight cannot stack another behind it.
    /// They are deliberately separate — see <see cref="RunOffThread{T}"/>.
    /// </summary>
    private readonly PollGate _fullGate = new();
    private readonly PollGate _planGate = new();

    /// <summary>
    /// Whether the plan-history provider this engine holds is one the caller actually asked
    /// for, and so may be published from directly.
    ///
    /// <para>False in exactly one case: a caller injected a whole
    /// <see cref="UsageEngineOptions.Provider"/> and said nothing about plan history. The
    /// engine still builds a provider then, because the off-plan arithmetic needs one — but it
    /// defaults to the <b>real machine's</b> <c>plan-usage-history.json</c>, and it is not part
    /// of the injected chain. Publishing from it would put the developer's own Claude Desktop
    /// usage over the caller's resolution, which in the test suite means results that depend
    /// on whose machine is running them.</para>
    ///
    /// <para>Naming either <see cref="UsageEngineOptions.PlanHistory"/> or
    /// <see cref="UsageEngineOptions.PlanHistoryPath"/> is enough — both say which file is
    /// meant, which is the whole question.</para>
    /// </summary>
    private readonly bool _planHistoryIsAddressed;

    /// <summary>Most recent snapshot — what the panel opens with.</summary>
    public UsageSnapshot Latest { get; private set; } = UsageSnapshot.None;

    /// <summary>Current persisted settings. Mutated only through <see cref="SetNotifyOnThreshold"/>.</summary>
    public TraySettings Settings { get; private set; }

    /// <summary>Raised after every refresh attempt that produced a snapshot. The head redraws from this.</summary>
    public event Action<UsageSnapshot>? SnapshotUpdated;

    /// <summary>The engine has decided the user should be told something. The head decides how it appears.</summary>
    public event Action<AppNotification>? NotificationRequested;

    /// <summary>Time to check for a newer release (ADR-0009). The head owns the HTTP and the UI.</summary>
    public event Action? UpdateCheckDue;

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

        // Observed weekly resets accrue in their own durable file, not in the rollup store
        // (ADR-0011): the store is a rebuildable cache that wipes itself on corruption,
        // whereas a missed reset costs a week. Older builds kept them in the store, so any
        // rows it still holds are carried across on the way past — idempotent, so this is
        // safe to run on every launch.
        var account = ClaudeAccount.TryRead();
        var weeklyResets = new WeeklyResetLog(_options.WeeklyResetLogPath);
        try
        {
            weeklyResets.ImportLegacy(_store.GetLegacyWeeklyResets(), account?.OrganizationUuid ?? "");
        }
        catch (Exception ex)
        {
            _log?.Write($"weekly-reset legacy import skipped: {ex.GetType().Name}: {ex.Message}");
        }

        _planHistory = _options.PlanHistory ?? new PlanHistoryProvider(
            path: _options.PlanHistoryPath,
            orgUuid: account?.OrganizationUuid,
            weeklyResetLog: weeklyResets,
            // Local request times tighten the five-hour window's start bracket (issue #185).
            // The engine owns both halves, so the wiring lives here rather than teaching the
            // plan-history provider about the rollup store.
            earliestActivity: _store.EarliestRequestBetween);

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
            ? new CachedUtilizationProvider()
            : new CachedUtilizationProvider(() => null));

        _provider = _options.Provider ?? new CompositeUsageProvider(
            _planHistory,
            _cachedUtilization,
            new JsonlUsageProvider(_store));

        _planHistoryIsAddressed = _options.Provider is null
            || _options.PlanHistory is not null
            || _options.PlanHistoryPath is not null;

        Settings = TraySettings.Load(_options.SettingsPath);
        _watcher = new ThresholdWatcher(Settings.ThresholdPercent);
        ApplyWeeklyResetSetting();
    }

    /// <summary>
    /// Pushes the user's entered weekly reset into the provider (GitHub issue #186). Called
    /// on construction and after every change, so the two can never disagree about what is
    /// set — the shape of bug where a preference is saved and does not take effect until a
    /// restart.
    /// </summary>
    private void ApplyWeeklyResetSetting()
    {
        if (_planHistory is not null)
        {
            _planHistory.ManualWeeklyReset = Settings.WeeklyReset;
        }
    }

    /// <summary>
    /// Applies and persists the user's weekly reset time, returning what is now in effect.
    /// Null clears it and returns to deriving.
    /// </summary>
    public ManualWeeklyReset? SetWeeklyReset(ManualWeeklyReset? reset)
    {
        Settings = Settings with
        {
            WeeklyResetDay = reset?.DayText ?? "",
            WeeklyResetTime = reset?.TimeText ?? "",
            // A new entry is a fresh claim: any conflict already reported was about the old
            // one, so re-arm the notice rather than staying silent about the new value.
            WeeklyResetConflictNoticed = "",
        };
        Settings.Save(_options.SettingsPath);
        ApplyWeeklyResetSetting();

        _log?.Write($"weekly reset {(reset is null ? "cleared — deriving" : $"set to {reset.DayText} {reset.TimeText} local")}");
        return Settings.WeeklyReset;
    }

    /// <summary>
    /// An observation that disproved the entered weekly reset, or null. Surfaced so the head
    /// can tell the user once — silently overriding a wrong entry leaves them believing the
    /// number they typed.
    /// </summary>
    public WeeklyResetObservation? WeeklyResetConflict => _planHistory?.ManualWeeklyResetConflict;

    /// <summary>
    /// Records that the user has been told about <paramref name="conflict"/>, so the notice
    /// fires once per conflicting observation rather than on every poll.
    /// </summary>
    public void MarkWeeklyResetConflictNoticed(WeeklyResetObservation conflict)
    {
        Settings = Settings with { WeeklyResetConflictNoticed = conflict.LatestUtc.ToString("o") };
        Settings.Save(_options.SettingsPath);
    }

    /// <summary>Whether this conflict is new to the user.</summary>
    public bool IsWeeklyResetConflictUnseen(WeeklyResetObservation conflict) =>
        Settings.WeeklyResetConflictNoticed != conflict.LatestUtc.ToString("o");

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
        _planTimer = Track(timers.Create(_planInterval, PollPlanHistory));
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
    /// The cheap half, on its own faster cadence: read the plan-history file and nothing else.
    ///
    /// <para><b>Why this is separate.</b> The two things a poll reads have costs three orders
    /// of magnitude apart. Measured on a real machine: the plan-history read, parse and
    /// reset-scan is <b>3.3 ms</b>; the transcript walk behind the token tiles is <b>32 files
    /// and 92 MB</b>. They were on one 60-second timer, so the percentages — the thing the
    /// icon and the bars actually show — waited on the ingest that froze the app on a large
    /// history (issue #125).</para>
    ///
    /// <para>What this does <i>not</i> do is make the data fresher than its source. Claude
    /// Desktop writes that file every ~5 minutes (measured median 5.00 min over 1,828 gaps),
    /// and nothing here can beat it. What it removes is O-view's own contribution: up to a
    /// full 60 s of extra lag on top, now at most <see cref="UsageEngineOptions.PlanPollInterval"/>.
    /// Polling faster than Desktop writes would re-read an unchanged file for nothing.</para>
    /// </summary>
    private void PollPlanHistory()
    {
        // Nothing to publish from when the caller injected a provider chain and never said
        // which plan-history file it stands for — see _planHistoryIsAddressed. Production
        // always passes this; it is only reachable from a test.
        if (!_planHistoryIsAddressed)
        {
            return;
        }

        RunOffThread(_planGate, "plan poll", ReadPlanHistory, PublishPlanHistory);
    }

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

    /// <summary>Never throws: a bad read must not kill the cadence.</summary>
    private UsageSnapshot ReadPlanHistory()
    {
        try
        {
            return _planHistory?.GetSnapshot(_clock.UtcNow) ?? UsageSnapshot.None;
        }
        catch (Exception ex)
        {
            _log?.Write($"plan read FAILED {ex.GetType().Name}: {ex.Message}");
            return UsageSnapshot.None;
        }
    }

    /// <summary>
    /// Publishes a plan-history-only snapshot, and declines to in the two cases where it would
    /// make the display worse than leaving it alone.
    ///
    /// <para><b>Non-authoritative results are dropped, not published.</b> This path skips the
    /// composite provider, so it cannot see the JSONL estimate that would otherwise win when
    /// plan history has nothing. Publishing its <c>None</c> would blank a panel that the full
    /// poll had correctly filled with an estimate, and the display would flicker between the
    /// two cadences.</para>
    ///
    /// <para><b>An older sample never replaces a newer one.</b> Two cadences now publish, and
    /// a first-run full poll can spend seconds ingesting before posting a snapshot it read
    /// before the plan tick did. Without this the bars would visibly jump backwards.</para>
    ///
    /// <para>Threshold notification runs here, which is a genuine gain rather than a side
    /// effect: crossing 70% is now noticed within <see cref="UsageEngineOptions.PlanPollInterval"/>
    /// rather than up to a minute later. Off-plan detection deliberately does not — it needs
    /// the 31-day SQLite aggregate, which is the expensive half this path exists to avoid.</para>
    /// </summary>
    private void PublishPlanHistory(UsageSnapshot snapshot)
    {
        if (!PollingCadence.IsAuthoritative(snapshot.Source) || IsOlderThanLatest(snapshot))
        {
            return;
        }

        Latest = snapshot;
        SnapshotUpdated?.Invoke(snapshot);
        NotifyOnThreshold(snapshot);
    }

    /// <summary>
    /// Whether this snapshot describes an older sample than the one already published.
    ///
    /// <para><b>Only ever compares two authoritative readings.</b> Both halves of that are
    /// load-bearing. The incoming side has to be checked as well as the current one, because a
    /// JSONL estimate <i>does</i> carry a capture time — the newest transcript activity — so a
    /// live plan reading would otherwise block the fallback the composite resolved to, and
    /// block it permanently: <c>Latest</c> stays Live, so every later estimate is compared
    /// against the same frozen sample and dropped in turn. The panel would sit on a reading
    /// that had stopped being true and never fall back.</para>
    ///
    /// <para>Comparing capture times rather than read times is deliberate. These stamps come
    /// from Claude Desktop's own samples, so the ordering is the data's, not this process's —
    /// which keeps it right across a wall-clock correction that would reorder read times.</para>
    /// </summary>
    private bool IsOlderThanLatest(UsageSnapshot snapshot) =>
        PollingCadence.IsAuthoritative(snapshot.Source)
        && PollingCadence.IsAuthoritative(Latest.Source)
        && snapshot.CapturedAtUtc is { } incoming
        && Latest.CapturedAtUtc is { } current
        && incoming < current;

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
            var snapshot = WithEnteredWeeklyReset(_provider.GetSnapshot(utcNow), utcNow);
            snapshot = WithReportedResets(snapshot, utcNow);
            return new PollResult(snapshot, ReadOffPlan(), utcNow, Failed: false);
        }
        catch (Exception ex)
        {
            _log?.Write($"refresh FAILED {ex.GetType().Name}: {ex.Message}");
            return new PollResult(UsageSnapshot.None, null, utcNow, Failed: true);
        }
    }

    /// <summary>
    /// Carries the user's entered weekly reset onto a snapshot that has none.
    ///
    /// <para><b>The case this exists for is the one the feature was built for.</b>
    /// <c>PlanHistoryProvider</c> resolves the entry itself — including checking it against
    /// observed resets — but it returns <c>None</c> before reaching that code when there is
    /// no plan-history file at all, and the composite then falls through to the JSONL
    /// estimate, whose weekly reset is null. So a user with no Claude Desktop entered their
    /// reset time and saw nothing change: exactly the population issue #186 was written for,
    /// and the only one that cannot derive the value instead.</para>
    ///
    /// <para>Applied here rather than by widening the provider, because the entry is a user
    /// setting rather than a reading of the plan-history file, and it should survive whichever
    /// provider ends up winning the composite's tier resolution.</para>
    ///
    /// <para><b>Only fills a gap; never overrides.</b> A snapshot that already carries a
    /// weekly reset has been through the provider's entry-versus-evidence rule, which is the
    /// one place allowed to decide between them. Overwriting it here would reinstate an entry
    /// that an observation had just disproved.</para>
    /// </summary>
    private UsageSnapshot WithEnteredWeeklyReset(UsageSnapshot snapshot, DateTimeOffset utcNow)
    {
        if (snapshot.WeeklyResetAtUtc is not null || Settings.WeeklyReset is not { } entry)
        {
            return snapshot;
        }

        // Nothing to attach a reset to: a panel with no data at all should stay blank rather
        // than grow one lonely populated line.
        if (snapshot.Source == DataSource.None)
        {
            return snapshot;
        }

        return snapshot with
        {
            WeeklyResetAtUtc = entry.NextAfter(utcNow, TimeZoneInfo.Local),
            // Exact, so it renders without the "~" that marks a derived bracket.
            WeeklyResetUncertainty = TimeSpan.Zero,
            WeeklyResetPeriod = WeeklyResetDetector.WindowLength,
        };
    }

    /// <summary>
    /// Replaces derived reset times with the ones Claude actually reported, wherever Claude
    /// Code has cached them (<see cref="CachedUtilization"/>).
    ///
    /// <para><b>These are the only exact reset times O-view has.</b> Every other one is inferred
    /// from a drop in a sampled series, so its precision is bounded by the gap between samples:
    /// about half an interval for the five-hour window even after local transcripts narrow the
    /// bracket (issue #185), and roughly ten hours for the weekly one, whose resets land
    /// overnight while Desktop is closed (ADR-0011) — the imprecision that made the manual entry
    /// of issue #186 worth building in the first place. A reported timestamp needs none of that
    /// machinery. It is simply correct.</para>
    ///
    /// <para><b>So this overrides rather than fills a gap</b> — the opposite of
    /// <see cref="WithEnteredWeeklyReset"/>, and the difference is the point. A derived bracket
    /// and a typed-in time are both attempts to recover a number that this source states
    /// outright; leaving either in place ahead of it would be preferring a guess to the
    /// answer.</para>
    ///
    /// <para>Applied to the winning snapshot rather than left to the provider chain, because
    /// the chain resolves on <em>percentages</em>: a machine running Claude Desktop takes its
    /// percentages from plan history and would otherwise keep plan history's derived reset
    /// times too, discarding exact ones that were sitting on disk the whole time. The two
    /// questions have different best answers, so they are answered separately.</para>
    ///
    /// <para>Silent when there is nothing to say: no cache, a window that has already rolled
    /// past its cached boundary, or a reader that throws all leave the snapshot untouched
    /// (<see cref="CachedUtilizationProvider"/> returns those as nulls). This only ever
    /// replaces an approximation with a fact, so it must never be able to remove one.</para>
    /// </summary>
    private UsageSnapshot WithReportedResets(UsageSnapshot snapshot, DateTimeOffset utcNow)
    {
        // Nothing to attach a reset to: a panel with no data at all should stay blank rather
        // than grow one lonely populated line (same rule as the entered reset).
        if (snapshot.Source == DataSource.None)
        {
            return snapshot;
        }

        UsageSnapshot reported;
        try
        {
            reported = _cachedUtilization.GetSnapshot(utcNow);
        }
        catch (Exception ex)
        {
            // A precision refinement must never take down the reading it refines.
            _log?.Write($"reported resets skipped: {ex.GetType().Name}: {ex.Message}");
            return snapshot;
        }

        if (reported.SessionResetAtUtc is { } sessionReset)
        {
            snapshot = snapshot with
            {
                SessionResetAtUtc = sessionReset,
                // Exact, so it renders without the "~" that marks a derived bracket.
                SessionResetUncertainty = TimeSpan.Zero,
            };
        }

        if (reported.WeeklyResetAtUtc is { } weeklyReset)
        {
            snapshot = snapshot with
            {
                WeeklyResetAtUtc = weeklyReset,
                WeeklyResetUncertainty = TimeSpan.Zero,
                WeeklyResetPeriod = reported.WeeklyResetPeriod ?? WeeklyResetDetector.WindowLength,
            };
        }

        return snapshot;
    }

    /// <summary>
    /// The divergence figures, or null when there is no plan history to compare against or
    /// the build failed. A failure here is not a failed poll — the snapshot is still good.
    /// </summary>
    private PanelStatistics? ReadOffPlan()
    {
        if (_planHistory is null)
        {
            return null;
        }

        try
        {
            return BuildStatistics();
        }
        catch (Exception ex)
        {
            _log?.Write($"off-plan check FAILED {ex.GetType().Name}: {ex.Message}");
            return null;
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

        if (!IsOlderThanLatest(result.Snapshot))
        {
            Latest = result.Snapshot;

            _log?.Write($"refresh source={result.Snapshot.Source} " +
                        $"session={result.Snapshot.SessionPercent?.ToString() ?? "null"}");
            SnapshotUpdated?.Invoke(result.Snapshot);

            NotifyOnThreshold(result.Snapshot);
        }
        else
        {
            _log?.Write($"refresh superseded — read a sample older than the one displayed " +
                        $"({result.Snapshot.CapturedAtUtc:u} < {Latest.CapturedAtUtc:u})");
        }

        CheckOffPlan(result.OffPlan);

        AdjustCadence(result.Snapshot.Source, result.UtcNow);
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
    public PanelStatistics BuildStatistics()
    {
        var utcNow = _clock.UtcNow;
        var stats = PanelStatistics.Build(_store, utcNow);
        if (_planHistory is null)
        {
            return stats;
        }

        var (windowStart, percents, meterAge) = _planHistory.GetCurrentWindow(utcNow);

        if (_options.SimulateDivergence is { } mode)
        {
            // Feed the real detector synthetic inputs rather than faking its output,
            // so the simulation exercises the same code path the real case would. The meter is
            // stated as current, because a simulation of divergence against a stopped meter
            // would now correctly render nothing at all.
            var fake = mode == "limit" ? new[] { 99, 100 } : [6, 6, 6];
            return stats.WithDivergence(_store, windowStart, fake, TimeSpan.Zero) with
            {
                EstOffPlanUsd = 92.75m,
                Divergence = DivergenceDetector.Evaluate(fake, 69_091, TimeSpan.Zero),
            };
        }

        return stats.WithDivergence(_store, windowStart, percents, meterAge);
    }

    /// <summary>
    /// Applies and persists the threshold-notification preference, returning the state as it
    /// actually stands afterwards — a failed write must not be reported as success
    /// (CLAUDE.md rule 6).
    /// </summary>
    public bool SetNotifyOnThreshold(bool enable)
    {
        var wasEnabled = Settings.NotifyOnThreshold;

        Settings = Settings with { NotifyOnThreshold = enable };
        Settings.Save(_options.SettingsPath);

        // Re-armed on the off→on edge, for the same reason SetThresholdPercent rebuilds the
        // watcher: it is edge-triggered, and while notifications are off it is never
        // consulted — `Settings.NotifyOnThreshold && _watcher.ShouldNotify(...)` short-circuits
        // — so its "we are currently above" flag freezes at whatever it held when they were
        // switched off.
        //
        // Left stale, that silently swallows the next crossing. Notify at 95%, switch
        // notifications off, let the window reset, switch them back on, climb past the
        // threshold again: the watcher still believes it is above, returns false, and the user
        // is never told. It stays stuck until usage happens to fall below the threshold during
        // a period when notifications are on.
        if (enable && !wasEnabled)
        {
            _watcher = new ThresholdWatcher(Settings.ThresholdPercent);
        }

        return Settings.NotifyOnThreshold;
    }

    /// <summary>
    /// Applies and persists the automatic-update preference, returning the state now in
    /// effect (ADR-0009 as amended, issue #140).
    ///
    /// <para>The engine stores it and nothing more. <b>Whether a build may act on it is not
    /// this class's decision</b> — <c>UpdatePolicy.MayDownloadAndRun</c> owns that, and the
    /// head consults it before every install. Keeping the two apart is what stops a
    /// preference read as permission: a tarball build can hold this setting true in a
    /// settings file copied from a Windows machine and must still install nothing.</para>
    /// </summary>
    public bool SetUpdateAutomatically(bool enable)
    {
        Settings = Settings with { UpdateAutomatically = enable };
        Settings.Save(_options.SettingsPath);
        return Settings.UpdateAutomatically;
    }

    /// <summary>
    /// Applies and persists the notification threshold, returning the percentage now in
    /// effect (issue #141).
    ///
    /// <para><b>The watcher is rebuilt, not just re-read.</b> It is edge-triggered — it fires
    /// once per upward crossing and re-arms on a drop below — so it holds "we are currently
    /// above" state that was decided against the OLD threshold. Rebuilding clears that, which
    /// makes lowering the threshold behave the way a user asking for it means: someone sitting
    /// at 75% who moves the threshold from 80 to 70 is notified on the next poll, rather than
    /// waiting for a window reset to re-arm a watcher that never saw them cross.</para>
    ///
    /// <para>Raising it does the right thing for the same reason: from 70 to 90 at 75% usage,
    /// the fresh watcher sees 75 &lt; 90 and stays silent.</para>
    ///
    /// <para>Clamped to the same 1–100 range <see cref="TraySettings.Load"/> enforces, so a
    /// caller cannot install a threshold the settings file would refuse to load back.</para>
    /// </summary>
    public int SetThresholdPercent(int percent)
    {
        Settings = Settings with { ThresholdPercent = Math.Clamp(percent, 1, 100) };
        Settings.Save(_options.SettingsPath);
        _watcher = new ThresholdWatcher(Settings.ThresholdPercent);
        return Settings.ThresholdPercent;
    }

    private void NotifyOnThreshold(UsageSnapshot snapshot)
    {
        if (Settings.NotifyOnThreshold && _watcher.ShouldNotify(snapshot.SessionPercent))
        {
            NotificationRequested?.Invoke(new AppNotification("Claude usage",
                $"Session usage is at {snapshot.SessionPercent}% of the 5-hour limit.",
                NotificationKind.Warning));
        }
    }

    /// <summary>
    /// Notifies once when usage starts bypassing the plan. Edge-triggered like the
    /// threshold watcher, and re-armed when it stops — the point is to catch the
    /// silent-and-expensive case the plan bars cannot show, not to nag.
    /// </summary>
    private void CheckOffPlan(PanelStatistics? stats)
    {
        // No plan history, or the figures could not be built — either way there is nothing
        // to decide on, and a failed build must not re-arm the edge trigger.
        if (stats is null)
        {
            return;
        }

        if (!stats.IsOffPlan)
        {
            _offPlanNotified = false;
            return;
        }

        _log?.Write($"off-plan detected state={stats.Divergence?.State} " +
                    $"tokens={stats.Divergence?.OutputTokensInWindow} rise={stats.Divergence?.PlanRisePoints}");

        if (_offPlanNotified || !Settings.NotifyOnThreshold)
        {
            return;
        }

        _offPlanNotified = true;
        // The panel's formatter, not a pinned-culture "C" lookup. This balloon sends
        // the user to the Est. tile, so the two must write the same amount the same
        // way — and ICU's currency pattern is not the same instruction as composing
        // "$" + a fixed decimal format, even where they agree today (issue #55).
        var spend = stats.EstOffPlanUsd is { } usd
            ? $" Est. {UsageFormatter.Usd(usd)} so far this window."
            : "";
        // The two the engine raises are both genuine warnings, and they are the reason the
        // kind exists: these are what the yellow triangle is FOR, and it meant nothing while
        // every "up to date" balloon wore the same one.
        NotificationRequested?.Invoke(new AppNotification("Usage is billing beyond your plan",
            $"Work this session isn't drawing from your plan allowance.{spend} Open O-view for detail.",
            NotificationKind.Warning));
    }

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
