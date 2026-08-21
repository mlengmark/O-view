namespace OView.App;

/// <summary>
/// How serious a notification is — the thing a head turns into an icon.
///
/// <para><b>The kind is stated, not inferred from the wording.</b> Every Windows balloon used
/// to carry <c>ToolTipIcon.Warning</c> because that was hard-coded at the single place they
/// all funnel through, so "O-view is up to date" arrived under a yellow warning triangle
/// alongside "Usage is billing beyond your plan". A tool that draws the same alarm for good
/// news and for money being spent has taught the user to ignore the alarm, which costs
/// exactly the notification that mattered.</para>
///
/// <para>Deliberately three values and not a platform icon name. What a head does with these
/// is its own business: Windows maps them onto <c>ToolTipIcon</c>, and Linux ignores them
/// entirely because a freedesktop notification already carries O-view's own app icon and its
/// only severity channel — the urgency hint — makes a notification persist on screen, which
/// is the wrong behaviour for all three of these.</para>
/// </summary>
public enum NotificationKind
{
    /// <summary>
    /// Nothing is wrong. A fact, a success, or progress: up to date, update available,
    /// installing, diagnostics copied.
    /// </summary>
    Information,

    /// <summary>
    /// Worth the user's attention, but nothing has failed outright — a usage threshold
    /// crossed, spend leaving the plan, or a retryable failure that will try itself again.
    /// </summary>
    Warning,

    /// <summary>
    /// Something the user asked for did not happen, or O-view refused to do it. Reserved for
    /// the cases that genuinely warrant the loudest icon — a checksum mismatch is the one
    /// that matters, because it means the bytes that arrived were not the bytes the release
    /// published.
    /// </summary>
    Error,
}

/// <summary>
/// A request to tell the user something, raised by the engine and rendered by whichever
/// head is attached — a balloon tip on Windows, a freedesktop notification on Linux.
///
/// <para>Deliberately not a balloon tip in the engine's vocabulary: the decision of
/// <em>whether</em> to notify is accounting logic and belongs here, while <em>how</em> it
/// appears is the platform's business.</para>
///
/// <para><see cref="Kind"/> defaults to <see cref="NotificationKind.Information"/> on
/// purpose. A caller that forgets it then under-states rather than over-states, and
/// over-stating is the defect this exists to fix — a false alarm devalues every real one,
/// while an under-stated icon costs nothing the words do not already say.</para>
/// </summary>
public sealed record AppNotification(
    string Title,
    string Message,
    NotificationKind Kind = NotificationKind.Information);
