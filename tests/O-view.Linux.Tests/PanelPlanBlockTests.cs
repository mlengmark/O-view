using System.Reflection;
using System.Text.RegularExpressions;

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using OView.App.Rendering;
using OView.Linux.Panel;
using OView.Presentation.Panel;
using OView.Presentation.Samples;
using OView.Presentation.Text;
using Line = Avalonia.Controls.Shapes.Line;
using Pointer = Avalonia.Input.Pointer;

namespace OView.Linux.Tests;

/// <summary>
/// The "Your plan" block as this head lays it out, and the off-plan banner's hide control: that
/// every measurement is the shared one, that the fill says what the figure says, and that a
/// hidden banner stays gone.
///
/// <para>The trees are real and laid out with a real font, for the reason
/// <see cref="PanelShortScreenTests"/> gives. The window itself cannot be built in a test
/// process, so what it does with a hide is held structurally at the end of this file, the way
/// that class holds the window's measuring.</para>
/// </summary>
public class PanelPlanBlockTests
{
    private const string BannerState = "offplan-on";

    /// <summary>
    /// A work area the panel overflows even compacted and with its banner hidden, which the shared
    /// short work area no longer does now the panel has no tiles.
    /// </summary>
    private const double TooShortWorkAreaDip = 480;

    // ── the block ───────────────────────────────────────────────────────────────────

    [Fact]
    public void TheTitleIsSmallCapitalsLetterSpacedAndIndented()
    {
        var title = Words(Build("typical"), PanelText.PlanBlockTitle.ToUpperInvariant());
        var size = PanelMetrics.BlockTitleFontSize;

        Assert.Equal(size, title.FontSize);
        Assert.Equal(FontWeight.SemiBold, title.FontWeight);
        Assert.Equal(size * PanelMetrics.LetterSpacingEm, title.LetterSpacing, 6);
        Assert.Equal(size * PanelMetrics.LetterSpacedIndentEm, title.Margin.Left, 6);
        Assert.Equal(Hex("TextSecondary"), Colour(title.Foreground));
        Assert.Equal(PanelText.PlanBlockTitle, AutomationProperties.GetName(title));
    }

    /// <summary>The block sits flat: nothing behind it, and only its vertical padding around it.</summary>
    [Fact]
    public void TheBlockHasNoFillAndOnlyItsVerticalPadding()
    {
        var panel = Build("typical");
        var block = BlockOf(panel);

        Assert.Null(block.Background);
        Assert.Equal(new Thickness(0, PanelMetrics.BlockPaddingYDip, 0, PanelMetrics.BlockPaddingYDip), block.Margin);
    }

    /// <summary>
    /// The hairline closes the block directly, so the space above it is the block's padding and
    /// nothing else: added as its own section it would pick up the panel's spacing too.
    /// </summary>
    [Fact]
    public void TheHairlineFollowsTheBlockAtTheSoftStrength()
    {
        var panel = Build("typical");
        var host = Assert.IsType<StackPanel>(BlockOf(panel).GetVisualParent());
        var line = Assert.IsType<Border>(host.Children[^1]);

        Assert.Same(BlockOf(panel), host.Children[0]);
        Assert.Equal(1, line.Height);
        Assert.Equal(PanelMetrics.SoftLineOpacity, line.Opacity);
        Assert.Equal(Hex("PanelBorder"), Colour(line.Background));
    }

    [Fact]
    public void AMeterHasTheSharedTypeBarAndGaps()
    {
        var panel = Build("typical");
        var label = Words(panel, PanelText.SessionBarLabel);
        var meter = Assert.IsType<StackPanel>(label.GetVisualParent()!.GetVisualParent());
        var track = TracksOf(panel)[0];

        Assert.Equal(PanelMetrics.MeterLabelFontSize, label.FontSize);
        Assert.Equal(FontWeight.SemiBold, label.FontWeight);

        var value = meter.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text!.EndsWith(" used", StringComparison.Ordinal));
        Assert.Equal(PanelMetrics.MeterLabelFontSize, value.FontSize);
        Assert.Equal(Hex("TextSecondary"), Colour(value.Foreground));

