namespace OView.Presentation.Text;

/// <summary>
/// The notifications that are not about updates, those are in <see cref="UpdateText"/>.
///
/// <para><b>A notification is the one surface the user cannot go back and re-read.</b> A
/// balloon is gone in seconds and a freedesktop notification may never be seen at all, so
/// each of these has to say what happened and what to do about it in the one pass it gets.
/// That makes them the copy most worth asserting and least worth writing twice.</para>
/// </summary>
public static class NoticeText
{
    /// <summary>
    /// Said after O-view restarts itself because WPF's extracted native libraries went
    /// missing. A tray icon that blinks out and back unexplained reads as a crash.
    /// </summary>
    public const string RestartedTitle = "O-view restarted";

    /// <inheritdoc cref="RestartedTitle"/>
    public const string RestartedBody =
        "A cleanup tool removed some of O-view's display files from your temp folder, so "
        + "O-view restarted to restore them. Click the icon again to open it.";

    /// <summary>
    /// Issue #186. <see cref="PanelText.WeeklyResetConflict"/> under it carries the observed
    /// time and what to do, so this only has to say which thing disagrees.
    /// </summary>
    public const string WeeklyResetConflictTitle = "Weekly reset time doesn't match";

    /// <summary>
    /// Said when session usage crosses the user's notification threshold (issue #141). The
    /// title names the feature rather than the crossing, <see cref="ThresholdBody"/> carries
    /// the number, because it is reused for every threshold a user may set.
    /// </summary>
    public const string ThresholdTitle = "Claude usage";

    /// <inheritdoc cref="ThresholdTitle"/>
    public static string ThresholdBody(int sessionPercent) =>
        $"Session usage is at {sessionPercent}% of the 5-hour limit.";

    public const string DiagnosticsCopiedTitle = "Diagnostics copied";

    /// <summary>
    /// Says where to put them, and what is not in them. The bundle is on its way to a public
    /// issue, so the reassurance is what gets it pasted rather than audited line by line
    /// first.
    /// </summary>
    public const string DiagnosticsCopiedBody =
        "Paste them into your bug report. No tokens or conversation content are included.";

    /// <summary>Said when the clipboard was locked by another process.</summary>
    public const string DiagnosticsCopyFailedTitle = "Couldn't copy diagnostics";

    /// <inheritdoc cref="DiagnosticsCopyFailedTitle"/>
    public const string DiagnosticsCopyFailedBody =
        "The clipboard was unavailable. Please try again.";

    /// <summary>
    /// Told to a Linux user when nothing on the session bus can draw a tray icon (ADR-0013
    /// decision 2). Notifications are a different service from the tray, so this reaches them
    /// on the one configuration where the icon cannot.
    ///
    /// <para>It leads with "O-view is running", because the user has just launched something
    /// that appears to have done nothing, and that is the fact which reframes the rest from
    /// a failure into a missing extension.</para>
    /// </summary>
    public const string NoTrayHostTitle = "O-view is running, but has nowhere to show its icon";

    /// <summary>
    /// Nothing shows this today, the notice only fires when a host is missing, but the
    /// probe's three states are three sentences here so a skin never has to invent copy for a
    /// state it was handed.
    /// </summary>
    public const string TrayHostPresent = "A notification-area host is running.";

    /// <summary>
    /// States the observation rather than asserting anything about the machine (rule 6): "no
    /// host on the bus" is a fact, "GNOME is missing an extension" is a guess about why. The
    /// recovery is stated because the icon does reappear on its own once a host arrives, and
    /// a user who assumed a restart was needed would never find that out.
    /// </summary>
    public const string TrayHostAbsent =
        "O-view found no notification-area host on the session bus, so its icon has nowhere "
        + "to appear. GNOME does not provide one by default, installing an "
        + "AppIndicator/KStatusNotifierItem extension adds it, and O-view will pick it up "
        + "without needing a restart.";

    /// <summary>
    /// Claims nothing about hosts: "I could not ask" and "I asked and nothing was there" are
    /// different facts, and only one of them justifies telling the user to install an
    /// extension.
    /// </summary>
    public const string TrayHostUnknown =
        "O-view could not reach the session bus to check for a notification-area host.";
}
