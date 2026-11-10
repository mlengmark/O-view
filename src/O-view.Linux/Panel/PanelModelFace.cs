using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OView.App.Rendering;
using OView.Presentation.Panel;
using static OView.Linux.Panel.PanelPrimitives;

namespace OView.Linux.Panel;

/// <summary>
/// The local block's model face: the two doughnuts and the table of every model behind them.
///
/// <para>Everything is read from <see cref="PanelModelFaceView"/>. Which models, in which order
/// and in which colour was decided there, so the rows and the sections cannot disagree; what is
/// left here is the table's grammar, which is the totals table's with a group heading added.</para>
/// </summary>
internal sealed class PanelModelFace(LinuxPanelTheme theme)
{
    private const double DoughnutsGapAboveDip = 10;

    private const double DoughnutColumnGapDip = 12;

    /// <summary>Space above and below the text in a row, before the density.</summary>
    private const double RowPaddingDip = 4;

    private const double HeaderGapBelowDip = 4;

    private const double GroupGapBelowDip = 2;

    /// <summary>
    /// The four data columns' headings, one size down from the totals tables', as on the Windows
    /// panel: the columns are narrow enough that <c>EST. VALUE</c> would fill its cell at theirs.
    /// </summary>
    private const double DataHeaderFontSize = 9;

    /// <summary>The line's own pixel, which a cell's padding starts after.</summary>
    private const double LineDip = 1;

    private const int DataColumns = 4;

    private readonly PanelDoughnut _doughnut = new(theme);

    private readonly PanelHoverCard _cards = new(theme);

    /// <summary>The doughnuts last built, which <see cref="Sweep"/> fills in.</summary>
    private PanelDoughnut.Doughnut[] _shown = [];

    /// <summary>Both doughnuts last built sweep in together, after <paramref name="delayMs"/>.</summary>
    public void Sweep(LinuxMotion motion, double delayMs)
    {
        foreach (var doughnut in _shown)
        {
            _doughnut.Sweep(doughnut, motion, delayMs);
        }
    }

