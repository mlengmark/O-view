using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using OView.App.Rendering;
using OView.Presentation.Panel;
using static OView.Linux.Panel.PanelPrimitives;

namespace OView.Linux.Panel;

/// <summary>
/// Both off-plan registers: the live banner near the top of the panel, and the standing spend
/// row that closes the plan block, which is absent while there is nothing to show.
///
/// <para>One class for the two because they are two halves of one claim; every word and figure
/// in them arrives built.</para>
/// </summary>
internal sealed class PanelOffPlan(LinuxPanelTheme theme)
{
    /// <summary>The hide control's mark, a cross this wide and tall inside its square.</summary>
    private const double HideMarkDip = 9;

    /// <summary>How far the cross's strokes stop short of the mark's edge, for their round caps.</summary>
    private const double HideMarkInsetDip = 0.9;

    private const double HideMarkStrokeDip = 1.44;

    private readonly LinuxPanelTheme _theme = theme;

    /// <summary>
    /// The off-plan banner: the heading, the line underneath, the address the line sends the
    /// reader to, and the control that hides it.
    ///
    /// <para><b>The address is printed, not labelled.</b> The Windows banner makes the link's
    /// label the thing you click; nothing in this head opens a URL, no part of the Linux
    /// panel does, so a bare "Open usage settings in Claude" here would be an instruction
    /// with no way to follow it. The label still introduces it, because the sentence above
    /// ends by sending the reader somewhere and the address alone does not say where.</para>
    ///
    /// <para>Hiding takes the card out at once, with no fold: this head adds no motion. Keeping
    /// it hidden is the caller's, through <paramref name="onHidden"/>.</para>
    /// </summary>
    public void AddBannerTo(
        StackPanel root, PanelOffPlanBannerView banner, string hideLabel, PanelDensity density, Action onHidden)
    {
        var hide = HideControl(hideLabel);
        var card = HideableWarnCard(
            _theme, NaturalTilePadding * density.SpacingScale, hide,
            banner.Title, banner.Detail, $"{banner.LinkLabel}: {banner.LinkUrl}");

        hide.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(hide).Properties.IsLeftButtonPressed)
            {
                return;
            }

            e.Handled = true;
            root.Children.Remove(card);
            onHidden();
        };

        root.Children.Add(card);
    }

    /// <summary>
    /// The × in the banner's corner. A <see cref="Border"/> because this head registers no theme
    /// for a <see cref="Button"/> to take its look from, and with an explicit background because
    /// a control without one is not hit-testable between the cross's strokes, which is most of
    /// a 16 DIP square. Two lines rather than a parsed path, because <c>Geometry.Parse</c> needs
    /// a rendering platform and a test process has none.
    /// </summary>
    private Border HideControl(string label)
    {
        var far = HideMarkDip - HideMarkInsetDip;
        var mark = new Canvas
        {
            Width = HideMarkDip,
            Height = HideMarkDip,
            Opacity = PanelMetrics.BannerHideOpacity,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        mark.Children.Add(Stroke(new Point(HideMarkInsetDip, HideMarkInsetDip), new Point(far, far)));
        mark.Children.Add(Stroke(new Point(far, HideMarkInsetDip), new Point(HideMarkInsetDip, far)));

        var hide = new Border
        {
            Width = PanelMetrics.BannerHideSizeDip,
            Height = PanelMetrics.BannerHideSizeDip,
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 7, 8, 0),
            Cursor = Application.Current is null ? null : new Cursor(StandardCursorType.Hand),
            Child = mark,
        };

        AutomationProperties.SetName(hide, label);
        ToolTip.SetTip(hide, label);
        return hide;
    }

    private Line Stroke(Point from, Point to) => new()
    {
        StartPoint = from,
        EndPoint = to,
        Stroke = _theme["WarnText"],
        StrokeThickness = HideMarkStrokeDip,
        StrokeLineCap = PenLineCap.Round,
    };

    /// <summary>
    /// The plan block's last row: a soft line, the heading and badge with what the figure is
    /// beneath them, and on the right the figure with the coverage caveat when it has a window.
    /// The figure is the estimate over the chart's window, or on an account with a model limit
    /// what Claude reports as spent, which has no window and so no coverage caveat.
    ///
    /// <para>The longer explanation is on hover, as the Windows panel puts it on a card (issue
    /// #181). The caption stands because it is the part that has to survive a compositor that
    /// never delivers hover: it says what the figure is.</para>
    ///
    /// <para>The transparent background makes the whole row the hover target, not only the
    /// glyphs in it, so the reader finds the answer wherever the eye lands.</para>
    /// </summary>
    public Control Row(PanelOffPlanRowView row, double scale)
    {
        var title = Text(row.Title, PanelMetrics.MeterLabelFontSize, _theme["TextPrimary"], FontWeight.SemiBold);
        title.VerticalAlignment = VerticalAlignment.Center;

        var heading = new StackPanel { Orientation = Orientation.Horizontal };
        heading.Children.Add(title);
        heading.Children.Add(new Border
        {
            Background = _theme["WarnBg"],
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Text(row.Badge, 10, _theme["WarnText"]),
        });

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(heading);
        left.Children.Add(Text(row.Caption, PanelMetrics.OffPlanCaptionFontSize, _theme["TextMuted"]));

        var figure = Text(row.Value, PanelMetrics.OffPlanFigureFontSize, _theme["TextPrimary"], FontWeight.SemiBold);
        figure.TextAlignment = TextAlignment.Right;

        var right = new StackPanel
        {
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        right.Children.Add(figure);
        if (row.Coverage is { Length: > 0 } coverage)
        {
            var caveat = Text(coverage, PanelMetrics.OffPlanCaveatFontSize, _theme["WarnText"]);
            caveat.TextAlignment = TextAlignment.Right;
            right.Children.Add(caveat);
        }

        Grid.SetColumn(right, 1);

        var columns = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, PanelMetrics.OffPlanPaddingAboveDip * scale, 0, PanelMetrics.MeterGapBelowDip * scale),
        };
        columns.Children.Add(left);
        columns.Children.Add(right);

        var line = SoftLine(_theme);
        line.Margin = new Thickness(0, 6 * scale, 0, 0);

        var host = new StackPanel { Background = Brushes.Transparent };
        host.Children.Add(line);
        host.Children.Add(columns);
        ToolTip.SetTip(host, row.Hint);
        return host;
    }
}
