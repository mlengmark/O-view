using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
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
/// </summary>
public static class PanelSampleRenderer
{
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
                var content = new PanelContent(new LinuxPanelTheme(light));
                content.Populate(state.Snapshot, state.Stats, state.Account, state.Plan, state.Scope,
                    SampleStates.Now, state.Boost, SampleStates.Zone, state.CachedUsage);

                Save(content, Path.Combine(directory, $"panel-{state.Name}-{(light ? "light" : "dark")}.png"));
                written++;
            }
        }

        Console.WriteLine($"wrote {written} panel PNGs to {Path.GetFullPath(directory)}");
    }

    private static void Save(Control content, string path)
    {
        // Two passes: the tree must be measured before its desired size is known, and
        // arranged at that size before it will render anything but blank.
        content.Measure(Size.Infinity);
        content.Arrange(new Rect(content.DesiredSize));

        var pixel = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(content.DesiredSize.Width)),
            Math.Max(1, (int)Math.Ceiling(content.DesiredSize.Height)));

        using var bitmap = new RenderTargetBitmap(pixel, new Vector(96, 96));
        bitmap.Render(content);
        bitmap.Save(path, new PngBitmapEncoderOptions());
    }
}
