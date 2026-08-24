using System.Runtime.InteropServices;
using System.Text;
using OView.Core.Models;
using OView.Core.Providers.Jsonl;
using OView.Core.Providers.PlanHistory;
using OView.Core.Storage;

namespace OView.App.Diagnostics;

/// <summary>
/// The facts about this machine that only the head can supply.
///
/// <para>Injected rather than detected here, so the bundle stays platform-neutral and every
/// field is testable against a value rather than against whatever the test runner happens
/// to be running on.</para>
/// </summary>
/// <param name="Version">The running build's version.</param>
/// <param name="InstallKind">How this build arrived — installer, portable, deb, tarball.</param>
/// <param name="Desktop">Desktop environment. Null on Windows, where there is only one.</param>
/// <param name="SessionType">X11 or Wayland. Null on Windows.</param>
/// <param name="TrayHost">
/// Whether a notification-area host exists. Null on Windows, where the notification area is
/// part of the shell and cannot be absent.
/// </param>
public sealed record DiagnosticsEnvironment(
    string Version,
    string InstallKind,
    string? Desktop = null,
    string? SessionType = null,
    string? TrayHost = null);

/// <summary>
/// The support bundle a user pastes into a bug report: what O-view read, from where, and
/// what it found.
///
/// <para>Shared because both heads need it and because a report has to read the same on
/// either platform to be comparable. It contains no token and no conversation content.</para>
///
/// <para><b>It is redacted before it is returned, because of where it goes.</b> Users are
/// told to paste this into a public GitHub issue, and it previously carried the account name
/// in every path and the organization UUID in full — both the one from <c>~/.claude.json</c>
/// and the whole list found in the plan-history file. That is a real identifier and a real
/// person's account name, published permanently and searchably, to diagnose a tray icon.
/// This project already treated an org UUID as sensitive when one was committed to the repo
/// (commit 3f7cc2f, "security: redact real org UUID"); publishing the same value from every
/// user's bug report was the same disclosure by a longer route.</para>
///
/// <para><see cref="Redact"/> runs once over the finished text rather than at each field, so
/// a field added later is covered whether or not its author thought about it. Path shape and
/// a UUID prefix survive, because those are what the bundle is read for — see that class for
/// the reasoning.</para>
///
/// <para><b>Labels name what a path is, not which platform's vocabulary it came from.</b>
/// This block used to say <c>appdata root</c>, which on Linux points at <c>~/.config</c> —
/// a Windows word over a Linux path. The whole reason the resolved roots are printed is so
/// that a wrong <c>SpecialFolder</c> resolution is visible, and a misleading label defeats
/// exactly that (CLAUDE.md rule 6).</para>
/// </summary>
public static class DiagnosticsBundle
{
    public static string Build(DiagnosticsEnvironment environment) =>
        Build(environment, PlanHistoryDiagnostics.Inspect(), TranscriptScopeReport.Inspect(),
            ClaudeAccount.TryRead(), new WeeklyResetLog(), DateTimeOffset.UtcNow,
            CorruptBackups.Inspect());

    /// <summary>Overload taking every input explicitly, so the layout is testable.</summary>
    public static string Build(
        DiagnosticsEnvironment environment,
        PlanHistoryReport planHistory,
        TranscriptScopeReport scope,
        ClaudeAccount? account,
        WeeklyResetLog weeklyResets,
        DateTimeOffset utcNow,
        CorruptBackupReport? corruptBackups = null)
    {
        var text = new StringBuilder();
        text.Append(planHistory.ToClipboardText(environment.Version));

        AppendMachine(text, environment);
        AppendRoots(text);
        AppendAccount(text, account);
        text.Append(scope.ToClipboardText());
        AppendWeeklyResets(text, weeklyResets, utcNow);
        AppendCorruptBackups(text, corruptBackups ?? CorruptBackupReport.Empty);

        // The single funnel. Every field above, and every field added below it later, is
        // redacted here rather than at its own call site — see the class remarks.
        return Redact.Bundle(text.ToString());
    }

