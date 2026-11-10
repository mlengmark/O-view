using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace OView.Linux.Panel;

/// <summary>
/// The panel's hover card: the one surface every mark's tooltip is drawn on, so a chart bar, a
/// doughnut section and a model's name all show the same card and none shows a bare string.
///
/// <para>Three shapes, as on the Windows panel. <see cref="Figure"/> leads with the number and
/// names it underneath; <see cref="ModelFigure"/> is that card for a doughnut section, with the
/// model's chip and name above the figure; <see cref="Text"/> carries a sentence, or a model id
/// the table trims, whole.</para>
/// </summary>
internal sealed class PanelHoverCard(LinuxPanelTheme theme)
{
    /// <summary>
    /// How wide a card's words may run: narrow enough that the widest card, a long model id, still
    /// ends inside the panel from any mark that opens one.
    /// </summary>
    internal const double MaxWordsWidthDip = 190;

    /// <summary>A model's colour chip, the size the Windows panel gives it, with the gap before its name.</summary>
    internal static Border Chip(IBrush colour) => new()
    {
        Width = 8,
        Height = 8,
        CornerRadius = new CornerRadius(2),
        Background = colour,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 6, 0),
    };

    /// <summary>The figure first and what it describes beneath it: a chart bar's card.</summary>
    public Border Figure(string figure, string caption)
    {
        var content = new StackPanel { MaxWidth = MaxWordsWidthDip };
        content.Children.Add(PanelPrimitives.Text(figure, 15, theme["TextPrimary"], FontWeight.SemiBold));
        content.Children.Add(CaptionLine(caption));
        return Surface(content);
    }

    /// <summary>
    /// A doughnut section's card: <see cref="Figure"/>'s shape with the model's chip and name above
    /// the figure, because a ring says which model only by colour. The name wraps rather than
    /// trims, so a raw id is whole here.
    /// </summary>
    public Border ModelFigure(string name, IBrush swatch, string figure, string shareLine, string valueLine)
    {
        var chip = Chip(swatch);
        chip.VerticalAlignment = VerticalAlignment.Top;
        chip.Margin = new Thickness(0, 3, chip.Margin.Right, 0);
        DockPanel.SetDock(chip, Dock.Left);

        var heading = new DockPanel();
        heading.Children.Add(chip);
        heading.Children.Add(PanelPrimitives.Text(name, 11, theme["TextSecondary"]));

        var line = PanelPrimitives.Text(figure, 15, theme["TextPrimary"], FontWeight.SemiBold);
        line.Margin = new Thickness(0, 2, 0, 0);

        var content = new StackPanel { MaxWidth = MaxWordsWidthDip };
        content.Children.Add(heading);
        content.Children.Add(line);
        content.Children.Add(CaptionLine($"{shareLine}\n{valueLine}"));
        return Surface(content);
    }

    /// <summary>A sentence, or a model's id where the table trims its name; it wraps, so the id is whole.</summary>
    public Border Text(string text)
    {
        var words = PanelPrimitives.Text(text, 11, theme["TextSecondary"]);
        words.MaxWidth = MaxWordsWidthDip;
        return Surface(words);
    }

    private TextBlock CaptionLine(string text)
    {
        var caption = PanelPrimitives.Text(text, 10, theme["TextMuted"]);
        caption.Margin = new Thickness(0, 3, 0, 0);
        return caption;
    }

    /// <summary>The card itself. Its own because this head has no theme to draw a tooltip's.</summary>
    private Border Surface(Control content)
    {
        var card = new Border
        {
            Background = theme["TooltipBg"],
            BorderBrush = theme["TooltipBorder"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 7),
            Child = content,
        };

        // With no theme the popup window behind the card is opaque white, which would show at the
        // rounded corners. The card always sits over the panel, so the panel's colour hides them.
        card.AttachedToVisualTree += (_, _) =>
        {
            if (TopLevel.GetTopLevel(card) is { } popup)
            {
                popup.Background = theme["PanelBg"];
            }
        };

        return card;
    }
}
