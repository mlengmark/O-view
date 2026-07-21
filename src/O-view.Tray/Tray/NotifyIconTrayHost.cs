using System.Drawing;
using System.Windows.Forms;
using OView.Core.Models;

namespace OView.Tray.Tray;

/// <summary>
/// First-party NotifyIcon implementation (ADR-0005 — no third-party tray package).
/// NotifyIcon owns the hidden message window and re-registers itself after Explorer
/// restarts (TaskbarCreated), which ADR-0003 item 5 relies on and Phase 3 acceptance
/// verifies explicitly.
/// </summary>
public sealed class NotifyIconTrayHost : ITrayHost
{
    private readonly NotifyIcon _notifyIcon = new();
    private Icon? _currentIcon;
    private nint _currentHandle;

    public event EventHandler? IconClicked;

    public NotifyIconTrayHost()
    {
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                IconClicked?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    public void Update(Bitmap icon, string tooltip)
    {
        // Shell_NotifyIcon copies the icon into the shell on assignment, so the
        // PREVIOUS handle is destroyed after the swap — destroying the current one
        // early would blank the tray icon.
        var newHandle = icon.GetHicon();
        var newIcon = Icon.FromHandle(newHandle);

        _notifyIcon.Icon = newIcon;
        _notifyIcon.Text = Truncate(tooltip, TooltipFormatter.MaxLength);
        _notifyIcon.Visible = true;

        var oldIcon = _currentIcon;
        var oldHandle = _currentHandle;
        _currentIcon = newIcon;
        _currentHandle = newHandle;

        // Icon.FromHandle does not own the handle; Dispose alone leaks it (CLAUDE.md rule 5).
        oldIcon?.Dispose();
        if (oldHandle != 0)
        {
            NativeMethods.DestroyIcon(oldHandle);
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _currentIcon?.Dispose();
        if (_currentHandle != 0)
        {
            NativeMethods.DestroyIcon(_currentHandle);
            _currentHandle = 0;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
