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
public partial class MenuWindow : Window, IFlyout
{
    /// <summary>Forces a theme for verification renders; null follows the OS.</summary>
    public bool? ThemeOverride { get; set; }

    /// <summary>
    /// Docking, the open/close transitions and the toggle grace window — shared with the
    /// detail panel rather than reimplemented here (issue #54).
    /// </summary>
    private readonly DockedFlyout _flyout;

    /// <summary>Disables auto-hide for verification screenshots.</summary>
    public bool PinForVerification
    {
        get => _flyout.PinForVerification;
        set => _flyout.PinForVerification = value;
    }

    /// <inheritdoc cref="DockedFlyout.ClosedByClickAway"/>
    public bool ClosedByClickAway => _flyout.ClosedByClickAway;

    /// <summary>Closes the flyout from outside — a second right-click completing the toggle.</summary>
    public void DismissNow() => _flyout.BeginClose();

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

        _flyout = new DockedFlyout(this);
        Deactivated += (_, _) => _flyout.OnDeactivated();

        // Rows are Buttons, so Tab, Space and Enter work already; Up/Down is added back
        // because the ContextMenu this replaces had it and it is how a menu is expected
        // to behave from the keyboard.
        PreviewKeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape:
                    _flyout.BeginClose();
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

        // takeForeground: a tray-resident app owns no activated window, so Activate()
        // alone is not guaranteed the foreground — and without it the flyout never
        // receives the deactivation that dismisses it on an outside click (issue #11,
        // which the old ContextMenu hit for the same reason).
        _flyout.Show(takeForeground: true);
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

    private void Dismiss(EventHandler? handler) =>
        _flyout.BeginClose(() => handler?.Invoke(this, EventArgs.Empty));

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

    // ── verification hooks (--menu-check) ──────────────────────────────────────

    /// <summary>The rows, so a verification run can name them without touching XAML fields.</summary>
    internal enum MenuRow { Startup, Notify, Diagnostics, Updates, Exit }

    private System.Windows.Controls.Button RowButton(MenuRow row) => row switch
    {
        MenuRow.Startup => StartupRow,
        MenuRow.Notify => NotifyRow,
        MenuRow.Diagnostics => DiagnosticsRow,
        MenuRow.Updates => UpdatesRow,
        _ => ExitRow,
    };

    /// <summary>
    /// Activates a row through its automation peer — the same <c>Click</c> the mouse and the
    /// keyboard both raise, so a verification run exercises the real handler rather than a
    /// parallel path that could pass while the real one is broken.
    /// </summary>
    internal void InvokeRow(MenuRow row)
    {
        var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(RowButton(row));
        ((System.Windows.Automation.Provider.IInvokeProvider)
            peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
    }

    /// <summary>Tick state of a toggle row, for asserting a toggle actually flipped.</summary>
    internal bool RowIsChecked(MenuRow row) =>
        row == MenuRow.Startup ? IsChecked(StartupCheck) : IsChecked(NotifyCheck);

    /// <summary>Whether a close transition is in flight — the state that made rows dead.</summary>
    internal bool IsClosing => _flyout.IsClosing;

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
    internal BitmapSource RenderToBitmap(double scale) => VisualRenderer.RenderContent(this, scale);
}