        Assert.Equal(PanelMetrics.MeterBarHeightDip, track.Height);
        Assert.Equal(new CornerRadius(PanelMetrics.MeterBarRadiusDip), track.CornerRadius);

        var reset = Assert.IsType<TextBlock>(meter.Children[^1]);
        Assert.Equal(PanelMetrics.MeterResetFontSize, reset.FontSize);
        Assert.Equal(Hex("TextMuted"), Colour(reset.Foreground));

        Assert.Equal(PanelMetrics.MeterGapAboveDip, meter.Margin.Top);
        Assert.Equal(PanelMetrics.MeterGapBelowDip, meter.Margin.Bottom);
    }

    /// <summary>
    /// The gaps tighten by the density's own proportion, so the block keeps its shape on a short
    /// display rather than being a second design.
    /// </summary>
    [Fact]
    public void TheCompactDensityScalesTheGapsAndNotTheBar()
    {
        var panel = Build("typical", PanelDensity.Compact);
        var scale = PanelDensity.Compact.SpacingScale;
        var meter = Assert.IsType<StackPanel>(Words(panel, PanelText.SessionBarLabel).GetVisualParent()!.GetVisualParent());

        Assert.Equal(PanelMetrics.BlockPaddingYDip * scale, BlockOf(panel).Margin.Top, 6);
        Assert.Equal(PanelMetrics.MeterGapAboveDip * scale, meter.Margin.Top, 6);
        Assert.Equal(PanelMetrics.MeterBarHeightDip, TracksOf(panel)[0].Height);
    }

    /// <summary>
    /// The width of the fill is a claim about the figure, so it is a share of the track as drawn.
    /// A width worked out from the panel's would overrun the track at 100% and misstate every
    /// reading in the narrower column a capped panel's scroll bar leaves.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheFillIsTheFiguresShareOfTheTrackAsDrawn(bool capped)
    {
        var panel = Build("high-usage");
        if (capped)
        {
            panel.ScrollContent(true);
            panel.Height = 400;
        }

        PanelSampleRenderer.LayOut(panel, passes: 3);

        var session = TracksOf(panel)[0];
        var fill = FillOf(session);

        Assert.True(session.Bounds.Width > 300, $"the track came out {session.Bounds.Width:0.#} dip");
        Assert.Equal(session.Bounds.Width * 0.91, fill.Bounds.Width, 0);
    }

    [Fact]
    public void AFullMeterFillsItsTrackAndNoFurther()
    {
        var panel = Laid(BannerState);
        var session = TracksOf(panel)[0];

        Assert.Equal(session.Bounds.Width, FillOf(session).Bounds.Width, 3);
    }

    /// <summary>No figure draws the track alone: a zero-width fill would read as "no usage" (rule 6).</summary>
    [Fact]
    public void AnUnknownFigureDrawsTheTrackAlone()
    {
        var panel = Laid("no-data");

        Assert.All(TracksOf(panel), track => Assert.Null(track.Child));
    }

    /// <summary>
    /// A model's own limit is one more meter of the same shape, not a row of another kind.
    /// </summary>
    [Fact]
    public void AModelsOwnLimitIsOneMoreMeter()
    {
        Assert.Equal(TracksOf(Build("typical")).Count + 1, TracksOf(Build("model-limit")).Count);
    }

    /// <summary>
    /// The widest promo the panel is asked for: the chip keeps clear of the figure beside it, and
    /// is not trimmed to do so.
    /// </summary>
    [Fact]
    public void TheBoostChipStaysClearOfTheFigure()
    {
        var panel = Laid("boosted");
        var value = Words(panel, "100% used");
        var row = Assert.IsType<Grid>(value.GetVisualParent());
        var chip = Assert.Single(row.Children.OfType<Border>());

        Assert.True(chip.Bounds.Right <= value.Bounds.Left, $"chip ends {chip.Bounds.Right:0.#}, figure starts {value.Bounds.Left:0.#}");
        Assert.False(Trimmed(Assert.IsType<TextBlock>(chip.Child)), "the chip was trimmed");
    }

    // ── the off-plan row ────────────────────────────────────────────────────────────

    [Fact]
    public void TheOffPlanRowIsTheBlocksLastAndHasTheSharedType()
    {
        var panel = Laid(BannerState);
        var view = Plan(BannerState);
        var row = Assert.IsType<StackPanel>(RowOf(panel, view));

        Assert.Same(row, Assert.IsType<StackPanel>(BlockOf(panel).Children[^1]).Children[^1]);

        var line = Assert.IsType<Border>(row.Children[0]);
        Assert.Equal(PanelMetrics.SoftLineOpacity, line.Opacity);
        Assert.Equal(PanelMetrics.OffPlanPaddingAboveDip, row.Children[1].Margin.Top);

        var rowView = view.OffPlanRow!;
        Assert.Equal(PanelMetrics.MeterLabelFontSize, Words(panel, rowView.Title).FontSize);
        Assert.Equal(PanelMetrics.OffPlanCaptionFontSize, Words(panel, rowView.Caption).FontSize);
        Assert.Equal(Hex("TextMuted"), Colour(Words(panel, rowView.Caption).Foreground));

        var figure = Words(panel, rowView.Value);
        Assert.Equal(PanelMetrics.OffPlanFigureFontSize, figure.FontSize);
        Assert.Equal(FontWeight.SemiBold, figure.FontWeight);

        var caveat = Words(panel, rowView.Coverage);
        Assert.Equal(PanelMetrics.OffPlanCaveatFontSize, caveat.FontSize);
        Assert.Equal(Hex("WarnText"), Colour(caveat.Foreground));

        var chip = Assert.IsType<Border>(Words(panel, rowView.Badge).GetVisualParent());
        Assert.Equal(Hex("WarnBg"), Colour(chip.Background));
        Assert.Equal(Hex("WarnText"), Colour(Words(panel, rowView.Badge).Foreground));
    }

    /// <summary>
    /// The longer explanation is on the row's hover, and the whole row answers it: without a
    /// background only the glyphs would be hit-testable.
    /// </summary>
    [Fact]
    public void TheRowsHintIsItsTooltipOverTheWholeRow()
    {
        var panel = Build(BannerState);
        var view = Plan(BannerState);
        var row = Assert.IsType<StackPanel>(RowOf(panel, view));

        Assert.Equal(view.OffPlanRow!.Hint, ToolTip.GetTip(row));
        Assert.NotNull(row.Background);
    }

    [Fact]
    public void AnAccountNotOffPlanHasNoRow()
    {
        Assert.Null(Plan("model-limit").OffPlanRow);
        Assert.DoesNotContain(
            Build("model-limit").GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text == OffPlanText.ReportedSpendHeading);
    }

    // ── the banner's hide control ───────────────────────────────────────────────────

    [Fact]
    public void TheHideControlIsAClickableLabelledSquareAtTheBannersCorner()
    {
        var panel = Laid(BannerState);
        var hide = HideControl(panel);

        Assert.Equal(PanelMetrics.BannerHideSizeDip, hide.Width);
        Assert.Equal(PanelMetrics.BannerHideSizeDip, hide.Height);

        // Without a background the square is not hit-testable between the cross's strokes.
        Assert.NotNull(hide.Background);
        Assert.Equal(PanelText.HideBannerLabel, ToolTip.GetTip(hide));

        var mark = Assert.IsType<Canvas>(hide.Child);
        Assert.Equal(PanelMetrics.BannerHideOpacity, mark.Opacity);
        Assert.All(mark.Children, stroke => Assert.Equal(Hex("WarnText"), Colour(Assert.IsType<Line>(stroke).Stroke)));

        var card = hide.FindAncestorOfType<Border>()!;
        Assert.Equal(Hex("WarnBg"), Colour(card.Background));
        Assert.True(hide.Bounds.Right > card.Bounds.Width / 2, "the control is not on the banner's right");
    }

    /// <summary>A sentence of the banner stops short of the control rather than running under it.</summary>
    [Fact]
    public void TheBannersWordsKeepClearOfTheHideControl()
    {
        var panel = Laid(BannerState);
        var hide = HideControl(panel);
        var left = hide.TranslatePoint(default, panel)!.Value.X;

        foreach (var line in hide.GetVisualParent()!.GetVisualDescendants().OfType<TextBlock>())
        {
            var right = line.TranslatePoint(new Point(line.Bounds.Width, 0), panel)!.Value.X;
            Assert.True(right <= left, $"[{line.Text}] reaches {right:0.#}, the control starts at {left:0.#}");
        }
    }

    [Fact]
    public void PressingTheControlTakesTheBannerOutAtOnceAndSaysSo()
    {
        var panel = Laid(BannerState);
        var title = Plan(BannerState).Banner!.Title;
        var hides = 0;
        panel.BannerHidden += () => hides++;

        Press(HideControl(panel), panel, left: true);

        Assert.Equal(1, hides);
        Assert.DoesNotContain(panel.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == title);
    }

    [Fact]
    public void AnotherButtonLeavesTheBannerAlone()
    {
        var panel = Laid(BannerState);
        var hides = 0;
        panel.BannerHidden += () => hides++;

        Press(HideControl(panel), panel, left: false);

        Assert.Equal(0, hides);
        Assert.NotNull(HideControl(panel));
    }

    /// <summary>
    /// The choice passed in decides it, and the row is not part of what is hidden: it is the
    /// standing figure, and only the notice was dismissed.
    /// </summary>
    [Fact]
    public void AHiddenChoiceDrawsNoBannerAndKeepsTheRow()
    {
        var panel = Build("offplan-on-hidden");
        var view = Plan("offplan-on-hidden");

        Assert.Null(view.Banner);
        Assert.Empty(HideControls(panel));
        Assert.NotNull(RowOf(panel, view));
    }

    // ── nothing crosses the edge ────────────────────────────────────────────────────

    public static TheoryData<string, bool> EveryState()
    {
        var data = new TheoryData<string, bool>();
        foreach (var state in SampleStates.All)
        {
            data.Add(state.Name, false);
            data.Add(state.Name, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryState))]
    public void NothingInTheBlockCrossesThePanelOrIsTrimmed(string state, bool light)
    {
        var panel = Laid(state, light);
        var inner = panel.Bounds.Width - 2 * panel.BorderThickness.Left;
        var block = BlockOf(panel);

        var marks = block.GetVisualDescendants().OfType<Control>().Append(block);
        foreach (var banner in HideControls(panel).Select(h => h.GetVisualParent()!))
        {
            marks = marks.Concat(banner.GetVisualDescendants().OfType<Control>());
        }

        foreach (var mark in marks)
        {
            var left = mark.TranslatePoint(default, panel)!.Value.X;
            var right = left + mark.Bounds.Width;

            Assert.True(left >= panel.BorderThickness.Left, $"{Named(mark)} starts at {left:0.#} in {state}");
            Assert.True(right <= inner + panel.BorderThickness.Left, $"{Named(mark)} ends at {right:0.#} of {inner:0.#} in {state}");

            if (mark is TextBlock line)
            {
                Assert.False(Trimmed(line), $"[{line.Text}] was trimmed in {state}");
            }
        }
    }

    // ── after a hide, the window ────────────────────────────────────────────────────

    /// <summary>
    /// The content's half of the re-fit: from a panel that was compacted and capped, the state an
    /// open starts from, and on the short display the same decision an open comes to. The notice
    /// going does not make the panel fit there, so it must come back capped and scrolling rather
    /// than left to run off the screen.
    /// </summary>
    [Fact]
    public void ARefitAfterAHideStartsWhereAnOpenStartsAndStaysCappedOnAShortDisplay()
    {
        var state = SampleStates.All.Single(s => s.Name == BannerState);
        var panel = Build(BannerState, PanelDensity.Compact);
        panel.ScrollContent(true);
        panel.Height = TooShortWorkAreaDip;
        PanelSampleRenderer.LayOut(panel, passes: PanelSampleRenderer.CappedLayoutPasses);

        var hidden = state.View with { BannerHidden = true };
        void Populate() => panel.Populate(state.Snapshot, state.Stats, state.Account, state.Plan, state.Scope,
            SampleStates.Now, state.Boost, SampleStates.Zone, state.CachedUsage, hidden);

        panel.Height = double.NaN;
        panel.ReturnToNaturalLayout();

        Assert.False(panel.Scrolls);
        Assert.Equal(PanelDensity.Normal, panel.Density);

        Populate();
        var outcome = PanelFit.Fit(TooShortWorkAreaDip, panel.NaturalHeightDip, density =>
        {
            panel.Density = density;
            Populate();
        });

        Assert.True(outcome.Density.IsCompact);
        Assert.True(outcome.Fit.MustScroll, "the short display no longer caps this panel, so this proves nothing");
        Assert.Equal(TooShortWorkAreaDip, outcome.Fit.WindowHeightDip);
        Assert.Empty(HideControls(panel));
    }

    /// <summary>
    /// The window's half, structural, because it cannot be built here: a hide has to persist the
    /// choice and take the same path an open takes, or a panel at the cap would be left with a
    /// stale height or lose its scroll bar.
    /// </summary>
    [Fact]
    public void AHideGoesBackThroughTheShellAndThePathAnOpenUses()
    {
        var source = WithoutComments("src", "O-view.Linux", "Panel", "PanelWindow.cs");

        Assert.Contains("_content.BannerHidden += OnBannerHidden", source, StringComparison.Ordinal);

        var body = Regex.Match(source, @"void OnBannerHidden\(\)\s*\{(?<body>.*?)\n    \}", RegexOptions.Singleline).Groups["body"].Value;
        Assert.Contains("SetViewChoice", body, StringComparison.Ordinal);
        Assert.Contains("_content.ReturnToNaturalLayout()", body, StringComparison.Ordinal);
        Assert.Contains("_populate()", body, StringComparison.Ordinal);
        Assert.Contains("FitAndPlace()", body, StringComparison.Ordinal);

        var fitAndPlace = Regex.Match(source, @"void FitAndPlace\(\)\s*\{(?<body>.*?)\n    \}", RegexOptions.Singleline).Groups["body"].Value;
        Assert.Contains("FitToScreen()", fitAndPlace, StringComparison.Ordinal);
        Assert.Contains("Place(_placedHeightDip)", fitAndPlace, StringComparison.Ordinal);

        Assert.Equal(3, Regex.Matches(source, @"\bFitAndPlace\(\);").Count);
    }

    /// <summary>The view is read from the shell at every open, and given back to it, in this head's shell.</summary>
    [Fact]
    public void TheShellHoldsTheViewAndIsReadAtEveryOpen()
    {
        var source = WithoutComments("src", "O-view.Linux", "LinuxApp.cs");

        Assert.Contains("ViewChoice = shell.PanelView", source, StringComparison.Ordinal);
        Assert.Contains("SetViewChoice = shell.SetPanelView", source, StringComparison.Ordinal);
    }

    /// <summary>The rule that brings a hidden notice back is App's; this head must not write its own.</summary>
    [Fact]
    public void ThisHeadDoesNotDecideWhenAHiddenBannerReturns()
    {
        var head = Path.Combine(RepositoryRoot(), "src", "O-view.Linux");

        foreach (var file in Directory.GetFiles(head, "*.cs", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("OffPlanBannerHidden", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────

    private static PanelContent Build(string name, PanelDensity? density = null, bool light = false)
    {
        UseRealFonts();

        var state = SampleStates.All.Single(s => s.Name == name);
        var panel = new PanelContent(new LinuxPanelTheme(light)) { Density = density ?? PanelDensity.Normal };
        panel.Populate(state.Snapshot, state.Stats, state.Account, state.Plan, state.Scope,
            SampleStates.Now, state.Boost, SampleStates.Zone, state.CachedUsage, state.View);
        return panel;
    }

    private static PanelContent Laid(string name, bool light = false)
    {
        var panel = Build(name, light: light);
        PanelSampleRenderer.LayOut(panel);
        return panel;
    }

    /// <summary>The block's view, built exactly as the panel builds it, for the words to look for.</summary>
    private static PanelPlanBlockView Plan(string name)
    {
        var state = SampleStates.All.Single(s => s.Name == name);
        var offPlan = PanelSections.OffPlan(state.Stats, state.CachedUsage, SampleStates.Now, SampleStates.Zone);
        var meter = new PanelMeterView("", null, "", null, null, null, null, null, null);

        return PanelSections.PlanBlock(meter, meter, [], offPlan, state.Stats, state.CachedUsage, state.View);
    }

    private static TextBlock Words(Control panel, string text) =>
        panel.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == text);

    /// <summary>The block's padded stack: the title's parent.</summary>
    private static StackPanel BlockOf(Control panel) =>
        Assert.IsType<StackPanel>(Words(panel, PanelText.PlanBlockTitle.ToUpperInvariant()).GetVisualParent());

    /// <summary>The meters' tracks, top to bottom.</summary>
    private static List<Border> TracksOf(Control panel) =>
        BlockOf(panel).GetVisualDescendants().OfType<Border>()
            .Where(b => b.Height == PanelMetrics.MeterBarHeightDip)
            .ToList();

    private static Border FillOf(Border track) =>
        Assert.IsType<Border>(Assert.IsType<Grid>(track.Child).Children[0]);

    private static Control? RowOf(Control panel, PanelPlanBlockView plan) =>
        Words(panel, plan.OffPlanRow!.Title).FindAncestorOfType<StackPanel>() is { } heading
            ? heading.GetVisualAncestors().OfType<StackPanel>().First(p => ToolTip.GetTip(p) is not null)
            : null;

    private static List<Border> HideControls(Control panel) =>
        panel.GetVisualDescendants().OfType<Border>()
            .Where(b => AutomationProperties.GetName(b) == PanelText.HideBannerLabel)
            .ToList();

    private static Border HideControl(Control panel) => Assert.Single(HideControls(panel));

    private static void Press(Border control, Visual root, bool left)
    {
        var (button, kind) = left
            ? (RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed)
            : (RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed);

        control.RaiseEvent(new PointerPressedEventArgs(
            control,
            new Pointer(1, PointerType.Mouse, isPrimary: true),
            root,
            new Point(1, 1),
            timestamp: 0,
            new PointerPointProperties(button, kind),
            KeyModifiers.None));
    }

    private static bool Trimmed(TextBlock line) => line.TextLayout.TextLines.Any(l => l.HasCollapsed);

    private static string Named(Control mark) => mark is TextBlock line ? $"[{line.Text}]" : mark.GetType().Name;

    private static Color Hex(string key) => Color.Parse(PanelPalette.Get(key, false));

    private static Color Colour(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static string WithoutComments(params string[] path) =>
        Regex.Replace(File.ReadAllText(Path.Combine([RepositoryRoot(), .. path])), @"//[^\n]*", string.Empty);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "O-view.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }

    /// <summary>The font setup <see cref="PanelShortScreenTests"/> runs once per process, through its own lock.</summary>
    private static void UseRealFonts() =>
        typeof(PanelShortScreenTests)
            .GetMethod("UseRealFonts", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null);
}
