using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OView.App.Rendering;
using OView.Core.Models;
using OView.Presentation.Text;
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
/// hit, which, since the tray icon sits *inside* the taskbar, is reliably the one place a
/// menu cannot fully fit. It clipped into the taskbar and off the screen edge, leaving
/// items unclickable, and grew worse with every item added.
///
/// So placement no longer depends on the cursor at all: the flyout docks to the same
/// work-area corner as the detail panel, via the same <see cref="PopupPositioner"/>, the
/// model Windows' own volume, network and calendar flyouts use. The work area excludes the
/// taskbar by definition, so clearing it is structural rather than a margin that happens to
/// be big enough. Height is measured after layout on every open, so adding rows keeps
/// working without touching the placement code.
///
/// Being a real window rather than a <c>ContextMenu</c> also buys the styling the issue
/// asked for, the panel's own palette (<see cref="PanelTheme"/>), rounded card, brand
/// mark, which a themed system context menu cannot give.
/// </summary>
public partial class MenuWindow : Window, IFlyout
{
    /// <summary>Forces a theme for verification renders; null follows the OS.</summary>
    public bool? ThemeOverride { get; set; }

    /// <summary>
    /// Docking, the open/close transitions and the toggle grace window, shared with the
    /// detail panel rather than reimplemented here (issue #54).
    /// </summary>
    private readonly DockedFlyout _flyout;

    /// <summary>The threshold list's fold, the same one the panel's breakdown uses.</summary>
    private readonly DisclosureAnimation _options;

    /// <summary>The beat between a choice and the list folding away, so the dot is seen first.</summary>
    private readonly DispatcherTimer _closeAfterChoice = new()
    {
        Interval = TimeSpan.FromMilliseconds(PanelMotion.Ms(PanelMotion.ChosenCloseDelayBaseMs)),
    };

    private readonly Tick _startupTick;
    private readonly Tick _updateAutoTick;
    private readonly Tick _notifyTick;

    /// <summary>Whether the list is meant to be open: one folding closed, or about to, already is not.</summary>
    private bool _optionsOpen;

    /// <summary>Disables auto-hide for verification screenshots.</summary>
    public bool PinForVerification
    {
        get => _flyout.PinForVerification;
        set => _flyout.PinForVerification = value;
    }

    /// <inheritdoc cref="DockedFlyout.ClosedByClickAway"/>
    public bool ClosedByClickAway => _flyout.ClosedByClickAway;

    /// <summary>Closes the flyout from outside, a second right-click completing the toggle.</summary>
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


    /// <summary>What the pill currently reads, the value the option marks are drawn from.</summary>
    private int _thresholdPercent = UsageLevels.CriticalPercent;

    /// <summary>The user asked to set or clear the weekly reset time (issue #186).</summary>
    public event EventHandler? WeeklyResetRequested;

    /// <summary>Raised when the user asks to undo a self-imposed usage-refresh block (issue #234).</summary>
    public event EventHandler? ResumeUsageRefreshRequested;

    /// <summary>
    /// Raised when the user asks for the panel back in the tray corner, forgetting wherever they
    /// dragged it to.
    /// </summary>
    public event EventHandler? ResetPanelPositionRequested;

    public event EventHandler? CopyDiagnosticsRequested;
    public event EventHandler? CheckForUpdatesRequested;
    public event EventHandler? ExitRequested;

