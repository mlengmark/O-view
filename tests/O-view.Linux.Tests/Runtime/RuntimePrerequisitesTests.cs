using System.Runtime.InteropServices;

namespace OView.Linux.Tests;

/// <summary>
/// The linux-runtime job's install step (.github/workflows/ci.yml) adds the shared libraries
/// Avalonia's X11 backend and SkiaSharp need to start under Xvfb, plus one screenshot tool to
/// photograph that display. This test loads each library exactly as the toolkit itself would
/// and checks the tool is on PATH, naming whichever is missing — so a gap shows up here
/// rather than as the app's own startup failure further down the job.
///
/// <para>Elsewhere there is nothing to check, so it returns early, same as
/// <see cref="LinuxRuntimeEnvironmentTests"/>.</para>
/// </summary>
[Trait("Category", "LinuxRuntime")]
public class RuntimePrerequisitesTests
{
    private static bool InRuntimeJob => Environment.GetEnvironmentVariable("OVIEW_LINUX_RUNTIME") == "1";

    // Avalonia's X11 backend (Program.BuildAvaloniaApp picks it via UsePlatformDetect, since
    // DISPLAY is set under Xvfb) and SkiaSharp's font loading. The same four packages
    // packaging/linux/build.sh lists as the .deb's runtime Depends, for the same reason given
    // there: .NET itself needs none of them.
    public static readonly TheoryData<string, string> RequiredLibraries = new()
    {
        { "libX11.so.6", "libx11-6" },
        { "libICE.so.6", "libice6" },
        { "libSM.so.6", "libsm6" },
        { "libfontconfig.so.1", "libfontconfig1" },
    };

    [Theory]
    [MemberData(nameof(RequiredLibraries))]
    public void ARequiredLibraryIsLoadable(string library, string package)
    {
        if (!InRuntimeJob) return;

        var loaded = NativeLibrary.TryLoad(library, out var handle);
        if (loaded) NativeLibrary.Free(handle);

        Assert.True(loaded,
            $"{library} did not load (apt package {package}) — add it to the linux-runtime job's install step");
    }

    [Fact]
    public void TheScreenshotToolIsOnPath()
    {
        if (!InRuntimeJob) return;

        Assert.True(IsOnPath("scrot"),
            "scrot is not on PATH — the linux-runtime job's install step must add it so the virtual display can be photographed");
    }

    private static bool IsOnPath(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Where(dir => dir.Length > 0)
            .Any(dir => File.Exists(Path.Combine(dir, tool)));
}
