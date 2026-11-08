using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using OView.App.Rendering;
using OView.Presentation.Panel;
using static OView.Linux.Panel.PanelPrimitives;

namespace OView.Linux.Panel;

/// <summary>
/// The "On this machine" block: its title and the line under it, the 31-day chart, the table, the
/// caveats that apply, and the footer line.
///
/// <para>Every word and figure is read from the view models <see cref="PanelSections"/> built, the
/// ones the Windows panel reads, so the two cannot disagree about what a figure counts.</para>
///
/// <para>Like the plan block it is flat on the panel. Its gaps follow the density's
/// <see cref="PanelDensity.SpacingScale"/> and its chart the <see cref="PanelDensity.GraphScale"/>,
/// so a short display tightens this block by the proportion it tightens the rest.</para>
/// </summary>
internal sealed class PanelLocalBlock(LinuxPanelTheme theme)
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

    private readonly LinuxPanelTheme _theme = theme;
    private readonly PanelGraph _graph = new(theme);
    private readonly PanelTotalsTable _table = new(theme);

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

        var surfaces = Text(footer.Caveat, PanelMetrics.FooterFontSize, _theme["TextMuted"]);
        surfaces.Margin = new Thickness(0, bodyGap, 0, 0);
        block.Children.Add(surfaces);

        root.Children.Add(block);
    }
}
