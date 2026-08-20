using OView.App.Platform;
using OView.Core.Models;
using OView.Core.Providers;
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
            weeklyResetLog: weeklyResets);

        _provider = _options.Provider ?? new CompositeUsageProvider(
            _planHistory,
            new JsonlUsageProvider(_store));

        Settings = TraySettings.Load(_options.SettingsPath);
        _watcher = new ThresholdWatcher(Settings.ThresholdPercent);
    }

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
        _planTimer = Track(timers.Create(_options.PlanPollInterval, PollPlanHistory));
        _planTimer.Start();
        _log?.Write($"plan-history interval={_options.PlanPollInterval.TotalSeconds}s");

        // Auto-update (ADR-0009): one check shortly after launch, then daily. The engine
        // owns only the *when*; the head performs the check and surfaces the result.
        IAppTimer? initial = null;
        initial = Track(timers.Create(_options.FirstUpdateCheckDelay, () =>
        {
            initial!.Stop();
            UpdateCheckDue?.Invoke();
        }));
        initial.Start();

        Track(timers.Create(_options.UpdateCheckInterval, () => UpdateCheckDue?.Invoke())).Start();
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
    private void PollPlanHistory() => RunOffThread(_planGate, "plan poll", ReadPlanHistory, PublishPlanHistory);

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

        _ = Task.Run(() =>
        {
            var result = read();

            try
            {
                dispatcher.Post(() =>
                {
                    try
                    {
                        publish(result);
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
    /// Whether this snapshot describes an older sample than the one already published. Only
    /// meaningful between two authoritative readings — an estimate carries no capture time to
    /// compare, and must never be held back by one.
    /// </summary>
    private bool IsOlderThanLatest(UsageSnapshot snapshot) =>
        PollingCadence.IsAuthoritative(Latest.Source)
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
            var snapshot = _provider.GetSnapshot(utcNow);
            return new PollResult(snapshot, ReadOffPlan(), utcNow, Failed: false);
        }
        catch (Exception ex)
        {
            _log?.Write($"refresh FAILED {ex.GetType().Name}: {ex.Message}");
            return new PollResult(UsageSnapshot.None, null, utcNow, Failed: true);
        }
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
    /// </summary>
    private void Publish(PollResult result)
    {
        if (result.Failed)
        {
            // Keep the previous state, and keep warming up if we still can.
            AdjustCadence(DataSource.None, result.UtcNow);
            return;
        }

        Latest = result.Snapshot;

        _log?.Write($"refresh source={result.Snapshot.Source} " +
                    $"session={result.Snapshot.SessionPercent?.ToString() ?? "null"}");
        SnapshotUpdated?.Invoke(result.Snapshot);

        NotifyOnThreshold(result.Snapshot);
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

        var (windowStart, percents) = _planHistory.GetCurrentWindow(utcNow);

        if (_options.SimulateDivergence is { } mode)
        {
            // Feed the real detector synthetic inputs rather than faking its output,
            // so the simulation exercises the same code path the real case would.
            var fake = mode == "limit" ? new[] { 99, 100 } : [6, 6, 6];
            return stats.WithDivergence(_store, windowStart, fake) with
            {
                EstOffPlanUsd = 92.75m,
                Divergence = DivergenceDetector.Evaluate(fake, 69_091),
            };
        }

        return stats.WithDivergence(_store, windowStart, percents);
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
                $"Session usage is at {snapshot.SessionPercent}% of the 5-hour limit."));
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
        NotificationRequested?.Invoke(new AppNotification("Usage is billing beyond your plan",
            $"Work this session isn't drawing from your plan allowance.{spend} Open O-view for detail."));
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
