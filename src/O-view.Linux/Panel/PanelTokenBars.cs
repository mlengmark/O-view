using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using OView.App.Rendering;
using OView.Core.Models;
using OView.Core.Providers.Jsonl;
using OView.Presentation.Panel;
using OView.Presentation.Text;
using static OView.Linux.Panel.PanelPrimitives;

namespace OView.Linux.Panel;

/// <summary>
/// The "Tokens used" block: a segmented composition bar per window, the Bars/Breakdown switch,
/// and the table behind it.
///
/// <para>Every figure arrives built, in <see cref="TokenComposition"/> and
/// <see cref="TokenText"/>; the segment widths come from the shared
/// <see cref="TokenBarGeometry"/>.</para>
/// </summary>
internal sealed class PanelTokenBars(LinuxPanelTheme theme)
{
    private readonly LinuxPanelTheme _theme = theme;

    public void AddTo(StackPanel root, PanelStatistics stats, TranscriptScopeReport? scopeReport)
    {
        // What was billed, beside tiles that headline only output (issue #253). Omitted when
        // there is nothing to break down — a composition of zero explains nothing.
        var today = stats.CompositionToday;
        var window31 = stats.Composition31Days;
        if (!today.HasTokens && !window31.HasTokens)
        {
            return;
        }

        var breakdown = BuildBreakdown(today, window31);

        var header = new Grid
        {
            Margin = new Thickness(0, 12, 0, 0),
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };
        header.Children.Add(Text(TokenText.TokensUsedHeading, 11, _theme["TextSecondary"]));

        var switcher = ViewSwitch(breakdown);
        Grid.SetColumn(switcher, 1);
        header.Children.Add(switcher);
        root.Children.Add(header);

        root.Children.Add(TokenBar(today, TokenText.TokensUsedTodayLabel, TokenText.TokenWindowToday, 6));
        root.Children.Add(TokenBar(window31, TokenText.TokensUsed31DaysLabel, TokenText.TokenWindow31Days, 9));
        root.Children.Add(breakdown);

        // Which surfaces these figures are made of. Empty when nothing was found at all —
        // the scope note owns that state and says considerably more (issue #171).
        if ((scopeReport ?? TranscriptScopeReport.Inspect()).CoverageLine() is { Length: > 0 } coverage)
        {
            root.Children.Add(Muted(_theme, coverage));
        }
    }

    /// <summary>
    /// The Bars/Breakdown switch. Two buttons rather than a disclosure, so the Linux panel
    /// offers the same two views the Windows one does — and, on a head where a compositor may
    /// never deliver hover at all, the breakdown is the only route to a segment's exact share
    /// and estimated value that needs no pointer.
    /// </summary>
    private Control ViewSwitch(Control breakdown)
    {
        var bars = SwitchButton(TokenText.TokenViewBarsLabel, active: true);
        var table = SwitchButton(TokenText.TokenViewBreakdownLabel, active: false);

        void Select(bool showBreakdown)
        {
            breakdown.IsVisible = showBreakdown;
            bars.Background = showBreakdown ? Brushes.Transparent : _theme["BarTrack"];
            table.Background = showBreakdown ? _theme["BarTrack"] : Brushes.Transparent;
        }

        bars.Click += (_, _) => Select(false);
        table.Click += (_, _) => Select(true);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        row.Children.Add(bars);
        row.Children.Add(table);

        return new Border
        {
            Background = _theme["TileBg"],
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(2),
            Child = row,
        };
    }

    private Button SwitchButton(string label, bool active) => new()
    {
        Content = Text(label, 10.5, _theme[active ? "TextPrimary" : "TextMuted"]),
        Background = active ? _theme["BarTrack"] : Brushes.Transparent,
        BorderThickness = new Thickness(0),
        CornerRadius = new CornerRadius(3),
        Padding = new Thickness(8, 3),
        Cursor = new Cursor(StandardCursorType.Hand),
    };

    /// <summary>
    /// One composition bar: heading and billed total, the segmented track, and the legend
    /// carrying every exact figure the track's floors do not.
    ///
    /// <para>Widths come from <see cref="TokenBarGeometry"/> — shared with the Windows head,
    /// because the segment order and the minimum width are measured decisions and two copies
    /// of a measured decision is how they drift.</para>
    /// </summary>
    private Control TokenBar(TokenComposition composition, string heading, string window, double topMargin)
    {
        var block = new StackPanel { Spacing = 4, Margin = new Thickness(0, topMargin, 0, 0) };

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(Text(heading, 10.5, _theme["TextSecondary"]));
        var total = Text(UsageFormatter.Tokens(composition.Total), 12, _theme["TextPrimary"], FontWeight.SemiBold);
        Grid.SetColumn(total, 1);
        head.Children.Add(total);
        block.Children.Add(head);

        var slices = composition.InDisplayOrder.Where(s => s.Tokens > 0).ToList();

        // Avalonia gives no laid-out width before the first arrange pass either, so the
        // widths are applied on size change against the track the bar actually got.
        var track = new Grid { Height = 12 };
        var pieces = new List<Border>(slices.Count);
        for (var i = 0; i < slices.Count; i++)
        {
            track.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(0, GridUnitType.Pixel)));