    public MenuWindow()
    {
        InitializeComponent();

        _flyout = new DockedFlyout(this);
        Deactivated += (_, _) => _flyout.OnDeactivated();

        _options = new DisclosureAnimation(
            this, ThresholdOptions, ChevronRotation,
            PanelMotion.Ms(PanelMotion.ThresholdOpenBaseMs),
            PanelMotion.Ms(PanelMotion.ThresholdCloseBaseMs),
            new DisclosureRowMotion(
                PanelMotion.OptionStartMs,
                PanelMotion.Ms(PanelMotion.OptionFadeBaseMs),
                PanelMotion.Ms(PanelMotion.OptionsCloseFadeBaseMs),
                RiseDip: 0),
            () => _flyout.Redock(ActualWidth, ActualHeight));
        _options.SetRows(OptionRows.Select(row => (IReadOnlyList<UIElement>)[row]));

        // One check rather than a stop on each of the ways out of the menu: a menu dismissed
        // during the hold has no list to fold, whichever way it went.
        _closeAfterChoice.Tick += (_, _) =>
        {
            _closeAfterChoice.Stop();
            if (IsVisible && !_flyout.IsClosing)
            {
                SetOptionsExpanded(false);
            }
        };

        _startupTick = new Tick(StartupCheck);
        _updateAutoTick = new Tick(UpdateAutoCheck);
        _notifyTick = new Tick(NotifyCheck);

        foreach (var mark in OptionMarks)
        {
            mark.RenderTransform = new ScaleTransform();
        }

        foreach (var row in FilledRows)
        {
            row.ApplyTemplate();
            row.MouseEnter += (_, _) => UpdateFill(row);
            row.MouseLeave += (_, _) => UpdateFill(row);
            row.IsKeyboardFocusedChanged += (_, _) => UpdateFill(row);
        }

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
        StartupRow.Click += (_, _) => Toggle(SetRunAtStartup, _startupTick);
        UpdateAutoRow.Click += (_, _) => Toggle(SetUpdateAutomatically, _updateAutoTick);
        NotifyRow.Click += (_, _) => Toggle(SetNotifyOnThreshold, _notifyTick);

        // Marked handled, or the Click bubbles to NotifyRow above and every interaction with
        // the selector would silently switch the notification off as a side effect. A click
        // during the hold after a choice sees an open list, so it closes it, just sooner.
        ThresholdPill.Click += (_, e) =>
        {
            e.Handled = true;
            SetOptionsExpanded(!_optionsOpen && !_closeAfterChoice.IsEnabled);
        };

        foreach (var (option, label) in OptionRows.Zip(OptionLabels))
        {
            // Labelled from the row's own Tag rather than from XAML, so a choice cannot read
            // one percentage and apply another, ChoiceOf is what the click uses too.
            label.Text = MenuText.ThresholdPercent(ChoiceOf(option));

            option.Click += (sender, e) =>
            {
                e.Handled = true;
                ChooseThreshold(ChoiceOf((FrameworkElement)sender));
            };
        }

        ResetPanelRow.Click += (_, _) => Dismiss(ResetPanelPositionRequested);
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
    /// False hides the row outright, see the XAML for why it is hidden and not disabled.
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
        SetOptionsExpanded(false, animate: false);

        // Every open starts at rest, with no fill left fading from the last one.
        foreach (var row in FilledRows)
        {
            UpdateFill(row, animate: false);
        }

        // takeForeground: a tray-resident app owns no activated window, so Activate()
        // alone is not guaranteed the foreground, and without it the flyout never
        // receives the deactivation that dismisses it on an outside click (issue #11,
        // which the old ContextMenu hit for the same reason).
        _flyout.Show(takeForeground: true);
    }

    /// <summary>Fills the rows without showing the window (also the verification-render path).</summary>
    public void Populate(MenuState state)
    {
        PanelTheme.Apply(Resources, ThemeOverride ?? PanelTheme.IsAppsLight());

        VersionText.Text = MenuText.VersionLabel(state.Version);
        // States the answer in effect, not just the affordance: "derived" tells a user who
        // has never opened this dialog what O-view is currently doing.
        WeeklyResetValue.Text = state.WeeklyReset.Length > 0
            ? state.WeeklyReset
            : MenuText.WeeklyResetDerived;
        NotifySuffix.Text = MenuText.NotifySuffix;
        _startupTick.Place(state.RunAtStartup);
        _notifyTick.Place(state.NotifyOnThreshold);
        ShowThreshold(state.ThresholdPercent);

        // Collapsed, not hidden: Hidden would keep the row's 34px of height and leave a gap
        // under "Run at startup" on every build that cannot self-install, which is most Linux
        // installs and every portable exe.
        UpdateAutoRow.Visibility = state.CanUpdateAutomatically ? Visibility.Visible : Visibility.Collapsed;

        // Only present while there is something to undo, and Collapsed for the same reason as
        // above, this is the ordinary case, so a permanent empty row would be 34px of nothing
        // for every user who never trips the guard.
        var blocked = state.UsageRefreshBlocked.Length > 0;
        ResumeRefreshRow.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
        if (blocked)
        {
            ResumeRefreshLabel.Text = PanelText.UsageRefreshBlockedRow;
            ResumeRefreshRow.ToolTip = PanelText.UsageRefreshBlockedHint(state.UsageRefreshBlocked);
        }
        _updateAutoTick.Place(state.UpdateAutomatically && state.CanUpdateAutomatically);
    }

