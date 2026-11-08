using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OView.App.Rendering;
using OView.Presentation.Panel;
using static OView.Linux.Panel.PanelPrimitives;

namespace OView.Linux.Panel;

/// <summary>
/// Lays out the local block's table from its view model.
///
/// <para><b>The label column takes what the data columns leave.</b> The data columns are the
/// widths the figures were measured against, and this head's column is not one width: the compact
/// density's padding widens it and the scroll bar narrows it. A fixed label column would be wrong
/// at every one of them but the width it was written for.</para>
///
/// <para><b>The lines are their own elements, drawn over the cells</b> rather than each cell's
/// border, because a <see cref="Border"/> has one brush and the row lines and column lines are
/// different strengths. Each data cell allows for the column line's own pixel on the left, which
/// is what puts the centre of its text on the centre of the column.</para>
/// </summary>
internal sealed class PanelTotalsTable(LinuxPanelTheme theme)
{
    /// <summary>Space above and below the text in a row, before the density.</summary>
    private const double RowPaddingDip = 4;

    /// <summary>Space between the column headings and the first row line.</summary>
    private const double HeaderGapBelowDip = 5;

    /// <summary>The line's own pixel, which the cell's padding starts after.</summary>
    private const double LineDip = 1;

    private readonly LinuxPanelTheme _theme = theme;

    /// <summary>Tightens the rows with the rest of the panel; the columns and the type stay put.</summary>
    public Grid Build(PanelLocalTableView table, double spacingScale)
    {
        var dataColumns = table.Headers.Count - 1;
        var rowCount = table.Rows.Count + 1;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        for (var i = 0; i < dataColumns; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(PanelMetrics.OutputColumnDip, GridUnitType.Pixel));
        }

        for (var i = 0; i < rowCount; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        AddHeaders(grid, table.Headers, spacingScale);
        for (var i = 0; i < table.Rows.Count; i++)
        {
            AddRow(grid, table.Rows[i], i + 1, spacingScale);
        }

        // Last, so they stand over the row lines they cross.
        for (var i = 0; i < dataColumns; i++)
        {
            var line = ColumnLine(_theme);
            Grid.SetRowSpan(line, rowCount);
            Place(grid, line, 0, 1 + i);
        }

        return grid;
    }

    private void AddHeaders(Grid grid, IReadOnlyList<string> headers, double scale)
    {
        for (var i = 0; i < headers.Count; i++)
        {
            var size = PanelMetrics.TableHeaderFontSize;
            var header = Caption(headers[i], size, _theme["TextMuted"]);
            var data = i > 0;

            header.HorizontalAlignment = data ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            header.Margin = new Thickness(
                header.Margin.Left + (data ? LineDip + PanelMetrics.TableCellPadLeftDip : 0), 0,
                data ? PanelMetrics.TableCellPadRightDip : 0, HeaderGapBelowDip * scale);
            Place(grid, header, 0, i);
        }
    }

    private void AddRow(Grid grid, PanelLocalRowView row, int gridRow, double scale)
    {
        var padding = RowPaddingDip * scale;
        var above = LineDip + padding;

        var line = SoftLine(_theme);
        line.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumnSpan(line, grid.ColumnDefinitions.Count);
        Place(grid, line, gridRow, 0);

        var label = Text(row.Label, PanelMetrics.TableLabelFontSize, _theme["TextSecondary"]);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(0, above, 0, padding);
        Place(grid, label, gridRow, 0);

        for (var i = 0; i < row.Cells.Count; i++)
        {
            var figure = Text(row.Cells[i], PanelMetrics.TableFigureFontSize, _theme["TextPrimary"], FontWeight.SemiBold);
            figure.FontFeatures = [new FontFeature { Tag = "tnum" }];
            figure.TextAlignment = TextAlignment.Center;
            figure.HorizontalAlignment = HorizontalAlignment.Center;
            figure.VerticalAlignment = VerticalAlignment.Center;
            figure.Margin = new Thickness(
                LineDip + PanelMetrics.TableCellPadLeftDip, above, PanelMetrics.TableCellPadRightDip, padding);
            Place(grid, figure, gridRow, 1 + i);
        }
    }

    private static void Place(Grid grid, Control element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }
}