            var slice = slices[i];
            var brush = KindBrush(slice.Kind);
            var piece = new Border
            {
                Background = brush,
                // Rounded only at the ends — a Border does not clip children to its corner
                // radius, so a rounded container over square segments shows through.
                CornerRadius = new CornerRadius(
                    i == 0 ? 3 : 0, i == slices.Count - 1 ? 3 : 0,
                    i == slices.Count - 1 ? 3 : 0, i == 0 ? 3 : 0),
            };
            ToolTip.SetTip(piece, SegmentTip(slice, window));
            Grid.SetColumn(piece, i);
            track.Children.Add(piece);
            pieces.Add(piece);
        }

        track.SizeChanged += (_, _) =>
        {
            var segments = TokenBarGeometry.Segments(composition, track.Bounds.Width);
            if (segments.Count != track.ColumnDefinitions.Count)
            {
                return;
            }

            for (var i = 0; i < segments.Count; i++)
            {
                track.ColumnDefinitions[i].Width = new GridLength(segments[i].Width, GridUnitType.Pixel);
            }
        };
        block.Children.Add(track);

        // Every figure the bar cannot draw honestly. Not decoration — see TokenBarGeometry.
        var legend = new WrapPanel();
        foreach (var slice in slices)
        {
            var entry = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Margin = new Thickness(0, 0, 10, 0),
            };
            entry.Children.Add(new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(2),
                Background = KindBrush(slice.Kind),
                VerticalAlignment = VerticalAlignment.Center,
            });
            entry.Children.Add(Text(
                $"{TokenText.TokenKindLabel(slice.Kind)} {UsageFormatter.Tokens(slice.Tokens)}",
                10.5, _theme["TextSecondary"]));
            ToolTip.SetTip(entry, SegmentTip(slice, window));
            legend.Children.Add(entry);
        }

        block.Children.Add(legend);
        return block;
    }

    private string SegmentTip(TokenKindSlice slice, string window) =>
        $"{UsageFormatter.Tokens(slice.Tokens)} · "
        + TokenText.TokenCardCaption(slice.Kind, slice.Share, slice.EstUsd, window);

    private IBrush KindBrush(TokenKind kind) => _theme[TokenBarGeometry.PaletteKey(kind)];

    /// <summary>
    /// The breakdown table, with <b>a share column per window</b> — the two windows have
    /// measurably different shapes, so one shared column would be read against whichever
    /// token column it sat nearer.
    /// </summary>
    private Control BuildBreakdown(TokenComposition today, TokenComposition window31)
    {
        var table = new Grid
        {
            IsVisible = false,
            Margin = new Thickness(0, 6, 0, 0),
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"),
        };

        AddBreakdownRow(table, 0, null,
            TokenText.TokenBreakdownKindHeader, TokenText.TokenBreakdownTodayHeader,
            TokenText.TokenBreakdownShareHeader, TokenText.TokenBreakdown31DaysHeader,
            TokenText.TokenBreakdownShareHeader, _theme["TextMuted"], 9.5);

        var row = 1;
        foreach (var slice in (today.HasTokens ? today : window31).InDisplayOrder)
        {
            var mine = today.InDisplayOrder.First(s => s.Kind == slice.Kind);
            var other = window31.InDisplayOrder.First(s => s.Kind == slice.Kind);

            AddBreakdownRow(table, row++, KindBrush(slice.Kind),
                TokenText.TokenKindLabel(slice.Kind),
                UsageFormatter.Tokens(mine.Tokens), TokenText.TokenShare(mine.Share),
                UsageFormatter.Tokens(other.Tokens), TokenText.TokenShare(other.Share),
                _theme["TextSecondary"], 10.5);
        }

        AddBreakdownRow(table, row, null,
            TokenText.TokenBreakdownTotalLabel,
            UsageFormatter.Tokens(today.Total), TokenText.TokenShare(today.HasTokens ? 1 : 0),
            UsageFormatter.Tokens(window31.Total), TokenText.TokenShare(window31.HasTokens ? 1 : 0),
            _theme["TextPrimary"], 10.5, FontWeight.SemiBold);

        return table;
    }

    private static void AddBreakdownRow(
        Grid table, int row, IBrush? chip, string kind, string todayTokens, string todayShare,
        string windowTokens, string windowShare, IBrush foreground, double size,
        FontWeight weight = FontWeight.Normal)
    {
        table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var label = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 2),
        };
        if (chip is not null)
        {
            label.Children.Add(new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(2),
                Background = chip,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        label.Children.Add(Text(kind, size, foreground, weight));
        Grid.SetRow(label, row);
        table.Children.Add(label);

        var cells = new[] { todayTokens, todayShare, windowTokens, windowShare };
        for (var i = 0; i < cells.Length; i++)
        {
            var cell = Text(cells[i], size, foreground, weight);
            cell.TextAlignment = TextAlignment.Right;
            cell.Margin = new Thickness(10, 2, 0, 2);
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, i + 1);
            table.Children.Add(cell);
        }
    }
}
