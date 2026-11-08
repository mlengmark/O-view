using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Size = System.Windows.Size;
using TextBlock = System.Windows.Controls.TextBlock;

namespace OView.Tray.Diagnostics;

/// <summary>
/// The no-clipping check <c>--popup-samples</c> runs on every panel it renders: nothing drawn
/// may cross the panel's edge, and no text may be cut short.
///
/// <para>A render that clips looks like a render. A hover card cut by the panel's edge was
/// caught by eye in a sketch, once, and nothing would catch the next one, so the question is
/// asked of the laid-out tree instead of the picture: where each element ended up, whether the
/// layout cut it to its slot, and how wide each line of text wanted to be. It runs on the settled panel, never on a frame of a fold,
/// whose rows are clipped by design.</para>
///
/// <para>One exemption, and it is a column rather than a state: a model's name in the model
/// table trims when the id is longer than the column, with the full id in its tooltip. A name
/// that trims with no tooltip behind it is a miss like any other.</para>
///
/// <para><b>Measures the text it checks,</b> so a panel is not to be rendered again after it.</para>
/// </summary>
internal static class PanelEdgeCheck
{
    /// <summary>How far past an edge or a line's width still counts as on it, for layout rounding.</summary>
    private const double ToleranceDip = 0.5;

    /// <summary>What is wrong with a laid-out tree; empty when nothing is.</summary>
    /// <param name="root">The panel's content: its bounds are the edge.</param>
    /// <param name="modelTable">The model table, whose name cells are the exemption; null where there is none.</param>
    public static List<string> Misses(FrameworkElement root, FrameworkElement? modelTable = null)
    {
        var misses = new List<string>();
        Walk(root, root, modelTable, inScroller: false, misses);
        return misses;
    }

    private static void Walk(
        FrameworkElement root, DependencyObject parent, FrameworkElement? modelTable, bool inScroller, List<string> misses)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            if (VisualTreeHelper.GetChild(parent, i) is not FrameworkElement e
                || e.Visibility != Visibility.Visible || e.Opacity == 0)
            {
                continue;
            }

            if (e.ActualWidth > 0 && e.ActualHeight > 0)
            {
                var bounds = e.TransformToAncestor(root).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));

                // A scroller's content runs past the bottom by design, so only its sides are the edge.
                var crosses = bounds.Left < -ToleranceDip || bounds.Right > root.ActualWidth + ToleranceDip
                              || (!inScroller && (bounds.Top < -ToleranceDip || bounds.Bottom > root.ActualHeight + ToleranceDip));
                if (crosses)
                {
                    misses.Add($"{Describe(e)} crosses the edge: x {bounds.Left:0.#}..{bounds.Right:0.#}, " +
                               $"y {bounds.Top:0.#}..{bounds.Bottom:0.#} in {root.ActualWidth:0.#} x {root.ActualHeight:0.#}");
                }

                // An element wider than the slot its parent gave it is cut at the slot: a figure in a
                // column too narrow for it is not trimmed, it overflows and the layout clips it.
                if (LayoutInformation.GetLayoutClip(e) is { } clip
                    && (clip.Bounds.Width < e.ActualWidth - ToleranceDip || clip.Bounds.Height < e.ActualHeight - ToleranceDip))
                {
                    misses.Add($"{Describe(e)} is cut by its slot: shows {clip.Bounds.Width:0.#} x {clip.Bounds.Height:0.#} " +
                               $"of {e.ActualWidth:0.#} x {e.ActualHeight:0.#}");
                }

                if (e is TextBlock text && Trim(text) is { } cut)
                {
                    var byDesign = text.Parent is DockPanel cell && cell.Parent == modelTable;
                    if (!byDesign)
                    {
                        misses.Add($"{Describe(e)} trims: {cut}");
                    }
                    else if (((DockPanel)text.Parent).ToolTip is null)
                    {
                        misses.Add($"{Describe(e)} trims with no tooltip: {cut}");
                    }
                }
            }

            Walk(root, e, modelTable, inScroller || e is ScrollViewer, misses);
        }
    }

    /// <summary>How the text falls short of its box, or null when it fits.</summary>
    private static string? Trim(TextBlock text)
    {
        var margin = text.Margin;
        if (text.TextWrapping == TextWrapping.NoWrap)
        {
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var wanted = text.DesiredSize.Width - margin.Left - margin.Right;
            return wanted > text.ActualWidth + ToleranceDip ? $"wants {wanted:0.#} wide, has {text.ActualWidth:0.#}" : null;
        }

        text.Measure(new Size(text.ActualWidth + margin.Left + margin.Right, double.PositiveInfinity));
        var tall = text.DesiredSize.Height - margin.Top - margin.Bottom;
        return tall > text.ActualHeight + ToleranceDip ? $"wants {tall:0.#} high, has {text.ActualHeight:0.#}" : null;
    }

    private static string Describe(FrameworkElement e)
    {
        var what = e is TextBlock text ? text.Text : e.Name;
        what = what.Length > 40 ? what[..40] + "..." : what;
        return what.Length > 0 ? $"{e.GetType().Name} \"{what}\"" : e.GetType().Name;
    }
}
