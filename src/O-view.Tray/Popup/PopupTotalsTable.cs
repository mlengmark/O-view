using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using OView.App.Rendering;
using OView.Presentation.Panel;
using Brush = System.Windows.Media.Brush;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using Size = System.Windows.Size;

namespace OView.Tray.Popup;

/// <summary>
/// Lays out the local block's table, Output's or Overall's, from the one view model both are.
///
/// <para><b>Built in code</b> because the two differ in rows and in column count, never in
/// grammar: every cell here is placed by the same three rules, so a figure cannot be centred one
/// way in one table and another way in the other.</para>
///
/// <para><b>The lines are their own elements, drawn over the cells</b> rather than each cell's
/// border, because a <c>Border</c> has one brush and the row lines and column lines are different
/// strengths. Each data cell allows for the column line's own pixel on the left, which is what
/// puts the centre of its text on the centre of the column.</para>
///
/// <para><b>The switch folds it.</b> The old rows leave, and then the height moves while the new
/// rows arrive, on <see cref="DisclosureAnimation"/>, whose re-docking is why the panel grows out
/// of its corner rather than into the taskbar. A press during any of that continues from what is
/// on screen: the height turns round where it stands, and rows still leaving come back.</para>
/// </summary>
/// <param name="refitForPendingGrowth">Re-fits the panel for height it is about to gain or lose.</param>
/// <param name="redock">Re-docks the panel at its current height.</param>
internal sealed class PopupTotalsTable(
    Window owner, Grid host, Action<double> refitForPendingGrowth, Action redock)
{
    /// <summary>Space above and below the text in a row, before the density.</summary>
    private const double RowPaddingDip = 4;

    /// <summary>Space between the column headings and the first row line.</summary>
    private const double HeaderGapBelowDip = 5;

    /// <summary>A token kind's colour chip, the size the legends used.</summary>
    private const double ChipSizeDip = 8;

    private const double ChipGapDip = 6;

    private const double ChipRadiusDip = 2;

    /// <summary>The line's own pixel, which the cell's padding starts after.</summary>
    private const double LineDip = 1;

    private static readonly double RowsLeaveMs = PanelMotion.Ms(PanelMotion.RowsCloseFadeBaseMs);

    private readonly DisclosureAnimation _fold = new(
        owner, host, chevron: null,
        PanelMotion.Ms(PanelMotion.FoldOpenBaseMs), PanelMotion.Ms(PanelMotion.FoldCloseBaseMs),
        new DisclosureRowMotion(
            PanelMotion.RowStartMs, PanelMotion.Ms(PanelMotion.RowFadeBaseMs), RowsLeaveMs, PanelMotion.RowRiseDip),
        redock);

    private PanelLocalTableView? _table;
    private double _scale = 1;

    /// <summary>What is in the grid, by row: the cells that fade and rise together.</summary>
    private List<List<UIElement>> _rows = [];

    /// <summary>Whether <see cref="_rows"/> have been given to the fold since they were built.</summary>
    private bool _armed;

    /// <summary>The height the fold is heading for.</summary>
    private double _target;

    /// <summary>Counts down the old rows' fade; null when no rows are waiting to be replaced.</summary>
    private DispatcherTimer? _swap;

    /// <summary>True while a press re-fits the panel, whose density passes would rebuild the rows.</summary>
    private bool _refitting;

    /// <summary>The table's height now: its animated height mid-fold, and its laid-out height at rest.</summary>
    public double Height => _fold.Current.Height;

    /// <summary>Shows a table at rest, ending any fold, because every open shows a settled view.</summary>
    public void Populate(PanelLocalTableView table)
    {
        CancelSwap();
        _fold.Apply(expanded: true);

        // Set on the fold's live path only: a clip left on at rest moves the text under it by
        // one colour level.
        host.ClipToBounds = false;

        _table = table;
        Build();
    }

    /// <summary>
    /// Tightens the rows with the rest of the panel; the columns and the type stay put. Mid-fold
    /// the rows on screen are left alone, and the next ones are built at this scale.
    /// </summary>
    public void ApplyDensity(double spacingScale)
    {
        _scale = spacingScale;

        if (!_refitting && _swap is null && !_fold.IsMoving)
        {
            Build();
        }
    }

    /// <summary>
    /// The switch was pressed: folds to <paramref name="table"/>, from whatever is on screen.
    ///
    /// <para>In the order the breakdown's fold keeps: pinned before anything is laid out, so the
    /// window is never laid out at the far end of the fold.</para>
    /// </summary>
    public void SwitchTo(PanelLocalTableView table)
    {
        if (!PanelMotion.Runs(WpfMotion.AnimationsOn, PanelMotion.Ms(PanelMotion.FoldOpenBaseMs)))
        {
            Populate(table);
            refitForPendingGrowth(0);
            redock();
            return;
        }

        // Read before Prepare stops the fold, which is the one reporting them.
        var leaving = _swap is not null;
        var moving = _fold.IsMoving;
        var from = Height;

        _fold.Prepare(from);
        host.ClipToBounds = true;

        _refitting = true;
        refitForPendingGrowth(NaturalHeight(table) - from);
        _refitting = false;
        owner.UpdateLayout();
        redock();

        // Again, because the re-fit may have changed the density the rows are built at.
        _target = NaturalHeight(table);
        _table = table;

        // Still leaving, so the rows on screen are the very ones this press asks for: they come
        // back from where they are, and nothing is replaced.
        if (leaving)
        {
            CancelSwap();
            _fold.Resize(from, _target);
            _fold.ArriveRows();
            return;
        }

        Arm();
        _fold.LeaveRows();

        // Turns round now; from rest, the height waits for the old rows to go.
        if (moving)
        {
            _fold.Resize(from, _target);
        }

        // Normal priority rather than the timer's default Background, which a fold's own layout
        // passes would hold back until the frames stop.
        _swap = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(RowsLeaveMs) };
        _swap.Tick += (_, _) => Swap();
        _swap.Start();
    }

    /// <summary>
    /// A frame of the switch at <paramref name="atMs"/> after the press, from the same curves and
    /// figures the fold runs, for a verification render.
    /// </summary>
    public void ApplyPartialSwitch(PanelLocalTableView table, double atMs)
    {
        var from = host.ActualHeight;
        var to = NaturalHeight(table);

        _table = table;
        Build();
        Arm();
        host.ClipToBounds = true;
        _fold.ApplyPartial(atMs - RowsLeaveMs, to, from);
    }

    /// <summary>The old rows have gone: the new ones take their place and the height moves.</summary>
    private void Swap()
    {
        CancelSwap();

        var moving = _fold.IsMoving;
        Build();
        Arm();
        _fold.ParkRows();

        if (!moving)
        {
            _fold.Resize(Height, _target);
        }

        _fold.ArriveRows();
    }

    private void CancelSwap()
    {
        _swap?.Stop();
        _swap = null;
    }

    /// <summary>Gives the rows in the grid to the fold, once, so a row already moving keeps its rise.</summary>
    private void Arm()
    {
        if (!_armed)
        {
            _armed = true;
            _fold.SetRows(_rows);
        }
    }

    /// <summary>
    /// How tall <paramref name="table"/> would be, without showing it. A child of the real grid
    /// for as long as the sum takes, so it measures with the fonts the panel hands down, and gone
    /// again before anything lays it out.
    /// </summary>
    private double NaturalHeight(PanelLocalTableView table)
    {
        var probe = new Grid();
        Fill(probe, table);
        host.Children.Add(probe);
        probe.Measure(new Size(host.ActualWidth, double.PositiveInfinity));
        host.Children.Remove(probe);
        return probe.DesiredSize.Height;
    }

    private void Build()
    {
        if (_table is { } table)
        {
            _rows = Fill(host, table);
            _armed = false;
        }
    }

    /// <summary>Lays the table out in <paramref name="grid"/> and returns its cells by row, the headings first.</summary>
    private List<List<UIElement>> Fill(Grid grid, PanelLocalTableView table)
    {
        grid.Children.Clear();
        grid.RowDefinitions.Clear();
        grid.ColumnDefinitions.Clear();

        var dataColumns = table.Headers.Count - 1;
        var columnWidth = dataColumns == 2 ? PanelMetrics.OutputColumnDip : PanelMetrics.OverallColumnDip;

        // The label column is the star, so the data columns stay at the widths the figures were
        // measured against and a wider compact panel only gives the labels more room.
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < dataColumns; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(columnWidth) });
        }

        var rowCount = table.Rows.Count + 1;
        var rows = new List<List<UIElement>>(rowCount);
        for (var i = 0; i < rowCount; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rows.Add([]);
        }

        AddHeaders(grid, rows[0], table);
        for (var i = 0; i < table.Rows.Count; i++)
        {
            AddRow(grid, rows[i + 1], table.Rows[i], i + 1);
        }

        // With the headings: a line runs the whole height, so it belongs to no one row, and the
        // headings are what it starts from.
        AddColumnLines(grid, rows[0], dataColumns, rowCount, table.Rows.Count > 0 ? table.Rows[^1].CellSpan : 1);
        return rows;
    }

    private void AddHeaders(Grid grid, List<UIElement> cells, PanelLocalTableView table)
    {
        for (var i = 0; i < table.Headers.Count; i++)
        {
            var header = new LetterSpacedText
            {
                Text = table.Headers[i].ToUpperInvariant(),
                HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Center,
                Margin = new Thickness(
                    i == 0 ? 0 : LineDip + PanelMetrics.TableCellPadLeftDip, 0,
                    i == 0 ? 0 : PanelMetrics.TableCellPadRightDip, HeaderGapBelowDip * _scale),
            };
            TextElement.SetFontSize(header, PanelMetrics.TableHeaderFontSize);
            TextElement.SetForeground(header, Palette("TextMuted"));
            AutomationProperties.SetName(header, table.Headers[i]);
            Place(grid, cells, header, 0, i);
        }
    }

    private void AddRow(Grid grid, List<UIElement> cells, PanelLocalRowView row, int gridRow)
    {
        var pills = row.CellSpan > 1;
        var above = LineDip + (pills ? PanelMetrics.PillGapAboveDip : RowPaddingDip) * _scale;
        var below = (pills ? PanelMetrics.PillGapBelowDip : RowPaddingDip) * _scale;

        var line = NewLine(PanelMetrics.SoftLineOpacity);
        line.Height = LineDip;
        line.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumnSpan(line, grid.ColumnDefinitions.Count);
        Place(grid, cells, line, gridRow, 0);

        var label = RowLabel(row);
        label.Margin = new Thickness(0, above, 0, below);
        Place(grid, cells, label, gridRow, 0);

        for (var i = 0; i < row.Cells.Count; i++)
        {
            var share = !pills && row.Cells.Count == 4 && i % 2 == 1;
            FrameworkElement cell = pills ? Pill(row.Cells[i]) : Figure(row.Cells[i], share);
            cell.HorizontalAlignment = HorizontalAlignment.Center;
            cell.VerticalAlignment = VerticalAlignment.Center;
            cell.Margin = new Thickness(
                LineDip + PanelMetrics.TableCellPadLeftDip, above, PanelMetrics.TableCellPadRightDip, below);

            Grid.SetColumnSpan(cell, row.CellSpan);
            Place(grid, cells, cell, gridRow, 1 + i * row.CellSpan);
        }
    }

    /// <summary>
    /// A line at the left of every data column, running the full height. On the value row a pill
    /// covers a pair of columns, so only the lines at the left of a pair reach it.
    /// </summary>
    private void AddColumnLines(Grid grid, List<UIElement> cells, int dataColumns, int rowCount, int lastRowSpan)
    {
        for (var i = 0; i < dataColumns; i++)
        {
            var line = NewLine(PanelMetrics.ColumnLineOpacity);
            line.Width = LineDip;
            line.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetRowSpan(line, i % lastRowSpan == 0 ? rowCount : rowCount - 1);
            Place(grid, cells, line, 0, 1 + i);
        }
    }

    private FrameworkElement RowLabel(PanelLocalRowView row)
    {
        var total = row.RowKind == PanelLocalRowKind.Total;
        var label = new TextBlock
        {
            Text = row.Label,
            FontSize = PanelMetrics.TableLabelFontSize,
            FontWeight = total ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = Palette(total ? "TextPrimary" : "TextSecondary"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (row.Kind is not { } kind)
        {
            return label;
        }

        var chipped = new StackPanel { Orientation = Orientation.Horizontal };
        chipped.Children.Add(new Border
        {
            Width = ChipSizeDip,
            Height = ChipSizeDip,
            CornerRadius = new CornerRadius(ChipRadiusDip),
            Background = Palette(TokenBarGeometry.PaletteKey(kind)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, ChipGapDip, 0),
        });
        chipped.Children.Add(label);
        return chipped;
    }

    private TextBlock Figure(string text, bool share)
    {
        var figure = new TextBlock
        {
            Text = text,
            FontSize = share ? PanelMetrics.TableShareFontSize : PanelMetrics.TableFigureFontSize,
            FontWeight = share ? FontWeights.Normal : FontWeights.SemiBold,
            Foreground = Palette(share ? "TextSecondary" : "TextPrimary"),
        };
        Typography.SetNumeralAlignment(figure, FontNumeralAlignment.Tabular);
        return figure;
    }

    /// <summary>
    /// One estimate in its pill. The border is a soft line, so its strength is on the brush: an
    /// opacity on the element would fade the fill and the figure with it.
    /// </summary>
    private Border Pill(string text)
    {
        var border = Palette("PanelBorder").Clone();
        border.Opacity = PanelMetrics.SoftLineOpacity;

        return new Border
        {
            Background = Palette("TileBg"),
            BorderBrush = border,
            BorderThickness = new Thickness(PanelMetrics.PillBorderDip),
            CornerRadius = new CornerRadius(PanelMetrics.PillRadiusDip),
            Padding = new Thickness(
                PanelMetrics.PillPaddingXDip, PanelMetrics.PillPaddingYDip,
                PanelMetrics.PillPaddingXDip, PanelMetrics.PillPaddingYDip),
            Child = Figure(text, share: false),
        };
    }

    /// <summary>
    /// A hairline, its strength on the brush for the pill's reason and one more: the fold fades
    /// every cell to an opacity of 1, which would bring a line up to full strength.
    /// </summary>
    private Border NewLine(double strength)
    {
        var brush = Palette("PanelBorder").Clone();
        brush.Opacity = strength;

        return new Border
        {
            Background = brush,
            SnapsToDevicePixels = true,
            IsHitTestVisible = false,
        };
    }

    private static void Place(Grid grid, List<UIElement> cells, UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
        cells.Add(element);
    }

    private Brush Palette(string key) => (Brush)owner.FindResource(key);
}
