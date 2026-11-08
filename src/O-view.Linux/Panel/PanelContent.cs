using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;

using Avalonia.Layout;
using Avalonia.Media;
using OView.App.Rendering;
using OView.Core.Models;
using OView.Core.Providers.CachedUsage;
using OView.Core.Providers.Jsonl;
using OView.Core.Providers.PlanHistory;
using OView.Presentation.Panel;
using OView.Presentation.Text;
using static OView.Linux.Panel.PanelPrimitives;

namespace OView.Linux.Panel;

/// <summary>
/// The detail panel's content (docs/ui-spec.md), built for Avalonia.
///
/// <para>Every figure and every sentence comes from the shared layer,
/// <see cref="PanelStatistics"/>, <see cref="PanelText"/>, <see cref="UsageFormatter"/>,
/// <see cref="TranscriptScopeReport"/>. Nothing here computes or words anything, so the two
/// panels cannot disagree about what a number means or how a caveat reads.</para>
///
/// <para>It is a plain control rather than a window so the same tree can be shown live or
/// rendered offscreen for verification, exactly as the WPF panel does.</para>
///
/// <para>This class owns the panel's frame and the one row it draws itself, the header. The
/// header is also the panel's drag handle, which is why it is the one row built once and
/// refilled rather than rebuilt (<see cref="Header"/>). Each section below it is its own piece,
/// named for what it draws: <see cref="PanelOffPlan"/>, <see cref="PanelPlanBlock"/> and
/// <see cref="PanelLocalBlock"/>.</para>
///
/// <para>The frame can also put those sections in a scrolling viewport, so a panel the window
/// has had to cap can still be reached to its last section, see <see cref="ScrollContent"/>,
/// <see cref="Viewport"/> and <see cref="ScrollBar"/>. A panel with room never asks for one
/// and is laid out exactly as it was before it could scroll at all. The gestures that move it,
/// the wheel, a thumb drag, a click on the track, are <see cref="PanelScroll"/>.</para>
/// </summary>
public sealed class PanelContent : Border
{
    private const double PanelWidth = PanelMetrics.PanelWidthDip;

    /// <summary>Gap between sections at the natural density, this head's own constant.</summary>
    private const double NaturalRootSpacing = 10;

    /// <summary>
    /// The scroll bar's width and the gap before it, shared with the Windows head through
    /// <see cref="PanelMetrics"/>; the two colours are shared through <see cref="PanelPalette"/>.
    /// </summary>
    private const double ScrollBarWidth = PanelMetrics.ScrollBarWidthDip;

    private const double ScrollBarGap = PanelMetrics.ScrollBarGapDip;

    /// <summary>
    /// How tightly to pack, for a display too short for the natural layout
    /// (<see cref="PanelDensity"/>). Set before <see cref="Populate"/>; defaults to the
    /// shipped layout, so a head that never sets it is unchanged.
    /// </summary>
    public PanelDensity Density { get; set; } = PanelDensity.Normal;

    private readonly LinuxPanelTheme _theme;
    private readonly StackPanel _root;
    private readonly Grid _header;
    private readonly ScrollContentPresenter _viewport;
    private readonly Border _scrollTrack;
    private readonly Border _scrollThumb;
    private readonly Grid _scrollFrame;
    private readonly PanelScroll _scroll;

    private readonly PanelOffPlan _offPlan;
    private readonly PanelPlanBlock _planBlock;
    private readonly PanelLocalBlock _localBlock;

    /// <summary>See <see cref="ScrollToFromFoot"/>: set by a press on the flip tag, spent by the scroll back.</summary>
    private double? _tagViewTop;

    /// <summary>
    /// The reader pressed the banner's hide control. The banner has already gone from the panel
    /// when this is raised; keeping it hidden and re-fitting the window are the window's.
    /// </summary>
    public event Action? BannerHidden;

    /// <summary>
    /// The reader pressed a segment of the Output | Overall switch or the flip tag, and this is the
    /// view it gives. Keeping it, rebuilding for it and re-fitting the window are the window's.
    /// </summary>
    public event Action<PanelViewChoice>? ViewChosen;

