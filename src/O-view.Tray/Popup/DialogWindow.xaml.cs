using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using OView.Tray.Tray;
using Size = System.Windows.Size;

namespace OView.Tray.Popup;

/// <summary>
/// The app's own dialog, replacing <c>MessageBox</c>.
///
/// A system message box is the one surface that gave the whole app away: raw Win32 chrome,
/// a stock blue "i" glyph and Yes/No buttons, sitting in front of a product that is
/// otherwise entirely rounded cards on one palette. It also has no room for a brand mark,
/// so nothing on it says which application is asking.
///
/// This carries the same mark, palette and type as the tray menu and the detail panel, and
/// gives the two answers different weight — one accent-filled primary, one outlined
/// secondary — instead of two identical buttons that make the user read both to tell them
/// apart.
/// </summary>
public partial class DialogWindow : Window
{
    private bool _confirmed;

    /// <summary>Forces a theme for verification renders; null follows the OS.</summary>
    public bool? ThemeOverride { get; set; }

    public DialogWindow()
    {
        InitializeComponent();

        ConfirmButton.Click += (_, _) => Close(confirmed: true);
        CancelButton.Click += (_, _) => Close(confirmed: false);

        // Esc cancels, as every dialog does. Enter is handled by IsDefault on the
        // primary button.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(confirmed: false);
            }
        };
    }

    private void Close(bool confirmed)
    {
        _confirmed = confirmed;
        DialogResult = confirmed;
    }

    /// <summary>
    /// Fills and shows the dialog modally, returning true if the primary action was taken.
    /// </summary>
    /// <param name="detail">Optional smaller line beneath the message; omitted when empty.</param>
    public static bool Confirm(
        string title, string message, string confirmLabel, string cancelLabel,
        string detail = "", bool? themeOverride = null)
    {
        var dialog = new DialogWindow { ThemeOverride = themeOverride };
        dialog.Populate(title, message, confirmLabel, cancelLabel, detail);

        // A tray app owns no activated window, so the same foreground problem the flyouts
        // have applies here — except a dialog that opens behind another window is worse:
        // it is modal, so the user is left with an app that appears to have frozen.
        //
        // This MUST run after the window is on screen. It used to be attached to
        // SourceInitialized, which fires while the HWND exists but is still WS_VISIBLE-less
        // — and SetForegroundWindow silently fails on an invisible window, so the whole
        // AttachThreadInput fallback was being spent on a no-op. The failure only shows on a
        // long-running instance, because a process that was just launched still holds
        // foreground rights and gets activated anyway; that is exactly why it survived
        // testing. ContentRendered is the first point the window is genuinely visible.
        dialog.ContentRendered += (_, _) =>
        {
            ForegroundWindow.Take(new WindowInteropHelper(dialog).Handle);
            dialog.Activate();
        };

        dialog.ShowDialog();
        return dialog._confirmed;
    }

    /// <summary>Fills the dialog without showing it (also the verification-render path).</summary>
    public void Populate(
        string title, string message, string confirmLabel, string cancelLabel, string detail = "")
    {
        PanelTheme.Apply(Resources, ThemeOverride ?? PanelTheme.IsAppsLight());

        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmLabel.Text = confirmLabel;
        CancelLabel.Text = cancelLabel;

        DetailText.Text = detail;
        DetailText.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Renders the dialog without showing it — the same verification path the tray menu
    /// uses, since a modal cannot be screenshotted without blocking the run that would
    /// take the screenshot.
    /// </summary>
    internal System.Windows.Media.Imaging.BitmapSource RenderToBitmap(double scale) =>
        VisualRenderer.RenderContent(this, scale);
}
