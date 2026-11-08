using System.Buffers.Binary;
using System.Reflection;
using System.Text.RegularExpressions;

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using OView.App.Rendering;
using OView.Core.Models;
using OView.Linux.Panel;
using OView.Presentation.Panel;
using OView.Presentation.Samples;
using OView.Presentation.Text;

namespace OView.Linux.Tests;

/// <summary>
/// The Output | Overall switch and the Overall table on this head: that the switch is real, that
/// it rebuilds the block for the measure pressed and hands the choice back, and that the table is
/// section 3.3's at every measurement.
///
/// <para>Avalonia raises no pointer events without a windowing subsystem, so these raise the
/// routed event a click raises, as <see cref="PanelViewSwitchTests"/> does.</para>
/// </summary>
public class PanelMeasureSwitchTests
{
    private const string Typical = "typical";

    private const string Overall = "typical-overall";

    private const string Longest = "longest-figures";

    private const string BannerState = "offplan-on";

    // ── the switch ──────────────────────────────────────────────────────────────────

    /// <summary>Each segment wears its word from Presentation, in the footer's size, on the tile strip.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheSwitchIsTwoLabelledSegmentsOnTheTileStrip(bool light)
    {
        var panel = Laid(Typical, light);
        var (output, overall) = Segments(panel);
        var strip = Assert.IsType<Border>(output.GetVisualParent()!.GetVisualParent());
        var footer = PanelSections.LocalFooter(State(Typical).Scope, State(Typical).View);

        Assert.Equal(footer.Options[0].Label, Word(output).Text);
        Assert.Equal(footer.Options[1].Label, Word(overall).Text);
        Assert.Equal(footer.Options[0].Label, output.Content);
        Assert.Equal(PanelMetrics.FooterFontSize, Word(output).FontSize);
        Assert.Equal(Hex("TileBg", light), Colour(strip.Background));
        Assert.Same(strip.GetVisualParent(), Words(panel, footer.Caveat).GetVisualParent());
    }

    /// <summary>A fresh install opens on Output, and that segment alone is lit.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFreshInstallOpensOnOutputWithOnlyThatSegmentLit(bool light)
    {
        var (output, overall) = Segments(Laid(Typical, light));

        Assert.Equal(default, State(Typical).View);
        Assert.True(output.IsChecked);
        Assert.False(overall.IsChecked);
        Assert.Equal(Hex("BarTrack", light), Colour(output.Background));
        Assert.Equal(Hex("TextPrimary", light), Colour(Word(output).Foreground));
        Assert.Equal(Colors.Transparent, Colour(overall.Background));
        Assert.Equal(Hex("TextMuted", light), Colour(Word(overall).Foreground));
    }

    /// <summary>
    /// A background on each segment, the transparent one too, and reaching the template's fill, so
    /// a press between the letters lands. A raised event cannot show this: it skips the hit test
    /// the missing brush would fail.
    /// </summary>
    [Theory]
    [InlineData(Typical)]
    [InlineData(Overall)]
    public void EverySegmentHasAnExplicitBackgroundOnItsFill(string state)
    {
        var (output, overall) = Segments(Laid(state, false));

        foreach (var segment in new[] { output, overall })
        {
            Assert.NotNull(segment.Background);

            var fill = Assert.IsType<Border>(Assert.Single(segment.GetVisualChildren()));
            Assert.NotNull(fill.Background);
            Assert.Equal(Colour(segment.Background), Colour(fill.Background));
        }
    }

    [Fact]
    public void EachSegmentIsReachableFromTheKeyboard()
    {
        var (output, overall) = Segments(Laid(Typical, false));

        Assert.True(output.Focusable);
        Assert.True(overall.Focusable);
    }

