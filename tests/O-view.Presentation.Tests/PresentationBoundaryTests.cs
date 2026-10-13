using OView.Presentation.Samples;

namespace OView.Presentation.Tests;

/// <summary>
/// Presentation is shared by both heads, so it must never pull in a UI toolkit or a platform
/// API (docs/architecture.md). Checked on the compiled assembly's references, which is what
/// actually ships, rather than on the project file. Verified by hand: referencing SkiaSharp
/// from Presentation fails with "O-view.Presentation references: SkiaSharp".
/// </summary>
public class PresentationBoundaryTests
{
    private static readonly string[] Forbidden =
    [
        "PresentationFramework", "PresentationCore", "WindowsBase", "System.Windows",
        "System.Xaml", "Avalonia", "System.Drawing", "Microsoft.Win32", "Tmds.DBus",
        "SkiaSharp",
    ];

    [Fact]
    public void ReferencesNoUiToolkitOrPlatformApi()
    {
        var referenced = typeof(SampleStates).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? "")
            .ToList();

        var offending = referenced
            .Where(name => Forbidden.Any(f => name.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(offending.Count == 0, $"O-view.Presentation references: {string.Join(", ", offending)}");
    }
}