    /// <summary>
    /// The viewport the sections are scrolled inside, once <see cref="ScrollContent"/> has put
    /// them there. Public for the same reason <see cref="PanelTokenBars.ViewSwitch"/> is: this
    /// is the state a wheel turn and a thumb drag drive, and a test process has no windowing
    /// subsystem to produce either gesture for real. It is also what says how much content is
    /// out of sight, the one number that proves nothing was dropped when the window had to
    /// cap itself.
    /// </summary>
    public ScrollContentPresenter Viewport => _viewport;

    /// <summary>The scroll bar's track, hidden while everything fits inside the viewport.</summary>
    public Control ScrollBar => _scrollTrack;

    /// <summary>Whether the sections are currently in the viewport rather than straight in
    /// the frame.</summary>
    public bool Scrolls => ReferenceEquals(Child, _scrollFrame);

    /// <summary>
    /// The title row, which is also the band the panel is dragged by, the one part of the
    /// panel with nothing on it to click, so a press here can mean "move me" without taking a
    /// click away from anything.
    ///
    /// <para>Built once in the constructor and refilled by each <see cref="Populate"/> rather
    /// than rebuilt with the rest of the tree. A re-populate happens on every open and again
    /// when the density changes, and a fresh control each time would silently drop whatever
    /// handlers the window had attached to the last one.</para>
    ///
    /// <para><b>Its transparent background is load-bearing.</b> A control with no background is
    /// not hit-testable in the gaps between its children, so without one the handle would only
    /// answer on the words themselves. It paints nothing, so the verification renders are
    /// unchanged.</para>
    /// </summary>
    public Control Header => _header;

    public PanelContent(LinuxPanelTheme theme)
    {
        _theme = theme;
        _offPlan = new PanelOffPlan(theme);
        _planBlock = new PanelPlanBlock(theme, _offPlan);
        _localBlock = new PanelLocalBlock(theme, view =>
        {
            _tagViewTop = TagViewTop();
            ViewChosen?.Invoke(view);
        });

        Width = PanelWidth;
        Background = theme["PanelBg"];
        BorderBrush = theme["PanelBorder"];
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(16);

        _root = new StackPanel { Spacing = 10 };

        // See Header: built here, filled by AddHeader, so the window's drag handlers survive a
        // re-populate. Transparent for hit-testing, which paints nothing.
        _header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Background = Brushes.Transparent,
        };

        // What a screen reader says the band is for. Set here, with the rest of what the header
        // is, rather than in AddHeader: the words never change between opens, and the one thing
        // the name must survive is the re-populate that refills the children. The move cursor
        // says the same thing to everyone who can see it, and the wording is Presentation's so
        // both heads say one name, PanelHeaderView.MoveName.
        AutomationProperties.SetName(_header, PanelHeaderView.MoveName);

        _scrollThumb = new Border
        {
            Background = theme["ScrollThumb"],
            CornerRadius = new CornerRadius(ScrollBarWidth / 2),
            Width = ScrollBarWidth,
            Height = PanelScroll.MinThumbHeight,
            VerticalAlignment = VerticalAlignment.Top,
        };

        _scrollTrack = new Border
        {
            Background = theme["ScrollTrack"],
            CornerRadius = new CornerRadius(ScrollBarWidth / 2),
            Width = ScrollBarWidth,
            Margin = new Thickness(ScrollBarGap, 0, 0, 0),
            ClipToBounds = true,
            Child = _scrollThumb,

            // Hidden until there is something out of sight, and it occupies an Auto column, so
            // a panel that fits is laid out at exactly the width it was before this existed.
            IsVisible = false,
        };

        _viewport = new ScrollContentPresenter
        {
            CanVerticallyScroll = true,
            CanHorizontallyScroll = false,
        };

        // Every gesture that moves the content, and the thumb that follows it, live together
        // in PanelScroll, including the wheel, which this file used to leave to the viewport.
        _scroll = new PanelScroll(this, _viewport, _scrollTrack, _scrollThumb);