    // ── pressing it ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole round: the press hands back the view, keeping the rest of what the reader chose,
    /// and a populate from that view shows Overall's chart, table and lit segment in one step.
    /// </summary>
    [Fact]
    public void APressHandsTheChoiceBackAndARebuildShowsThatMeasure()
    {
        var state = State(Typical);
        var kept = state.View with { BannerHidden = true };
        var panel = Laid(state, kept);
        PanelViewChoice? chosen = null;
        panel.ViewChosen += view => chosen = view;

        var (_, overallSegment) = Segments(panel);
        overallSegment.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(kept with { Measure = PanelMeasure.Overall }, chosen);

        Populate(panel, state, chosen!.Value);
        PanelSampleRenderer.LayOut(panel);

        var overallView = PanelSections.LocalTotals(state.Stats, state.Snapshot, SampleStates.Zone, chosen.Value);
        var table = TableOf(panel);
        var (output, overall) = Segments(panel);

        Assert.Equal(overallView.Subtitle, Words(panel, overallView.Subtitle).Text);
        Assert.Equal(5, table.ColumnDefinitions.Count);
        Assert.Equal(overallView.Table.Rows.Count + 1, table.RowDefinitions.Count);
        Assert.Equal(overallView.Table.Rows[0].Cells[0], TextIn(table, 1, 1));
        Assert.Equal(
            Tips(overallView.Chart),
            PanelGraphLayoutTests.BarsOf(PanelGraphLayoutTests.ChartOf(panel)).Select(b => (string)ToolTip.GetTip(b)!));
        Assert.False(output.IsChecked);
        Assert.True(overall.IsChecked);
        Assert.Equal(Hex("BarTrack", false), Colour(overall.Background));
    }

    /// <summary>And back again, to the Output table, as often as the reader likes.</summary>
    [Fact]
    public void APressOnOutputComesBackToTheOutputTable()
    {
        var state = State(Overall);
        var panel = Laid(state, state.View);
        PanelViewChoice? chosen = null;
        panel.ViewChosen += view => chosen = view;

        Segments(panel).Output.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(state.View with { Measure = PanelMeasure.Output }, chosen);

        Populate(panel, state, chosen!.Value);
        PanelSampleRenderer.LayOut(panel);

        Assert.Equal(3, TableOf(panel).ColumnDefinitions.Count);
    }

    /// <summary>A press on the segment already on changes nothing, so it rebuilds nothing.</summary>
    [Fact]
    public void APressOnTheSegmentAlreadyOnHandsNothingBack()
    {
        var panel = Laid(Typical, false);
        var fired = false;
        panel.ViewChosen += _ => fired = true;

        Segments(panel).Output.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.False(fired);
    }

    /// <summary>
    /// A capped panel keeps its foot where it was: the switch is at the foot, so a press read as a
    /// fresh open would scroll the control the reader is using out of sight.
    /// </summary>
    [Fact]
    public void ACappedPanelBringsTheFootBackAfterTheRebuild()
    {
        var state = State(BannerState);
        var panel = Capped(state, state.View);
        var hidden = panel.Viewport.Extent.Height - panel.Viewport.Bounds.Height;

        panel.Viewport.Offset = panel.Viewport.Offset.WithY(hidden);
        Assert.Equal(0, panel.ScrollFromFoot, 1);

        panel.ReturnToNaturalLayout();
        panel.Height = double.NaN;
        var overall = state.View with { Measure = PanelMeasure.Overall };
        panel.Density = PanelDensity.Compact;
        Populate(panel, state, overall);

        var height = PanelFit.Resolve(panel.NaturalHeightDip(), PanelFitHeight).WindowHeightDip;
        panel.ScrollContent(true);
        panel.Height = height;
        PanelSampleRenderer.LayOut(panel, PanelSampleRenderer.CappedLayoutPasses);

        panel.ScrollToFromFoot(0);
        PanelSampleRenderer.LayOut(panel, PanelSampleRenderer.CappedLayoutPasses);

        var segment = Segments(panel).Output;
        var top = segment.TranslatePoint(default, panel.Viewport)!.Value.Y;
        Assert.InRange(top, 0, panel.Viewport.Bounds.Height - segment.Bounds.Height);
    }

    /// <summary>
    /// The window's half, structural, because it cannot be built here: a press goes through the
    /// shell and takes the path an open takes, and puts the reader back once the new layout has
    /// run, because the offset is clamped against the old content's extent until then.
    /// </summary>
    [Fact]
    public void APressGoesBackThroughTheShellAndThePathAnOpenUses()
    {
        var source = WindowSourceWithoutComments();

        Assert.Contains("_content.ViewChosen += OnViewChosen", source, StringComparison.Ordinal);

        var body = Regex.Match(source, @"void OnViewChosen\(PanelViewChoice next\)\s*\{(?<body>.*?)\n    \}", RegexOptions.Singleline).Groups["body"].Value;
        Assert.Contains("SetViewChoice", body, StringComparison.Ordinal);
        Assert.Contains("_content.ReturnToNaturalLayout()", body, StringComparison.Ordinal);
        Assert.Contains("_populate()", body, StringComparison.Ordinal);
        Assert.Contains("FitAndPlace()", body, StringComparison.Ordinal);

        var fitted = body.IndexOf("FitAndPlace()", StringComparison.Ordinal);
        var laidOut = body.IndexOf("UpdateLayout()", StringComparison.Ordinal);
        var restored = body.IndexOf("_content.ScrollToFromFoot", StringComparison.Ordinal);

        Assert.True(fitted >= 0 && fitted < laidOut && laidOut < restored, "the scroll has to come back after the new layout");
        Assert.Contains("_content.FocusChosenSegment()", body, StringComparison.Ordinal);
    }

