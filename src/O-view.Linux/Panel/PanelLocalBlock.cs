using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using OView.App.Rendering;
using OView.Presentation.Panel;
using static OView.Linux.Panel.PanelPrimitives;

namespace OView.Linux.Panel;

/// <summary>
/// The "On this machine" block: its title and the line under it, the 31-day chart, the table, the
/// caveats that apply, and the footer line with the Output | Overall switch.
///
/// <para>Every word and figure is read from the view models <see cref="PanelSections"/> built, the
/// ones the Windows panel reads, so the two cannot disagree about what a figure counts. What
/// pressing the switch means is decided there too: a segment carries the whole view it gives, and
/// this class hands that to <paramref name="choose"/> to store and redraw.</para>
///
/// <para>Like the plan block it is flat on the panel. Its gaps follow the density's
/// <see cref="PanelDensity.SpacingScale"/> and its chart the <see cref="PanelDensity.GraphScale"/>,
/// so a short display tightens this block by the proportion it tightens the rest.</para>
/// </summary>
internal sealed class PanelLocalBlock(LinuxPanelTheme theme, Action<PanelViewChoice> choose)
{
    /// <summary>Space between the block's title and the line under it.</summary>
    private const double SubtitleGapDip = 1;

    /// <summary>Space from the title's line to the chart, and from the table to the footer.</summary>
    private const double BodyGapDip = 8;

    /// <summary>Space between the table and the caveat on its 31-day figures.</summary>
    private const double WindowCaveatGapDip = 4;

    /// <summary>Space above the note explaining a zero, and its size.</summary>
    private const double ScopeNoteGapDip = 6;

    private const double ScopeNoteFontSize = 11;

    /// <summary>Space between the footer's caveat and the switch.</summary>
    private const double SwitchGapDip = 10;

    /// <summary>The switch's inset, its rounding, and the gap between its segments.</summary>
    private const double SwitchPaddingDip = 2;

    private const double SwitchRadiusDip = 4;

    private const double SegmentGapDip = 2;

    private const double SegmentRadiusDip = 3;

    private const double SegmentPaddingXDip = 8;

    private const double SegmentPaddingYDip = 3;

    private readonly LinuxPanelTheme _theme = theme;
    private readonly PanelGraph _graph = new(theme);
    private readonly PanelTotalsTable _table = new(theme);

    /// <summary>The segment now on, for <see cref="FocusChosenSegment"/>.</summary>
    private RadioButton? _chosen;

    /// <param name="note">Why the figures are zero while the meters are not, or null.</param>
    public void AddTo(
        StackPanel root, PanelLocalTotalsView totals, PanelLocalFooterView footer, string? note, PanelDensity density)
    {
        var scale = density.SpacingScale;
        var padding = PanelMetrics.BlockPaddingYDip * scale;
        var bodyGap = BodyGapDip * scale;
        var tableGap = PanelMetrics.ChartToTableGapDip * scale;

        // The panel's own spacing already stands between the plan block's line and this block, so
        // it counts towards the padding above, or the line would not sit midway between the two.
        var block = new StackPanel { Margin = new Thickness(0, padding - root.Spacing, 0, padding) };

        var title = Caption(totals.Title, PanelMetrics.BlockTitleFontSize, _theme["TextSecondary"], FontWeight.SemiBold);
        block.Children.Add(title);

        var subtitle = Text(totals.Subtitle, PanelMetrics.BlockSubtitleFontSize, _theme["TextMuted"]);
        subtitle.Margin = new Thickness(0, SubtitleGapDip, 0, 0);
        block.Children.Add(subtitle);

        var chart = _graph.Chart(totals.Chart, density);
        if (chart is not null)
        {
            chart.Margin = new Thickness(0, bodyGap, 0, 0);
            block.Children.Add(chart);
        }

        var line = SoftLine(_theme);
        line.Margin = new Thickness(0, tableGap, 0, 0);
        block.Children.Add(line);

        var table = _table.Build(totals.Table, scale);
        table.Margin = new Thickness(0, tableGap, 0, 0);
        block.Children.Add(table);

        // Under the table and flush right, so it ends beneath the 31-day figures it qualifies.
        if (totals.WindowCaveat is { Length: > 0 } caveat)
        {
            var coverage = Text(caveat, PanelMetrics.OffPlanCaveatFontSize, _theme["WarnText"]);
            coverage.TextAlignment = TextAlignment.Right;
            coverage.Margin = new Thickness(0, WindowCaveatGapDip, 0, 0);
            block.Children.Add(coverage);
        }

        if (note is not null)
        {
            var explained = Text(note, ScopeNoteFontSize, _theme["TextSecondary"]);
            explained.Margin = new Thickness(0, ScopeNoteGapDip, 0, 0);
            block.Children.Add(explained);
        }

        block.Children.Add(FooterLine(footer, bodyGap));

        root.Children.Add(block);
    }

