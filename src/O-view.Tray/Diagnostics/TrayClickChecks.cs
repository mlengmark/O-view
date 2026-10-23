using System.IO;
using System.Text;
using System.Windows.Threading;
using OView.App;
using OView.Tray.Popup;
using OView.Tray.Updates;

namespace OView.Tray.Diagnostics;

/// <summary>
/// Drives the real tray click on the real desktop, over --toggle-check and --popup-check.
///
/// <para><b>Why these live here and not beside <see cref="SampleRenderer"/>'s checks</b> (issue
/// #249). <c>--menu-check</c> and <c>--fold-check</c> construct their own windows, so they can
/// run before the single-instance mutex. These two cannot: driving the real <c>ShowPopup</c> is
/// the entire point, and that needs the engine, the tray host and the settings that only exist
/// after startup. So <see cref="App"/> still owns <c>ShowPopup</c> and the popup field, each
/// check here is handed both rather than reaching for <c>Application.Current</c>, and shuts the
/// app down itself when its sequence ends.</para>
/// </summary>
internal static class TrayClickChecks
{
    /// <summary>
    /// Drives the tray icon's own open → close → open sequence and reports the panel's state
    /// after each step. The toggle regressed in a way that only shows on the SECOND open, which
    /// a single --show-popup run can never catch.
    /// </summary>
    public static void RunToggleCheck(string path, Action showPopup, Func<PopupWindow?> popup, Action shutdown)
    {
        var log = new StringBuilder();
        var step = 0;

        void Record(string label)
        {
            var p = popup();
            log.AppendLine($"{label,-28} visible={p?.IsVisible} opacity={p?.Opacity:0.00} " +
                           $"clickAway={p?.ClosedByClickAway} " +
                           $"size={p?.ActualWidth:0}x{p?.ActualHeight:0} " +
                           $"left={p?.Left:0} top={p?.Top:0}");
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            switch (step++)
            {
                case 0:
                    showPopup();
                    break;
                case 1:
                    Record("after 1st click (open)");
                    showPopup();          // the second click, while it is open
                    break;
                case 2:
                    Record("after 2nd click (close)");
                    break;
                case 3:
                    showPopup();          // the third click, should open again
                    break;
                case 4:
                    Record("after 3rd click (reopen)");
                    showPopup();
                    break;
                case 5:
                    Record("after 4th click (close)");
                    showPopup();
                    break;
                case 6:
                    Record("after 5th click (reopen)");
                    timer.Stop();
                    File.WriteAllText(path, log.ToString());
                    shutdown();
                    break;
            }
        };
        timer.Start();
    }

    /// <summary>
    /// Exercises the tray click the way a user does: open, close, open again, each step through
    /// the same <c>ShowPopup</c> the icon itself calls, spaced far enough apart that the close
    /// transition and the toggle's grace window have both elapsed. Writes what the panel actually
    /// is at each point, so a "stopped reacting" report becomes a fact rather than a guess.
    ///
    /// <para><b>The reopen is the assertion that matters.</b> A tray click is a toggle, and the
    /// click itself dismisses an open surface by taking focus from it, so reopening naively made
    /// the icon a one-way switch that could only ever open. <c>App.CompletedToggle</c> is what
    /// stops that, and its grace window is why the steps are spaced rather than run back to
    /// back.</para>
    ///
    /// <para>It also records <paramref name="shell"/>'s <see cref="ISkinToShell.UsageRefreshAttempts"/>
    /// either side of the first open, which is the one part of issue #234 no unit test could reach:
    /// everything behind the panel-open path is verified, but the call inside <c>ShowPopup</c> had
    /// never run assembled.</para>
    /// </summary>
    public static void RunPopupCheck(
        string path, Action showPopup, Func<PopupWindow?> popup, ISkinToShell shell, Action shutdown)
    {
        var report = new StringBuilder();
        var refreshesBefore = shell.UsageRefreshAttempts;

        void Record(string label) =>
            report.AppendLine(
                $"{label,-34} visible={popup()?.IsVisible.ToString() ?? "<none>",-5} " +
                $"clickAway={popup()?.ClosedByClickAway.ToString() ?? "<none>",-5} " +
                $"refreshAttempts={shell.UsageRefreshAttempts}");

        var opened = new bool[2];

        var steps = new List<(string Label, Action Do)>
        {
            ("open (first click)", () => showPopup()),
            ("after first open", () => opened[0] = popup()?.IsVisible == true),
            ("dismiss", () => popup()?.DismissNow()),
            ("after dismiss", () => { }),
            ("open again", () => showPopup()),
            ("after reopen", () => opened[1] = popup()?.IsVisible == true),
        };

        var step = 0;
        report.AppendLine($"popup check · {UpdateService.CurrentVersion}");
        report.AppendLine($"refresher available: {shell.CanRefreshUsageCache}");
        report.AppendLine();

        // Comfortably longer than both the close transition and the toggle's grace window, so a
        // failure here is the behaviour rather than the spacing.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        timer.Tick += (_, _) =>
        {
            if (step > 0)
            {
                Record(steps[step - 1].Label);
            }

            if (step == steps.Count)
            {
                timer.Stop();

                var refreshed = shell.UsageRefreshAttempts > refreshesBefore;
                report.AppendLine();
                report.AppendLine($"first click opened the panel   : {opened[0]}");
                report.AppendLine($"reopen after dismiss opened it : {opened[1]}");
                report.AppendLine($"panel open triggered a refresh : {refreshed}" +
                                  (shell.CanRefreshUsageCache ? "" : "  (no refresher, not required)"));

                // The refresh is only required of a build that has a refresher at all; the other
                // two are required of every build, because they are the click working.
                var pass = opened[0] && opened[1] && (refreshed || !shell.CanRefreshUsageCache);
                report.AppendLine($"RESULT: {(pass ? "PASS" : "FAIL")}");

                File.WriteAllText(path, report.ToString());
                shutdown();
                return;
            }

            steps[step++].Do();
        };
        timer.Start();
    }
}
