using System.Drawing;
using System.Windows.Forms;
using OView.App;
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
    public event EventHandler? IconRightClicked;

    public NotifyIconTrayHost()
    {
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                IconClicked?.Invoke(this, EventArgs.Empty);
            }
            else if (e.Button == MouseButtons.Right)
            {
                IconRightClicked?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    public void ShowNotification(string title, string message,
        NotificationKind kind = NotificationKind.Information) =>
        _notifyIcon.ShowBalloonTip(10_000, title, message, GlyphFor(kind));

    /// <summary>
    /// The severity the app decided, in the four values <c>Shell_NotifyIcon</c> offers.
    ///
    /// <para>There is no "upgrading" glyph to reach for — <see cref="ToolTipIcon"/> is
    /// <c>None</c>, <c>Info</c>, <c>Warning</c> and <c>Error</c>, and ADR-0005 rules out
    /// adding a toast package to get a richer one. <c>Info</c> is the friendly one, and the
    /// balloon already carries O-view's own icon in its header, so an update notification now
    /// reads as the app telling the user something rather than as an alert.</para>
    ///
    /// <para><c>None</c> is unused deliberately: it renders no glyph at all, which reads as a
    /// rendering failure rather than as a deliberately quiet notification.</para>
    /// </summary>
    private static ToolTipIcon GlyphFor(NotificationKind kind) => kind switch
    {
        NotificationKind.Warning => ToolTipIcon.Warning,
        NotificationKind.Error => ToolTipIcon.Error,
        _ => ToolTipIcon.Info,
    };

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

    /// <summary>
    /// Backstop, not the primary cap. <see cref="TooltipFormatter.Format"/> already
    /// truncates to <see cref="TooltipFormatter.MaxLength"/>, and today it is the only
    /// producer of this string — so this never fires in practice.
    ///
    /// <para>It stays because the 127-character limit is <c>NotifyIcon.Text</c>'s, i.e.
    /// this class's platform constraint rather than Core's formatting choice, and
    /// <see cref="ITrayHost.Update"/> accepts any string from any caller. Deliberate
    /// redundancy at the boundary that owns the constraint — not an oversight.</para>
    /// </summary>
    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