    /// <summary>Tightens the gaps and the rows with the rest of the panel; the rings and the type stay put.</summary>
    public void AddTo(StackPanel block, PanelModelFaceView view, double spacingScale)
    {
        var rows = view.Table.Rows;

        var today = _doughnut.Build(view.Today, rows, cardOpensLeft: false);
        var window = _doughnut.Build(view.Window, rows, cardOpensLeft: true);
        Grid.SetColumn(window, 2);
        _shown = [today, window];

        var doughnuts = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"*,{DoughnutColumnGapDip},*"),
            Margin = new Thickness(0, DoughnutsGapAboveDip * spacingScale, 0, 0),
        };
        doughnuts.Children.Add(today);
        doughnuts.Children.Add(window);
        block.Children.Add(doughnuts);

        var table = Table(view.Table, spacingScale);
        table.Margin = new Thickness(0, PanelMetrics.DoughnutsToTableGapDip * spacingScale, 0, 0);
        block.Children.Add(table);
    }

    private Grid Table(PanelModelTableView view, double scale)
    {
        // The name is the star, so a wider compact panel only gives the names more room.
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        for (var i = 0; i < DataColumns; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(PanelMetrics.ModelColumnDip, GridUnitType.Pixel));
        }

        var rowCount = view.Rows.Count + 2;
        for (var i = 0; i < rowCount; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        AddHeading(grid, view.TodayGroup, PanelMetrics.TableHeaderFontSize, 0, 1, 2, GroupGapBelowDip * scale);
        AddHeading(grid, view.WindowGroup, PanelMetrics.TableHeaderFontSize, 0, 3, 2, GroupGapBelowDip * scale);
        for (var i = 0; i < view.Headers.Count; i++)
        {
            AddHeading(
                grid, view.Headers[i], i == 0 ? PanelMetrics.TableHeaderFontSize : DataHeaderFontSize,
                1, i, 1, HeaderGapBelowDip * scale);
        }

        for (var i = 0; i < view.Rows.Count; i++)
        {
            AddRow(grid, view.Rows[i], i + 2, scale);
        }

        // Last, so they stand over the row lines they cross. Those between a pair start under the
        // group heading, which runs across them.
        for (var i = 0; i < DataColumns; i++)
        {
            var line = ColumnLine(theme);
            Grid.SetRowSpan(line, rowCount - i % 2);
            PanelTotalsTable.Place(grid, line, i % 2, 1 + i);
        }

        return grid;
    }

    private void AddHeading(Grid grid, string text, double fontSize, int row, int column, int span, double gapBelow)
    {
        var heading = Caption(text, fontSize, theme["TextMuted"]);

        // The whole column, without the cell padding a figure keeps: the wider Linux font needs
        // it, and the padding is equal on both sides, so the centre does not move.
        heading.HorizontalAlignment = column > 0 ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        heading.Margin = new Thickness(heading.Margin.Left, 0, 0, gapBelow);
        Grid.SetColumnSpan(heading, span);
        PanelTotalsTable.Place(grid, heading, row, column);
    }

    private void AddRow(Grid grid, PanelModelRowView row, int gridRow, double scale)
    {
        var above = LineDip + RowPaddingDip * scale;
        var below = RowPaddingDip * scale;

        var line = SoftLine(theme);
        line.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumnSpan(line, grid.ColumnDefinitions.Count);
        PanelTotalsTable.Place(grid, line, gridRow, 0);

        var name = NameCell(row);
        name.Margin = new Thickness(0, above, PanelMetrics.TableCellPadRightDip, below);
        PanelTotalsTable.Place(grid, name, gridRow, 0);

        PanelModelFigureView[] windows = [row.Today, row.Window];
        for (var i = 0; i < windows.Length; i++)
        {
            var tokens = Figure(windows[i].Tokens, PanelMetrics.ModelTokensFontSize, FontWeight.SemiBold, "TextPrimary", above, below);
            PanelTotalsTable.Place(grid, tokens, gridRow, 1 + 2 * i);

            var value = Figure(
                windows[i].Value, PanelMetrics.ModelValueFontSize, FontWeight.Normal,
                windows[i].ValueKnown ? "TextSecondary" : "TextMuted", above, below);
            PanelTotalsTable.Place(grid, value, gridRow, 2 + 2 * i);
        }
    }

    /// <summary>
    /// The chip and the name. The one cell that may trim, with the full id in the panel's hover
    /// card: the transparent fill is what lets the pointer find the cell beyond the end of a short name.
    /// </summary>
    private DockPanel NameCell(PanelModelRowView row)
    {
        var chip = PanelHoverCard.Chip(_doughnut.Swatch(row));
        DockPanel.SetDock(chip, Dock.Left);

        var name = Text(row.Name, PanelMetrics.ModelTokensFontSize, theme["TextSecondary"]);
        name.TextWrapping = TextWrapping.NoWrap;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.VerticalAlignment = VerticalAlignment.Center;

        var cell = new DockPanel { Background = Brushes.Transparent };
        ToolTip.SetTip(cell, _cards.Text(row.Id));
        cell.Children.Add(chip);
        cell.Children.Add(name);
        return cell;
    }

    private TextBlock Figure(string text, double fontSize, FontWeight weight, string brush, double above, double below)
    {
        var figure = Text(text, fontSize, theme[brush], weight);
        figure.FontFeatures = [new FontFeature { Tag = "tnum" }];
        figure.HorizontalAlignment = HorizontalAlignment.Center;
        figure.VerticalAlignment = VerticalAlignment.Center;
        figure.Margin = new Thickness(
            LineDip + PanelMetrics.TableCellPadLeftDip, above, PanelMetrics.TableCellPadRightDip, below);
        return figure;
    }
}
