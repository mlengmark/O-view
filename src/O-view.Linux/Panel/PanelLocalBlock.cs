using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using OView.App.Rendering;
using OView.Core.Models;
using OView.Presentation.Panel;
using OView.Presentation.Text;
using static OView.Linux.Panel.PanelPrimitives;

namespace OView.Linux.Panel;

/// <summary>
/// The "On this machine" block: its title and the line under it with the flip tag, the face the
/// reader chose (the 31-day chart and table, or <see cref="PanelModelFace"/>), the caveats that
/// apply, and the footer line with the Output | Overall switch.
///
/// <para>Every word and figure is read from the view models <see cref="PanelSections"/> built, the
/// ones the Windows panel reads, so the two cannot disagree about what a figure counts. What
/// pressing the switch means is decided there too: a segment carries the whole view it gives, and
/// this class hands that to <paramref name="choose"/> to store and redraw. The flip tag changes
/// the face of that same view.</para>
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
    private readonly PanelModelFace _modelFace = new(theme);

    /// <summary>The segment now on, for <see cref="FocusChosenSegment"/>.</summary>
    private RadioButton? _chosen;

    private Button? _tag;

    /// <summary>Whether the last press was the tag's rather than a segment's. Survives the rebuild that press causes.</summary>
    private bool _tagPressed;

    /// <summary>The flip tag on screen when it was the last thing pressed, otherwise null.</summary>
    public Control? PressedTag => _tagPressed ? _tag : null;

    /// <param name="models">The model face, or null while the totals face is the one chosen.</param>
    /// <param name="note">Why the figures are zero while the meters are not, or null.</param>
    public void AddTo(
        StackPanel root, PanelLocalTotalsView totals, PanelModelFaceView? models, PanelLocalFooterView footer,
        string? note, PanelDensity density)
    {
        var scale = density.SpacingScale;
        var padding = PanelMetrics.BlockPaddingYDip * scale;

        // The panel's own spacing already stands between the plan block's line and this block, so
        // it counts towards the padding above, or the line would not sit midway between the two.
        var block = new StackPanel { Margin = new Thickness(0, padding - root.Spacing, 0, padding) };

        block.Children.Add(Head(totals.Title, models?.Subtitle ?? totals.Subtitle, footer, models is not null));

        if (models is not null)
        {
            _modelFace.AddTo(block, models, scale);
        }
        else
        {
            AddTotalsFace(block, totals, density);
        }

        AddFoot(block, models?.WindowCaveat ?? totals.WindowCaveat, note, footer, BodyGapDip * scale);

        root.Children.Add(block);
    }

    private void AddTotalsFace(StackPanel block, PanelLocalTotalsView totals, PanelDensity density)
    {
        var scale = density.SpacingScale;
        var bodyGap = BodyGapDip * scale;
        var tableGap = PanelMetrics.ChartToTableGapDip * scale;

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
    }

    /// <summary>What stands under either face: the caveats that apply, and the footer line.</summary>
    private void AddFoot(StackPanel block, string windowCaveat, string? note, PanelLocalFooterView footer, double bodyGap)
    {
        // Under the table and flush right, so it ends beneath the 31-day figures it qualifies.
        if (windowCaveat is { Length: > 0 } caveat)
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
    }

    /// <summary>
    /// Puts the keyboard back on what was pressed, the segment now on or the flip tag. Every press
    /// rebuilds the panel, and the old control takes the focus with it.
    /// </summary>
    public void FocusChosenSegment() => (PressedTag ?? (Control?)_chosen)?.Focus();

    /// <summary>The title and the line under it, with the flip tag beside them at the block's top right.</summary>
    private Grid Head(string titleText, string subtitleText, PanelLocalFooterView footer, bool showingModels)
    {
        var words = new StackPanel();
        words.Children.Add(Caption(titleText, PanelMetrics.BlockTitleFontSize, _theme["TextSecondary"], FontWeight.SemiBold));

        var subtitle = Text(subtitleText, PanelMetrics.BlockSubtitleFontSize, _theme["TextMuted"]);
        subtitle.Margin = new Thickness(0, SubtitleGapDip, 0, 0);
        words.Children.Add(subtitle);

        _tag = FlipTag(footer, showingModels);
        Grid.SetColumn(_tag, 1);

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(words);
        head.Children.Add(_tag);
        return head;
    }

    /// <summary>
    /// The tag that turns the block over: the view now chosen with the other face, stored and
    /// rebuilt exactly as a press on the switch is. The face changes at once: this head adds no motion.
    /// </summary>
    private Button FlipTag(PanelLocalFooterView footer, bool showingModels)
    {
        var current = footer.Options.First(o => o.Selected).Choice;
        var label = TokenText.FlipTagLabel(showingModels ? PanelFace.Models : PanelFace.Totals);

        var tag = new Button
        {
            // The word a screen reader announces; the one on screen is drawn by the template.
            Content = label,
            Template = FlipTagTemplate(label),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = Application.Current is null ? null : new Cursor(StandardCursorType.Hand),
        };

        tag.Click += (_, _) =>
        {
            _tagPressed = true;
            choose(current with { Face = showingModels ? PanelFace.Totals : PanelFace.Models });
        };

        return tag;
    }

    /// <summary>The tag's word and the two-arrow mark on its pill, the Windows panel's drawing, in the template for the reason a segment's word is.</summary>
    private FuncControlTemplate<Button> FlipTagTemplate(string label) => new((_, _) =>
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = PanelMetrics.FlipTagFontSize,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _theme["TextMuted"],
        });
        row.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M0,2.5 L8,2.5 M5.6,0.4 L8,2.5 L5.6,4.6 M8,7.5 L0,7.5 M2.4,5.4 L0,7.5 L2.4,9.6"),
            StrokeThickness = 1.3,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Opacity = 0.8,
            Margin = new Thickness(5, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Stroke = _theme["TextMuted"],
        });

        return new Border { Background = _theme["TileBg"], CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1), Child = row };
    });

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
                _tagPressed = false;
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
