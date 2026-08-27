using System.Runtime.InteropServices;
using System.Text;
using OView.Core.Models;
using OView.Core.Providers.CachedUsage;
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
    /// <param name="deepAudit">
    /// Re-read every transcript and reconcile it against the ledger (<see cref="IngestAudit"/>).
    /// Seconds of work on a large history, so it is opt-in and belongs to <c>--diagnose</c>:
    /// Copy diagnostics runs on the UI thread, where a pass that scales with total history is
    /// the freeze issue #125 was about.
    /// </param>
    public static string Build(DiagnosticsEnvironment environment, bool deepAudit = false) =>
        Build(environment, PlanHistoryDiagnostics.Inspect(), TranscriptScopeReport.Inspect(),
            ClaudeAccount.TryRead(), new WeeklyResetAnchor(), DateTimeOffset.UtcNow,
            CorruptBackups.Inspect(), FileLog.Tail(), RollupStoreReport.Inspect(),
            deepAudit ? IngestAuditReport.Run() : IngestAuditReport.NotRun,
            CoworkSessionReport.Inspect(), ClaudeWriteSurvey.Inspect());

    /// <summary>
    /// The bundle as the <b>running</b> app sees it: identical except that the store is read
    /// through the connection the app already holds. See <see cref="RollupStoreReport.Origin"/>
    /// for why that distinction is worth a whole parameter.
    /// </summary>
    public static string Build(DiagnosticsEnvironment environment, RollupStoreReport store) =>
        Build(environment, PlanHistoryDiagnostics.Inspect(), TranscriptScopeReport.Inspect(),
            ClaudeAccount.TryRead(), new WeeklyResetAnchor(), DateTimeOffset.UtcNow,
            CorruptBackups.Inspect(), FileLog.Tail(), store, IngestAuditReport.NotRun,
            CoworkSessionReport.Inspect(), ClaudeWriteSurvey.Inspect());

    /// <summary>Overload taking every input explicitly, so the layout is testable.</summary>
    public static string Build(
        DiagnosticsEnvironment environment,
        PlanHistoryReport planHistory,
        TranscriptScopeReport scope,
        ClaudeAccount? account,
        WeeklyResetAnchor weeklyReset,
        DateTimeOffset utcNow,
        CorruptBackupReport? corruptBackups = null,
        IReadOnlyList<string>? logTail = null,
        RollupStoreReport? store = null,
        IngestAuditReport? ingestAudit = null,
        CoworkSessionReport? coworkSessions = null,
        ClaudeWriteSurvey? writeSurvey = null)
    {
        var text = new StringBuilder();
        text.Append(planHistory.ToClipboardText(environment.Version));

        AppendMachine(text, environment);
        AppendRoots(text);
        AppendAccount(text, account);
        text.Append(scope.ToClipboardText());

        // Directly after the transcript scan, because it is the answer to the question that
        // scan raises and cannot settle: those counts say what O-view found, and this says what
        // Cowork itself expected to be there (issue #218). A machine actively running Cowork
        // whose newest transcript is two days old reads as perfectly healthy in every line
        // above and is named outright here.
        text.Append((coworkSessions ?? CoworkSessionReport.None).ToClipboardText(utcNow));

        // And if it is in neither place, where is it? The two sections above only ever look
        // where this build already believes the answer is, so a layout that has moved reads as
        // an absence with no explanation — identical to a machine sitting idle. This sweeps
        // Claude's own directories and prints what is actually being written (issue #218).
        text.Append((writeSurvey ?? ClaudeWriteSurvey.Empty).ToClipboardText(utcNow));

        AppendWeeklyReset(text, weeklyReset, utcNow);
        AppendCorruptBackups(text, corruptBackups ?? CorruptBackupReport.Empty);

        // Placed before the log tail so the store's state and the poll lines that produced it
        // read together — "0 changed" means one thing beside a ledger that is current and
        // quite another beside one that is five days stale.
        var storeReport = store ?? RollupStoreReport.Unavailable(
            RollupStore.DefaultPath, RollupStoreReport.OpenedForReport, "not inspected");

        text.Append(storeReport.ToClipboardText());
        AppendIngestGap(text, scope, storeReport);

        // Straight after the store, because it answers the question the store's own numbers
        // raise and cannot settle: the store reports what it believes it ingested, this reports
        // what is actually on disk beside it (issue #218). Its "not run" line is printed rather
        // than omitted so the section's absence is never mistaken for a section that failed.
        text.Append((ingestAudit ?? IngestAuditReport.NotRun).ToClipboardText());

        AppendRecentLog(text, logTail ?? []);

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

    /// <summary>
    /// The account read, and <b>every candidate it considered</b>.
    ///
    /// <para>One line saying "not readable" was actively misleading on 2026-08-24: Claude Code
    /// had created a second <c>.claude.json</c> carrying only migration keys, O-view resolved to
    /// it because it existed, and the bundle reported a failure to read a file that read
    /// perfectly — while the populated one sat unmentioned. A resolution that picks between
    /// candidates has to show the picking, or the next report of this costs another round trip
    /// to discover which file was even opened.</para>
    /// </summary>
    private static void AppendAccount(StringBuilder text, ClaudeAccount? account)
    {
        text.AppendLine($"  account file  : {(account is null ? "no candidate has an account" : "read ok")}"
                        + $" (org {account?.OrganizationUuid ?? "n/a"}, tier {account?.Tier ?? "n/a"})");

        foreach (var candidate in ClaudeAccount.Candidates())
        {
            var state = !File.Exists(candidate) ? "missing"
                : ClaudeAccount.TryRead(candidate) is not null ? "has oauthAccount"
                : "no oauthAccount";
            var cached = File.Exists(candidate) && CachedUtilization.TryRead(candidate) is { } u
                ? $", cached figures {(DateTimeOffset.UtcNow - u.FetchedAtUtc).TotalMinutes:0} min old"
                : File.Exists(candidate) ? ", no cached figures" : "";

            // Paths are redacted by Redact.Bundle on the way out, as everywhere else here.
            text.AppendLine($"    candidate   : {candidate}  <-- {state}{cached}");
        }
    }

    /// <summary>
    /// The stored weekly-reset anchor and what it projects to (ADR-0014).
    ///
    /// <para>This replaces a list of derived observations with their brackets. That list was
    /// the right report while the reset was inferred from drops in a sampled series — it
    /// separated "no reset seen yet" from "drops detected and discarded". None of those states
    /// exist any more: either an exact instant has been reported and stored, or it has not.
    /// Printing the anchor's weekday alongside it makes a wrong one obvious at a glance, which
    /// a bare timestamp does not.</para>
    ///
    /// <para>Timestamps only — no usage figures, nothing identifying.</para>
    /// </summary>
    private static void AppendWeeklyReset(StringBuilder text, WeeklyResetAnchor anchor, DateTimeOffset utcNow)
    {
        try
        {
            text.AppendLine($"  weekly anchor : {WeeklyResetAnchor.DefaultPath}");

            if (anchor.Read() is not { } stored)
            {
                // Not a fault. It means Claude Code has never reported one on this machine,
                // which is exactly when the user should be entering it by hand.
                text.AppendLine("    anchor      : none stored — never reported by Claude Code");
                text.AppendLine("  next weekly   : unknown — no anchor and no entered value");
                return;
            }

            text.AppendLine($"    anchor      : {stored:u} ({stored.UtcDateTime.DayOfWeek})");
            text.AppendLine($"  next weekly   : {WeeklyWindow.NextAfter(stored, utcNow):u}");
        }
        catch (Exception ex)
        {
            text.AppendLine($"  weekly anchor : unreadable ({ex.GetType().Name})");
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
    /// <summary>
    /// The tail of the log, which is the only part of this bundle that describes what the
    /// app <i>did</i> rather than what its inputs look like.
    ///
    /// <para>Every other field here reports a file: which paths exist, how many samples they
    /// hold, how old they are. All of it can read perfectly while the app has not completed a
    /// poll in days — that is exactly the report that arrived three times over, each one
    /// leading with <c>status : Ok</c>. Three lines per poll turn that ambiguity into a fact
    /// the user pastes without being asked for it, which matters because the alternative is a
    /// round trip asking someone to re-run with a flag and reproduce a stall on demand.</para>
    ///
    /// <para>Thirty lines: enough to show the last few polls and a session header, small
    /// enough that the bundle stays pasteable. The log carries no tokens and no conversation
    /// content by construction (<see cref="IAppLog"/>), and <see cref="Redact"/> runs over
    /// this like every other field on the way out.</para>
    ///
    /// <para><b>Passed in rather than read here</b>, for the reason
    /// <see cref="UsageEngine"/> gives about reaching past an injected dependency to real user
    /// data: reading <see cref="FileLog.DefaultPath"/> from inside the bundle would put the
    /// developer's own log into every test that builds one, passing on a CI runner that has
    /// none and failing on the machine of whoever last ran the app.</para>
    /// </summary>
    private static void AppendRecentLog(StringBuilder text, IReadOnlyList<string> lines)
    {
        text.AppendLine($"  log           : {FileLog.DefaultPath}");

        if (lines.Count == 0)
        {
            // Distinguished from a log that exists and is empty only by the path above, and
            // deliberately not explained away: "no log" on a build that logs by default is
            // itself a finding — it means this run never reached the first write.
            text.AppendLine("    (no log lines yet)");
            return;
        }

        foreach (var line in lines)
        {
            text.AppendLine($"    | {line}");
        }
    }

    /// <summary>
    /// The one subtraction a reader should not have to do themselves: transcripts on disk
    /// against transcripts the store has ever recorded a watermark for (issue #218).
    ///
    /// <para>The two figures already sit in this bundle, twenty lines apart, in sections written
    /// for different reasons — and on the machine this was added from they read 38 and 32, with
    /// three of the 32 belonging to files Claude Code has since deleted. Nine transcripts had
    /// never been ingested at all, and nothing said so. <c>0 behind by 0 bytes</c> is only ever
    /// a statement about files the store already knows about; a file it has never seen is behind
    /// by its entire length and appears in none of those numbers.</para>
    ///
    /// <para>Printed even when it is zero. A gap that closes is worth seeing closed, and a line
    /// that appears only on a bad machine cannot be recognised as missing on a good one.</para>
    /// </summary>
    private static void AppendIngestGap(
        StringBuilder text, TranscriptScopeReport scope, RollupStoreReport store)
    {
        if (store.Failure is { Length: > 0 })
        {
            // The store could not be read at all, so "0 tracked" would be a fact about this
            // reader rather than about the machine.
            return;
        }

        // Watermarks whose file is still on disk. The rest point at transcripts Claude Code has
        // deleted since — ordinary, already counted as "gone", and not evidence of a gap.
        var tracked = Math.Max(0, store.TrackedFiles - store.FilesGone);
        var untracked = Math.Max(0, scope.TotalFiles - tracked);

        text.AppendLine($"  ingest gap    : {scope.TotalFiles} file(s) on disk, {tracked} tracked and present, "
                        + $"{untracked} with no watermark");
    }

    private static void AppendCorruptBackups(StringBuilder text, CorruptBackupReport report) =>
        text.AppendLine($"  corrupt stores: {(report.Generations == 0
            ? "none"
            : $"{report.Generations} quarantined, newest {report.NewestStamp}, "
              + $"{CorruptBackups.DescribeBytes(report.Bytes)} (keeping {CorruptBackups.KeepGenerations})")}");
}
