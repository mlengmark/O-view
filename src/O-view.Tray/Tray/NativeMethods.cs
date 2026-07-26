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
    /// A menu opened from a tray icon will not dismiss on an outside click unless its
    /// window is the foreground window: a tray-resident app owns no activated window,
    /// so it never receives the deactivation that closes the menu (issue #11).
    /// Foreground its HWND immediately after opening so an off-menu click dismisses it.
    /// Applied by MenuWindow after Show(); Activate() alone is not guaranteed to take
    /// foreground for a process that does not already hold it.
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hWnd);

    /// <summary>
    /// SetForegroundWindow is not guaranteed: Windows only grants the foreground to a
    /// process that already holds it or received the last input event, and refuses
    /// otherwise. A menu that loses that race stays on screen with no way to dismiss it,
    /// which is <em>worse</em> than the clipping issue #33 set out to fix — verified by
    /// reproducing it. AttachThreadInput to the current foreground thread makes the two
    /// threads share an input queue, at which point the grant succeeds; detach again
    /// immediately. The documented workaround for exactly this case.
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint hWnd, nint processId);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachThreadInput(uint attachTo, uint attachFrom, [MarshalAs(UnmanagedType.Bool)] bool attach);
}
