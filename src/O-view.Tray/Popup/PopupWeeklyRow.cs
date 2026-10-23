using System.Windows;
using System.Windows.Controls;
using OView.Presentation.Panel;

namespace OView.Tray.Popup;

/// <summary>
/// Draws the weekly reset line and the boost chip beside it (issue #254).
///
/// <para>Which of the reset line's three states applies, known, not known, or no plan data
/// at all, is <see cref="PanelSections.WeeklyMeter"/>'s to decide, and the hint it hands over
/// is the hover card's content rather than a hint about whether to have one. No line means no
/// row, which is the no-plan-data case: the banner above already explains it.</para>
///
/// <para>The chip is absent in every other case, and absent is the ordinary state, most of
/// the time there is no promo running. The chip carries O-view's summary; the hover card
/// carries Claude's own sentence, unedited, with who said it and when it was read.</para>
/// </summary>
internal sealed class PopupWeeklyRow(FrameworkElement owner, TextBlock resetText, Border chip, TextBlock chipText)
{
    public void Populate(PanelMeterView weekly)
    {
        PopupText.SetLine(resetText, weekly.ResetLine ?? "");
        resetText.ToolTip = weekly.ResetHint is { } hint ? HoverCard.Text(owner, hint) : null;
        HoverCard.ApplyTiming(resetText);

        PopulateChip(weekly.Chip);
    }

    private void PopulateChip(PanelBoostChipView? boostChip)
    {
        if (boostChip is null)
        {
            chip.Visibility = Visibility.Collapsed;
            chip.ToolTip = null;
            return;
        }

        chipText.Text = boostChip.Text;
        chip.ToolTip = HoverCard.Text(owner, boostChip.Card);
        HoverCard.ApplyTiming(chip);
        chip.Visibility = Visibility.Visible;
    }
}
