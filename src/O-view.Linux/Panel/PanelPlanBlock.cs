using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OView.App.Rendering;
using OView.Presentation.Panel;
using static OView.Linux.Panel.PanelPrimitives;

namespace OView.Linux.Panel;

/// <summary>
/// The panel's first block: its title, its meters, the off-plan row that closes them, and the
/// one soft line that separates it from what follows.
///
/// <para><b>Flat on the panel, on purpose.</b> The block has no fill and no side padding: what
/// separates it from its neighbour is space and that one line. Every figure is read from
/// <see cref="PanelMetrics"/>, the same ones the Windows panel reads, so the two cannot drift
/// apart.</para>
///
/// <para>Every gap follows the density's <see cref="PanelDensity.SpacingScale"/> rather than a
/// compact figure of its own, so the block tightens by the proportion the rest of the panel
/// does.</para>
/// </summary>
internal sealed class PanelPlanBlock(LinuxPanelTheme theme, PanelOffPlan offPlan)
{
    /// <summary>Space between the block's title and its first meter's own gap.</summary>
    private const double TitleGapDip = 4;

    /// <summary>Space between a meter's name and its bar.</summary>
    private const double BarGapAboveDip = 5;

    /// <summary>Space between a meter's bar and its reset line.</summary>
    private const double BarGapBelowDip = 4;

    private readonly LinuxPanelTheme _theme = theme;
    private readonly PanelOffPlan _offPlan = offPlan;

    public void AddTo(StackPanel root, PanelPlanBlockView plan, PanelDensity density)
    {
        var scale = density.SpacingScale;
        var padding = PanelMetrics.BlockPaddingYDip * scale;

        var meters = new StackPanel { Margin = new Thickness(0, TitleGapDip, 0, 0) };
        foreach (var meter in plan.Meters)
        {
            meters.Children.Add(Meter(meter, scale));
        }

        if (plan.OffPlanRow is { } row)
        {
            meters.Children.Add(_offPlan.Row(row, scale));
        }

        var block = new StackPanel { Margin = new Thickness(0, padding, 0, padding) };
        block.Children.Add(Title(plan.Title));
        block.Children.Add(meters);

        // The line travels with the block: as its own child of the panel it would be held off
        // the block's padding by the panel's spacing as well.
        var host = new StackPanel();
        host.Children.Add(block);
        host.Children.Add(SoftLine(_theme));
        root.Children.Add(host);
    }

    /// <summary>
    /// The block's title, in capitals and letter-spaced. Spacing adds room after every letter
    /// including the last, so the first letter is indented by the same amount for the caption to
    /// sit centred in whatever holds it. A screen reader is given the words as written, which
    /// capitals would otherwise have it spell out.
    /// </summary>
    private TextBlock Title(string words)
    {
        var size = PanelMetrics.BlockTitleFontSize;
        var title = Text(words.ToUpperInvariant(), size, _theme["TextSecondary"], FontWeight.SemiBold);
        title.LetterSpacing = size * PanelMetrics.LetterSpacingEm;
        title.Margin = new Thickness(size * PanelMetrics.LetterSpacedIndentEm, 0, 0, 0);
        AutomationProperties.SetName(title, words);
        return title;
    }

    /// <summary>
    /// One usage meter. No fill percentage draws the track alone, never a fabricated fill, and
    /// never a zero that would read as "no usage" when the truth is "not known" (rule 6).
    ///
    /// <para>The reset line is the one with the action spliced in: this head's rows carry a
    /// single line of text and a compositor may never deliver hover at all, so the step that
    /// ends an unknown weekly reset rides on the row or never appears
    /// (<see cref="PanelMeterView.ResetLineWithAction"/>).</para>
    /// </summary>
    private StackPanel Meter(PanelMeterView meter, double scale)
    {
        var block = new StackPanel
        {
            Margin = new Thickness(
                0, PanelMetrics.MeterGapAboveDip * scale, 0, PanelMetrics.MeterGapBelowDip * scale),
        };

        block.Children.Add(Heading(meter));
        block.Children.Add(Bar(meter));
        if (meter.ResetLineWithAction is { Length: > 0 } resetLine)
        {
            block.Children.Add(Text(resetLine, PanelMetrics.MeterResetFontSize, _theme["TextMuted"]));
        }

        return block;
    }

    private Grid Heading(PanelMeterView meter)
    {
        var label = Text(meter.Label, PanelMetrics.MeterLabelFontSize, _theme["TextPrimary"], FontWeight.SemiBold);
        label.VerticalAlignment = VerticalAlignment.Center;

        var value = Text(meter.Value, PanelMetrics.MeterLabelFontSize, _theme["TextSecondary"]);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        value.VerticalAlignment = VerticalAlignment.Center;

        if (meter.Chip is not { } chip)
        {
            // Straight in the star column, so a long model name, which is whatever Claude Code
            // cached, wraps here rather than running across the figure.
            label.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(value, 1);

            var plain = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            plain.Children.Add(label);
            plain.Children.Add(value);
            return plain;
        }

        // Three columns, not an overlay: the label and the figure both keep their space whatever
        // the chip says, and a chip too long for what is left wraps in its own column rather
        // than running under the figure. It wraps where the Windows chip trims because this
        // head's font is whatever the desktop provides, and the widest promo does not fit one
        // line of Ubuntu's. The tier badge's colour pair, so it reads as the same class of
        // object, a factual label about the account and not a warning.
        var badge = new Border
        {
            Background = _theme["BadgeBg"],
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1),
            Margin = new Thickness(8, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = Text(chip.Text, 10, _theme["BadgeText"]),
        };

        Grid.SetColumn(badge, 1);
        Grid.SetColumn(value, 2);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        row.Children.Add(label);
        row.Children.Add(badge);
        row.Children.Add(value);
        return row;
    }

    /// <summary>
    /// The bar. The fill is a share of the track's own width, in star columns, so it stays true
    /// in the narrower column a capped panel's scroll bar leaves and needs no second layout pass
    /// to find out how wide the track came out.
    /// </summary>
    private Border Bar(PanelMeterView meter)
    {
        var track = new Border
        {
            Background = _theme["BarTrack"],
            CornerRadius = new CornerRadius(PanelMetrics.MeterBarRadiusDip),
            Height = PanelMetrics.MeterBarHeightDip,
            Margin = new Thickness(0, BarGapAboveDip, 0, BarGapBelowDip),
        };

        if (meter.FillPercent is { } percent and > 0)
        {
            var columns = new Grid();

            // Never narrower than its own rounded end, so a reading of 1% is still a mark.
            columns.ColumnDefinitions.Add(new ColumnDefinition(percent, GridUnitType.Star)
            {
                MinWidth = PanelMetrics.MeterBarRadiusDip,
            });
            columns.ColumnDefinitions.Add(new ColumnDefinition(100 - percent, GridUnitType.Star));

            // Keyed on the percentage rather than the band, because LinuxPanelTheme is where
            // this head shares the band classification with the tray icon, the same colours,
            // light or dark, that the ring gauge uses.
            columns.Children.Add(new Border
            {
                Background = _theme.Level(meter.Percent),
                CornerRadius = new CornerRadius(PanelMetrics.MeterBarRadiusDip),
            });

            track.Child = columns;
        }

        return track;
    }
}