        _viewport.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollContentPresenter.OffsetProperty
                || e.Property == ScrollContentPresenter.ExtentProperty
                || e.Property == ScrollContentPresenter.ViewportProperty)
            {
                _scroll.UpdateBar();
            }
        };

        _scrollFrame = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_scrollTrack, 1);
        _scrollFrame.Children.Add(_viewport);
        _scrollFrame.Children.Add(_scrollTrack);

        Child = _root;
    }

    /// <summary>
    /// Puts the sections in a scrolling viewport, or takes them straight back into the frame.
    ///
    /// <para>The window asks for one exactly when <see cref="PanelFit"/> has had to cap it, so
    /// everything past the cap stays reachable instead of being clipped off the bottom of the
    /// screen.</para>
    ///
    /// <para><b>Why a panel with room does not simply always have one.</b> A viewport clips to
    /// its own bounds and is a second layout path for everything inside it, so one on every
    /// display would be a change to how the panel looks on every display that already fits. A
    /// panel with room keeps exactly the tree it has always had.</para>
    /// </summary>
    public void ScrollContent(bool scrolls)
    {
        if (scrolls == Scrolls)
        {
            return;
        }

        // Detached from the old parent first: Avalonia refuses a control that still has one.
        if (scrolls)
        {
            Child = null;
            _viewport.Content = _root;

            // A presenter fills itself from its content only once its tree reaches a window,
            // and the verification renders come from a tree that never does, the same trap
            // that left both halves of the Bars/Breakdown switch blank in every sample image.
            // Asking for the child here means the sections are built either way.
            _viewport.UpdateChild();
            Child = _scrollFrame;
        }
        else
        {
            Child = null;
            _viewport.Content = null;
            _viewport.UpdateChild();
            _scrollTrack.IsVisible = false;
            Child = _root;
        }
    }

    /// <summary>
    /// Puts the frame back where a fresh open starts from, straight in the border at the natural
    /// density, so a re-fit decides from the same place an open does. Without it a panel that had
    /// been capped would be measured through its viewport, in the narrower column the scroll bar
    /// leaves, and a compact one could never get back to the natural layout.
    /// </summary>
    public void ReturnToNaturalLayout()
    {
        ScrollContent(false);
        Density = PanelDensity.Normal;
    }

    /// <summary>
    /// How far the view stands above the foot of the content, zero when nothing scrolls. Measured
    /// from the foot because the Output | Overall switch is down there: the table above it changes
    /// height, so the same distance from the top would carry the switch away from the pointer.
    /// </summary>
    public double ScrollFromFoot =>
        Scrolls ? Math.Max(0, _viewport.Extent.Height - _viewport.Viewport.Height - _viewport.Offset.Y) : 0;

    /// <summary>
    /// Scrolls back to <paramref name="fromFoot"/> above the foot, once the new content is laid out.
    /// A press on the flip tag instead puts the tag back where it stood in the view: the tag is
    /// above the face that changes height, so the distance from the foot would carry it away.
    /// </summary>
    public void ScrollToFromFoot(double fromFoot)
    {
        var tag = _localBlock.PressedTag?.TranslatePoint(default, _root);
        var offset = _tagViewTop is { } top && tag is { } at
            ? at.Y - top
            : _viewport.Extent.Height - _viewport.Viewport.Height - fromFoot;

        _tagViewTop = null;
        _viewport.Offset = _viewport.Offset.WithY(Math.Max(0, offset));
    }

    /// <summary>How far below the top of the view the flip tag stands, when it is what was just pressed.</summary>
    private double? TagViewTop() =>
        _localBlock.PressedTag?.TranslatePoint(default, _root)?.Y - (Scrolls ? _viewport.Offset.Y : 0);

    /// <inheritdoc cref="PanelLocalBlock.FocusChosenSegment"/>
    public void FocusChosenSegment() => _localBlock.FocusChosenSegment();

    /// <summary>
    /// The height this panel's content wants, measured against an unbounded height, the
    /// content's own answer, not the window's.
    ///
    /// <para><b>Why it cannot be read off the window.</b> The panel is <c>SizeToContent</c>, so
    /// its frame is whatever the compositor granted, and a compositor will not grant more than
    /// the screen: on Maximilian's 1024×768 desktop the frame came back as exactly 768 and the
    /// panel therefore concluded it was not too tall for a 712 DIP work area, while four
    /// sections sat off the bottom edge with no way to reach them (his report of 2026-10-15).
    /// Measuring the tree is how the verification renders have always sized themselves, and it
    /// is the only number that is about the content rather than about the display.</para>
    ///
    /// <para>The measure is left invalidated afterwards so the next layout pass measures the
    /// tree against the constraint it is actually being laid out in, rather than keeping this
    /// unbounded answer.</para>
    /// </summary>
    public double NaturalHeightDip()
    {
        Measure(Size.Infinity);
        var wanted = DesiredSize.Height;
        InvalidateMeasure();
        return wanted;
    }

    public void Populate(
        UsageSnapshot snapshot,
        PanelStatistics stats,
        ClaudeAccount? account,
        PlanHistoryReport? dataReport,
        TranscriptScopeReport? scopeReport,
        DateTimeOffset utcNow,
        BoostNotices? boostNotices = null,
        TimeZoneInfo? zone = null,
        CachedUtilization? cachedUsage = null,
        PanelViewChoice view = default)
    {
        _root.Children.Clear();

        // Back to the top. A re-populate is a fresh open or a re-layout at a new density, and
        // either way the header is where the panel should start.
        _viewport.Offset = default;

        // Applied here rather than in the constructor: the density depends on the screen this
        // open lands on, and a multi-monitor desktop can move between them. Normal reproduces
        // the shipped layout exactly (PanelDensity).
        Padding = new Thickness(Density.RootPadding);
        _root.Spacing = NaturalRootSpacing * Density.SpacingScale;

        // A pinned zone is for verification renders, so a UTC CI runner and a CET desktop
        // draw the same reset times; the live panel always follows the machine.
        var local = zone ?? TimeZoneInfo.Local;
        var authoritative = PanelSections.IsAuthoritative(snapshot);

        AddHeader(PanelSections.Header(snapshot, account, utcNow, local));

        // Explain a blank panel rather than leaving the user to guess (rule 6). Only shown
        // when the figures are genuinely unavailable, and worded from both reports together,
        // a missing plan file beside working transcripts is a CLI-only user, not a fault
        // (issue #170).
        var banner = PanelBanner.Resolve(authoritative, dataReport, scopeReport, stats.Tokens31Days);

        // No settings for the weekly meter: this head does not receive them, which reads the
        // same as not having entered a weekly reset, so the "you set this" label the Windows
        // panel can show never appears here. Plumbing them through is a change to this head's
        // shell.
        var plan = PanelSections.PlanBlock(
            PanelSections.SessionMeter(snapshot, banner, utcNow, local),
            PanelSections.WeeklyMeter(snapshot, banner, null, boostNotices, utcNow, local),
            PanelSections.ModelLimitMeters(cachedUsage, utcNow, local),
            PanelSections.OffPlan(stats, cachedUsage, utcNow, local),
            stats,
            cachedUsage,
            view);

        // What the plan meters cannot show: work running past a meter that is not moving, or
        // past the window entirely. Above the no-data banner, as on the Windows panel, this
        // one describes live work, that one describes figures that are missing, and the live
        // signal is the more urgent of the two.
        if (plan.Banner is { } offPlanBanner)
        {
            _offPlan.AddBannerTo(_root, offPlanBanner, plan.HideBannerLabel, Density, () => BannerHidden?.Invoke());
        }

        if (banner is not null)
        {
            AddBanner(banner.Title, banner.Detail);
        }

        _planBlock.AddTo(_root, plan, Density);

        var report = scopeReport ?? TranscriptScopeReport.Inspect();

        _localBlock.AddTo(
            _root,
            PanelSections.LocalTotals(stats, snapshot, local, view),
            view.Face == PanelFace.Models ? PanelSections.ModelFace(stats, view) : null,
            PanelSections.LocalFooter(report, view),
            PanelSections.ScopeNote(stats, snapshot, report),
            Density);
    }

    // ── sections ────────────────────────────────────────────────────────────────────

    private void AddHeader(PanelHeaderView header)
    {
        _header.Children.Clear();

        var left = new StackPanel { Spacing = 2 };
        left.Children.Add(Text(header.Title, 20, _theme["TextPrimary"], FontWeight.SemiBold));
        left.Children.Add(Text(header.Freshness, 12, _theme["TextSecondary"]));

        var right = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(Text(header.AccountName, 13, _theme["TextPrimary"]));
        if (header.Email is { } email)
        {
            right.Children.Add(Text(email, 11, _theme["TextSecondary"]));
        }

        right.Children.Add(new Border
        {
            Background = _theme["BadgeBg"],
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = Text(header.Tier, 11, _theme["BadgeText"]),
        });

        Grid.SetColumn(right, 1);
        _header.Children.Add(left);
        _header.Children.Add(right);
        _root.Children.Add(_header);
    }

    private void AddBanner(string title, string detail) =>
        _root.Children.Add(WarnCard(_theme, NaturalTilePadding * Density.SpacingScale, title, detail));
}
