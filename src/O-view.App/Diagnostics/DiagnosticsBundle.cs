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
/// either platform to be comparable. It contains no token and no conversation content; the
/// org UUID is included because it is the documented filter key.</para>
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
            ClaudeAccount.TryRead(), new WeeklyResetLog(), DateTimeOffset.UtcNow);

    /// <summary>Overload taking every input explicitly, so the layout is testable.</summary>
    public static string Build(
        DiagnosticsEnvironment environment,
        PlanHistoryReport planHistory,
        TranscriptScopeReport scope,
        ClaudeAccount? account,
        WeeklyResetLog weeklyResets,
        DateTimeOffset utcNow)
    {
        var text = new StringBuilder();
        text.Append(planHistory.ToClipboardText(environment.Version));

        AppendMachine(text, environment);
        AppendRoots(text);
        AppendAccount(text, account);
        text.Append(scope.ToClipboardText());
        AppendWeeklyResets(text, weeklyResets, utcNow);

        return text.ToString();
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
}
