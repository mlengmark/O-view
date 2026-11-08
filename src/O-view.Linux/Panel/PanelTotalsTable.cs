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

    /// <summary>A token kind's colour chip, the size the Windows panel gives it.</summary>
    private const double ChipSizeDip = 8;

    private const double ChipGapDip = 6;

    private const double ChipRadiusDip = 2;

    private readonly LinuxPanelTheme _theme = theme;

    /// <summary>Tightens the rows with the rest of the panel; the columns and the type stay put.</summary>
    public Grid Build(PanelLocalTableView table, double spacingScale)
    {
        var dataColumns = table.Headers.Count - 1;
        var columnWidth = dataColumns == 2 ? PanelMetrics.OutputColumnDip : PanelMetrics.OverallColumnDip;
        var rowCount = table.Rows.Count + 1;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        for (var i = 0; i < dataColumns; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(columnWidth, GridUnitType.Pixel));
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

        // Last, so they stand over the row lines they cross. On the value row a pill covers a pair
        // of columns, so only the lines at the left of a pair run down to it.
        var lastRowSpan = table.Rows.Count > 0 ? table.Rows[^1].CellSpan : 1;
        for (var i = 0; i < dataColumns; i++)
        {
            var line = ColumnLine(_theme);
            Grid.SetRowSpan(line, i % lastRowSpan == 0 ? rowCount : rowCount - 1);
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
        var pills = row.CellSpan > 1;
        var above = LineDip + (pills ? PanelMetrics.PillGapAboveDip : RowPaddingDip) * scale;
        var below = (pills ? PanelMetrics.PillGapBelowDip : RowPaddingDip) * scale;

        var line = SoftLine(_theme);
        line.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumnSpan(line, grid.ColumnDefinitions.Count);
        Place(grid, line, gridRow, 0);

        var label = RowLabel(row);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(0, above, 0, below);
        Place(grid, label, gridRow, 0);

        for (var i = 0; i < row.Cells.Count; i++)
        {
            var share = !pills && row.Cells.Count == 4 && i % 2 == 1;
            Control cell = pills ? Pill(row.Cells[i]) : Figure(row.Cells[i], share);
            cell.HorizontalAlignment = HorizontalAlignment.Center;
            cell.VerticalAlignment = VerticalAlignment.Center;
            cell.Margin = new Thickness(
                LineDip + PanelMetrics.TableCellPadLeftDip, above, PanelMetrics.TableCellPadRightDip, below);

            Grid.SetColumnSpan(cell, row.CellSpan);
            Place(grid, cell, gridRow, 1 + i * row.CellSpan);
        }
    }

    private Control RowLabel(PanelLocalRowView row)
    {
        var total = row.RowKind == PanelLocalRowKind.Total;
        var label = Text(
            row.Label, PanelMetrics.TableLabelFontSize, _theme[total ? "TextPrimary" : "TextSecondary"],
            total ? FontWeight.SemiBold : FontWeight.Normal);

        if (row.Kind is not { } kind)
        {
            return label;
        }

        label.VerticalAlignment = VerticalAlignment.Center;

        var chipped = new StackPanel { Orientation = Orientation.Horizontal };
        chipped.Children.Add(new Border
        {
            Width = ChipSizeDip,
            Height = ChipSizeDip,
            CornerRadius = new CornerRadius(ChipRadiusDip),
            Background = _theme[TokenBarGeometry.PaletteKey(kind)],
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, ChipGapDip, 0),
        });
        chipped.Children.Add(label);
        return chipped;
    }

    private TextBlock Figure(string text, bool share)
    {
        var figure = Text(
            text,
            share ? PanelMetrics.TableShareFontSize : PanelMetrics.TableFigureFontSize,
            _theme[share ? "TextSecondary" : "TextPrimary"],
            share ? FontWeight.Normal : FontWeight.SemiBold);
        figure.FontFeatures = [new FontFeature { Tag = "tnum" }];
        figure.TextAlignment = TextAlignment.Center;
        return figure;
    }

    /// <summary>One estimate in its pill, across the pair of columns it belongs to.</summary>
    private Border Pill(string text) => new()
    {
        Background = _theme["TileBg"],
        BorderBrush = Softened(_theme, "PanelBorder", PanelMetrics.SoftLineOpacity),
        BorderThickness = new Thickness(PanelMetrics.PillBorderDip),
        CornerRadius = new CornerRadius(PanelMetrics.PillRadiusDip),
        Padding = new Thickness(PanelMetrics.PillPaddingXDip, PanelMetrics.PillPaddingYDip),
        Child = Figure(text, share: false),
    };

    private static void Place(Grid grid, Control element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }
}
