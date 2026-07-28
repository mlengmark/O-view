using System.Windows;
using System.Windows.Controls;
using Brush = System.Windows.Media.Brush;
using Orientation = System.Windows.Controls.Orientation;
using ToolTip = System.Windows.Controls.ToolTip;

namespace OView.Tray.Popup;

/// <summary>
/// Builders and timing for the app's hover cards, so every tooltip in the panel is the
/// same object with the same delays.
///
/// <para>The card template used to live inside <see cref="StatTile"/>, which meant only the
/// tiles had it: the usage graph's bars fell back to raw WPF chrome, and the panel showed
/// two unrelated tooltip designs depending on where the pointer landed. The template moved
/// to <c>HoverCard.xaml</c> and the construction moved here.</para>
///
/// <para>The two shapes are deliberate and cover everything the panel needs. <see
/// cref="Figure"/> leads with the number and names it underneath — the reading order for
/// "what am I pointing at, and how much"; <see cref="Text"/> carries a sentence, for
/// caveats and explanations. Anything else should reuse one of these rather than hand-roll
/// a third.</para>
/// </summary>
internal static class HoverCard
{
    // Timing is applied per element, never inherited. Setting it once on a container looks
    // right and silently does not work — the children resolve framework values instead,
    // which is invisible in a screenshot. --tile-samples reports what actually resolves on
    // a bar segment for exactly this reason.

    /// <summary>
    /// 400 ms — the Windows convention, and a real change: the unset value measured on this
    /// machine was 1000 ms, which reads as sluggish for a deliberate point-at. The delay
    /// still exists to require *lingering*, since the pointer crosses these marks on its way
    /// elsewhere and anything much shorter flashes tooltips during ordinary movement.
    /// </summary>
    public const int InitialDelayMs = 400;

    /// <summary>
    /// 3 s — the one that matters most for a row of adjacent marks. Within this window of
    /// the last tooltip the next shows with NO delay, so sliding along the bars or segments
    /// reads as one continuous reveal rather than re-waiting on each. The 100 ms default
    /// makes traversing them feel broken.
    /// </summary>
    public const int BetweenDelayMs = 3000;

    /// <summary>
    /// 20 s — set for determinism, not to extend anything. The documented WPF default is
    /// 5 s, but the unset value measured here was int.MaxValue, i.e. effectively unlimited;
    /// so this is a deliberate CAP rather than the extension it might look like. 20 s still
    /// far outlasts reading a two-field card, so WCAG 1.4.13 (Content on Hover or Focus) is
    /// satisfied in practice while the behaviour is guaranteed on a machine whose default
    /// really is 5 s.
    /// </summary>
    public const int DurationMs = 20_000;

    /// <summary>Applies the shared delays. Must be called on each element that owns a card.</summary>
    public static void ApplyTiming(DependencyObject element)
    {
        ToolTipService.SetInitialShowDelay(element, InitialDelayMs);
        ToolTipService.SetBetweenShowDelay(element, BetweenDelayMs);
        ToolTipService.SetShowDuration(element, DurationMs);
    }

    /// <summary>
    /// A card leading with a figure, with a quieter identifying line beneath it, and an
    /// optional colour swatch tying it to the mark being pointed at.
    /// </summary>
    /// <param name="owner">Element whose resources resolve the palette.</param>
    /// <param name="figure">The headline — a token count, a value, a total.</param>
    /// <param name="caption">What the figure describes: a date, a model id.</param>
    /// <param name="swatch">
    /// Optional. Carries identity where a card floats clear of a coloured mark and would
    /// otherwise lose its connection to the exact colour under the pointer.
    /// </param>
    public static ToolTip Figure(FrameworkElement owner, string figure, string caption, Brush? swatch = null)
    {
        var content = new StackPanel { MaxWidth = 220 };

        var heading = new StackPanel { Orientation = Orientation.Horizontal };
        if (swatch is not null)
        {
            heading.Children.Add(new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(2),
                Background = swatch,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        heading.Children.Add(new TextBlock
        {
            Text = figure,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(swatch is null ? 0 : 6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)owner.FindResource("TextPrimary"),
        });
        content.Children.Add(heading);

        content.Children.Add(new TextBlock
        {
            Text = caption,
            FontSize = 10,
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)owner.FindResource("TextMuted"),
        });

        return Card(owner, content);
    }

    /// <summary>A card carrying a sentence — caveats, explanations, and anything unmeasured.</summary>
    public static ToolTip Text(FrameworkElement owner, string text) =>
        Card(owner, new TextBlock
        {
            Text = text,
            FontSize = 11,
            MaxWidth = 240,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)owner.FindResource("TextSecondary"),
        });

    private static ToolTip Card(FrameworkElement owner, object content) =>
        new()
        {
            Content = content,
            Style = (Style)owner.FindResource("PanelTooltip"),
        };
}
