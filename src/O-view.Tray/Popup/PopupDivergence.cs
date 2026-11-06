using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using OView.Core.Models;
using OView.Core.Providers.CachedUsage;
using OView.Presentation.Panel;

namespace OView.Tray.Popup;

/// <summary>
/// Draws off-plan usage in the two registers <see cref="PanelOffPlanView"/> decides:
///   • a live banner for what is happening in the CURRENT session window (the divergence
///     detector's real-time signal), and
///   • a standing off-plan SPEND section (GitHub issue #3). Without a model limit it is the
///     estimated value of credit-billed models over the chart's window; on an account with
///     one it is the amount Claude reports as spent, with no window stated. It is absent
///     while there is nothing to show.
/// The two are independent: the section shows even when the current window is on-plan, and
/// the banner shows even before any credit spend has accrued.
///
/// <para>Every sentence and figure below arrives built. What is left here is the three
/// things WPF has an opinion about: whether each register is collapsed or taking space, how a
/// hyperlink's inlines are filled, and where a tooltip hangs.</para>
/// </summary>
internal sealed class PopupDivergence(
    FrameworkElement owner,
    Border banner, TextBlock title, TextBlock detail, Hyperlink linkAnchor,
    TextBlock creditsHeading, TextBlock badgeText, TextBlock spendLabel, TextBlock spendValue,
    TextBlock coverage, FrameworkElement creditsSection, FrameworkElement creditsSeparator)
{
    public void Populate(PanelStatistics stats, CachedUtilization? cachedUsage, DateTimeOffset utcNow, TimeZoneInfo local)
    {
        var offPlan = PanelSections.OffPlan(stats, cachedUsage, utcNow, local);

        // The Est.-today tile's label and note also flip with off-plan state; that happens in
        // PopupTiles, which owns everything the tiles show, from the same view model's
        // ValueTileLabel and ValueTileNote.

        // ── live, session-scoped signal ──────────────────────────────────────────
        banner.Visibility = offPlan.Banner is null ? Visibility.Collapsed : Visibility.Visible;

        if (offPlan.Banner is { } b)
        {
            title.Text = b.Title;
            detail.Text = b.Detail;

            // Filled here rather than in XAML so the label and the address have one definition
            // in Presentation, next to the sentences that send the reader to them (issue #259).
            linkAnchor.NavigateUri = new Uri(b.LinkUrl);
            linkAnchor.Inlines.Clear();
            linkAnchor.Inlines.Add(b.LinkLabel);
        }

        // ── standing off-plan spend (issue #3) ───────────────────────────────────
        // The separator is its own element in the panel's stack, so it collapses with the
        // section: left behind it would close the panel on a rule with nothing under it.
        var sectionVisibility = offPlan.Section is null ? Visibility.Collapsed : Visibility.Visible;
        creditsSeparator.Visibility = sectionVisibility;
        creditsSection.Visibility = sectionVisibility;

        if (offPlan.Section is { } s)
        {
            creditsHeading.Text = s.Heading;
            badgeText.Text = s.Badge;
            spendLabel.Text = s.SpendLabel;
            spendValue.Text = s.SpendValue;
            coverage.Text = s.Coverage;

            // The explanation is on hover now rather than standing under the figure (issue #181).
            // It still carries the rule-6 caveat of what the figure is (an estimate, or another
            // application's cached answer), moving a caveat behind a hover is the obvious way
            // to lose one, so the sentence lives in
            // OffPlanText where it has one definition and a test can assert it.
            creditsSection.ToolTip = HoverCard.Text(owner, s.Hint);
            HoverCard.ApplyTiming(creditsSection);
        }
    }
}