    /// <summary>
    /// What kind of machine this is. On a two-platform build it is the first question any
    /// report has to answer, and it was entirely absent before.
    ///
    /// <para>The <c>tray host</c> line is the highest-value field here: "my icon doesn't
    /// appear" is the most likely Linux report by a wide margin, and this answers it without
    /// a round trip. An Avalonia tray icon reports success whether or not a host exists, so
    /// this is the only place the truth is written down
    /// (docs/findings/linux-tray-spike.md).</para>
    /// </summary>
    private static void AppendMachine(StringBuilder text, DiagnosticsEnvironment environment)
    {
        text.AppendLine($"  platform      : {RuntimeInformation.OSDescription.Trim()} / {RuntimeInformation.OSArchitecture}");
        text.AppendLine($"  install kind  : {environment.InstallKind}");

        // Only printed where they mean something. A "desktop: n/a" line on Windows is noise
        // that makes the Linux fields harder to spot in a pasted report.
        if (environment.Desktop is { Length: > 0 } desktop)
        {
            text.AppendLine($"  desktop       : {desktop}");
        }

        if (environment.SessionType is { Length: > 0 } session)
        {
            text.AppendLine($"  session       : {session}");
        }

        if (environment.TrayHost is { Length: > 0 } trayHost)
        {
            text.AppendLine($"  tray host     : {trayHost}");
        }
    }

    /// <summary>
    /// The resolved roots. If <see cref="Environment.SpecialFolder"/> resolution ever
    /// returns something unexpected, every path above is wrong and every other field is a
    /// consequence — so the roots are printed rather than assumed.
    /// </summary>
    private static void AppendRoots(StringBuilder text)
    {
        text.AppendLine($"  config root   : {Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}");
        text.AppendLine($"  data root     : {Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}");
        text.AppendLine($"  home          : {Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}");
        text.AppendLine($"  process       : {Environment.ProcessPath}");
    }

    private static void AppendAccount(StringBuilder text, ClaudeAccount? account) =>
        text.AppendLine($"  account file  : {(account is null ? "not readable" : "read ok")}"
                        + $" (org {account?.OrganizationUuid ?? "n/a"}, tier {account?.Tier ?? "n/a"})");

    /// <summary>
    /// What the weekly-reset discovery has actually found (ADR-0011). "Weekly says waiting"
    /// is otherwise undiagnosable from the outside: it looks identical whether no reset has
    /// occurred yet, the file was never written, or drops are being detected and discarded.
    /// Listing the observations with their brackets separates those in one glance.
    /// Timestamps only — no usage figures, nothing identifying.
    /// </summary>
    private static void AppendWeeklyResets(StringBuilder text, WeeklyResetLog log, DateTimeOffset utcNow)
    {
        try
        {
            var observations = log.GetObservations();
            text.AppendLine($"  weekly resets : {observations.Count} observed ({WeeklyResetLog.DefaultPath})");
            foreach (var o in observations.TakeLast(5))
            {
                text.AppendLine($"    reset       : by {o.LatestUtc:u} "
                                + $"(± {o.Uncertainty.TotalMinutes:0} min{(o.IsPrecise ? "" : ", Desktop not sampling")})");
            }

            var next = WeeklyResetDetector.PredictNextReset(observations, utcNow);
            text.AppendLine($"  next weekly   : {(next is null ? "unknown — waiting for first reset" : $"{next.AtUtc:u}")}");
        }
        catch (Exception ex)
        {
            text.AppendLine($"  weekly resets : unreadable ({ex.GetType().Name})");
        }
    }

    /// <summary>
    /// Whether the rollup store has had to quarantine itself, and what is still on disk from
    /// the last time it did (issue #160).
    ///
    /// <para>This is the only place the retained <c>.corrupt-*</c> files are named. Before it,
    /// they were kept "so the corruption can still be examined" while nothing told anyone they
    /// existed — which is what makes bounding their number reasonable: a bug report can now
    /// point at them.</para>
    ///
    /// <para><b>The count is of files retained, not of corruption events, and the wording says
    /// so.</b> Pruning is what bounds the first and it necessarily discards the second — after
    /// a prune, a machine that corrupted seven times and one that corrupted twice look
    /// identical here. That is the trade the issue accepts, and the retention limit is printed
    /// alongside the count so a reader can see the ceiling rather than mistake it for a
    /// total (rule 6). The newest stamp still answers the question worth asking of a single
    /// report — <i>when did this last happen</i> — and the "none" case is printed rather than
    /// omitted so its absence is never ambiguous with a field that failed to render.</para>
    /// </summary>
    private static void AppendCorruptBackups(StringBuilder text, CorruptBackupReport report) =>
        text.AppendLine($"  corrupt stores: {(report.Generations == 0
            ? "none"
            : $"{report.Generations} quarantined, newest {report.NewestStamp}, "
              + $"{CorruptBackups.DescribeBytes(report.Bytes)} (keeping {CorruptBackups.KeepGenerations})")}");
}