    // ── the Overall table ───────────────────────────────────────────────────────────

    /// <summary>Four data columns at the shared width, the labels taking what they leave.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheOverallColumnsAreTheSharedWidths(bool compact)
    {
        var table = TableOf(Laid(Overall, false, compact ? PanelDensity.Compact : PanelDensity.Normal));

        Assert.Equal(5, table.ColumnDefinitions.Count);
        foreach (var column in Enumerable.Range(1, 4))
        {
            Assert.Equal(PanelMetrics.OverallColumnDip, table.ColumnDefinitions[column].ActualWidth);
        }

        Assert.Equal(table.Bounds.Width - 4 * PanelMetrics.OverallColumnDip, table.ColumnDefinitions[0].ActualWidth);
    }

    /// <summary>Every kind row wears its chip from the token ramp, before its label.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryKindRowHasItsChipInTheRampsColour(bool light)
    {
        var table = TableOf(Laid(Overall, light));
        var view = Totals(Overall).Table;
        var kinds = view.Rows.Where(r => r.Kind is not null).ToList();

        Assert.Equal(4, kinds.Count);

        foreach (var row in kinds)
        {
            var label = Words(table, row.Label);
            var line = Assert.IsType<StackPanel>(label.GetVisualParent());
            var chip = Assert.IsType<Border>(line.Children[0]);

            Assert.Same(label, line.Children[1]);
            Assert.Equal(new Size(8, 8), chip.Bounds.Size);
            Assert.Equal(new CornerRadius(2), chip.CornerRadius);
            Assert.Equal(6, chip.Margin.Right);
            Assert.Equal(Hex(TokenBarGeometry.PaletteKey(row.Kind!.Value), light), Colour(chip.Background));
        }
    }

    /// <summary>The Total row is the bold one, and has no chip.</summary>
    [Fact]
    public void TheTotalRowIsBoldAndHasNoChip()
    {
        var table = TableOf(Laid(Overall, false));
        var total = Totals(Overall).Table.Rows.Single(r => r.RowKind == PanelLocalRowKind.Total);
        var label = Words(table, total.Label);

        Assert.Equal(FontWeight.SemiBold, label.FontWeight);
        Assert.Equal(Hex("TextPrimary", false), Colour(label.Foreground));
        Assert.Same(table, label.GetVisualParent());
    }

    /// <summary>A share is the smaller, unbolded secondary figure, and the figure beside it is not.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharesAreTheSmallerSecondaryFigures(bool light)
    {
        var table = TableOf(Laid(Overall, light));

        foreach (var column in new[] { 2, 4 })
        {
            var share = Cell(table, 1, column);
            Assert.Equal(PanelMetrics.TableShareFontSize, share.FontSize);
            Assert.Equal(FontWeight.Normal, share.FontWeight);
            Assert.Equal(Hex("TextSecondary", light), Colour(share.Foreground));
        }

        foreach (var column in new[] { 1, 3 })
        {
            var figure = Cell(table, 1, column);
            Assert.Equal(PanelMetrics.TableFigureFontSize, figure.FontSize);
            Assert.Equal(FontWeight.SemiBold, figure.FontWeight);
        }
    }

