using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OView.Tray.Tray;
using Path = System.Windows.Shapes.Path;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace OView.Tray.Popup;

/// <summary>
/// The tray right-click menu (GitHub issue #33), rebuilt as a docked flyout window.
///
/// It replaces a WPF <c>ContextMenu</c> placed at <c>PlacementMode.MousePoint</c>. Opening
/// at the cursor put the menu wherever the pointer happened to be when the tray icon was
/// hit — which, since the tray icon sits *inside* the taskbar, is reliably the one place a
/// menu cannot fully fit. It clipped into the taskbar and off the screen edge, leaving
/// items unclickable, and grew worse with every item added.
///
/// So placement no longer depends on the cursor at all: the flyout docks to the same
/// work-area corner as the detail panel, via the same <see cref="PopupPositioner"/> — the
/// model Windows' own volume, network and calendar flyouts use. The work area excludes the
/// taskbar by definition, so clearing it is structural rather than a margin that happens to
/// be big enough. Height is measured after layout on every open, so adding rows keeps
/// working without touching the placement code.
///
/// Being a real window rather than a <c>ContextMenu</c> also buys the styling the issue
/// asked for — the panel's own palette (<see cref="PanelTheme"/>), rounded card, brand
/// mark — which a themed system context menu cannot give.
/// </summary>
public partial class MenuWindow : Window
{
    /// <summary>Forces a theme for verification renders; null follows the OS.</summary>
    public bool? ThemeOverride { get; set; }

    /// <summary>Disables auto-hide for verification screenshots.</summary>
    public bool PinForVerification { get; set; }

    /// <summary>Corner the flyout is docked to, so it grows from the tray.</summary>
    private Point _dockOrigin = new(1, 1);

    /// <summary>Guards the close transition against a re-open landing mid-fade.</summary>
    private bool _closing;

    /// <summary>
    /// Applies the requested "run at startup" state and returns what the state
    /// <em>actually</em> is afterwards. The row renders the return value, not the request:
    /// a registry write can fail, and a tick that lies about the machine would break rule 6
    /// as surely as a fabricated number would.
    /// </summary>
    public Func<bool, bool>? SetRunAtStartup { get; set; }

    /// <summary>As <see cref="SetRunAtStartup"/>, for the threshold-notification setting.</summary>
    public Func<bool, bool>? SetNotifyOnThreshold { get; set; }

    public event EventHandler? CopyDiagnosticsRequested;
    public event EventHandler? CheckForUpdatesRequested;
    public event EventHandler? ExitRequested;

