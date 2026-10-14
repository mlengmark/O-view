using OView.Core.Models;
using OView.Core.Providers.CachedUsage;
using OView.Core.Providers.PlanHistory;

namespace OView.App;

/// <summary>
/// What a skin asks of the shell — the skin-to-shell half of the seam between
/// <c>O-view.Tray</c>/<c>O-view.Linux</c> and <c>O-view.App</c>
/// (<see cref="IShellToSkin"/> is the other direction). <see cref="UsageEngine"/> implements
/// it; nothing here changes what any member does, only that a skin may now depend on this
/// shape instead of the concrete engine.
///
/// <para>Ported in shape only from <c>mlengmark/oview</c>'s <c>ISkinToShell.cs</c>: there
/// every member is a one-way command, with the answer arriving later through
/// <c>IShellToSkin</c>. Here a skin still reads state straight back — <see cref="Latest"/>,
/// <see cref="Settings"/> and the rest are queries, not commands — because that is what
/// both skins actually do today; this interface only names the shape already in use, and no
/// skin is wired to it yet.</para>
/// </summary>
public interface ISkinToShell
{
    /// <summary>Most recent snapshot — what the panel opens with.</summary>
    UsageSnapshot Latest { get; }

    /// <summary>Current persisted settings. Mutated only through the setters below.</summary>
    TraySettings Settings { get; }

    /// <summary>Claude Code's cached block as it stands now, or null when there is none to read.</summary>
    CachedUtilization? CachedUsage { get; }

    /// <summary>The panel's figures: 31-day rollups plus off-plan divergence for the current window.</summary>
    PanelStatistics BuildStatistics();

    /// <summary>One poll, start to finish on the calling thread.</summary>
    void Refresh();

    /// <summary>Asks Claude Code to refresh its usage cache, if it is allowed and due.</summary>
    void RefreshUsageCache(bool force = false);

    /// <summary>Whether a refresher was supplied at all — the feature being on, not it being allowed.</summary>
    bool CanRefreshUsageCache { get; }

    /// <summary>Why usage refreshing is switched off, or null while it is allowed.</summary>
    string? UsageRefreshBlocked { get; }

    /// <summary>How many refresh attempts have actually been started.</summary>
    int UsageRefreshAttempts { get; }

    /// <summary>Clears the block set by a suspected charge and allows refreshing again.</summary>
    bool ResumeUsageRefresh();

    /// <summary>Applies and persists the threshold-notification preference, returning the
    /// state as it actually stands afterwards.</summary>
    bool SetNotifyOnThreshold(bool enable);

    /// <summary>Applies and persists the automatic-update preference, returning the state
    /// now in effect.</summary>
    bool SetUpdateAutomatically(bool enable);

    /// <summary>Applies and persists the notification threshold, returning the percentage
    /// now in effect.</summary>
    int SetThresholdPercent(int percent);

    /// <summary>Applies and persists the user's weekly reset time, returning what is now in
    /// effect. Null clears it and returns to deriving.</summary>
    ManualWeeklyReset? SetWeeklyReset(ManualWeeklyReset? reset);

    /// <summary>The reset instant Claude reported that disproves the entered weekly reset,
    /// or null.</summary>
    DateTimeOffset? WeeklyResetConflict { get; }

    /// <summary>Records that the user has been told about <paramref name="conflict"/>.</summary>
    void MarkWeeklyResetConflictNoticed(DateTimeOffset conflict);

    /// <summary>Whether this conflict is new to the user.</summary>
    bool IsWeeklyResetConflictUnseen(DateTimeOffset conflict);
}
