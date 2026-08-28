using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OView.Core.Models;
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

    /// <summary>As <see cref="SetRunAtStartup"/>, for the automatic-update setting (issue #140).</summary>
    public Func<bool, bool>? SetUpdateAutomatically { get; set; }

    /// <summary>
    /// As <see cref="SetRunAtStartup"/>, for the notification threshold itself. Returns the
    /// percentage as it actually stands after the write, so a settings file that could not be
    /// saved leaves the pill showing the real value rather than the requested one.
    /// </summary>
    public Func<int, int>? SetThresholdPercent { get; set; }

    /// <summary>
    /// The percentages the selector offers. Three, deliberately: enough to cover "warn me
    /// early" through "warn me only when it matters", few enough to stay one glance on a row
    /// rather than a scrolling list.
    /// </summary>
    internal static readonly int[] ThresholdChoices = [70, 80, 90];

    /// <summary>What the pill currently reads — the value the option marks are drawn from.</summary>
    private int _thresholdPercent = UsageLevels.CriticalPercent;

    /// <summary>
    /// The words after the pill, giving the row its full reading: "Notify at [70% ⌄] usage".
    ///
    /// <para>The shorter wording is a deliberate choice (@mlengmark on issue #141), taken over
    /// "session usage". The watcher reads <see cref="UsageSnapshot.SessionPercent"/> only, so
    /// the row is silent about which of the panel's two meters it watches; what closes that gap
    /// is the notification itself, which names the window explicitly — "Session usage is at N%
    /// of the 5-hour limit". <b>If that balloon's copy is ever shortened, this row becomes the
    /// only place the distinction could have been made, and it no longer makes it.</b>
    /// </para>
    /// </summary>
    internal const string NotifySuffixText = "usage";

    /// <summary>The user asked to set or clear the weekly reset time (issue #186).</summary>
    public event EventHandler? WeeklyResetRequested;

    /// <summary>Raised when the user asks to undo a self-imposed usage-refresh block (issue #234).</summary>
    public event EventHandler? ResumeUsageRefreshRequested;

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
        UpdateAutoRow.Click += (_, _) => Toggle(SetUpdateAutomatically, UpdateAutoCheck);
        NotifyRow.Click += (_, _) => Toggle(SetNotifyOnThreshold, NotifyCheck);

        // Marked handled, or the Click bubbles to NotifyRow above and every interaction with
        // the selector would silently switch the notification off as a side effect.
        ThresholdPill.Click += (_, e) =>
        {
            e.Handled = true;
            SetOptionsExpanded(ThresholdOptions.Visibility != Visibility.Visible);
        };

        foreach (var option in OptionRows)
        {
            option.Click += (sender, e) =>
            {
                e.Handled = true;
                ChooseThreshold(ChoiceOf((FrameworkElement)sender));
            };
        }

        WeeklyResetRow.Click += (_, _) => Dismiss(WeeklyResetRequested);
        ResumeRefreshRow.Click += (_, _) => Dismiss(ResumeUsageRefreshRequested);
        DiagnosticsRow.Click += (_, _) => Dismiss(CopyDiagnosticsRequested);
        UpdatesRow.Click += (_, _) => Dismiss(CheckForUpdatesRequested);
        ExitRow.Click += (_, _) => Dismiss(ExitRequested);
    }

    /// <summary>
    /// Everything the menu draws, gathered at the moment it is opened.
    ///
    /// <para>A record rather than a parameter list: with the automatic-update row (issue #140)
    /// the menu renders three independent booleans, and three adjacent <c>bool</c> arguments
    /// is the shape where a transposed pair compiles cleanly and silently draws the wrong
    /// ticks. Named members make the call sites say which is which.</para>
    /// </summary>
    /// <param name="CanUpdateAutomatically">
    /// Whether this build can self-install at all (<c>UpdatePolicy.MayDownloadAndRun</c>).
    /// False hides the row outright — see the XAML for why it is hidden and not disabled.
    /// </param>
    /// <param name="WeeklyReset">
    /// The entered weekly reset as the row should show it, or empty when O-view is deriving
    /// it (issue #186).
    /// </param>
    public readonly record struct MenuState(
        bool RunAtStartup,
        bool NotifyOnThreshold,
        int ThresholdPercent,
        bool UpdateAutomatically,
        bool CanUpdateAutomatically,
        string Version,
        string WeeklyReset = "",
        string UsageRefreshBlocked = "");

    /// <summary>
    /// Fills the rows from the current state and docks the flyout at the tray corner.
    /// Callers pass state on every open rather than caching it, because these settings can
    /// change outside the app (another instance, a manual registry edit, Task Manager's
    /// startup page).
    /// </summary>
    public void ShowDocked(MenuState state)
    {
        Populate(state);

        // The list always opens closed. Reopening the flyout with it still expanded would
        // show a taller card with no explanation of why, and the choice has already been made.
        SetOptionsExpanded(false, redock: false);

        // takeForeground: a tray-resident app owns no activated window, so Activate()
        // alone is not guaranteed the foreground — and without it the flyout never
        // receives the deactivation that dismisses it on an outside click (issue #11,
        // which the old ContextMenu hit for the same reason).
        _flyout.Show(takeForeground: true);
    }

    /// <summary>Fills the rows without showing the window (also the verification-render path).</summary>
    public void Populate(MenuState state)
    {
        PanelTheme.Apply(Resources, ThemeOverride ?? PanelTheme.IsAppsLight());

        VersionText.Text = $"v{state.Version}";
        // States the answer in effect, not just the affordance: "derived" tells a user who
        // has never opened this dialog what O-view is currently doing.
        WeeklyResetValue.Text = state.WeeklyReset.Length > 0 ? state.WeeklyReset : "derived";
        NotifySuffix.Text = NotifySuffixText;
        SetChecked(StartupCheck, state.RunAtStartup);
        SetChecked(NotifyCheck, state.NotifyOnThreshold);
        ShowThreshold(state.ThresholdPercent);

        // Collapsed, not hidden: Hidden would keep the row's 34px of height and leave a gap
        // under "Run at startup" on every build that cannot self-install, which is most Linux
        // installs and every portable exe.
        UpdateAutoRow.Visibility = state.CanUpdateAutomatically ? Visibility.Visible : Visibility.Collapsed;

        // Only present while there is something to undo, and Collapsed for the same reason as
        // above — this is the ordinary case, so a permanent empty row would be 34px of nothing
        // for every user who never trips the guard.
        var blocked = state.UsageRefreshBlocked.Length > 0;
        ResumeRefreshRow.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
        if (blocked)
        {
            ResumeRefreshLabel.Text = PanelText.UsageRefreshBlockedRow;
            ResumeRefreshRow.ToolTip = PanelText.UsageRefreshBlockedHint(state.UsageRefreshBlocked);
        }
        SetChecked(UpdateAutoCheck, state.UpdateAutomatically && state.CanUpdateAutomatically);
    }

    /// <summary>
    /// Draws a threshold onto the pill and the option marks.
    ///
    /// <para>The value is <b>rendered as it is</b>, not snapped to one of
    /// <see cref="ThresholdChoices"/>. <c>TraySettings</c> accepts any 1–100 value and a
    /// hand-edited settings.json is a legitimate way to hold one; showing 75 as "80%" would
    /// be the menu asserting something about the machine that is not true.</para>
    /// </summary>
    private void ShowThreshold(int percent)
    {
        _thresholdPercent = percent;
        ThresholdText.Text = $"{percent}%";

        foreach (var (row, mark) in OptionRows.Zip(OptionMarks))
        {
            mark.Visibility = ChoiceOf(row) == percent ? Visibility.Visible : Visibility.Hidden;
        }
    }

    private System.Windows.Controls.Button[] OptionRows => [Threshold70, Threshold80, Threshold90];

    /// <summary>
    /// The percentage an option row stands for. XAML <c>Tag</c> is a string — the first
    /// version cast it straight to <c>int</c> and threw on the very first render.
    /// </summary>
    private static int ChoiceOf(FrameworkElement row) =>
        int.Parse((string)row.Tag, System.Globalization.CultureInfo.InvariantCulture);

    private System.Windows.Shapes.Ellipse[] OptionMarks => [Mark70, Mark80, Mark90];

    /// <summary>
    /// Applies a chosen percentage and closes the list. Renders what the setter
    /// <em>returns</em>, exactly as the toggle rows do — a settings write can fail, and a
    /// pill claiming a value that was never persisted is the same fabrication a tick that
    /// lies about the registry would be (CLAUDE.md rule 6).
    /// </summary>
    private void ChooseThreshold(int percent)
    {
        ShowThreshold(SetThresholdPercent?.Invoke(percent) ?? percent);
        SetOptionsExpanded(false);
    }

    /// <summary>
    /// Opens or closes the option list, then re-docks — the card is <c>SizeToContent</c>, so
    /// growing it downward from a fixed top would push the new rows into the taskbar, which
    /// is the failure the docked placement exists to prevent (issue #33).
    /// </summary>
    private void SetOptionsExpanded(bool expanded, bool redock = true)
    {
        ThresholdOptions.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        ChevronRotation.Angle = expanded ? 180 : 0;

        if (redock && IsVisible)
        {
            UpdateLayout();
            _flyout.Redock(ActualWidth, ActualHeight);
        }
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
    internal enum MenuRow { Startup, UpdateAuto, Notify, Diagnostics, Updates, Exit }

    private System.Windows.Controls.Button RowButton(MenuRow row) => row switch
    {
        MenuRow.Startup => StartupRow,
        MenuRow.UpdateAuto => UpdateAutoRow,
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
    internal void InvokeRow(MenuRow row) => InvokeButton(RowButton(row));

    private static void InvokeButton(System.Windows.Controls.Button button)
    {
        var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(button);
        ((System.Windows.Automation.Provider.IInvokeProvider)
            peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
    }

    /// <summary>
    /// Opens or closes the threshold list for an offscreen render. Skips the re-dock, which
    /// needs a placed window; the sample renderer never shows one.
    ///
    /// <para>Note that skipping the re-dock also skips the clip re-fit, so this path cannot
    /// see the clipping bug that <see cref="ContentFullyDrawn"/> exists for — nothing here
    /// ever animates, so there is no clip to be stale. <c>--menu-check</c> drives the real
    /// window and is where that property is actually asserted.</para>
    /// </summary>
    internal void ExpandThresholdsForVerification(bool expanded) =>
        SetOptionsExpanded(expanded, redock: false);

    /// <summary>The pill's current text, so a verification run can assert what it reads.</summary>
    internal string ThresholdLabel => ThresholdText.Text;

    /// <summary>Whether the option list is open — the state a click on the pill must flip.</summary>
    internal bool ThresholdOptionsOpen => ThresholdOptions.Visibility == Visibility.Visible;

    /// <summary>
    /// Whether every pixel of the card is actually being drawn — i.e. the open transition's
    /// clip, if one is still attached, covers the whole content.
    ///
    /// <para>This exists because expanding the threshold list severed the two: the card grew,
    /// the window grew with it, and the content stayed clipped to the height it had when the
    /// flyout opened. Rows below the fold were sliced in half and the bottom border and corner
    /// vanished, which reads as the UI breaking outright. Logical state was entirely correct
    /// throughout — the list was open, the label had moved, the tick had not — so nothing the
    /// verification asserted could see it.</para>
    /// </summary>
    internal bool ContentFullyDrawn =>
        Content is not FrameworkElement content
        || content.Clip is not RectangleGeometry clip
        || (clip.Rect.Width + 0.5 >= content.ActualWidth && clip.Rect.Height + 0.5 >= content.ActualHeight);

    /// <summary>
    /// Activates the pill through its automation peer, exactly as <see cref="InvokeRow"/>
    /// does for a row — so a verification run drives the real <c>Click</c> handler, including
    /// the <c>e.Handled</c> that stops the click bubbling to the row underneath.
    /// </summary>
    internal void InvokeThresholdPill() => InvokeButton(ThresholdPill);

    /// <summary>Activates one of the three choices. Unknown values are ignored, not guessed at.</summary>
    internal void InvokeThresholdOption(int percent)
    {
        if (OptionRows.FirstOrDefault(r => ChoiceOf(r) == percent) is { } row)
        {
            InvokeButton(row);
        }
    }

    /// <summary>Tick state of a toggle row, for asserting a toggle actually flipped.</summary>
    internal bool RowIsChecked(MenuRow row) => row switch
    {
        MenuRow.Startup => IsChecked(StartupCheck),
        MenuRow.UpdateAuto => IsChecked(UpdateAutoCheck),
        _ => IsChecked(NotifyCheck),
    };

    /// <summary>Whether a row is present at all — the automatic-update row is dropped on a
    /// build that cannot self-install, and "absent" is a state worth asserting.</summary>
    internal bool RowIsVisible(MenuRow row) => RowButton(row).Visibility == Visibility.Visible;

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
