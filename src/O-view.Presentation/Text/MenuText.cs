using System.Globalization;

namespace OView.Presentation.Text;

/// <summary>
/// The words on the tray menu's rows (see <see cref="PanelText"/> for why wording like this
/// is shared rather than written per platform).
///
/// <para>Both heads draw this menu, with entirely different machinery — a WPF flyout of
/// templated Buttons, a DBusMenu the desktop renders — which is the shape in which two
/// surfaces drift. The Linux menu carries three of these rows and once had its own copy of
/// two of them, so "Exit O-view" existed twice and nothing would have failed if one of them
/// had become "Quit". The resume row is the exception that proves the rule: it already read
/// <see cref="PanelText.UsageRefreshBlockedRow"/> on both heads.</para>
/// </summary>
public static class MenuText
{
    public const string AppName = "O-view";

    public static string VersionLabel(string version) => $"v{version}";

    /// <summary>
    /// The row that opens the panel. Linux only in practice, and not because the wording is
    /// platform-specific: on Windows the icon's own left click opens the panel, so a row saying
    /// the same thing would be a second way to do what the obvious gesture already does.
    ///
    /// <para>Ubuntu's AppIndicators extension gives the panel no gesture at all — a single click
    /// opens the menu and only a double click opens the panel, which nothing tells the user to
    /// try (issue #315). A row works on every SNI host whatever its click handling, which is why
    /// it is the fix rather than asking each desktop for different behaviour.</para>
    /// </summary>
    public const string ShowUsageRow = "Show usage";

    /// <summary>On both heads' menus.</summary>
    public const string RunAtStartupRow = "Run at startup";

    /// <summary>Issue #140. Windows only in practice — the row is dropped on a build that
    /// cannot self-install — but the wording is not platform-specific.</summary>
    public const string UpdateAutomaticallyRow = "Update automatically";

    /// <summary>The threshold row reads "Notify at [70% ⌄] usage": this, the pill, then
    /// <see cref="NotifySuffix"/>.</summary>
    public const string NotifyPrefix = "Notify at";

    /// <summary>
    /// The words after the pill. The shorter wording is a deliberate choice (@mlengmark on
    /// issue #141), taken over "session usage": the watcher reads
    /// <c>UsageSnapshot.SessionPercentValue</c> only, and what closes that gap is the notification
    /// itself, which names the window. <b>If that balloon's copy is ever shortened, this row
    /// becomes the only place the distinction could have been made.</b>
    /// </summary>
    public const string NotifySuffix = "usage";

    /// <summary>
    /// A percentage as the pill and the choices below it read it. One definition, because the
    /// pill renders the setting as it actually stands and the choices render the three offers:
    /// the same number formatted two ways would make a matching value look like a mismatch.
    /// </summary>
    public static string ThresholdPercent(int percent) =>
        string.Create(CultureInfo.InvariantCulture, $"{percent}%");

    /// <summary>Issue #186. The ellipsis says a dialog follows.</summary>
    public const string WeeklyResetRow = "Weekly reset…";

    /// <summary>The value beside that row when nothing has been entered. States the answer in
    /// effect rather than only offering to change it: it tells a user who has never opened the
    /// dialog what O-view is currently doing.</summary>
    public const string WeeklyResetDerived = "derived";

    /// <summary>An entry as the row shows it — three letters and the time, because the row
    /// has a label beside it and no room for more.</summary>
    public static string WeeklyResetValue(string dayText, string timeText) =>
        $"{dayText[..3]} {timeText}";

    public const string CopyDiagnosticsRow = "Copy diagnostics";

    /// <summary>Sits directly above exit, as issue #18 asks.</summary>
    public const string CheckForUpdatesRow = "Check for updates…";

    /// <summary>On both heads' menus. Names the app, because a bare "Exit" in a
    /// desktop-rendered menu does not say what it is exiting.</summary>
    public const string ExitRow = "Exit " + AppName;
}
