using OView.Core.Models;

namespace OView.App;

/// <summary>
/// What the shell asks of a skin — the shell-to-skin half of the seam between
/// <c>O-view.App</c> and <c>O-view.Tray</c>/<c>O-view.Linux</c>
/// (<see cref="ISkinToShell"/> is the other direction). The shell pushes state and events;
/// no member returns anything the shell waits on.
///
/// <para>Ported in shape only from <c>mlengmark/oview</c>'s <c>IShellToSkin.cs</c>: a
/// one-way, fire-and-forget interface the skin implements. The member list itself is this
/// app's own, matching what <see cref="UsageEngine"/>'s events already carry
/// (<see cref="UsageEngine.SnapshotUpdated"/>, <see cref="UsageEngine.NotificationRequested"/>,
/// <see cref="UsageEngine.UpdateCheckDue"/>, <see cref="UsageEngine.RateCheckDue"/>). No skin
/// implements this yet — both still subscribe to those events directly.</para>
/// </summary>
public interface IShellToSkin
{
    /// <summary>A new snapshot is ready to draw.</summary>
    void ShowSnapshot(UsageSnapshot snapshot);

    /// <summary>The shell has decided the user should be told something.</summary>
    void ShowNotification(AppNotification notification);

    /// <summary>Time to check for a newer release (ADR-0009). The skin owns the HTTP and the UI.</summary>
    void UpdateCheckDue();

    /// <summary>Time to compare the bundled rate card against Anthropic's published table (ADR-0016).</summary>
    void RateCheckDue();
}
