using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using OView.App.Rendering;
using OView.Presentation.Panel;
using Brush = System.Windows.Media.Brush;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Path = System.Windows.Shapes.Path;
using Point = System.Windows.Point;
using Shape = System.Windows.Shapes.Shape;
using Size = System.Windows.Size;

namespace OView.Tray.Popup;

/// <summary>
/// One of the model face's two doughnuts: a ring of sections clockwise from the top in the
/// table's order, the window's total in its centre and the window's name under it.
///
/// <para><b>A section is a stroked arc of its own</b> rather than a dash on one ellipse, so each
/// has its own hit area and its own hover card, and the pointer finds nothing in the hole.</para>
/// </summary>
/// <param name="owner">Where the palette and the cards resolve from.</param>
/// <param name="cardOpensLeft">True for the doughnut on the right, whose cards would otherwise cross the panel's edge.</param>
internal sealed class PopupDoughnut(FrameworkElement owner, bool cardOpensLeft)
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

    /// <summary>The greys <c>PanelPalette</c> states; a model past the last takes it again.</summary>
    private const int TailGreys = 4;

    /// <summary>The sweep's cover: a little wider than the ring, so it leaves no fringe either side.</summary>
    private const double CoverStrokeDip = PanelMetrics.DoughnutStrokeDip + 2;

    /// <summary>
    /// How much longer than the ring the cover's dash and gap are, so the dash meets no edge of
    /// the path: a pattern exactly one ring long would draw a sliver at the join.
    /// </summary>
    private const double CoverDashScale = 1.05;

    private static readonly double Circumference = 2 * Math.PI * Radius;

    private readonly Canvas _ring = new() { Width = SizeDip, Height = SizeDip };

    private bool _hasSections;

    private readonly TextBlock _total = CentreLine(PanelMetrics.DoughnutCentreTotalFontSize, FontWeights.SemiBold);

    private readonly TextBlock _unit = CentreLine(PanelMetrics.DoughnutCentreUnitFontSize, FontWeights.Normal);

    private readonly LetterSpacedText _caption = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, CaptionGapDip, 0, 0),
    };

    /// <summary>The colour a table row wears: the same on its chip and on its sections.</summary>
    public static Brush Swatch(FrameworkElement owner, PanelModelRowView row) =>
        (Brush)owner.FindResource(row.Slot >= 0 ? $"Series{row.Slot + 1}" : $"SeriesTail{Math.Min(row.GreyStep + 1, TailGreys)}");

    /// <summary>The doughnut with its caption, for the face to place.</summary>
    public FrameworkElement Build()
    {
        Typography.SetNumeralAlignment(_total, FontNumeralAlignment.Tabular);
        TextElement.SetFontSize(_caption, PanelMetrics.DoughnutLabelFontSize);

        // Not hit-testable: the middle of the ring is not part of any section.
        var centre = new StackPanel { VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        centre.Children.Add(_total);
        centre.Children.Add(_unit);

        var ring = new Grid { Width = SizeDip, Height = SizeDip };
        ring.Children.Add(_ring);
        ring.Children.Add(centre);

        var doughnut = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        doughnut.Children.Add(ring);
        doughnut.Children.Add(_caption);
        return doughnut;
    }

    /// <summary>
    /// The ring fills as one clockwise sweep from the top: a cover in the panel's colour draws
    /// over the sections and retreats along the ring, so each section appears as the sweep passes
    /// it rather than drawing on its own.
    ///
    /// <para>The dash runs from where the sweep has reached to the top, and the offset is all that
    /// moves. Its resting offset is all gap, so the cover paints nothing once the sweep lets go,
    /// and a missed completion can leave nothing hiding the ring.</para>
    /// </summary>
    public void Sweep(double delayMs)
    {
        var sweepMs = PanelMotion.Ms(PanelMotion.DoughnutSweepBaseMs);
        if (!_hasSections || !PanelMotion.Runs(WpfMotion.AnimationsOn, sweepMs))
        {
            return;
        }

        var dash = Circumference * CoverDashScale / CoverStrokeDip;
        var cover = new Path
        {
            Data = WholeRingFromTop(),
            Stroke = Palette("PanelBg"),
            StrokeThickness = CoverStrokeDip,
            StrokeDashArray = [dash, dash],
            StrokeDashOffset = 2 * dash - Circumference / CoverStrokeDip,
            IsHitTestVisible = false,
        };

        _ring.Children.Add(cover);
        cover.BeginAnimation(Shape.StrokeDashOffsetProperty, WpfMotion.Travel(
            2 * dash, cover.StrokeDashOffset, sweepMs, delayMs));
    }

    public void Populate(PanelModelDoughnutView view, IReadOnlyList<PanelModelRowView> rows)
    {
        _total.Text = view.Total;
        _total.Foreground = Palette("TextPrimary");
        _unit.Text = view.Unit;
        _unit.Foreground = Palette("TextMuted");
        _caption.Text = view.Caption.ToUpperInvariant();
        AutomationProperties.SetName(_caption, view.Caption);
        TextElement.SetForeground(_caption, Palette("TextMuted"));

        _ring.Children.Clear();
        _hasSections = view.Sections.Count > 0;

        // A window with no usage has no sections, and an empty ring holds its place in the row.
        if (view.Sections.Count == 0)
        {
            _ring.Children.Add(NewArc(WholeRing(), Palette("BarTrack")));
            return;
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
            var brush = Swatch(owner, row);

            // The only section is the whole ring, with no neighbour to leave a gap for.
            var arc = NewArc(
                view.Sections.Count == 1
                    ? WholeRing()
                    : Arc(along, span - PanelMetrics.DoughnutSectionGapDip),
                brush);
            arc.ToolTip = HoverCard.ModelFigure(owner, row.Name, brush, section.Figure, section.ShareLine, section.ValueLine);
            HoverCard.ApplyTiming(arc);
            if (cardOpensLeft)
            {
                HoverCard.OpenLeftOf(arc, _ring);
            }

            _ring.Children.Add(arc);
            along += span;
        }
    }

    private static TextBlock CentreLine(double fontSize, FontWeight weight) =>
        new() { FontSize = fontSize, FontWeight = weight, HorizontalAlignment = HorizontalAlignment.Center };

    private static Path NewArc(Geometry geometry, Brush brush) =>
        new() { Data = geometry, Stroke = brush, StrokeThickness = PanelMetrics.DoughnutStrokeDip };

    private static EllipseGeometry WholeRing() => new(new Point(Centre, Centre), Radius, Radius);

    /// <summary>The ring as one figure from the top, clockwise, so a dash along it starts where the sweep does.</summary>
    private static PathGeometry WholeRingFromTop()
    {
        var figure = new PathFigure { StartPoint = PointAt(0) };
        foreach (var along in new[] { Circumference / 2, Circumference })
        {
            figure.Segments.Add(new ArcSegment(
                PointAt(along), new Size(Radius, Radius), 0, isLargeArc: false, SweepDirection.Clockwise, isStroked: true));
        }

        return new PathGeometry([figure]);
    }

    /// <summary>An arc of the ring, both ends measured along it from the top, clockwise.</summary>
    private static PathGeometry Arc(double startDip, double lengthDip)
    {
        var figure = new PathFigure { StartPoint = PointAt(startDip) };
        figure.Segments.Add(new ArcSegment(
            PointAt(startDip + lengthDip), new Size(Radius, Radius), 0,
            isLargeArc: lengthDip > Circumference / 2, SweepDirection.Clockwise, isStroked: true));
        return new PathGeometry([figure]);
    }

    private static Point PointAt(double along) =>
        new(Centre + Radius * Math.Sin(along / Radius), Centre - Radius * Math.Cos(along / Radius));

    private Brush Palette(string key) => (Brush)owner.FindResource(key);
}