    public MenuWindow()
    {
        InitializeComponent();

        Deactivated += (_, _) => { if (!PinForVerification) BeginClose(); };

        // Rows are Buttons, so Tab, Space and Enter work already; Up/Down is added back
        // because the ContextMenu this replaces had it and it is how a menu is expected
        // to behave from the keyboard.
        PreviewKeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape:
                    BeginClose();
                    break;
                case Key.Down:
                    MoveRowFocus(FocusNavigationDirection.Next);
                    e.Handled = true;
                    break;
                case Key.Up:
                    MoveRowFocus(FocusNavigationDirection.Previous);
                    e.Handled = true;
                    break;
            }
        };

        // Toggles leave the flyout open so the tick confirms the change; actions close it
        // first, so a balloon or a modal never appears behind a topmost window.
        StartupRow.Click += (_, _) => Toggle(SetRunAtStartup, StartupCheck);
        NotifyRow.Click += (_, _) => Toggle(SetNotifyOnThreshold, NotifyCheck);
        DiagnosticsRow.Click += (_, _) => Dismiss(CopyDiagnosticsRequested);
        UpdatesRow.Click += (_, _) => Dismiss(CheckForUpdatesRequested);
        ExitRow.Click += (_, _) => Dismiss(ExitRequested);
    }

    /// <summary>
    /// Fills the rows from the current state and docks the flyout at the tray corner.
    /// Callers pass state on every open rather than caching it, because both settings can
    /// change outside the app (another instance, a manual registry edit, Task Manager's
    /// startup page).
    /// </summary>
    public void ShowDocked(bool runAtStartup, bool notifyOnThreshold, int thresholdPercent, string version)
    {
        Populate(runAtStartup, notifyOnThreshold, thresholdPercent, version);

        // Cancels a close still in flight, so re-opening mid-fade brings it straight back.
        _closing = false;

        // SizeToContent height is unknown until measured: lay out off-screen, then place.
        // Opacity starts at 0 so the off-screen frame never flashes at the final spot.
        Opacity = 0;
        Left = -10_000;
        Top = -10_000;
        Show();
        UpdateLayout();
        (Left, Top, _dockOrigin) = PopupPositioner.Place(ActualWidth, ActualHeight);
        Activate();

        if (PinForVerification)
        {
            FlyoutAnimation.Reset(this);
        }
        else
        {
            FlyoutAnimation.Open(this, _dockOrigin);
        }

        // A tray-resident app owns no activated window, so Activate() alone is not
        // guaranteed the foreground — and without it the flyout never receives the
        // deactivation that dismisses it on an outside click (issue #11, which the old
        // ContextMenu hit for the same reason). Foreground the HWND explicitly.
        ForegroundWindow.Take(new WindowInteropHelper(this).Handle);
    }

    /// <summary>Fills the rows without showing the window (also the verification-render path).</summary>
    public void Populate(bool runAtStartup, bool notifyOnThreshold, int thresholdPercent, string version)
    {
        PanelTheme.Apply(Resources, ThemeOverride ?? PanelTheme.IsAppsLight());

        VersionText.Text = $"v{version}";
        NotifyLabel.Text = $"Notify at {thresholdPercent}% session usage";
        SetChecked(StartupCheck, runAtStartup);
        SetChecked(NotifyCheck, notifyOnThreshold);
    }

    private void Toggle(Func<bool, bool>? apply, Path check)
    {
        if (apply is null)
        {
            return;
        }

        SetChecked(check, apply(!IsChecked(check)));
    }

    /// <summary>
    /// Fades and shrinks back into the docked corner, then hides — and only then runs
    /// <paramref name="after"/>. Waiting the ~110ms costs nothing perceptible and keeps
    /// the original guarantee that an action's balloon or modal never appears behind a
    /// still-visible topmost flyout.
    /// </summary>
    private void BeginClose(Action? after = null)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        FlyoutAnimation.Close(this, _dockOrigin, () =>
        {
            // Re-checked: the flyout may have been re-opened while this was running.
            if (_closing)
            {
                _closing = false;
                Hide();
            }
            after?.Invoke();
        });
    }

    private void Dismiss(EventHandler? handler) =>
        BeginClose(() => handler?.Invoke(this, EventArgs.Empty));

    private void MoveRowFocus(FocusNavigationDirection direction)
    {
        // Nothing focused yet (the flyout opens with focus on the window itself), so the
        // first arrow press lands on the first row rather than being swallowed.
        if (Keyboard.FocusedElement is FrameworkElement focused && !ReferenceEquals(focused, this))
        {
            focused.MoveFocus(new TraversalRequest(direction));
        }
        else
        {
            StartupRow.Focus();
        }
    }

    private static bool IsChecked(Path check) => check.Visibility == Visibility.Visible;

    // Hidden rather than removed: the row keeps its 22px check gutter either way, so
    // labels stay on one left edge whichever settings are on.
    private static void SetChecked(Path check, bool value) =>
        check.Visibility = value ? Visibility.Visible : Visibility.Hidden;

    /// <summary>
    /// Renders the flyout to a bitmap without showing it — the menu's equivalent of the
    /// tray icon's <c>--samples</c> hook, so both themes can be eyeballed side by side
    /// without a desktop session or disturbing a running instance.
    /// </summary>
    internal BitmapSource RenderToBitmap(double scale)
    {
        var root = (FrameworkElement)Content;
        root.Measure(new Size(Width, double.PositiveInfinity));
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth * scale),
            (int)Math.Ceiling(root.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        return bitmap;
    }
}
