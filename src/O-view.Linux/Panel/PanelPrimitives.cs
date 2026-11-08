using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using OView.App.Rendering;

namespace OView.Linux.Panel;

/// <summary>
/// The marks every piece of the panel draws with: a line of text, a soft rule, and the inset of
/// a card.
///
/// <para>Shared rather than repeated, so the sections cannot drift apart on the things that
/// would be visible immediately if they did.</para>
/// </summary>
internal static class PanelPrimitives
{
    /// <summary>Inset inside a tile or a banner card at the natural density.</summary>
    internal const double NaturalTilePadding = 10;

    /// <summary>How much of a hideable card's right side its sentences keep clear of the hide control.</summary>
    private const double HideControlReserveDip = 28;

    internal static TextBlock Muted(LinuxPanelTheme theme, string text) =>
        Text(text, 11, theme["TextMuted"]);

    /// <summary>
    /// The hairline between blocks and above the off-plan row. Soft, because on a flat surface a
    /// full-strength line reads as a box.
    /// </summary>
    internal static Border SoftLine(LinuxPanelTheme theme) => new()
    {
        Height = 1,
        Background = theme["PanelBorder"],
        Opacity = PanelMetrics.SoftLineOpacity,
    };

    /// <summary>
    /// The amber card the no-data banner and the off-plan banner share: a semi-bold title and
    /// one or more detail lines, in the warn colours, on a rounded warn background.
    /// </summary>
    internal static Border WarnCard(LinuxPanelTheme theme, double padding, string title, params string[] lines)
    {
        var card = WarnSurface(theme, WarnLines(theme, title, lines));
        card.Padding = new Thickness(padding);
        return card;
    }

    /// <summary>
    /// The same card with <paramref name="hide"/> in its corner. Its sentences stop short of the
    /// control, so a long one wraps rather than running under it.
    /// </summary>
    internal static Border HideableWarnCard(
        LinuxPanelTheme theme, double padding, Control hide, string title, params string[] lines)
    {
        var words = WarnLines(theme, title, lines);
        words.Margin = new Thickness(padding, padding, HideControlReserveDip, padding);

        var layers = new Grid();
        layers.Children.Add(words);
        layers.Children.Add(hide);
        return WarnSurface(theme, layers);
    }

    private static StackPanel WarnLines(LinuxPanelTheme theme, string title, string[] lines)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(Text(title, 13, theme["WarnText"], FontWeight.SemiBold));
        foreach (var line in lines)
        {
            stack.Children.Add(Text(line, 11, theme["WarnText"]));
        }

        return stack;
    }

    private static Border WarnSurface(LinuxPanelTheme theme, Control content) => new()
    {
        Background = theme["WarnBg"],
        CornerRadius = new CornerRadius(6),
        Child = content,
    };

    internal static TextBlock Text(string text, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new()
        {
            Text = text,
            FontSize = size,
            Foreground = brush,
            FontWeight = weight,
            TextWrapping = TextWrapping.Wrap,
        };
}
