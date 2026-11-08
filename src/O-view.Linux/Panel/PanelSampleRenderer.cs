using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using OView.App.Rendering;
using OView.Core.Models;
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
/// room shows. Two more renders are views of a state no natural-height render contains: the
/// compact density, and the tallest state at a short display's work area, compacted, capped
/// and scrolling, which is the layout that was reported broken.</para>
/// </summary>
public static class PanelSampleRenderer
{
    private const double ShortWorkAreaDip = SampleStates.ShortWorkAreaDip;

    /// <summary>
    /// The state rendered a second time for the short display: the tallest that shows the model
    /// face, as the Windows panel's second short render is, because a state that fits shows
    /// nothing about capping it. It is not <see cref="SampleStates.ShortState"/>, which is what
    /// the Windows head photographs in the totals face.
    /// </summary>
    public const string ShortState = "offplan-on-models";

    /// <summary>The short display's state, with the view it carries: the model face, counting everything billed.</summary>
    public static SampleState ShortSample => SampleStates.All.First(s => s.Name == ShortState);

    /// <summary>The state rendered at the compact density, the one the Windows panel uses for it.</summary>
    public const string CompactState = "typical-overall";

    public static SampleState CompactSample => SampleStates.All.First(s => s.Name == CompactState);

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

            // The density a small display gets while the panel still fits: tighter gaps are a
            // layout no normal-density render contains.
            Save(Build(CompactSample, light, PanelDensity.Compact),
                Path.Combine(directory, $"panel-typical-compact-{Theme(light)}.png"));
            written++;

            Save(ShortPanel(light), Path.Combine(directory, $"panel-{ShortState}-short-{Theme(light)}.png"),
                CappedLayoutPasses);
            written++;
        }

        Console.WriteLine($"wrote {written} panel PNGs to {Path.GetFullPath(directory)}");
    }

    private static string Theme(bool light) => light ? "light" : "dark";

    /// <summary>
    /// The short display's panel, not yet laid out: exactly what the window does on that
    /// desktop, compact first, then capped at the work area, with the content scrolling inside
    /// the cap. The height is pinned here rather than granted by a window, because there is no
    /// window offscreen. Public so a test can check the tree this renders.
    /// </summary>
    public static PanelContent ShortPanel(bool light)
    {
        var panel = Build(ShortSample, light, PanelDensity.Compact);

        var fit = PanelFit.Resolve(panel.NaturalHeightDip(), ShortWorkAreaDip);
        panel.ScrollContent(fit.MustScroll);
        panel.Height = fit.WindowHeightDip;
        return panel;
    }

    /// <summary>A sample state as the panel draws it, populated and not yet laid out.</summary>
    public static PanelContent Build(SampleState state, bool light, PanelDensity density)
    {
        var content = new PanelContent(new LinuxPanelTheme(light)) { Density = density };
        content.Populate(state.Snapshot, state.Stats, state.Account, state.Plan, state.Scope,
            SampleStates.Now, state.Boost, SampleStates.Zone, state.CachedUsage, state.View);
        return content;
    }

    /// <summary>
    /// How many measure/arrange pairs a capped panel needs from its root. The first discovers
    /// that the content overflows and brings the scroll bar in; the second lays the content
    /// out in the narrower column that leaves. <see cref="Settle"/> does the rest.
    ///
    /// <para>Run by hand because nothing offscreen has a layout manager to run them, the
    /// tree never reaches a window, so an invalidation inside it is never picked up.</para>
    /// </summary>
    public const int CappedLayoutPasses = 2;

    /// <summary>
    /// The most times a control can be gone back to, the same limit a window's layout manager
    /// stops at, so a control that invalidates itself on every arrange cannot hang the render.
    /// </summary>
    private const int MaxSettlePasses = 10;

    /// <summary>
    /// Lays the tree out as far as a window would before it paints.
    ///
    /// <para>The tree must be measured before its desired size is known, and arranged at that
    /// size before it will render anything but blank. <paramref name="passes"/> repeats that
    /// from the root; <see cref="Settle"/> then does the part only a layout manager does.</para>
    ///
    /// <para>Public so a test can read the bounds this leaves, which is what a render draws.</para>
    /// </summary>
    public static void LayOut(Control content, int passes = 1)
    {
        for (var pass = 0; pass < passes; pass++)
        {
            content.InvalidateMeasure();
            content.Measure(Size.Infinity);
            content.InvalidateArrange();
            content.Arrange(new Rect(content.DesiredSize));
        }

        Settle(content);
    }

    /// <summary>
    /// Goes back to every control that invalidated itself during the arrange, at the
    /// constraint and bounds it last had, until none is left.
    ///
    /// <para><b>Why the root's own passes cannot do this.</b> A window's layout manager queues
    /// the control that invalidated itself and returns to that one control. Repeating the
    /// layout from the root stops at the first valid ancestor, so a descendant's invalidation
    /// is never reached. The token bars are the case that showed it: they size their columns
    /// when their track gets its width, during the arrange, so every sample image drew all four
    /// segments one pixel wide at the left of the track while the window drew them in full.</para>
    ///
    /// <para>A control with no previous constraint has never been laid out, because it sits
    /// under something hidden, and the layout manager skips those too.</para>
    /// </summary>
    private static void Settle(Control content)
    {
        for (var pass = 0; pass < MaxSettlePasses; pass++)
        {
            var settled = false;
            foreach (var control in content.GetVisualDescendants().OfType<Layoutable>())
            {
                if (!control.IsMeasureValid
                    && LayoutInformation.GetPreviousMeasureConstraint(control) is { } constraint)
                {
                    control.Measure(constraint);
                    settled = true;
                }

                if (!control.IsArrangeValid
                    && LayoutInformation.GetPreviousArrangeBounds(control) is { } bounds)
                {
                    control.Arrange(bounds);
                    settled = true;
                }
            }

            if (!settled)
            {
                return;
            }
        }
    }

    private static void Save(Control content, string path, int passes = 1)
    {
        LayOut(content, passes);

        var pixel = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(content.DesiredSize.Width)),
            Math.Max(1, (int)Math.Ceiling(content.DesiredSize.Height)));

        using var bitmap = new RenderTargetBitmap(pixel, new Vector(96, 96));
        bitmap.Render(content);
        bitmap.Save(path, new PngBitmapEncoderOptions());
    }
}
