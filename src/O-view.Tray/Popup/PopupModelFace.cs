using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using OView.App.Rendering;
using OView.Presentation.Panel;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace OView.Tray.Popup;

/// <summary>
/// The local block's model face: the two doughnuts and the table of every model behind them.
///
/// <para>Everything is read from <see cref="PanelModelFaceView"/>. Which models, in which order
/// and in which colour was decided there, so the rows and the sections cannot disagree; what is
/// left here is the table's grammar, which is the totals table's with a group heading added.</para>
/// </summary>
internal sealed class PopupModelFace(FrameworkElement owner, Grid doughnuts, Grid table)
{
    private const double DoughnutsGapAboveDip = 10;

    private const double DoughnutColumnGapDip = 12;

    /// <summary>Space above and below the text in a row, before the density.</summary>
    private const double RowPaddingDip = 4;

    private const double HeaderGapBelowDip = 4;

    private const double GroupGapBelowDip = 2;

    /// <summary>
    /// The four data columns' headings, one size down from the totals tables' as drawn: the
    /// columns are narrow enough that <c>EST. VALUE</c> would fill its cell to the pixel at theirs.
    /// </summary>
    private const double DataHeaderFontSize = 9;

    /// <summary>The line's own pixel, which a cell's padding starts after.</summary>
    private const double LineDip = 1;

    private const int DataColumns = 4;

    private readonly PopupDoughnut _today = new(owner, cardOpensLeft: false);
    private readonly PopupDoughnut _window = new(owner, cardOpensLeft: true);

    private PanelModelFaceView? _view;
    private double _scale = 1;

    /// <summary>The scale the rows on screen were built at, for <see cref="ReleaseRows"/>.</summary>
    private double _builtScale = 1;

    /// <summary>What is in the table, by grid row: the cells that fade in together, heading rows included.</summary>
    private List<List<UIElement>> _rows = [];

    /// <summary>
    /// True while a flip is moving the rows on screen: a re-fit during it runs the density passes,
    /// and rebuilding the table then would snap the rows' fades to their ends.
    /// </summary>
    public bool HoldRows { get; set; }

