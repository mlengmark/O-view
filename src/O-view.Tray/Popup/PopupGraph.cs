using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using OView.App.Rendering;
using OView.Core.Models;
using OView.Presentation.Panel;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Rectangle = System.Windows.Shapes.Rectangle;
using Size = System.Windows.Size;

namespace OView.Tray.Popup;

/// <summary>
/// Draws the 31-day usage chart on the Canvas (issues #4, #5): one bar per day,
/// coloured light→dark blue by its intensity within its own plan week, dotted
/// gridlines at the weekly resets, vertical date labels, and a per-bar hover
/// tooltip. Pre-install days are blank columns (no bar), with the date axis, an
/// empty column reads as "no data" without needing the old caption.
///
/// <para>Every one of those is read from <see cref="PanelSections.Graph"/>, which the
/// Linux chart draws from too. What is left here is turning fractions into pixels against
/// this canvas, and the hover cards' chrome. The week bands, the boundary placement and
/// the card wording used to live in this file, where the other head could not reach
/// them, so it drew flat bars and no gridlines at all.</para>
///
/// Bar and label are both placed from <see cref="BarCentre"/> (issue #31) so a date
/// always sits under the bar it describes; with 31 columns in ~344 px a few pixels
/// of drift is enough to make a label read against its neighbour.
/// </summary>
internal sealed class PopupGraph(FrameworkElement owner, Canvas host)
{
    // Blue gradient endpoints for within-week intensity (issue #5): light → dark.
    private static readonly Color GraphBlueLo = Color.FromRgb(176, 208, 240);
    private static readonly Color GraphBlueHi = Color.FromRgb(24, 95, 165);

    public void Draw(PanelStatistics stats, UsageSnapshot snapshot, TimeZoneInfo localZone)
    {
        host.Children.Clear();

        var graph = PanelSections.Graph(stats, snapshot, localZone);
        if (graph.Columns.Count == 0)
        {
            return;
        }

        var width = host.ActualWidth > 0 ? host.ActualWidth : 344;

        // Derived from the canvas rather than fixed at 52. The canvas height is no longer a
        // constant, a display too short for the panel gets a shorter one (PanelDensity),
        // and a fixed bar area meant the rotated date labels below it simply ran off the
        // bottom and collided with the next section. Caught by rendering it; the constants
        // alone look fine. At the normal 86 px canvas this is 52, exactly as before.
        const double labelAreaHeight = 34;
        var barAreaHeight = Math.Max(18, host.Height - labelAreaHeight);
        var labelTop = barAreaHeight + 6;
        var col = width / graph.Columns.Count;

        var labelBrush = (Brush)owner.FindResource("TextMuted");

        for (var i = 0; i < graph.Columns.Count; i++)
        {
            var column = graph.Columns[i];
            var x = i * col;

            // Bar, height by absolute tokens, colour by within-week intensity.
            if (column.HasBar)
            {
                var height = Math.Max(PanelMetrics.GraphMinBarHeightDip, barAreaHeight * column.Height);
                var barWidth = Math.Max(2, col - 2);
                var bar = new Rectangle
                {
                    Width = barWidth,
                    Height = height,
                    Fill = new SolidColorBrush(Lerp(GraphBlueLo, GraphBlueHi, column.Intensity)),
                    RadiusX = 1,
                    RadiusY = 1,
                    // Figure first, date beneath, the panel's shared hover card, not the
                    // raw string this used to be. A bare `ToolTip = "…"` renders as system
                    // chrome: a pale rectangle with a hard border, on a panel that is
                    // otherwise entirely rounded cards on one palette.
                    ToolTip = HoverCard.Figure(owner, column.CardFigure, column.CardCaption),
                };
                HoverCard.ApplyTiming(bar);
                // Placed from the column centre, like the label below it, so the two
                // cannot drift apart as the gutter or column width changes (issue #31).
                Canvas.SetLeft(bar, BarCentre(x, col) - barWidth / 2);
                Canvas.SetTop(bar, barAreaHeight - height);
                host.Children.Add(bar);
            }

            // Vertical date label under every column (rotated, small but legible),
            // centred on its own bar (issue #31).
            //
            // A RenderTransform does not move the element's LAYOUT box, so Canvas.SetLeft
            // still positions the un-rotated text while the ink lands somewhere else
            // entirely: at 90° the box [0,w]×[0,h] maps to [-h,0]×[0,w], i.e. the label
            // hangs to the LEFT of its anchor by one line height. The old code
            // compensated with a constant +3, which stood in for half a line and was ~2 px
            // short of an 8 pt one, a fifth of a column at 31 days, and what made the
            // dates read as offset from their bars.
            //
            // Ask the transform for the rendered bounds rather than re-deriving them: the
            // line height moves with font, DPI and the OS text-scaling setting, and this
            // stays correct if the angle is ever changed.
            var rotation = new RotateTransform(90);
            var label = new TextBlock
            {
                Text = column.DateLabel,
                FontSize = PanelMetrics.GraphLabelFontSize,
                Foreground = labelBrush,
                RenderTransform = rotation,
                Opacity = column.Recorded ? 1.0 : PanelMetrics.GraphUnrecordedLabelOpacity,
            };
            // Parent first, so the font properties measured are the inherited ones.
            host.Children.Add(label);
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var ink = rotation.TransformBounds(new Rect(label.DesiredSize));
            Canvas.SetLeft(label, BarCentre(x, col) - ink.Left - ink.Width / 2);
            Canvas.SetTop(label, labelTop - ink.Top);
        }

        DrawWeekBoundaries(graph.Boundaries, col, barAreaHeight);
    }