    /// <summary>
    /// Draws a threshold onto the pill and the option marks.
    ///
    /// <para>The value is <b>rendered as it is</b>, not snapped to one of
    /// the three the rows offer (70, 80, 90). <c>TraySettings</c> accepts any 1–100 value and a
    /// hand-edited settings.json is a legitimate way to hold one; showing 75 as "80%" would
    /// be the menu asserting something about the machine that is not true.</para>
    /// </summary>
    private void ShowThreshold(int percent, bool animate = false)
    {
        var moved = percent != _thresholdPercent;
        _thresholdPercent = percent;
        ThresholdText.Text = MenuText.ThresholdPercent(percent);

        foreach (var (row, mark) in OptionRows.Zip(OptionMarks))
        {
            var chosen = ChoiceOf(row) == percent;
            PlaceDot(mark, chosen);

            if (chosen && moved && animate && WpfMotion.AnimationsOn)
            {
                GrowDot(mark);
            }
        }
    }

    private static void PlaceDot(System.Windows.Shapes.Ellipse mark, bool chosen)
    {
        // Released first: an attached animation holds its end value over the local one.
        var scale = (ScaleTransform)mark.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        mark.BeginAnimation(OpacityProperty, null);
        mark.Visibility = chosen ? Visibility.Visible : Visibility.Hidden;
    }

    private static void GrowDot(System.Windows.Shapes.Ellipse mark)
    {
        var ms = PanelMotion.Ms(PanelMotion.ChosenDotBaseMs);
        var scale = (ScaleTransform)mark.RenderTransform;
        var grow = WpfMotion.DelayedFade(PanelMotion.ChosenDotFromScale, 1, ms, 0, PanelMotion.Decelerate);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        mark.BeginAnimation(OpacityProperty, WpfMotion.DelayedFade(0, 1, ms, 0, PanelMotion.Decelerate));
    }

    private System.Windows.Controls.Button[] OptionRows => [Threshold70, Threshold80, Threshold90];

    /// <summary>
    /// The percentage an option row stands for. XAML <c>Tag</c> is a string, the first
    /// version cast it straight to <c>int</c> and threw on the very first render.
    /// </summary>
    private static int ChoiceOf(FrameworkElement row) =>
        int.Parse((string)row.Tag, System.Globalization.CultureInfo.InvariantCulture);

    private System.Windows.Shapes.Ellipse[] OptionMarks => [Mark70, Mark80, Mark90];

    private System.Windows.Controls.TextBlock[] OptionLabels => [Label70, Label80, Label90];

    /// <summary>Every row whose hover and keyboard-focus fill fades.</summary>
    private System.Windows.Controls.Button[] FilledRows =>
    [
        ResetPanelRow, StartupRow, UpdateAutoRow, NotifyRow, ThresholdPill,
        Threshold70, Threshold80, Threshold90,
        WeeklyResetRow, ResumeRefreshRow, DiagnosticsRow, UpdatesRow, ExitRow,
    ];

    /// <summary>Fades a row's hover and keyboard-focus fill from wherever it is. Never a press.</summary>
    private static void UpdateFill(System.Windows.Controls.Button row, bool animate = true)
    {
        var fill = (System.Windows.Controls.Border)row.Template.FindName("HoverFill", row);
        var lit = row.IsMouseOver || row.IsKeyboardFocused;
        var ms = PanelMotion.Ms(lit ? PanelMotion.HoverFillInBaseMs : PanelMotion.HoverFillOutBaseMs);

        if (animate && PanelMotion.Runs(WpfMotion.AnimationsOn, ms))
        {
            fill.BeginAnimation(OpacityProperty, WpfMotion.DelayedFade(
                fill.Opacity, lit ? 1 : 0, ms, 0, lit ? PanelMotion.Decelerate : PanelMotion.Accelerate));
            return;
        }

        // Released first, for the reason PlaceDot gives.
        fill.BeginAnimation(OpacityProperty, null);
        fill.Opacity = lit ? 1 : 0;
    }

