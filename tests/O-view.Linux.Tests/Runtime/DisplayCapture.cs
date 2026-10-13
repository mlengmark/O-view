using System.Diagnostics;

namespace OView.Linux.Tests;

/// <summary>
/// The one call a failing LinuxRuntime test reaches for to photograph the virtual display,
/// using scrot (installed by the linux-runtime job's install step, see
/// RuntimePrerequisitesTests). The photograph is saved under the folder
/// OVIEW_RUNTIME_ARTIFACTS names; .github/workflows/ci.yml sets that variable and uploads the
/// folder whether or not the run that invoked it passed, so a Linux failure can be looked at
/// rather than only read from a log.
///
/// <para>Elsewhere — the ordinary test job, or a developer's own machine — the variable is
/// unset, so this writes nothing and throws nothing rather than failing a run that was never
/// meant to photograph anything.</para>
/// </summary>
public static class DisplayCapture
{
    /// <summary>Captures the whole virtual display to "{name}.png" under OVIEW_RUNTIME_ARTIFACTS.</summary>
    public static void CaptureTo(string name)
    {
        var artifactsDir = Environment.GetEnvironmentVariable("OVIEW_RUNTIME_ARTIFACTS");
        if (string.IsNullOrEmpty(artifactsDir)) return;

        Directory.CreateDirectory(artifactsDir);
        var path = Path.Combine(artifactsDir, $"{name}.png");

        var info = new ProcessStartInfo("scrot")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // -o: overwrite, so a second capture under the same name does not fail the call.
        info.ArgumentList.Add("-o");
        info.ArgumentList.Add(path);

        using var scrot = Process.Start(info) ?? throw new InvalidOperationException("scrot did not start");
        var error = scrot.StandardError.ReadToEnd();
        scrot.WaitForExit();

        if (scrot.ExitCode != 0)
        {
            throw new InvalidOperationException($"scrot exited {scrot.ExitCode} capturing {path}: {error}");
        }
    }
}