    /// <summary>
    /// The week-boundary gridlines, drawn <em>last</em> so they sit above the bars.
    ///
    /// <para>A Canvas paints in child order, so these used to be added first and were
    /// overdrawn by every bar they crossed, which is most of them in a busy week, leaving
    /// the boundary visible only in the gaps. A reset line that disappears exactly where
    /// the usage is heaviest is missing the moment it exists to mark.</para>
    ///
    /// <para>They wear the panel's note colour (<c>WarnText</c>), the same amber the
    /// coverage and caveat lines use, rather than the muted grey they shared with the date
    /// labels: this is an annotation over the data, not another axis decoration, and grey
    /// on blue at one pixel was not carrying that.</para>
    ///
    /// <para>Placement is unchanged, and now decided above this head: a plan reset happens at
    /// an instant, not at midnight, so the line sits at its true fractional position inside
    /// the day it falls in, snapping to the nearest column edge would claim a boundary the
    /// data does not have. The calendar-week fallback lands on a column edge because midnight
    /// is one.</para>
    /// </summary>
    private void DrawWeekBoundaries(
        IReadOnlyList<PanelGraphBoundaryView> boundaries, double col, double barAreaHeight)
    {
        var gridBrush = (Brush)owner.FindResource("WarnText");

        foreach (var boundary in boundaries)
        {
            var x = (boundary.Column + boundary.FractionOfDay) * col;
            var line = new Line
            {
                X1 = x, X2 = x, Y1 = 0, Y2 = barAreaHeight + PanelMetrics.GraphBoundaryOverhangDip,
                Stroke = gridBrush,
                StrokeThickness = 1,
                StrokeDashArray = [PanelMetrics.GraphBoundaryDashOnDip, PanelMetrics.GraphBoundaryDashOffDip],
                // No figure is the Monday fallback, which has no observed instant to state,
                // so it takes the text-only card rather than a headline standing empty.
                ToolTip = boundary.CardFigure.Length == 0
                    ? HoverCard.Text(owner, boundary.CardCaption)
                    : HoverCard.Figure(owner, boundary.CardFigure, boundary.CardCaption),
            };
            HoverCard.ApplyTiming(line);
            host.Children.Add(line);
        }
    }

    /// <summary>
    /// Horizontal centre of the column starting at <paramref name="x"/>. The single
    /// anchor the bar and its date label are both placed from (issue #31), the label
    /// was previously offset by a constant that no longer matched the bar.
    /// </summary>
    private static double BarCentre(double x, double col) => x + col / 2;

    private static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }
}
