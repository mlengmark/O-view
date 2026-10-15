using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OView.App.Rendering;
using OView.Presentation.Panel;
using static OView.Linux.Panel.PanelPrimitives;

namespace OView.Linux.Panel;

/// <summary>
/// Both off-plan registers: the live banner near the top of the panel, and the standing
/// 31-day spend section at its foot.
///
/// <para>One class for the two because they are two halves of one claim, built from one
/// <see cref="PanelOffPlanView"/>; every word and figure in them arrives built.</para>
/// </summary>
internal sealed class PanelOffPlan(LinuxPanelTheme theme)
{
    private readonly LinuxPanelTheme _theme = theme;

    /// <summary>
    /// The off-plan banner: the heading, the line underneath, and the address the line sends
    /// the reader to.
    ///
    /// <para><b>The address is printed, not labelled.</b> The Windows banner makes the link's
    /// label the thing you click; nothing in this head opens a URL — no part of the Linux
    /// panel does — so a bare "Open usage settings in Claude" here would be an instruction
    /// with no way to follow it. The label still introduces it, because the sentence above
    /// ends by sending the reader somewhere and the address alone does not say where.</para>
    /// </summary>
    public void AddBannerTo(StackPanel root, PanelOffPlanBannerView banner, PanelDensity density)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(Text(banner.Title, 13, _theme["WarnText"], FontWeight.SemiBold));
        stack.Children.Add(Text(banner.Detail, 11, _theme["WarnText"]));
        stack.Children.Add(Text(
            $"{banner.LinkLabel}: {banner.LinkUrl}", 11, _theme["WarnText"]));

        root.Children.Add(new Border
        {
            Background = _theme["WarnBg"],
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(NaturalTilePadding * density.SpacingScale),
            Child = stack,
        });
    }

    /// <summary>
    /// The standing 31-day off-plan spend section (<see cref="PanelOffPlanView"/>): heading and
    /// badge, the figure, the coverage caveat, and what the figure is.
    ///
    /// <para><b>The explanation stands rather than hovering.</b> The Windows panel moved it to
    /// a hover card to save three permanent lines (issue #181), which is the right trade where
    /// hover exists; a compositor may never deliver it here, and what would be lost is the
    /// rule-6 caveat that this is an estimate and not the charge — the one sentence the
    /// section cannot ship without. The same reasoning puts the weekly reset's action on its
    /// row rather than behind a card (<see cref="PanelMeterView.ResetLineWithAction"/>).</para>
    /// </summary>
    public void AddSectionTo(StackPanel root, PanelOffPlanView offPlan)
    {
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        heading.Children.Add(Text(offPlan.Heading, 13, _theme["TextPrimary"], FontWeight.SemiBold));

        var badge = new Border
        {
            Background = _theme["WarnBg"],
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Child = Text(offPlan.Badge, 11, _theme["WarnText"]),
        };
        Grid.SetColumn(badge, 1);
        heading.Children.Add(badge);

        var figure = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        figure.Children.Add(Text(offPlan.SpendLabel, 12, _theme["TextSecondary"]));
        var value = Text(offPlan.SpendValue, 18, _theme["TextPrimary"], FontWeight.SemiBold);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(value, 1);
        figure.Children.Add(value);

        var block = new StackPanel { Spacing = 4 };
        block.Children.Add(heading);
        block.Children.Add(figure);
        if (offPlan.Coverage is { Length: > 0 } coverage)
        {
            block.Children.Add(Text(coverage, 11, _theme["WarnText"]));
        }

        block.Children.Add(Muted(_theme, offPlan.Hint));
        root.Children.Add(block);
    }
}
