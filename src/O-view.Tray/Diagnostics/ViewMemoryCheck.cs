using System.IO;
using System.Text;
using OView.Core.Models;
using OView.Presentation.Panel;
using OView.Presentation.Samples;
using OView.Tray.Popup;

namespace OView.Tray.Diagnostics;

/// <summary>
/// <c>--view-check</c>: the panel opens on the view the reader left it in. The switch and the
/// flip tag are pressed on a real panel, which is closed and opened again, on each face and each
/// measure, each time on a panel built from nothing but the settings file, which is all a restart
/// carries over.
///
/// <para>Two things can break and neither shows in a still: a press that is never stored, and a
/// stored view that the next panel is not given. So what is read is the screen, the lit segment
/// and the face on show, and not the choice the panel was handed. The panel is closed straight
/// after a press, with the fold still running, because that is the press a reader makes on the
/// way out.</para>
///
/// <para>The file is a copy in the temp folder, written through <see cref="TraySettings"/> the way
/// the engine writes the reader's, which a skin may not name. The engine's side of that round trip
/// is pinned by <c>UsageEngineTests</c>. The reader's own file is never touched.</para>
/// </summary>
internal static class ViewMemoryCheck
{
    /// <summary>How long a panel is given to place itself and settle its face before it is read.</summary>
    private const double OpenSettleMs = 300;

    public static void Run(string path, Action shutdown)
    {
        var report = new StringBuilder();
        var failures = 0;
        var settings = Path.Combine(Path.GetTempPath(), $"o-view-view-check-{Environment.ProcessId}.json");
        var state = SampleStates.All.Single(s => s.Name == "typical-models");

        // Read from the file every time, so nothing but the file is carried from one panel to the next.
        PanelViewChoice Stored()
        {
            var saved = TraySettings.Load(settings);
            return new PanelViewChoice(saved.PanelMeasure, saved.PanelFace, saved.OffPlanBannerHidden);
        }

        PanelViewChoice Keep(PanelViewChoice view)
        {
            (TraySettings.Load(settings) with
            {
                PanelMeasure = view.Measure, PanelFace = view.Face, OffPlanBannerHidden = view.BannerHidden,
            }).Save(settings);
            return Stored();
        }

        PopupWindow? panel = null;

        void Open()
        {
            panel = new PopupWindow
            {
                PinForVerification = true,
                ThemeOverride = false,
                FixedNow = SampleStates.Now,
                FixedZone = SampleStates.Zone,
                ScopeReport = state.Scope,
                DataReport = state.Plan,
                CachedUsage = state.CachedUsage,
                BoostNotices = state.Boost,
                ViewChoice = Stored(),
                SetViewChoice = Keep,
            };
            panel.ShowNearTrayIcon(state.Snapshot, state.Stats, state.Account);
        }

        void Close()
        {
            panel!.Close();
            panel = null;
        }

        // What is on the panel's screen, against what was left.
        void Expect(string when, PanelMeasure measure, PanelFace face)
        {
            var shown = (panel!.MeasureOnScreen, panel.FlipReadout.ShowingModels ? PanelFace.Models : PanelFace.Totals);
            var stored = Stored();
            var holds = shown == (measure, face) && (stored.Measure, stored.Face) == (measure, face);
            failures += holds ? 0 : 1;
            report.AppendLine($"{(holds ? "ok  " : "FAIL")} {when,-46} " +
                              $"wanted {measure}/{face}, on screen {shown.Item1}/{shown.Item2}, stored {stored.Measure}/{stored.Face}");
        }

        void Press(bool measure, bool face)
        {
            if (measure)
            {
                panel!.ToggleMeasureForVerification();
            }

            if (face)
            {
                panel!.ClickFlipForVerification();
            }
        }

        var queue = new Queue<(double DelayMs, Action Do)>();
        void Step(Action step, double delayMs = OpenSettleMs) => queue.Enqueue((delayMs, step));

        Step(Open, 0);
        Step(() => Expect("a fresh install opens on the default", PanelMeasure.Output, PanelFace.Totals));

        // Each of the four views, reached by a press and closed at once, mid-fold: it must
        // come back as it was left, and again on a panel opened after that one is gone.
        (PanelMeasure Measure, PanelFace Face, bool PressMeasure, bool PressFace)[] views =
        [
            (PanelMeasure.Overall, PanelFace.Totals, true, false),
            (PanelMeasure.Overall, PanelFace.Models, false, true),
            (PanelMeasure.Output, PanelFace.Models, true, false),
            (PanelMeasure.Output, PanelFace.Totals, false, true),
        ];

        foreach (var (measure, face, pressMeasure, pressFace) in views)
        {
            Step(() =>
            {
                Press(pressMeasure, pressFace);
                Close();
            });
            Step(Open);
            Step(() => Expect($"reopened on {measure}/{face}", measure, face));
            Step(Close, 0);
            Step(Open, 0);
            Step(() => Expect($"and again, on {measure}/{face}", measure, face));
        }

        MotionChecks.Drive(queue, error =>
        {
            if (error is not null)
            {
                failures++;
                report.AppendLine($"threw: {error}");
            }

            panel?.Close();
            File.Delete(settings);
            report.AppendLine();
            report.AppendLine($"RESULT: {(failures == 0 ? "PASS" : "FAIL")}");
            File.WriteAllText(path, report.ToString());
            shutdown();
        });
    }
}
