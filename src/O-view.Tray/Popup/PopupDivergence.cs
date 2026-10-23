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
///   • a standing 31-day off-plan SPEND total (GitHub issue #3), estimated from
///     credit-billed models rather than the 5-hour session.
/// The two are independent: the 31-day figure shows even when the current window is
/// on-plan, and the banner shows even before any credit spend has accrued.
///
/// <para>Every sentence and figure below arrives built. What is left here is the three
/// things WPF has an opinion about: whether the banner is collapsed or taking space, how a
/// hyperlink's inlines are filled, and where a tooltip hangs.</para>
/// </summary>
internal sealed class PopupDivergence(
    FrameworkElement owner,
    Border banner, TextBlock title, TextBlock detail, Hyperlink linkAnchor,
    TextBlock creditsHeading, TextBlock badgeText, TextBlock spendLabel, TextBlock spendValue,
    TextBlock coverage, FrameworkElement creditsSection)
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

        // ── standing 31-day off-plan spend (issue #3) ────────────────────────────
        creditsHeading.Text = offPlan.Heading;
        badgeText.Text = offPlan.Badge;
        spendLabel.Text = offPlan.SpendLabel;
        spendValue.Text = offPlan.SpendValue;
        coverage.Text = offPlan.Coverage;

        // The explanation is on hover now rather than standing under the figure (issue #181).
        // Both wordings still carry the rule-6 caveat that this is not what was charged,
        // moving a caveat behind a hover is the obvious way to lose one, so the sentences
        // live in OffPlanText where they have one definition and a test can assert them.
        creditsSection.ToolTip = HoverCard.Text(owner, offPlan.Hint);
        HoverCard.ApplyTiming(creditsSection);
    }
}
