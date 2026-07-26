namespace OView.Tray.Tray;

/// <summary>
/// Takes the foreground for a window, falling back to AttachThreadInput when the plain
/// request is refused.
///
/// This is not belt-and-braces. Windows grants SetForegroundWindow only to a process that
/// already holds the foreground or received the last input event, and a tray-resident app
/// frequently holds neither. Losing that race is not cosmetic: a flyout is shown but never
/// activated, so it never fires Deactivated, so it stays on screen with no way to dismiss
/// it. That was reproduced on a real desktop for the tray menu (issue #33), not theorised.
/// Sharing an input queue with the current foreground thread for the duration of the call
/// makes the grant succeed.
///
/// Lifted out of MenuWindow so the dialogs can use the same one path rather than growing a
/// second near-identical copy.
/// </summary>
internal static class ForegroundWindow
{
    public static void Take(nint hwnd)
    {
        if (hwnd == 0 || NativeMethods.SetForegroundWindow(hwnd) && NativeMethods.GetForegroundWindow() == hwnd)
        {
            return;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == 0)
        {
            return;
        }

        var foregroundThread = NativeMethods.GetWindowThreadProcessId(foreground, 0);
        var ownThread = NativeMethods.GetCurrentThreadId();
        if (foregroundThread == 0 || foregroundThread == ownThread)
        {
            return;
        }

        if (!NativeMethods.AttachThreadInput(ownThread, foregroundThread, true))
        {
            return;
        }

        try
        {
            NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            NativeMethods.AttachThreadInput(ownThread, foregroundThread, false);
        }
    }
}
