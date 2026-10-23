using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using OView.App.Rendering;
using OView.Presentation.Samples;

namespace OView.Linux.Panel;

/// <summary>
/// Renders the panel offscreen to PNGs, for every shared sample state
/// (<see cref="SampleStates"/>) in both themes: the Linux golden images
/// (<c>tools/check.ps1</c>), and the counterpart of the WPF head's <c>--popup-samples</c>.
///
/// <para>It exists because the panel is otherwise only inspectable on a desktop none of its
/// authors has. Rendering to a file means legibility and layout can be judged from images
/// before anyone installs anything, and it reaches states real data on any one machine may
/// never produce. The Windows head renders the same states under the same names, so where
/// the two panels differ is visible side by side.</para>
///
/// <para>Every state is rendered at the height it asks for, which is the panel a display with
/// room shows. One state is rendered a second time at a short display's work area, because
/// that panel — compacted, capped and scrolling — is a layout no natural-height render
/// contains, and it is the one that was reported broken.</para>
/// </summary>
public static class PanelSampleRenderer
{
    private const double ShortWorkAreaDip = SampleStates.ShortWorkAreaDip;

    /// <summary>The state rendered a second time for the short display: <see cref="SampleStates.ShortState"/>.</summary>
    public const string ShortState = SampleStates.ShortState;

    public static void RenderAll(string directory)
    {
        Directory.CreateDirectory(directory);

        var written = 0;
        foreach (var light in new[] { false, true })
        {
            foreach (var state in SampleStates.All)
            {
                // A FRESH control per sample. Reusing one silently produced eleven copies
                // of the first image: layout is cached against the tree, so re-populating
                // and re-rendering returns the size and content already arranged.
                Save(Build(state, light, PanelDensity.Normal),
                    Path.Combine(directory, $"panel-{state.Name}-{Theme(light)}.png"));
                written++;
            }

            var shortState = SampleStates.All.First(s => s.Name == ShortState);
            var panel = Build(shortState, light, PanelDensity.Compact);

            // Exactly what the window does on that desktop: compact first, then capped at the
            // work area, with the content scrolling inside the cap. The height is pinned here
            // rather than granted by a window, because there is no window offscreen.
            var fit = PanelFit.Resolve(panel.NaturalHeightDip(), ShortWorkAreaDip);
            panel.ScrollContent(fit.MustScroll);
            panel.Height = fit.WindowHeightDip;

            Save(panel, Path.Combine(directory, $"panel-{shortState.Name}-short-{Theme(light)}.png"),
                CappedLayoutPasses);
            written++;
        }

        Console.WriteLine($"wrote {written} panel PNGs to {Path.GetFullPath(directory)}");
    }

    private static string Theme(bool light) => light ? "light" : "dark";

    private static PanelContent Build(SampleState state, bool light, PanelDensity density)
    {
        var content = new PanelContent(new LinuxPanelTheme(light)) { Density = density };
        content.Populate(state.Snapshot, state.Stats, state.Account, state.Plan, state.Scope,
            SampleStates.Now, state.Boost, SampleStates.Zone, state.CachedUsage);
        return content;
    }

    /// <summary>
    /// How many measure/arrange pairs a capped panel needs before its layout settles. The
    /// first discovers that the content overflows and brings the scroll bar in; the second
    /// lays the content out in the narrower column that leaves; the third re-cuts the token
    /// bars, which size their own segments against the track they were handed.
    ///
    /// <para>Run by hand because nothing offscreen has a layout manager to run them — the
    /// tree never reaches a window, so an invalidation inside it is never picked up. Counted
    /// rather than looped-until-valid for the same reason: the root stays valid while a
    /// descendant is not.</para>
    /// </summary>
    private const int CappedLayoutPasses = 3;

    private static void Save(Control content, string path, int passes = 1)
    {
        // The tree must be measured before its desired size is known, and arranged at that
        // size before it will render anything but blank. A panel with room is done in one
        // pass, exactly as before.
        for (var pass = 0; pass < passes; pass++)
        {
            content.InvalidateMeasure();
            content.Measure(Size.Infinity);
            content.InvalidateArrange();
            content.Arrange(new Rect(content.DesiredSize));
        }

        var pixel = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(content.DesiredSize.Width)),
            Math.Max(1, (int)Math.Ceiling(content.DesiredSize.Height)));

        using var bitmap = new RenderTargetBitmap(pixel, new Vector(96, 96));
        bitmap.Render(content);
        bitmap.Save(path, new PngBitmapEncoderOptions());
    }
}