    /// <summary>A pill per window, centred across the pair of columns it belongs to, at section 3.3's measurements.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EachEstimateSitsInAPillAcrossItsPairOfColumns(bool light)
    {
        var table = TableOf(Laid(Overall, light));
        var value = Totals(Overall).Table.Rows[^1];
        var row = table.RowDefinitions.Count - 1;
        var pills = Pills(table);

        Assert.Equal(2, value.CellSpan);
        Assert.Equal(value.Cells, pills.Select(p => ((TextBlock)p.Child!).Text));
        Assert.Equal([1, 3], pills.Select(Grid.GetColumn));

        foreach (var pill in pills)
        {
            Assert.Equal(row, Grid.GetRow(pill));
            Assert.Equal(2, Grid.GetColumnSpan(pill));
            Assert.Equal(Hex("TileBg", light), Colour(pill.Background));
            Assert.Equal(new Thickness(PanelMetrics.PillBorderDip), pill.BorderThickness);
            Assert.Equal(new CornerRadius(PanelMetrics.PillRadiusDip), pill.CornerRadius);
            Assert.Equal(new Thickness(PanelMetrics.PillPaddingXDip, PanelMetrics.PillPaddingYDip), pill.Padding);
            Assert.Equal(1 + PanelMetrics.PillGapAboveDip, pill.Margin.Top);
            Assert.Equal(PanelMetrics.PillGapBelowDip, pill.Margin.Bottom);

            // The soft border is on the brush: an opacity on the element would fade the figure with it.
            var border = Assert.IsAssignableFrom<ISolidColorBrush>(pill.BorderBrush);
            Assert.Equal(Hex("PanelBorder", light), border.Color);
            Assert.Equal(PanelMetrics.SoftLineOpacity, border.Opacity);
            Assert.Equal(1, pill.Opacity);

            var pairLeft = table.ColumnDefinitions[0].ActualWidth + (Grid.GetColumn(pill) - 1) * PanelMetrics.OverallColumnDip;
            Assert.Equal(pairLeft + PanelMetrics.OverallColumnDip, pill.Bounds.Center.X, 0.51);
        }
    }

    /// <summary>A pill is a pair wide, so the line inside the pair stops at the row above it.</summary>
    [Fact]
    public void OnlyTheLinesAtTheLeftOfAPairRunDownToTheValueRow()
    {
        var table = TableOf(Laid(Overall, false));
        var lines = table.Children.OfType<Border>().Where(b => Grid.GetRowSpan(b) > 1).ToList();

        Assert.Equal([1, 2, 3, 4], lines.Select(Grid.GetColumn));
        Assert.Equal(
            [table.RowDefinitions.Count, table.RowDefinitions.Count - 1, table.RowDefinitions.Count, table.RowDefinitions.Count - 1],
            lines.Select(Grid.GetRowSpan));
    }

    /// <summary>
    /// The longest figures in both themes at both densities: nothing crosses its cell, and no
    /// figure wraps, and the wider Linux font is the reason it is worth asking.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TheLongestFiguresFitTheOverallTableOnOneLine(bool light, bool compact)
    {
        var table = TableOf(Laid(Longest, light, compact ? PanelDensity.Compact : PanelDensity.Normal));

        foreach (var text in table.GetVisualDescendants().OfType<TextBlock>())
        {
            Assert.False(text.TextLayout.TextLines.Any(l => l.HasCollapsed), $"[{text.Text}] was trimmed");
            Assert.Single(text.TextLayout.TextLines);

            var left = text.TranslatePoint(default, table)!.Value.X;
            Assert.True(left >= -0.5, $"[{text.Text}] starts at {left:0.#}");
            Assert.True(left + text.Bounds.Width <= table.Bounds.Width + 0.5, $"[{text.Text}] ends at {left + text.Bounds.Width:0.#}");
        }

        foreach (var label in Totals(Longest).Table.Rows.Where(r => r.Kind is not null).Select(r => Words(table, r.Label)))
        {
            var labelRight = label.TranslatePoint(default, table)!.Value.X + label.Bounds.Width;
            Assert.True(labelRight <= table.ColumnDefinitions[0].ActualWidth, $"[{label.Text}] runs into the figures");
        }
    }

