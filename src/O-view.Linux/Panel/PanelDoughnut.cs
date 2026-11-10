using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using OView.App.Rendering;
using OView.Presentation.Panel;
using static OView.Linux.Panel.PanelPrimitives;
using Path = Avalonia.Controls.Shapes.Path;

namespace OView.Linux.Panel;

/// <summary>
/// One of the model face's two doughnuts: a ring of sections clockwise from the top in the
/// table's order, the window's total in its centre and the window's name under it.
///
/// <para><b>A section is a stroked arc of its own</b>, so each has its own hover card and the
/// pointer finds nothing in the hole. Every arc's box is the whole ring's, which is what a card
/// opening to the left is placed against.</para>
/// </summary>
internal sealed class PanelDoughnut(LinuxPanelTheme theme)
{
    private const double SizeDip = PanelMetrics.DoughnutSizeDip;

    private const double Centre = SizeDip / 2;

    private const double Radius = PanelMetrics.DoughnutRingRadiusDip;

    private const double CaptionGapDip = 4;

    /// <summary>
    /// The least of a ring a section keeps once its gap is taken off, so a model with a sliver of
    /// the window is still there to see and to point at.
    /// </summary>
    private const double MinSectionDip = 0.75;

    /// <summary>The greys <see cref="PanelPalette"/> states; a model past the last takes it again.</summary>
    private const int TailGreys = 4;

    private static readonly double Circumference = 2 * Math.PI * Radius;

    private readonly PanelHoverCard _cards = new(theme);

    /// <summary>The colour a table row wears: the same on its chip, its sections and its card.</summary>
    public IBrush Swatch(PanelModelRowView row) =>
        theme[row.Slot >= 0 ? $"Series{row.Slot + 1}" : $"SeriesTail{Math.Min(row.GreyStep + 1, TailGreys)}"];