    /// <summary>
    /// Applies a chosen percentage and closes the list. Renders what the setter
    /// <em>returns</em>, exactly as the toggle rows do, a settings write can fail, and a
    /// pill claiming a value that was never persisted is the same fabrication a tick that
    /// lies about the registry would be (CLAUDE.md rule 6).
    /// </summary>
    private void ChooseThreshold(int percent)
    {
        ShowThreshold(SetThresholdPercent?.Invoke(percent) ?? percent, animate: true);

        if (!PanelMotion.Runs(WpfMotion.AnimationsOn, _closeAfterChoice.Interval.TotalMilliseconds))
        {
            SetOptionsExpanded(false);
            return;
        }

        // Restarted, not left running: the beat is counted from the latest click.
        _optionsOpen = false;
        _closeAfterChoice.Stop();
        _closeAfterChoice.Start();
    }

    /// <summary>
    /// Opens or closes the option list, re-docking on every frame, the card is
    /// <c>SizeToContent</c>, so growing it downward from a fixed top would push the new rows
    /// into the taskbar, which is the failure the docked placement exists to prevent (issue
    /// #33). In the order the panel's fold uses, see <see cref="DisclosureAnimation.Prepare"/>.
    /// </summary>
    private void SetOptionsExpanded(bool expanded, bool animate = true)
    {
        _closeAfterChoice.Stop();
        _optionsOpen = expanded;

        // For the fold alone: a clip at rest moves the labels by a level of antialiasing, and
        // the stills must not move.
        var folding = animate && IsVisible;
        ThresholdOptions.ClipToBounds = folding;

        if (!folding)
        {
            _options.Apply(expanded);
            return;
        }

        var (fromHeight, fromAngle) = _options.Current;
        var toHeight = expanded ? _options.MeasureNatural(NotifyRow.ActualWidth) : 0;

        _options.Prepare(fromHeight);
        UpdateLayout();
        _flyout.Redock(ActualWidth, ActualHeight);

        _options.Run(expanded, fromHeight, fromAngle, toHeight);

        // The fold's own fade out is the linear one the panel's table calls for. These rows leave
        // on the curve everything else leaves on, so it is replaced in the same frame.
        var fadeMs = PanelMotion.Ms(PanelMotion.OptionsCloseFadeBaseMs);
        if (!expanded && PanelMotion.Runs(WpfMotion.AnimationsOn, fadeMs))
        {
            foreach (var row in OptionRows)
            {
                row.BeginAnimation(OpacityProperty,
                    WpfMotion.DelayedFade(row.Opacity, 0, fadeMs, 0, PanelMotion.Accelerate));
            }
        }
    }

    private static void Toggle(Func<bool, bool>? apply, Tick tick)
    {
        if (apply is null)
        {
            return;
        }

        tick.Turn(apply(!tick.On));
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
    /// Activates a row through its automation peer, the same <c>Click</c> the mouse and the
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
    /// Opens or closes the threshold list for an offscreen render. Skips the fold and its re-dock,
    /// which need a placed window; the sample renderer never shows one.
    ///
    /// <para>So this path cannot see the fault <see cref="ContentFullyDrawn"/> exists for,
    /// nothing here ever animates. <c>--menu-check</c> drives the real
    /// window and is where that property is actually asserted.</para>
    /// </summary>
    internal void ExpandThresholdsForVerification(bool expanded) =>
        SetOptionsExpanded(expanded, animate: false);

    /// <summary>The pill's current text, so a verification run can assert what it reads.</summary>
    internal string ThresholdLabel => ThresholdText.Text;

    /// <summary>Whether the option list is open, the state a click on the pill must flip.</summary>
    internal bool ThresholdOptionsOpen => _optionsOpen;

    /// <summary>
    /// Whether every pixel of the card is actually being drawn, i.e. no clip is attached, and
    /// an open list's fold has let go of its height, with every row at full strength.
    ///
    /// <para>This exists because expanding the threshold list severed the two: the card grew,
    /// the window grew with it, and the content stayed clipped to the height it had when the
    /// flyout opened. Rows below the fold were sliced in half and the bottom border and corner
    /// vanished, which reads as the UI breaking outright. Logical state was entirely correct
    /// throughout, the list was open, the label had moved, the tick had not, so nothing the
    /// verification asserted could see it.</para>
    /// </summary>
    internal bool ContentFullyDrawn =>
        Content is not FrameworkElement { Clip: not null }
        && (!_optionsOpen
            || (double.IsNaN(ThresholdOptions.Height) && OptionRows.All(row => row.Opacity >= 1)));

    /// <summary>
    /// Activates the pill through its automation peer, exactly as <see cref="InvokeRow"/>
    /// does for a row, so a verification run drives the real <c>Click</c> handler, including
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
        MenuRow.Startup => _startupTick.On,
        MenuRow.UpdateAuto => _updateAutoTick.On,
        _ => _notifyTick.On,
    };

