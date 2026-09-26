using System.Runtime.InteropServices;
using Visual = System.Windows.Media.Visual;

namespace OView.Tray.Tray;

/// <summary>
/// Holds WPF's native libraries open for the life of the process.
///
/// <para><b>Why this exists.</b> The release exe is single-file with
/// <c>IncludeNativeLibrariesForSelfExtract</c>, so the host unpacks WPF's native DLLs into
/// <c>%TEMP%\.net\O-view.Tray\&lt;hash&gt;\</c> at launch. A tray app draws no WPF window until
/// the first click, so nothing loads them — and a file nothing has loaded is exactly what
/// Storage Sense and third-party temp cleaners delete. O-view runs for days; the first click
/// after such a sweep then failed with <c>XamlParseException</c> "Add value to collection of
/// type 'System.Windows.Controls.UIElementCollection' threw an exception", whose inner
/// exception is <c>DllNotFoundException: wpfgfx_cor3.dll</c>. It surfaced as WinForms'
/// "Unhandled exception" box because the click arrives through NotifyIcon's window.</para>
///
/// <para>Measured, not assumed: deleting <c>wpfgfx_cor3.dll</c> reproduces the exact message,
/// and the host re-extracts missing files on the next launch — so the window is only ever
/// between launch and first click. Loading them here closes it: Windows will not delete a
/// file that is mapped as a loaded image.</para>
/// </summary>
internal static class WpfNativeLibraries
{
    /// <summary>
    /// The native libraries WPF ships, dependency first. Not every one is used on every
    /// machine (PenImc only with pen or touch), but each is loaded on demand from the same
    /// directory, so each is exposed to the same cleaner.
    /// </summary>
    internal static readonly string[] Names =
    [
        "vcruntime140_cor3.dll",
        "wpfgfx_cor3.dll",
        "PresentationNative_cor3.dll",
        "D3DCompiler_47_cor3.dll",
        "PenImc_cor3.dll",
    ];

    /// <summary>
    /// Loads each library through the same probing WPF's own P/Invokes use, and never frees
    /// the handle. Returns the names that could not be loaded, for the log — a failure here
    /// is not fatal, because a build that is not single-file finds them elsewhere anyway.
    /// </summary>
    public static IReadOnlyList<string> Pin()
    {
        var wpf = typeof(Visual).Assembly;
        var missing = new List<string>();
        foreach (var name in Names)
        {
            if (!NativeLibrary.TryLoad(name, wpf, searchPath: null, out _))
            {
                missing.Add(name);
            }
        }

        return missing;
    }

    /// <summary>
    /// True when a failure anywhere in the chain is a native library that could not be
    /// found — the signature of the files having been removed underneath a running process,
    /// whatever wrapper (XamlParseException, TypeInitializationException) WPF put round it.
    /// </summary>
    public static bool IsMissingNativeLibrary(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is DllNotFoundException)
            {
                return true;
            }
        }

        return false;
    }
}