    /// <summary>
    /// The short render uses the taller measure, as Windows' does, so its picture shows a panel
    /// that scrolls.
    ///
    /// <para><b>The picture is read from the committed golden, not measured here,</b> because this
    /// panel's height depends on the font: it overflows 712 where the goldens are rendered and
    /// fits inside 712 with Ubuntu's. A panel the fit has capped is exactly as tall as the work
    /// area, so the image's height says which it was. That Overall is the taller measure holds in
    /// any font, so that half is measured live.</para>
    /// </summary>
    [Fact]
    public void TheShortSampleIsInOverallAndItsPictureIsCappedAtTheWorkArea()
    {
        var sample = PanelSampleRenderer.ShortSample;
        var overall = Build(sample, sample.View, PanelDensity.Compact).NaturalHeightDip();
        var output = Build(sample, sample.View with { Measure = PanelMeasure.Output }, PanelDensity.Compact).NaturalHeightDip();

        Assert.Equal(PanelMeasure.Overall, sample.View.Measure);
        Assert.True(
            PanelFit.Resolve(overall, output).MustScroll,
            $"Overall measures {overall:0} against Output's {output:0}, so Overall is no longer what takes the panel past the work area");

        foreach (var theme in new[] { "dark", "light" })
        {
            Assert.Equal(SampleStates.ShortWorkAreaDip, GoldenHeight($"panel-{sample.Name}-short-{theme}.png"));
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────

    private const double PanelFitHeight = 544;

    private static SampleState State(string name) => SampleStates.All.Single(s => s.Name == name);

    private static PanelContent Build(SampleState state, PanelViewChoice view, PanelDensity? density = null, bool light = false)
    {
        UseRealFonts();

        var panel = new PanelContent(new LinuxPanelTheme(light)) { Density = density ?? PanelDensity.Normal };
        Populate(panel, state, view);
        return panel;
    }

    private static void Populate(PanelContent panel, SampleState state, PanelViewChoice view) =>
        panel.Populate(state.Snapshot, state.Stats, state.Account, state.Plan, state.Scope,
            SampleStates.Now, state.Boost, SampleStates.Zone, state.CachedUsage, view);

    private static PanelContent Laid(string name, bool light, PanelDensity? density = null)
    {
        var state = State(name);
        var panel = Build(state, state.View, density, light);
        PanelSampleRenderer.LayOut(panel);
        return panel;
    }

    private static PanelContent Laid(SampleState state, PanelViewChoice view)
    {
        var panel = Build(state, view);
        PanelSampleRenderer.LayOut(panel);
        return panel;
    }

    private static PanelContent Capped(SampleState state, PanelViewChoice view)
    {
        var panel = Build(state, view, PanelDensity.Compact);
        var fit = PanelFit.Resolve(panel.NaturalHeightDip(), PanelFitHeight);
        Assert.True(fit.MustScroll);

        panel.ScrollContent(true);
        panel.Height = fit.WindowHeightDip;
        PanelSampleRenderer.LayOut(panel, PanelSampleRenderer.CappedLayoutPasses);
        return panel;
    }

    private static PanelLocalTotalsView Totals(string name) =>
        PanelSections.LocalTotals(State(name).Stats, State(name).Snapshot, SampleStates.Zone, State(name).View);

    private static (RadioButton Output, RadioButton Overall) Segments(Control panel)
    {
        var segments = panel.GetVisualDescendants().OfType<RadioButton>().ToList();
        Assert.Equal(2, segments.Count);
        return (segments[0], segments[1]);
    }

    private static TextBlock Word(RadioButton segment) => Assert.Single(segment.GetVisualDescendants().OfType<TextBlock>());

    private static Grid TableOf(Control panel) =>
        panel.GetVisualDescendants().OfType<Grid>()
            .Single(g => g.RowDefinitions.Count > 0 && g.ColumnDefinitions.Count >= 3 && g.ColumnDefinitions[1].Width.IsAbsolute);

    private static TextBlock Cell(Grid table, int row, int column) =>
        table.Children.OfType<TextBlock>().Single(t => Grid.GetRow(t) == row && Grid.GetColumn(t) == column);

    private static string? TextIn(Grid table, int row, int column) => Cell(table, row, column).Text;

    private static List<Border> Pills(Grid table) => table.Children.OfType<Border>().Where(b => b.Child is TextBlock).ToList();

    private static TextBlock Words(Control root, string text) =>
        root.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == text);

    private static List<string> Tips(PanelLocalChartView chart) =>
        chart.Columns.Where(c => c.HasBar).Select(c => $"{c.CardFigure} · {c.CardCaption}").ToList();

    private static Color Hex(string key, bool light) => Color.Parse(PanelPalette.Get(key, light));

    private static Color Colour(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static void UseRealFonts() =>
        typeof(PanelShortScreenTests)
            .GetMethod("UseRealFonts", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null);

    /// <summary>A committed image's height, from the PNG header: four big-endian bytes at offset 20.</summary>
    private static int GoldenHeight(string file)
    {
        var root = (string)typeof(PanelShortScreenTests)
            .GetMethod("RepositoryRoot", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null)!;

        return BinaryPrimitives.ReadInt32BigEndian(
            File.ReadAllBytes(Path.Combine(root, "tests", "golden", "linux", file)).AsSpan(20, 4));
    }

    private static string WindowSourceWithoutComments() =>
        (string)typeof(PanelShortScreenTests)
            .GetMethod("WindowSourceWithoutComments", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null)!;
}
