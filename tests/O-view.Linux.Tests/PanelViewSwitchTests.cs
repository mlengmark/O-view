using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using OView.Linux.Panel;
using OView.Presentation.Text;

namespace OView.Linux.Tests;

/// <summary>
/// The Bars/Breakdown switch above the "Tokens used" bars: that each half shows its word, and
/// that a click still swaps the view.
///
/// <para><b>Why the words are worth a test.</b> This head registers no Avalonia theme, so a
/// button falls back to a bare content presenter — which fills itself from the content only
/// once its tree reaches a window, and the sample images are rendered from a tree that never
/// does. Both halves stood blank in all 22 Linux sample images while the Windows panel read
/// Bars | Breakdown. An image says so only once someone looks at it; this fails the moment
/// either half is built with nothing to read.</para>
///
/// <para>Avalonia raises no pointer events without a windowing subsystem, so these raise the
/// routed event a click raises rather than clicking — the same reason
/// <see cref="PanelTileFlipTests"/> drives the tiles' flip instead.</para>
/// </summary>
public class PanelViewSwitchTests
{
    /// <summary>
    /// The switch as the panel builds it, with a stand-in for the table it shows and hides.
    /// </summary>
    private static (Border Breakdown, Button Bars, Button Table) Switch(bool light)
    {
        var breakdown = new Border { IsVisible = false };
        var row = new PanelTokenBars(new LinuxPanelTheme(light)).ViewSwitch(breakdown);

        var halves = row.GetVisualDescendants().OfType<Button>().ToArray();
        Assert.Equal(2, halves.Length);
        return (breakdown, halves[0], halves[1]);
    }

    /// <summary>
    /// What a reader has in front of them on one half. The template is applied by hand because
    /// nothing lays this control out here: a test process has no windowing platform, and
    /// measuring text would need one.
    ///
    /// <para>Nothing else is prodded into life. A half whose word arrives only once something
    /// fills it in is exactly the half that came out blank in every sample image, so this walks
    /// the control as it stands.</para>
    /// </summary>
    private static TextBlock Word(Button half)
    {
        half.ApplyTemplate();
        return Assert.Single(half.GetVisualDescendants().OfType<TextBlock>());
    }

    private static Color Colour(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    /// <summary>
    /// The feature: both halves carry their word, in both themes, in a colour that is not the
    /// colour of the pill beneath it — a word the shade of its own background is no better
    /// than the blank pill it replaces.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BothHalvesShowTheirWord(bool light)
    {
        var (_, bars, table) = Switch(light);

        var onBars = Word(bars);
        var onTable = Word(table);

        Assert.Equal(TokenText.TokenViewBarsLabel, onBars.Text);
        Assert.Equal(TokenText.TokenViewBreakdownLabel, onTable.Text);

        foreach (var (half, word) in new[] { (bars, onBars), (table, onTable) })
        {
            Assert.False(string.IsNullOrWhiteSpace(word.Text));
            Assert.True(word.IsVisible);
            Assert.NotEqual(Colour(half.Background), Colour(word.Foreground));
        }
    }

    /// <summary>
    /// Which half reads as the selected one: the filled pill and the brighter word, the two
    /// cues the Windows switch shows, and they move with the selection.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheSelectedHalfIsTheFilledOne(bool light)
    {
        var theme = new LinuxPanelTheme(light);
        var (_, bars, table) = Switch(light);
        var onBars = Word(bars);
        var onTable = Word(table);

        void AssertSelected(Button selected, TextBlock selectedWord, Button other, TextBlock otherWord)
        {
            Assert.Equal(Colour(theme["BarTrack"]), Colour(selected.Background));
            Assert.Equal(Colors.Transparent, Colour(other.Background));
            Assert.Equal(Colour(theme["TextPrimary"]), Colour(selectedWord.Foreground));
            Assert.Equal(Colour(theme["TextMuted"]), Colour(otherWord.Foreground));
        }

        // The switch opens on the bars, as the Windows panel does.
        AssertSelected(bars, onBars, table, onTable);

        table.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        AssertSelected(table, onTable, bars, onBars);
    }

    /// <summary>
    /// The left half shows the bars and the right one the breakdown, as often as you like.
    /// </summary>
    [Fact]
    public void ClickingAHalfShowsThatView()
    {
        var (breakdown, bars, table) = Switch(light: true);

        Assert.False(breakdown.IsVisible);

        for (var i = 0; i < 3; i++)
        {
            table.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(breakdown.IsVisible);

            bars.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(breakdown.IsVisible);
        }
    }

    /// <summary>
    /// Each half is still a focusable button, so Space and Enter still work it. Neither key can
    /// be pressed here — keyboard input needs a window — so what is pinned is the button that
    /// carries them: a plain border in its place would draw the words just as well and leave
    /// the switch reachable by pointer alone.
    /// </summary>
    [Fact]
    public void EachHalfStaysReachableFromTheKeyboard()
    {
        var (_, bars, table) = Switch(light: true);

        Assert.True(bars.Focusable);
        Assert.True(table.Focusable);
    }
}