    /// <summary>
    /// Puts the keyboard back on the segment now on. Every change of measure rebuilds the panel,
    /// and the old segment takes the focus with it.
    /// </summary>
    public void FocusChosenSegment() => _chosen?.Focus();

    /// <summary>The caveat on the left, and on the right the switch.</summary>
    private Grid FooterLine(PanelLocalFooterView footer, double gapAbove)
    {
        var surfaces = Text(footer.Caveat, PanelMetrics.FooterFontSize, _theme["TextMuted"]);
        surfaces.VerticalAlignment = VerticalAlignment.Center;

        var measures = MeasureSwitch(footer.Options);
        measures.Margin = new Thickness(SwitchGapDip, 0, 0, 0);
        Grid.SetColumn(measures, 1);

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, gapAbove, 0, 0),
        };
        row.Children.Add(surfaces);
        row.Children.Add(measures);
        return row;
    }

    /// <summary>
    /// The segments on one strip. Radio buttons rather than the plain two the Bars/Breakdown switch
    /// uses, because the measures exclude each other and a screen reader should say which is on.
    /// The lit fill moves at once, with no slide: this head adds no motion.
    /// </summary>
    private Border MeasureSwitch(IReadOnlyList<PanelMeasureOption> options)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = SegmentGapDip };
        foreach (var option in options)
        {
            var segment = Segment(option);
            row.Children.Add(segment);

            if (option.Selected)
            {
                _chosen = segment;
            }
        }

        return new Border
        {
            Background = _theme["TileBg"],
            CornerRadius = new CornerRadius(SwitchRadiusDip),
            Padding = new Thickness(SwitchPaddingDip),
            VerticalAlignment = VerticalAlignment.Center,
            Child = row,
        };
    }

    private RadioButton Segment(PanelMeasureOption option)
    {
        var segment = new RadioButton
        {
            // The word a screen reader announces; the one on screen is drawn by the template.
            Content = option.Label,
            IsChecked = option.Selected,
            Template = SegmentTemplate(option.Label),
            CornerRadius = new CornerRadius(SegmentRadiusDip),
            Padding = new Thickness(SegmentPaddingXDip, SegmentPaddingYDip),

            // Explicit on both, the transparent one too: a control with no background is not
            // hit-testable between its letters, so a segment would answer only on the word.
            Background = option.Selected ? _theme["BarTrack"] : Brushes.Transparent,
            Foreground = _theme[option.Selected ? "TextPrimary" : "TextMuted"],

            // A cursor can only be made once an Avalonia application has started the windowing
            // platform, and the switch is built in a test process as well.
            Cursor = Application.Current is null ? null : new Cursor(StandardCursorType.Hand),
        };

        // A press on the segment already on is no change, and rebuilds nothing.
        segment.Click += (_, _) =>
        {
            if (!option.Selected)
            {
                choose(option.Choice);
            }
        };

        return segment;
    }

    /// <summary>
    /// What a segment is drawn as. The word is in the template for the reason the Bars/Breakdown
    /// switch's is: with no theme a button's content presenter stays empty offscreen.
    /// </summary>
    private static FuncControlTemplate<RadioButton> SegmentTemplate(string label) => new((_, _) => new Border
    {
        [!Border.BackgroundProperty] = new TemplateBinding(TemplatedControl.BackgroundProperty),
        [!Border.CornerRadiusProperty] = new TemplateBinding(TemplatedControl.CornerRadiusProperty),
        [!Border.PaddingProperty] = new TemplateBinding(TemplatedControl.PaddingProperty),
        Child = new TextBlock
        {
            Text = label,
            FontSize = PanelMetrics.FooterFontSize,
            [!TextBlock.ForegroundProperty] = new TemplateBinding(TemplatedControl.ForegroundProperty),
        },
    });
}