    /// <param name="view">The ring's total, caption and sections.</param>
    /// <param name="rows">The table's rows, which say each section's name and colour.</param>
    /// <param name="cardOpensLeft">True for the doughnut on the right, whose cards would otherwise cross the panel's edge.</param>
    /// <param name="swept">The ring, for <see cref="Sweep"/> to fill in.</param>
    public StackPanel Build(
        PanelModelDoughnutView view, IReadOnlyList<PanelModelRowView> rows, bool cardOpensLeft, out Ring swept)
    {
        var ring = new Canvas { Width = SizeDip, Height = SizeDip };
        foreach (var arc in Sections(view, rows, cardOpensLeft))
        {
            ring.Children.Add(arc);
        }

        // Not hit-testable: the middle of the ring is not part of any section.
        var centre = new StackPanel { VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        centre.Children.Add(CentreLine(view.Total, PanelMetrics.DoughnutCentreTotalFontSize, "TextPrimary", FontWeight.SemiBold));
        centre.Children.Add(CentreLine(view.Unit, PanelMetrics.DoughnutCentreUnitFontSize, "TextMuted", FontWeight.Normal));

        var layers = new Grid { Width = SizeDip, Height = SizeDip };
        layers.Children.Add(ring);
        layers.Children.Add(centre);

        var caption = Caption(view.Caption, PanelMetrics.DoughnutLabelFontSize, theme["TextMuted"]);
        caption.HorizontalAlignment = HorizontalAlignment.Center;
        caption.Margin = new Thickness(caption.Margin.Left, CaptionGapDip, 0, 0);

        var doughnut = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        doughnut.Children.Add(layers);
        doughnut.Children.Add(caption);
        swept = new Ring(ring, view.Sections.Count > 0);
        return doughnut;
    }

    /// <summary>A built doughnut's ring, and whether it has sections: an empty ring has nothing to sweep in.</summary>
    internal sealed record Ring(Canvas Canvas, bool HasSections);

    /// <summary>
    /// The ring fills as one clockwise sweep from the top, after <paramref name="delayMs"/>. A cover
    /// in the panel's colour lies over the part not yet reached and shrinks back along the ring, so
    /// each section appears as the sweep passes it. The cover takes no pointer, so every section's
    /// card answers throughout, and it is gone once the sweep lands.
    /// </summary>
    public void Sweep(Ring ring, LinuxMotion motion, double delayMs)
    {
        if (!ring.HasSections)
        {
            return;
        }

        var cover = new Path
        {
            Width = SizeDip,
            Height = SizeDip,
            Stroke = theme["PanelBg"],
            StrokeThickness = CoverStrokeDip,
            IsHitTestVisible = false,
        };
        ring.Canvas.Children.Add(cover);

        motion.Value(0, reached => cover.Data = CoverAt(reached))
            .To(1, PanelMotion.Ms(PanelMotion.DoughnutSweepBaseMs), PanelMotion.Decelerate,
                () => ring.Canvas.Children.Remove(cover), delayMs);
    }

    /// <summary>The sweep's cover: a little wider than the ring, so it leaves no fringe either side.</summary>
    private const double CoverStrokeDip = PanelMetrics.DoughnutStrokeDip + 2;

    /// <summary>What is still covered once the sweep has reached <paramref name="reached"/> of the ring.</summary>
    private static Geometry? CoverAt(double reached) => reached switch
    {
        <= 0 => WholeRing(),
        >= 1 => null,
        _ => Arc(Circumference * reached, Circumference * (1 - reached)),
    };

    private IEnumerable<Path> Sections(PanelModelDoughnutView view, IReadOnlyList<PanelModelRowView> rows, bool cardOpensLeft)
    {
        // A window with no usage has no sections, and an empty ring holds its place in the row.
        if (view.Sections.Count == 0)
        {
            yield return NewArc(WholeRing(), theme["BarTrack"]);
            yield break;
        }

        // A sliver takes the least room a section can have, and the larger sections give that
        // room up in proportion, so every section keeps its gap however small its neighbour is.
        var least = MinSectionDip + PanelMetrics.DoughnutSectionGapDip;
        bool IsSliver(PanelModelSectionView s) => s.Share * Circumference < least;
        var larger = view.Sections.Where(s => !IsSliver(s)).Sum(s => s.Share * Circumference);
        var scale = (Circumference - least * view.Sections.Count(IsSliver)) / larger;

        var along = 0.0;
        foreach (var section in view.Sections)
        {
            var span = IsSliver(section) ? least : section.Share * Circumference * scale;
            var row = rows[section.Row];

            // The only section is the whole ring, with no neighbour to leave a gap for.
            var arc = NewArc(
                view.Sections.Count == 1 ? WholeRing() : Arc(along, span - PanelMetrics.DoughnutSectionGapDip),
                Swatch(row));

            ToolTip.SetTip(arc, _cards.ModelFigure(row.Name, Swatch(row), section.Figure, section.ShareLine, section.ValueLine));
            if (cardOpensLeft)
            {
                ToolTip.SetPlacement(arc, PlacementMode.Left);
            }

            yield return arc;
            along += span;
        }
    }

    private TextBlock CentreLine(string text, double size, string brush, FontWeight weight)
    {
        var line = Text(text, size, theme[brush], weight);
        line.TextWrapping = TextWrapping.NoWrap;
        line.HorizontalAlignment = HorizontalAlignment.Center;
        line.FontFeatures = [new FontFeature { Tag = "tnum" }];
        return line;
    }

    private static Path NewArc(Geometry geometry, IBrush brush) => new()
    {
        Width = SizeDip,
        Height = SizeDip,
        Data = geometry,
        Stroke = brush,
        StrokeThickness = PanelMetrics.DoughnutStrokeDip,
    };

    private static EllipseGeometry WholeRing() =>
        new() { Center = new Point(Centre, Centre), RadiusX = Radius, RadiusY = Radius };

    /// <summary>An arc of the ring, both ends measured along it from the top, clockwise.</summary>
    private static PathGeometry Arc(double startDip, double lengthDip)
    {
        var arc = new ArcSegment
        {
            Point = PointAt(startDip + lengthDip), Size = new Size(Radius, Radius),
            IsLargeArc = lengthDip > Circumference / 2, SweepDirection = SweepDirection.Clockwise,
        };

        var figure = new PathFigure { StartPoint = PointAt(startDip), IsClosed = false, Segments = [arc] };
        return new PathGeometry { Figures = [figure] };
    }

    private static Point PointAt(double along) =>
        new(Centre + Radius * Math.Sin(along / Radius), Centre - Radius * Math.Cos(along / Radius));
}
