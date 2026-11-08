using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using OView.Presentation.Panel;
using Button = System.Windows.Controls.Button;

namespace OView.Tray.Popup;

/// <summary>
/// Draws the live off-plan banner for what is happening in the CURRENT session window (the
/// divergence detector's real-time signal), and folds it away when the reader hides it. The
/// standing off-plan spend is the plan block's last row (<see cref="PopupPlanBlock"/>); the two
/// are independent, the row shows even when the current window is on-plan, and the banner shows
/// even before any credit spend has accrued.
///
/// <para>Every sentence arrives built. What is left here is what WPF has an opinion about:
/// whether the banner is collapsed, how a hyperlink's inlines are filled, and how it leaves.</para>
///
/// <para><b>It reuses the panel's fold.</b> <see cref="DisclosureAnimation"/> already releases
/// its animations, guards against a missed completion, and re-docks on each frame so the panel
/// shrinks into its docked corner, which is exactly what hiding this needs.</para>
/// </summary>
/// <param name="foldHost">What folds: the banner and the space above it, so both leave together.</param>
/// <param name="onHidden">Records that the reader hid the banner, the moment it is pressed.</param>
/// <param name="refitForPendingGrowth">Re-fits the panel for height it is about to lose.</param>
/// <param name="redock">Re-docks the panel at its current height.</param>
internal sealed class PopupDivergence(
    FrameworkElement owner, FrameworkElement foldHost, DisclosureAnimation fold,
    TextBlock title, TextBlock detail, Hyperlink linkAnchor, Button hideButton,
    Action onHidden, Action<double> refitForPendingGrowth, Action redock)
{
    /// <summary>Whether the banner is gone or going, so a second press on the × changes nothing.</summary>
    private bool _hidden = true;

    /// <summary>Hooks the × up. Separate from the constructor only because a primary one has no body.</summary>
    public void Start() => hideButton.Click += (_, _) => Hide();

    public void Populate(PanelPlanBlockView plan)
    {
        // Set on the fold's live path only: a clip left on at rest moves the text under it by
        // one colour level.
        foldHost.ClipToBounds = false;

        _hidden = plan.Banner is null;
        fold.Apply(!_hidden);

        if (plan.Banner is { } b)
        {
            title.Text = b.Title;
            detail.Text = b.Detail;

            // Filled here rather than in XAML so the label and the address have one definition
            // in Presentation, next to the sentences that send the reader to them (issue #259).
            linkAnchor.NavigateUri = new Uri(b.LinkUrl);
            linkAnchor.Inlines.Clear();
            linkAnchor.Inlines.Add(b.LinkLabel);

            AutomationProperties.SetName(hideButton, plan.HideBannerLabel);
            hideButton.ToolTip = HoverCard.Text(owner, plan.HideBannerLabel);
            HoverCard.ApplyTiming(hideButton);
        }
    }

    private void Hide()
    {
        // Already asked for, and the banner cannot be brought back from here, so a second press
        // during the fold has nothing to reverse: it ends where the first one was heading.
        if (_hidden)
        {
            return;
        }

        _hidden = true;
        onHidden();

        // The same order the breakdown's fold keeps: pinned before anything is laid out, so the
        // window is never laid out at the far end of the fold.
        var (fromHeight, fromAngle) = fold.Current;
        foldHost.ClipToBounds = true;
        fold.Prepare(fromHeight);
        refitForPendingGrowth(-fromHeight);
        owner.UpdateLayout();
        redock();

        fold.Run(expanded: false, fromHeight, fromAngle, toHeight: 0);
    }
}
