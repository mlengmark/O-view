using Avalonia.Logging;

namespace OView.Linux.Tests;

/// <summary>
/// What the toolkit's log looks like once it is in O-view's log file. Every start on a machine
/// with no usable GPU wrote the software-rendering fallback as an Error (issue #358), which is
/// the expected outcome there and buried whatever had actually failed beneath it.
/// </summary>
public class AvaloniaLogTests
{
    /// <summary>
    /// Avalonia's own template and area, as its X11 backend writes them, with the reason as a
    /// Hyper-V VM's log quoted it on 2026-10-15. The template is what is matched, so it is copied
    /// exactly from the toolkit; the reason only fills the hole.
    /// </summary>
    private const string GlxTemplate = "Unable to initialize GLX-based rendering: {0}";
    private const string GlxReason = "Renderer 'llvmpipe' is blacklisted";

    [Fact]
    public void TheSoftwareRenderingFallbackIsWrittenOnceAndBelowError()
    {
        var log = new ListLog();
        var sink = new AvaloniaLog(log);

        sink.Log(LogEventLevel.Error, "OpenGL", null, GlxTemplate, GlxReason);

        var line = Assert.Single(log.Lines);
        Assert.Equal($"avalonia Information [OpenGL] -: Unable to initialize GLX-based rendering: {GlxReason}", line);
    }

    [Fact]
    public void EveryOtherErrorIsStillWrittenAsError()
    {
        var log = new ListLog();
        var sink = new AvaloniaLog(log);

        sink.Log(LogEventLevel.Error, "OpenGL", null, GlxTemplate, GlxReason);
        sink.Log(LogEventLevel.Error, "FreeDesktopPlatform", null, "Unable to register the tray icon: {0}", "no host");
        sink.Log(LogEventLevel.Error, "OpenGL", null, "Unable to initialize EGL-based rendering: {0}", "no display");

        string[] expected =
        [
            $"avalonia Information [OpenGL] -: Unable to initialize GLX-based rendering: {GlxReason}",
            "avalonia Error [FreeDesktopPlatform] -: Unable to register the tray icon: no host",
            "avalonia Error [OpenGL] -: Unable to initialize EGL-based rendering: no display",
        ];
        Assert.Equal(expected, log.Lines);
    }

    /// <summary>
    /// Only the level changes the label, so the same text at Warning, should the toolkit ever
    /// lower it itself, is left as the toolkit wrote it.
    /// </summary>
    [Fact]
    public void TheFallbackAtAnyOtherLevelKeepsItsOwnLabel()
    {
        var log = new ListLog();
        var sink = new AvaloniaLog(log);

        sink.Log(LogEventLevel.Warning, "OpenGL", null, GlxTemplate, GlxReason);

        Assert.StartsWith("avalonia Warning [OpenGL]", Assert.Single(log.Lines));
    }

    [Fact]
    public void TheFloorIsUnchanged()
    {
        var sink = new AvaloniaLog(new ListLog());

        Assert.True(sink.IsEnabled(LogEventLevel.Error, "OpenGL"));
        Assert.True(sink.IsEnabled(LogEventLevel.Warning, "OpenGL"));
        Assert.False(sink.IsEnabled(LogEventLevel.Information, "OpenGL"));
    }
}
