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
