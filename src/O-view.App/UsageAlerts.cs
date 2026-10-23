using OView.Core.Models;
using OView.Core.Providers.CachedUsage;
using OView.Presentation.Text;

namespace OView.App;

/// <summary>
/// The two notifications the engine decides to raise on its own, the usage threshold and
/// the off-plan warning, end to end: the edge-trigger state, the decision, and (now in
/// <see cref="NoticeText"/> and <see cref="OffPlanText"/>) the wording.
///
/// <para>Split out of <see cref="UsageEngine"/> purely to shorten that file, same members,
/// same behaviour, same logging, the same seam <see cref="WeeklyResetCoordinator"/> uses for
/// settings and <see cref="UsageCacheRefresh"/> uses for publishing back onto the engine.</para>
/// </summary>
public sealed class UsageAlerts
{
    private readonly bool _hasPlanHistory;
    private readonly Func<PanelStatistics> _buildStatistics;
    private readonly Func<ExtraUsageState> _readExtraUsage;
    private readonly IAppLog? _log;
    private readonly Func<TraySettings> _getSettings;
    private readonly Action<TraySettings> _saveSettings;
    private readonly Action<AppNotification> _notify;

    /// <summary>
    /// Not readonly: the threshold is user-settable from the menu (issue #141), and the
    /// watcher carries the edge-trigger state that has to be rebuilt with it. See
    /// <see cref="SetThresholdPercent"/>.
    /// </summary>
    private ThresholdWatcher _watcher;

    private bool _offPlanNotified;

    public UsageAlerts(
        bool hasPlanHistory,
        Func<PanelStatistics> buildStatistics,
        Func<ExtraUsageState> readExtraUsage,
        IAppLog? log,
        Func<TraySettings> getSettings,
        Action<TraySettings> saveSettings,
        Action<AppNotification> notify)
    {
        _hasPlanHistory = hasPlanHistory;
        _buildStatistics = buildStatistics;
        _readExtraUsage = readExtraUsage;
        _log = log;
        _getSettings = getSettings;
        _saveSettings = saveSettings;
        _notify = notify;
        _watcher = new ThresholdWatcher(_getSettings().ThresholdPercent);
    }

    /// <summary>
    /// Applies and persists the threshold-notification preference, returning the state as it
    /// actually stands afterwards, a failed write must not be reported as success
    /// (CLAUDE.md rule 6).
    /// </summary>
    public bool SetNotifyOnThreshold(bool enable)
    {
        var wasEnabled = _getSettings().NotifyOnThreshold;

        var settings = _getSettings() with { NotifyOnThreshold = enable };
        _saveSettings(settings);

        // Re-armed on the off→on edge, for the same reason SetThresholdPercent rebuilds the
        // watcher: it is edge-triggered, and while notifications are off it is never
        // consulted, `Settings.NotifyOnThreshold && _watcher.ShouldNotify(...)` short-circuits,
        // so its "we are currently above" flag freezes at whatever it held when they were
        // switched off.
        //
        // Left stale, that silently swallows the next crossing. Notify at 95%, switch
        // notifications off, let the window reset, switch them back on, climb past the
        // threshold again: the watcher still believes it is above, returns false, and the user
        // is never told. It stays stuck until usage happens to fall below the threshold during
        // a period when notifications are on.
        if (enable && !wasEnabled)
        {
            _watcher = new ThresholdWatcher(settings.ThresholdPercent);
        }

        return settings.NotifyOnThreshold;
    }

    /// <summary>
    /// Applies and persists the automatic-update preference, returning the state now in
    /// effect (ADR-0009 as amended, issue #140).
    ///
    /// <para>This class stores it and nothing more. <b>Whether a build may act on it is not
    /// this class's decision</b>, <c>UpdatePolicy.MayDownloadAndRun</c> owns that, and the
    /// head consults it before every install. Keeping the two apart is what stops a
    /// preference read as permission: a tarball build can hold this setting true in a
    /// settings file copied from a Windows machine and must still install nothing.</para>
    /// </summary>
    public bool SetUpdateAutomatically(bool enable)
    {
        var settings = _getSettings() with { UpdateAutomatically = enable };
        _saveSettings(settings);
        return settings.UpdateAutomatically;
    }

