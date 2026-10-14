using System.IO;
using OView.App;
using OView.App.Diagnostics;
using OView.Presentation.Text;
using OView.Tray.Tray;
using OView.Tray.Updates;

namespace OView.Tray.Diagnostics;

/// <summary>
/// Delivery of the support bundle on Windows: onto the clipboard from the menu row, or into
/// a file for the <c>--diagnose</c> hook. <see cref="DiagnosticsBundle"/> assembles and
/// redacts the text itself, so a report reads the same on either platform; what lives here is
/// only how this head hands it over, and what it tells the user afterwards.
/// </summary>
public sealed class DiagnosticsClipboard
{
    private readonly ITrayHost _trayHost;
    private readonly UsageEngine _engine;

    public DiagnosticsClipboard(ITrayHost trayHost, UsageEngine engine)
    {
        _trayHost = trayHost;
        _engine = engine;
    }

    /// <summary>
    /// Puts a support bundle on the clipboard: what the app reads, from where, and what it
    /// found. Covers both usage sources — the plan-history file (session/weekly %) and the
    /// JSONL transcripts (token tiles) — plus whether account info resolved, because which
    /// of those are blank narrows the cause immediately. Contains no token and no
    /// conversation content, and is redacted before it leaves — the account name is replaced
    /// in every path and org UUIDs are truncated to eight characters, because this goes on
    /// the clipboard on its way to a public issue (see <c>Redact</c>).
    /// </summary>
    public void Copy()
    {
        try
        {
            System.Windows.Clipboard.SetText(Build(_engine));
            _trayHost.ShowNotification(
                NoticeText.DiagnosticsCopiedTitle, NoticeText.DiagnosticsCopiedBody,
                NotificationKind.Information);
        }
        catch (Exception)
        {
            // The clipboard can be locked by another process; failing to copy must not crash.
            _trayHost.ShowNotification(
                NoticeText.DiagnosticsCopyFailedTitle, NoticeText.DiagnosticsCopyFailedBody,
                NotificationKind.Warning);
        }
    }

    /// <summary>Writes the diagnostics report to a file (the --diagnose hook).</summary>
    public static void WriteToFile(string? path)
    {
        var target = path is { Length: > 0 } p ? p : "oview-diagnostics.txt";
        try
        {
            // No engine: the hook runs before the app is assembled, which is also what makes
            // the deep audit affordable — a deliberate command-line run has no UI to freeze
            // and no engine to interrupt (issue #218).
            File.WriteAllText(target, Build(engine: null, deepAudit: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Diagnostic hook — a failure here must not surface as a crash dialog.
        }
    }

    /// <summary>
    /// The bundle itself is shared with the Linux head (<see cref="DiagnosticsBundle"/>) so
    /// a report reads the same on either platform and cannot drift. What is supplied here
    /// is only what this head knows: its version and how it was installed. Windows has no
    /// desktop-environment or session-type question, and its notification area is part of
    /// the shell and cannot be absent — so those fields are omitted rather than padded with
    /// "n/a".
    /// </summary>
    private static string Build(UsageEngine? engine, bool deepAudit = false)
    {
        var environment = new DiagnosticsEnvironment(
            Version: UpdateService.CurrentVersion,
            InstallKind: UpdateService.CurrentInstallKind.ToString());

        // When there is a running engine, the store is read through the connection that engine
        // is actually using — so the bundle reports what the app believes rather than what a
        // reader opened for the occasion finds. The --diagnose hook has no engine and takes the
        // other path, and the two label themselves differently on purpose.
        return engine is null
            ? DiagnosticsBundle.Build(environment, deepAudit)
            : DiagnosticsBundle.Build(environment, engine.InspectStore());
    }
}