    public void Start()
    {
        doughnuts.ColumnDefinitions.Add(new ColumnDefinition());
        doughnuts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DoughnutColumnGapDip) });
        doughnuts.ColumnDefinitions.Add(new ColumnDefinition());
        doughnuts.Children.Add(_today.Build());

        var window = _window.Build();
        Grid.SetColumn(window, 2);
        doughnuts.Children.Add(window);
    }

    public void Populate(PanelModelFaceView view)
    {
        _view = view;
        _today.Populate(view.Today, view.Table.Rows);
        _window.Populate(view.Window, view.Table.Rows);
        Build();
    }

    /// <summary>Tightens the gaps and the rows with the rest of the panel; the rings and the type stay put.</summary>
    public void ApplyDensity(double spacingScale)
    {
        _scale = spacingScale;
        doughnuts.Margin = new Thickness(0, DoughnutsGapAboveDip * spacingScale, 0, 0);
        table.Margin = new Thickness(0, PanelMetrics.DoughnutsToTableGapDip * spacingScale, 0, 0);

        if (!HoldRows)
        {
            Build();
        }
    }

    /// <summary>The flip has ended: the rows catch up with a density that changed while they were moving.</summary>
    public void ReleaseRows()
    {
        HoldRows = false;

        if (_builtScale != _scale)
        {
            Build();
        }
    }

    /// <summary>
    /// The face fills in: the doughnuts sweep and the rows fade in from the top. A heading row
    /// counts as a row, as the document's <c>i</c> does.
    /// </summary>
    /// <param name="sweepDelayMs">When the sweep starts.</param>
    /// <param name="rowStartMs">When row <c>i</c> starts fading in.</param>
    /// <returns>When the last of it ends, in milliseconds.</returns>
    public double FillIn(double sweepDelayMs, Func<int, double> rowStartMs)
    {
        var sweepMs = PanelMotion.Ms(PanelMotion.DoughnutSweepBaseMs);
        var rowMs = PanelMotion.Ms(PanelMotion.ModelRowFadeBaseMs);
        if (!PanelMotion.Runs(WpfMotion.AnimationsOn, sweepMs))
        {
            return 0;
        }

        _today.Sweep(sweepDelayMs);
        _window.Sweep(sweepDelayMs);

        for (var i = 0; i < _rows.Count; i++)
        {
            // Travel: the resting opacity is 1, so nothing is stranded if a completion is missed.
            var fade = WpfMotion.Travel(0, 1, rowMs, rowStartMs(i));
            _rows[i].ForEach(cell => cell.BeginAnimation(UIElement.OpacityProperty, fade));
        }

        return Math.Max(sweepDelayMs + sweepMs, rowStartMs(_rows.Count - 1) + rowMs);
    }

    private void Build()
    {
        if (_view is not { Table: var view })
        {
            return;
        }

        _builtScale = _scale;
        table.Children.Clear();
        table.RowDefinitions.Clear();
        table.ColumnDefinitions.Clear();

        // The name is the star, so a wider compact panel only gives the names more room.
        table.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < DataColumns; i++)
        {
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PanelMetrics.ModelColumnDip) });
        }

        var rowCount = view.Rows.Count + 2;
        _rows = new List<List<UIElement>>(rowCount);
        for (var i = 0; i < rowCount; i++)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _rows.Add([]);
        }

        AddHeading(view.TodayGroup, PanelMetrics.TableHeaderFontSize, 0, 1, 2, GroupGapBelowDip);
        AddHeading(view.WindowGroup, PanelMetrics.TableHeaderFontSize, 0, 3, 2, GroupGapBelowDip);
        for (var i = 0; i < view.Headers.Count; i++)
        {
            AddHeading(view.Headers[i], i == 0 ? PanelMetrics.TableHeaderFontSize : DataHeaderFontSize, 1, i, 1, HeaderGapBelowDip);
        }

        for (var i = 0; i < view.Rows.Count; i++)
        {
            AddRow(view.Rows[i], i + 2);
        }

        // A line at the left of every data column. Those between a pair start under the group
        // heading, which runs across them.
        for (var i = 0; i < DataColumns; i++)
        {
            var firstOfPair = i % 2 == 0;
            var line = NewLine(PanelMetrics.ColumnLineOpacity);
            line.Width = LineDip;
            line.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetRowSpan(line, firstOfPair ? rowCount : rowCount - 1);
            Place(line, firstOfPair ? 0 : 1, 1 + i);
        }
    }

    private void AddHeading(string text, double fontSize, int row, int column, int span, double gapBelow)
    {
        var heading = new LetterSpacedText
        {
            Text = text.ToUpperInvariant(),
            HorizontalAlignment = column == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Center,
            Margin = column > 0 ? CellMargin(0, gapBelow * _scale) : new Thickness(0, 0, 0, gapBelow * _scale),
        };
        TextElement.SetFontSize(heading, fontSize);
        TextElement.SetForeground(heading, Palette("TextMuted"));
        AutomationProperties.SetName(heading, text);
        Grid.SetColumnSpan(heading, span);
        Place(heading, row, column);
    }

    private void AddRow(PanelModelRowView row, int gridRow)
    {
        var above = LineDip + RowPaddingDip * _scale;
        var below = RowPaddingDip * _scale;

        var line = NewLine(PanelMetrics.SoftLineOpacity);
        line.Height = LineDip;
        line.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumnSpan(line, table.ColumnDefinitions.Count);
        Place(line, gridRow, 0);

        var name = NameCell(row);
        name.Margin = new Thickness(0, above, PanelMetrics.TableCellPadRightDip, below);
        Place(name, gridRow, 0);

        PanelModelFigureView[] windows = [row.Today, row.Window];
        for (var i = 0; i < windows.Length; i++)
        {
            AddFigure(windows[i].Tokens, PanelMetrics.ModelTokensFontSize, FontWeights.SemiBold, "TextPrimary", gridRow, 1 + 2 * i, above, below);
            AddFigure(
                windows[i].Value, PanelMetrics.ModelValueFontSize, FontWeights.Normal,
                windows[i].ValueKnown ? "TextSecondary" : "TextMuted", gridRow, 2 + 2 * i, above, below);
        }
    }

    /// <summary>
    /// The chip and the name. The one cell that may trim, with the full id a hover away: the
    /// transparent fill is what lets the pointer find the cell beyond the end of a short name.
    /// </summary>
    private DockPanel NameCell(PanelModelRowView row)
    {
        var chip = HoverCard.Chip(PopupDoughnut.Swatch(owner, row));
        chip.VerticalAlignment = VerticalAlignment.Center;
        chip.Margin = new Thickness(0, 0, 6, 0);
        DockPanel.SetDock(chip, Dock.Left);

        var cell = new DockPanel { Background = Brushes.Transparent, ToolTip = HoverCard.Text(owner, row.Id) };
        HoverCard.ApplyTiming(cell);
        cell.Children.Add(chip);
        cell.Children.Add(new TextBlock
        {
            Text = row.Name,
            FontSize = PanelMetrics.ModelTokensFontSize,
            Foreground = Palette("TextSecondary"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return cell;
    }

    private void AddFigure(string text, double fontSize, FontWeight weight, string brush, int row, int column, double above, double below)
    {
        var figure = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            FontWeight = weight,
            Foreground = Palette(brush),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = CellMargin(above, below),
        };
        Typography.SetNumeralAlignment(figure, FontNumeralAlignment.Tabular);
        Place(figure, row, column);
    }

    private static Thickness CellMargin(double above, double below) =>
        new(LineDip + PanelMetrics.TableCellPadLeftDip, above, PanelMetrics.TableCellPadRightDip, below);

    /// <summary>A hairline with its strength on the brush, which is where the totals table keeps it.</summary>
    private Border NewLine(double strength)
    {
        var brush = Palette("PanelBorder").Clone();
        brush.Opacity = strength;
        return new Border { Background = brush, SnapsToDevicePixels = true, IsHitTestVisible = false };
    }

    private void Place(UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        table.Children.Add(element);
        _rows[row].Add(element);
    }

    private Brush Palette(string key) => (Brush)owner.FindResource(key);
}
