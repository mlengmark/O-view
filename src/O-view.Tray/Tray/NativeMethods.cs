using System.Runtime.InteropServices;

namespace OView.Tray.Tray;

// Classic DllImport rather than LibraryImport: the source-generated marshaller
// demands AllowUnsafeBlocks project-wide, which is not worth it for three
// blittable-signature calls.
internal static class NativeMethods
{
    /// <summary>
    /// Bitmap.GetHicon() allocates an unmanaged GDI handle that Icon does NOT own.
    /// Every icon refresh must pair with a DestroyIcon or the process leaks one
    /// handle per update — a slow leak in an app designed to run for days
    /// (CLAUDE.md rule 5; ADR-0005 consequences).
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(nint hIcon);

    internal const uint GR_GDIOBJECTS = 0;

    /// <summary>GDI object count for the leak self-check (build-plan Phase 3 acceptance).</summary>
    [DllImport("user32.dll")]
    internal static extern uint GetGuiResources(nint hProcess, uint uiFlags);

    /// <summary>Small-icon metric; reflects the active DPI under PerMonitorV2.</summary>
    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);

    internal const int SM_CXSMICON = 49;

    /// <summary>
    /// A WPF ContextMenu opened from a tray icon will not dismiss on an outside
    /// click unless its popup window is the foreground window: a tray-resident app
    /// has no activated window of its own, so the StaysOpen=false menu never
    /// receives the deactivation that closes it (issue #11). Foreground the popup's
    /// own HWND immediately after opening so an off-menu click deactivates it.
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hWnd);
}
