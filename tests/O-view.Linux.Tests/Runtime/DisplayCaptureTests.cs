using System.Runtime.InteropServices;
using SkiaSharp;

namespace OView.Linux.Tests;

/// <summary>
/// Proves <see cref="DisplayCapture"/> both ways: a no-op, no throw, with
/// OVIEW_RUNTIME_ARTIFACTS unset — the state the ordinary test job runs in — and, under the
/// linux-runtime job's virtual display, a real PNG sized to that display, not an empty file.
/// </summary>
public class DisplayCaptureTests
{
    private static bool InRuntimeJob => Environment.GetEnvironmentVariable("OVIEW_LINUX_RUNTIME") == "1";

    [Fact]
    public void WithNoArtifactsDirectoryItWritesNothingAndThrowsNothing()
    {
        var original = Environment.GetEnvironmentVariable("OVIEW_RUNTIME_ARTIFACTS");
        try
        {
            Environment.SetEnvironmentVariable("OVIEW_RUNTIME_ARTIFACTS", null);
            DisplayCapture.CaptureTo("should-not-exist");
            // Reaching here without an exception is the assertion; with no artifacts
            // directory there is no path to check for an absent file either.
        }
        finally
        {
            Environment.SetEnvironmentVariable("OVIEW_RUNTIME_ARTIFACTS", original);
        }
    }

    [Trait("Category", "LinuxRuntime")]
    [Fact]
    public void CapturesAPngSizedToTheDisplay()
    {
        if (!InRuntimeJob) return;

        var artifactsDir = Environment.GetEnvironmentVariable("OVIEW_RUNTIME_ARTIFACTS");
        Assert.False(string.IsNullOrEmpty(artifactsDir),
            "OVIEW_RUNTIME_ARTIFACTS is not set: the job did not configure where to save screenshots");

        DisplayCapture.CaptureTo(nameof(CapturesAPngSizedToTheDisplay));

        var path = Path.Combine(artifactsDir!, $"{nameof(CapturesAPngSizedToTheDisplay)}.png");
        Assert.True(File.Exists(path), $"{path} was not written");
        Assert.True(new FileInfo(path).Length > 0, $"{path} is empty");

        using var bitmap = SKBitmap.Decode(path);
        Assert.NotNull(bitmap);

        var (width, height) = QueryDisplaySize();
        Assert.Equal(width, bitmap!.Width);
        Assert.Equal(height, bitmap.Height);
    }

    // libX11.so.6 is already a confirmed-loadable prerequisite (RuntimePrerequisitesTests);
    // asking it directly for the screen size is the ground truth to check scrot's PNG
    // against, rather than hard-coding Xvfb's default resolution.
    private static (int Width, int Height) QueryDisplaySize()
    {
        var display = XOpenDisplay(null);
        if (display == IntPtr.Zero)
        {
            throw new InvalidOperationException("XOpenDisplay failed: no DISPLAY to query");
        }

        try
        {
            var screen = XDefaultScreen(display);
            return (XDisplayWidth(display, screen), XDisplayHeight(display, screen));
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(string? displayName);

    [DllImport("libX11.so.6")]
    private static extern int XDefaultScreen(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XDisplayWidth(IntPtr display, int screenNumber);

    [DllImport("libX11.so.6")]
    private static extern int XDisplayHeight(IntPtr display, int screenNumber);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);
}