    /// <summary>Whether a row is present at all, the automatic-update row is dropped on a
    /// build that cannot self-install, and "absent" is a state worth asserting.</summary>
    internal bool RowIsVisible(MenuRow row) => RowButton(row).Visibility == Visibility.Visible;

    /// <summary>Whether a close transition is in flight, the state that made rows dead.</summary>
    internal bool IsClosing => _flyout.IsClosing;

    /// <summary>
    /// Renders the flyout to a bitmap without showing it, the menu's equivalent of the
    /// tray icon's <c>--samples</c> hook, so both themes can be eyeballed side by side
    /// without a desktop session or disturbing a running instance.
    /// </summary>
    internal BitmapSource RenderToBitmap(double scale) => VisualRenderer.RenderContent(this, scale);

    /// <summary>
    /// One row's tick. The state is held here and not read back off the mark: a tick fading out
    /// is still visible, so a second click would read the wrong state and write it to the machine.
    /// </summary>
    private sealed class Tick(Path mark)
    {
        /// <summary>The RowCheck stroke's length in DIP, a touch over, so no end shows.</summary>
        private const double StrokeLength = 14.3;

        private int _run;

        public bool On { get; private set; }

        public void Place(bool on)
        {
            _run++;
            On = on;
            Rest();
        }

        /// <summary>Draws the tick on or fades it out, carrying on from what is on screen.</summary>
        public void Turn(bool on)
        {
            if (on == On)
            {
                return;
            }

            var ms = PanelMotion.Ms(on ? PanelMotion.TickOnBaseMs : PanelMotion.TickOffBaseMs);
            if (!PanelMotion.Runs(WpfMotion.AnimationsOn, ms))
            {
                Place(on);
                return;
            }

            On = on;

            if (on && mark.Visibility != Visibility.Visible)
            {
                // Dash units are multiples of the stroke thickness.
                var dash = StrokeLength / mark.StrokeThickness;
                mark.StrokeDashArray = [dash, dash];
                mark.StrokeDashOffset = dash;
                mark.Visibility = Visibility.Visible;
            }

            var curve = on ? PanelMotion.Decelerate : PanelMotion.Accelerate;
            var fade = WpfMotion.DelayedFade(mark.Opacity, on ? 1 : 0, ms, 0, curve);

            // Attached first and only this run's, as in DisclosureAnimation.Run. No guard timer:
            // a missed completion still looks like the state it ended in.
            var run = ++_run;
            fade.Completed += (_, _) =>
            {
                if (run == _run)
                {
                    Rest();
                }
            };

            // Held where it is on the way out: a half-drawn tick fades as it stands.
            var drawn = mark.StrokeDashOffset;
            mark.BeginAnimation(
                Path.StrokeDashOffsetProperty,
                on ? WpfMotion.DelayedFade(drawn, 0, ms, 0, curve) : null);
            mark.StrokeDashOffset = drawn;

            mark.BeginAnimation(OpacityProperty, fade);
        }

        // Hidden rather than removed: the row keeps its 22px check gutter either way, so
        // labels stay on one left edge whichever settings are on. And no dash left on it, so a
        // tick at rest is drawn exactly as a plain stroke.
        private void Rest()
        {
            mark.BeginAnimation(OpacityProperty, null);
            mark.BeginAnimation(Path.StrokeDashOffsetProperty, null);
            mark.Opacity = 1;
            mark.ClearValue(Path.StrokeDashArrayProperty);
            mark.ClearValue(Path.StrokeDashOffsetProperty);
            mark.Visibility = On ? Visibility.Visible : Visibility.Hidden;
        }
    }
}
