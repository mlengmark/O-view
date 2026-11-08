using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using OView.App.Rendering;
using OView.Presentation.Panel;
using Brush = System.Windows.Media.Brush;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

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
/// </summary>
internal sealed class PopupTotalsTable(FrameworkElement owner, Grid host)
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

    private PanelLocalTableView? _table;
    private double _scale = 1;

    public void Populate(PanelLocalTableView table)
    {
        _table = table;
        Build();
    }

    /// <summary>Tightens the rows with the rest of the panel; the columns and the type stay put.</summary>
    public void ApplyDensity(double spacingScale)
    {
        _scale = spacingScale;
        Build();
    }

    private void Build()
    {
        if (_table is not { } table)
        {
            return;
        }

        host.Children.Clear();
        host.RowDefinitions.Clear();
        host.ColumnDefinitions.Clear();

        var dataColumns = table.Headers.Count - 1;
        var columnWidth = dataColumns == 2 ? PanelMetrics.OutputColumnDip : PanelMetrics.OverallColumnDip;

        // The label column is the star, so the data columns stay at the widths the figures were
        // measured against and a wider compact panel only gives the labels more room.
        host.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < dataColumns; i++)
        {
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(columnWidth) });
        }

        var rowCount = table.Rows.Count + 1;
        for (var i = 0; i < rowCount; i++)
        {
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        AddHeaders(table);
        for (var i = 0; i < table.Rows.Count; i++)
        {
            AddRow(table.Rows[i], i + 1);
        }

        AddColumnLines(dataColumns, rowCount, table.Rows.Count > 0 ? table.Rows[^1].CellSpan : 1);
    }

    private void AddHeaders(PanelLocalTableView table)
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
            Place(header, 0, i);
        }
    }

    private void AddRow(PanelLocalRowView row, int gridRow)
    {
        var pills = row.CellSpan > 1;
        var above = LineDip + (pills ? PanelMetrics.PillGapAboveDip : RowPaddingDip) * _scale;
        var below = (pills ? PanelMetrics.PillGapBelowDip : RowPaddingDip) * _scale;

        var line = NewLine(PanelMetrics.SoftLineOpacity);
        line.Height = LineDip;
        line.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumnSpan(line, host.ColumnDefinitions.Count);
        Place(line, gridRow, 0);

        var label = RowLabel(row);
        label.Margin = new Thickness(0, above, 0, below);
        Place(label, gridRow, 0);

        for (var i = 0; i < row.Cells.Count; i++)
        {
            var share = !pills && row.Cells.Count == 4 && i % 2 == 1;
            FrameworkElement cell = pills ? Pill(row.Cells[i]) : Figure(row.Cells[i], share);
            cell.HorizontalAlignment = HorizontalAlignment.Center;
            cell.VerticalAlignment = VerticalAlignment.Center;
            cell.Margin = new Thickness(
                LineDip + PanelMetrics.TableCellPadLeftDip, above, PanelMetrics.TableCellPadRightDip, below);

            Grid.SetColumnSpan(cell, row.CellSpan);
            Place(cell, gridRow, 1 + i * row.CellSpan);
        }
    }

    /// <summary>
    /// A line at the left of every data column, running the full height. On the value row a pill
    /// covers a pair of columns, so only the lines at the left of a pair reach it.
    /// </summary>
    private void AddColumnLines(int dataColumns, int rowCount, int lastRowSpan)
    {
        for (var i = 0; i < dataColumns; i++)
        {
            var line = NewLine(PanelMetrics.ColumnLineOpacity);
            line.Width = LineDip;
            line.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetRowSpan(line, i % lastRowSpan == 0 ? rowCount : rowCount - 1);
            Place(line, 0, 1 + i);
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

    private Border NewLine(double strength) => new()
    {
        Background = Palette("PanelBorder"),
        Opacity = strength,
        SnapsToDevicePixels = true,
        IsHitTestVisible = false,
    };

    private void Place(UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        host.Children.Add(element);
    }

    private Brush Palette(string key) => (Brush)owner.FindResource(key);
}
