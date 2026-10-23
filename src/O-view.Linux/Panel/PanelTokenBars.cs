using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using OView.App.Rendering;
using OView.Core.Models;
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
public sealed class PanelTokenBars(LinuxPanelTheme theme)
{
    private readonly LinuxPanelTheme _theme = theme;

    public void AddTo(StackPanel root, PanelTokenBarsView view)
    {
        if (!view.Show)
        {
            return;
        }

        var breakdown = BuildBreakdown(view.Breakdown);

        var header = new Grid
        {
            Margin = new Thickness(0, 12, 0, 0),
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };
        header.Children.Add(Text(view.Heading, 11, _theme["TextSecondary"]));

        var switcher = ViewSwitch(breakdown);
        Grid.SetColumn(switcher, 1);
        header.Children.Add(switcher);
        root.Children.Add(header);

        root.Children.Add(TokenBar(view.Today, 6));
        root.Children.Add(TokenBar(view.Window31, 9));
        root.Children.Add(breakdown);

        if (view.CoverageLine is { Length: > 0 } coverage)
        {
            root.Children.Add(Muted(_theme, coverage));
        }
    }

    /// <summary>
    /// The Bars/Breakdown switch. Two buttons rather than a disclosure, so the Linux panel
    /// offers the same two views the Windows one does — and, on a head where a compositor may
    /// never deliver hover at all, the breakdown is the only route to a segment's exact share
    /// and estimated value that needs no pointer.
    ///
    /// <para>Public so a test can read both halves and what a click leaves on screen: Avalonia
    /// raises no pointer events without a windowing subsystem, the same reason
    /// <see cref="PanelStatTiles.TileFlip"/> is reachable from outside.</para>
    /// </summary>
    public Control ViewSwitch(Control breakdown)
    {
        var bars = SwitchButton(TokenText.TokenViewBarsLabel, active: true);
        var table = SwitchButton(TokenText.TokenViewBreakdownLabel, active: false);

        void Select(bool showBreakdown)
        {
            breakdown.IsVisible = showBreakdown;
            Paint(bars, active: !showBreakdown);
            Paint(table, active: showBreakdown);
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

    private Button SwitchButton(string label, bool active)
    {
        var half = new Button
        {
            // The word a screen reader announces. The one on screen is drawn by the template,
            // from the same string.
            Content = label,
            Template = Pill(label),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(8, 3),

            // A cursor can only be made once an Avalonia application has started the windowing
            // platform. The switch is built in a test process as well, which has neither one
            // nor a pointer to shape.
            Cursor = Application.Current is null ? null : new Cursor(StandardCursorType.Hand),
        };

        Paint(half, active);
        return half;
    }

    /// <summary>
    /// Which half reads as the selected one — the filled pill and the brighter word, the same
    /// two cues the Windows switch shows.
    /// </summary>
    private void Paint(Button half, bool active)
    {
        half.Background = active ? _theme["BarTrack"] : Brushes.Transparent;
        half.Foreground = _theme[active ? "TextPrimary" : "TextMuted"];
    }

    /// <summary>
    /// What a half of the switch is drawn as: the pill, and the word inside it.
    ///
    /// <para><b>Why the word sits in the template rather than in the button's content.</b> This
    /// head registers no Avalonia theme, so a button falls back to a bare content presenter —
    /// and a presenter fills itself from the content only once the tree it belongs to reaches a
    /// window. The sample images are rendered offscreen, from a tree that never does, so both
    /// halves came out as empty pills while the Windows panel read Bars | Breakdown. A control
    /// placed in the template is measured and drawn either way, which is what every other piece
    /// of this panel is built from.</para>
    ///
    /// <para>A border with a pointer handler would draw the word just as well, as the stat
    /// tiles do, but it would cost the Space and Enter activation a button carries. The two
    /// cues of the selection stay on the button and reach the pill through template bindings,
    /// so the switch is still painted in one place.</para>
    /// </summary>
    private static FuncControlTemplate<Button> Pill(string label) => new((_, _) => new Border
    {
        [!Border.BackgroundProperty] = new TemplateBinding(TemplatedControl.BackgroundProperty),
        [!Border.CornerRadiusProperty] = new TemplateBinding(TemplatedControl.CornerRadiusProperty),
        [!Border.PaddingProperty] = new TemplateBinding(TemplatedControl.PaddingProperty),
        Child = new TextBlock
        {
            Text = label,
            FontSize = 10.5,
            [!TextBlock.ForegroundProperty] = new TemplateBinding(TemplatedControl.ForegroundProperty),
        },
    });

    /// <summary>
    /// One composition bar: heading and billed total, the segmented track, and the legend
    /// carrying every exact figure the track's floors do not.
    ///
    /// <para>Widths come from <see cref="TokenBarGeometry"/> — shared with the Windows head,
    /// because the segment order and the minimum width are measured decisions and two copies
    /// of a measured decision is how they drift.</para>
    /// </summary>
    private Control TokenBar(PanelTokenBarView bar, double topMargin)
    {
        var block = new StackPanel { Spacing = 4, Margin = new Thickness(0, topMargin, 0, 0) };

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(Text(bar.Heading, 10.5, _theme["TextSecondary"]));
        var total = Text(bar.Total, 12, _theme["TextPrimary"], FontWeight.SemiBold);
        Grid.SetColumn(total, 1);
        head.Children.Add(total);
        block.Children.Add(head);

        var slices = bar.Segments;

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
            ToolTip.SetTip(piece, SegmentTip(slice, bar.Window));
            Grid.SetColumn(piece, i);
            track.Children.Add(piece);
            pieces.Add(piece);
        }

        track.SizeChanged += (_, _) =>
        {
            var segments = TokenBarGeometry.Segments(bar.Composition, track.Bounds.Width);
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
        for (var i = 0; i < slices.Count; i++)
        {
            var slice = slices[i];
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
            entry.Children.Add(Text(bar.Legend[i].Text, 10.5, _theme["TextSecondary"]));
            ToolTip.SetTip(entry, SegmentTip(slice, bar.Window));
            legend.Children.Add(entry);
        }

        block.Children.Add(legend);
        return block;
    }

    private string SegmentTip(TokenKindSlice slice, string window) =>
        $"{UsageFormatter.Tokens(slice.Tokens)} · "
        + TokenText.TokenCardCaption(slice.Kind, slice.Share, slice.EstUsdValue.Value, window);

    private IBrush KindBrush(TokenKind kind) => _theme[TokenBarGeometry.PaletteKey(kind)];

    /// <summary>
    /// The breakdown table, laid out from the rows <see cref="PanelSections.TokenBars"/> built:
    /// the header row muted and small, each kind's row with its colour chip, the total row bold.
    /// </summary>
    private Control BuildBreakdown(IReadOnlyList<PanelTokenBreakdownRow> rows)
    {
        var table = new Grid
        {
            IsVisible = false,
            Margin = new Thickness(0, 6, 0, 0),
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"),
        };

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var (foreground, size, weight) = row.RowKind switch
            {
                PanelTokenRowKind.Header => (_theme["TextMuted"], 9.5, FontWeight.Normal),
                PanelTokenRowKind.Total => (_theme["TextPrimary"], 10.5, FontWeight.SemiBold),
                _ => (_theme["TextSecondary"], 10.5, FontWeight.Normal),
            };

            AddBreakdownRow(table, i, row.Kind is { } kind ? KindBrush(kind) : null,
                row.Label, row.TodayTokens, row.TodayShare, row.WindowTokens, row.WindowShare,
                foreground, size, weight);
        }

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