    /// <summary>
    /// Applies and persists the notification threshold, returning the percentage now in
    /// effect (issue #141).
    ///
    /// <para><b>The watcher is rebuilt, not just re-read.</b> It is edge-triggered, it fires
    /// once per upward crossing and re-arms on a drop below, so it holds "we are currently
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
        var settings = _getSettings() with { ThresholdPercent = Math.Clamp(percent, 1, 100) };
        _saveSettings(settings);
        _watcher = new ThresholdWatcher(settings.ThresholdPercent);
        return settings.ThresholdPercent;
    }

    public void NotifyOnThreshold(UsageSnapshot snapshot)
    {
        // Branches on the figure rather than the status: an Unavailable percentage carries no
        // figure by construction (UsageValueRule), so this can never read one as a crossing to
        // zero, the trap a plain `?? 0` on the nullable field would have walked straight into.
        // The converse isn't guaranteed (UsagePercent), a present status can still carry no
        // figure yet, so branching on the status instead would dereference a null one.
        var session = snapshot.SessionPercentValue;
        var percent = session.Value is { } sessionPercent ? (int?)sessionPercent : null;

        if (_getSettings().NotifyOnThreshold && _watcher.ShouldNotify(percent))
        {
            _notify(new AppNotification(NoticeText.ThresholdTitle,
                NoticeText.ThresholdBody(percent!.Value),
                NotificationKind.Warning));
        }
    }

    /// <summary>
    /// Notifies once when usage starts bypassing the plan. Edge-triggered like the
    /// threshold watcher, and re-armed when it stops, the point is to catch the
    /// silent-and-expensive case the plan bars cannot show, not to nag.
    /// </summary>
    public void CheckOffPlan(PanelStatistics? stats)
    {
        // No plan history, or the figures could not be built, either way there is nothing
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

        if (_offPlanNotified || !_getSettings().NotifyOnThreshold)
        {
            return;
        }

        _offPlanNotified = true;

        // What Claude Code says about extra usage on this account (issue #259). Read through
        // the same source the reset anchor uses, so a test that supplied its own Provider gets
        // Unknown rather than the developer's real ~/.claude.json, the hazard PlanHistoryPoll's
        // addressed-flag doc is about, pointed at this field.
        var extraUsage = _readExtraUsage();

        // The panel's formatter, not a pinned-culture "C" lookup. This balloon sends
        // the user to the Est. tile, so the two must write the same amount the same
        // way, and ICU's currency pattern is not the same instruction as composing
        // "$" + a fixed decimal format, even where they agree today (issue #55).
        //
        // The claim is made only where the setting supports it. Asserting a charge at an
        // account that cannot be charged is issue #259, and it is worse in a balloon than in
        // the panel: there is nothing beside it to qualify it.
        //
        // The two the engine raises are both genuine warnings, and they are the reason the
        // kind exists: these are what the yellow triangle is FOR, and it meant nothing while
        // every "up to date" balloon wore the same one.
        // Branches on the figure rather than the status, for the same reason NotifyOnThreshold
        // does, EstimatedUsd gives the same guarantee only in the absent direction.
        var estOffPlanUsd = stats.EstOffPlanValue.Value;

        _notify(new AppNotification(
            OffPlanText.NotificationTitle(extraUsage),
            OffPlanText.NotificationBody(estOffPlanUsd, extraUsage),
            NotificationKind.Warning));
    }

    /// <summary>
    /// The divergence figures, or null when there is no plan history to compare against or
    /// the build failed. A failure here is not a failed poll, the snapshot is still good.
    /// </summary>
    public PanelStatistics? ReadOffPlan()
    {
        if (!_hasPlanHistory)
        {
            return null;
        }

        try
        {
            return _buildStatistics();
        }
        catch (Exception ex)
        {
            _log?.Write($"off-plan check FAILED {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
