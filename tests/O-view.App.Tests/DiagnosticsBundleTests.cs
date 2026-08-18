using OView.App.Diagnostics;
using OView.Core.Models;
using OView.Core.Providers.Jsonl;
using OView.Core.Providers.PlanHistory;
using OView.Core.Storage;

namespace OView.App.Tests;

/// <summary>
/// The support bundle. It is the only view a maintainer gets of someone else's machine, so
/// what it says has to be true of that machine and readable by someone who did not write
/// it.
/// </summary>
public class DiagnosticsBundleTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose()
    {
        _dir.Dispose();
        GC.SuppressFinalize(this);
    }

    private string Build(DiagnosticsEnvironment environment) =>
        DiagnosticsBundle.Build(
            environment,
            PlanHistoryDiagnostics.Inspect(_dir.File("absent.json")),
            TranscriptScopeReport.Inspect(null, []),
            account: null,
            new WeeklyResetLog(_dir.File("weekly-resets.json")),
            new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero));

    private static DiagnosticsEnvironment Windows => new("0.6.0", "WindowsInstaller");

    private static DiagnosticsEnvironment Linux => new(
        "0.6.0", "LinuxPackage", Desktop: "GNOME", SessionType: "wayland", TrayHost: "Absent");

    // ── the labels the issue was raised about ───────────────────────────────────────

    /// <summary>
    /// "appdata root" is a Windows word. On Linux it pointed at ~/.config, so the label
    /// taught the reader the wrong concept for the path beside it — and the whole reason
    /// the roots are printed is to make a wrong resolution visible (rule 6).
    /// </summary>
    [Fact]
    public void RootLabelsNameWhatThePathIsNotWhichPlatformItCameFrom()
    {
        var bundle = Build(Linux);

        Assert.Contains("config root", bundle, StringComparison.Ordinal);
        Assert.Contains("data root", bundle, StringComparison.Ordinal);
        Assert.Contains("home", bundle, StringComparison.Ordinal);
        Assert.DoesNotContain("appdata root", bundle, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("user profile", bundle, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryRootIsPrintedWithItsResolvedAbsolutePath()
    {
        var bundle = Build(Windows);

        // The label alone is useless; the point is the value beside it.
        //
        // Compared against the redacted spelling, not the raw one. The bundle now removes
        // the account name before returning (Redact), so the raw path never appears — but
        // what this test guards is unchanged: that the RESOLVED value is printed rather
        // than the label alone, because a wrong SpecialFolder resolution is only visible
        // in the value. Redaction replaces the account segment and nothing else, so the
        // resolution is still what is being asserted.
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.ApplicationData,
                     Environment.SpecialFolder.LocalApplicationData,
                     Environment.SpecialFolder.UserProfile,
                 })
        {
            var resolved = Redact.Bundle(Environment.GetFolderPath(folder));

            Assert.Contains(resolved, bundle, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NothingIdentifyingSurvivesIntoTheBundle()
    {
        // The bundle is pasted into public issues. This asserts the property at the funnel,
        // so a field added later cannot reintroduce the leak without failing here.
        var bundle = Build(Windows);

        foreach (var name in Redact.AccountNames())
        {
            Assert.DoesNotContain(name, bundle, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotMatch(
            @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
            bundle);
    }

    // ── the platform block ──────────────────────────────────────────────────────────

    [Fact]
    public void EveryBundleSaysWhatKindOfMachineItIs()
    {
        var bundle = Build(Windows);

        Assert.Contains("platform", bundle, StringComparison.Ordinal);
        Assert.Contains("install kind  : WindowsInstaller", bundle, StringComparison.Ordinal);
    }

    /// <summary>
    /// The highest-value field in the whole bundle. "My icon doesn't appear" is the most
    /// likely Linux report by a long way, and an Avalonia tray icon reports success whether
    /// or not a host exists — so this line is the only written record of the truth.
    /// </summary>
    [Fact]
    public void ALinuxBundleReportsTheTrayHostDesktopAndSession()
    {
        var bundle = Build(Linux);

        Assert.Contains("tray host     : Absent", bundle, StringComparison.Ordinal);
        Assert.Contains("desktop       : GNOME", bundle, StringComparison.Ordinal);
        Assert.Contains("session       : wayland", bundle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Windows has one desktop, and its notification area is part of the shell and cannot
    /// be absent. Padding those lines with "n/a" would make the fields that DO mean
    /// something harder to find in a pasted report.
    /// </summary>
    [Fact]
    public void AWindowsBundleOmitsTheFieldsThatDoNotApply()
    {
        var bundle = Build(Windows);

        Assert.DoesNotContain("desktop  ", bundle, StringComparison.Ordinal);
        Assert.DoesNotContain("session  ", bundle, StringComparison.Ordinal);
        Assert.DoesNotContain("tray host", bundle, StringComparison.Ordinal);
    }

    // ── the fields Windows must not lose ────────────────────────────────────────────

    [Fact]
    public void TheWindowsBundleStillCarriesEveryFieldItHadBefore()
    {
        var bundle = Build(Windows);

        Assert.Contains("app version", bundle, StringComparison.Ordinal);
        Assert.Contains("process", bundle, StringComparison.Ordinal);
        Assert.Contains("account file", bundle, StringComparison.Ordinal);
        Assert.Contains("weekly resets", bundle, StringComparison.Ordinal);
        // "installed : True/False" became "install kind", which subsumes it — a portable
        // build and an installer build are still distinguishable, and now so are the Linux
        // kinds.
        Assert.Contains("install kind", bundle, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bundle is pasted into public bug reports. It carries no token and no
    /// conversation content; the org UUID is the one identifier included, because it is the
    /// documented filter key.
    /// </summary>
    [Fact]
    public void NothingIdentifyingBeyondTheOrgKeyIsAdded()
    {
        var bundle = Build(Linux);

        foreach (var forbidden in new[] { "token", "sk-ant", "Bearer", "password", "secret" })
        {
            Assert.DoesNotContain(forbidden, bundle, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void AnUnreadableWeeklyResetLogDoesNotTakeTheBundleDown()
    {
        // A support bundle that throws is worse than one with a gap in it — the user is
        // already reporting a problem.
        var bundle = DiagnosticsBundle.Build(
            Linux,
            PlanHistoryDiagnostics.Inspect(_dir.File("absent.json")),
            TranscriptScopeReport.Inspect(null, []),
            account: null,
            new WeeklyResetLog(_dir.Path),   // a directory, not a file
            DateTimeOffset.UnixEpoch);

        Assert.Contains("weekly resets", bundle, StringComparison.Ordinal);
    }
}
