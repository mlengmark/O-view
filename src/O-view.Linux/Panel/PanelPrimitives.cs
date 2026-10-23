using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace OView.Linux.Panel;

/// <summary>
/// The marks every piece of the panel draws with: a line of text, and the inset of a card.
///
/// <para>Shared rather than repeated, so the sections cannot drift apart on the two things
/// that would be visible immediately if they did.</para>
/// </summary>
internal static class PanelPrimitives
{
    /// <summary>Inset inside a tile or a banner card at the natural density.</summary>
    internal const double NaturalTilePadding = 10;

    internal static TextBlock Muted(LinuxPanelTheme theme, string text) =>
        Text(text, 11, theme["TextMuted"]);

    /// <summary>
    /// The amber card the no-data banner and the off-plan banner share: a semi-bold title and
    /// one or more detail lines, in the warn colours, on a rounded warn background.
    /// </summary>
    internal static Border WarnCard(LinuxPanelTheme theme, double padding, string title, params string[] lines)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(Text(title, 13, theme["WarnText"], FontWeight.SemiBold));
        foreach (var line in lines)
        {
            stack.Children.Add(Text(line, 11, theme["WarnText"]));
        }

        return new Border
        {
            Background = theme["WarnBg"],
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(padding),
            Child = stack,
        };
    }

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
