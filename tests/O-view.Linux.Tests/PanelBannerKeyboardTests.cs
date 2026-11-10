using System.Reflection;

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using OView.App;
using OView.Core.Models;
using OView.Linux.Panel;
using OView.Presentation.Panel;
using OView.Presentation.Samples;
using OView.Presentation.Text;

namespace OView.Linux.Tests;

/// <summary>
/// The off-plan banner's hide control from the keyboard: it takes the focus, and Enter or Space
/// hides the banner exactly as a pointer press does, kept hidden on the next open.
///
/// <para>The keys are the routed events a press raises, sent straight to the control, because
/// Avalonia moves no keyboard focus without a windowing subsystem. That the control can be
/// reached by Tab at all is held by what decides it, its being focusable and a tab stop.</para>
/// </summary>
public class PanelBannerKeyboardTests
{
    private const string BannerState = "offplan-on";

    [Fact]
    public void TheHideControlTakesTheKeyboardFocus()
    {
        var hide = HideControl(Laid());

        Assert.True(hide.Focusable, "the hide control cannot take the focus, so only a mouse can hide the banner");
        Assert.True(hide.IsTabStop, "Tab never stops on the hide control");
    }

    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.Space)]
    public void EnterOrSpaceHidesTheBannerAtOnceAndSaysSo(Key key)
    {
        var panel = Laid();
        var title = BannerTitle(panel);
        var hides = 0;
        panel.BannerHidden += () => hides++;

        var press = Press(HideControl(panel), key);

        Assert.True(press.Handled);
        Assert.Equal(1, hides);
        Assert.DoesNotContain(panel.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == title);
        Assert.Empty(HideControls(panel));
    }

    /// <summary>Every other key reaches the panel as before: Esc still dismisses it, arrows still move it.</summary>
    [Theory]
    [InlineData(Key.Escape)]
    [InlineData(Key.Tab)]
    [InlineData(Key.Left)]
    [InlineData(Key.A)]
    public void AnotherKeyLeavesTheBannerAloneAndUnhandled(Key key)
    {
        var panel = Laid();
        var hides = 0;
        panel.BannerHidden += () => hides++;

        var press = Press(HideControl(panel), key);

        Assert.False(press.Handled);
        Assert.Equal(0, hides);
        Assert.Single(HideControls(panel));
    }

    /// <summary>
    /// Hidden from the keyboard, the banner stays hidden on a panel built afresh from the settings
    /// file alone, which is what a pointer press already does.
    /// </summary>
    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.Space)]
    public void ABannerHiddenFromTheKeyboardStaysHiddenOnTheNextOpen(Key key)
    {
        using var dir = new TempDir();
        var state = SampleStates.All.Single(s => s.Name == BannerState);
        var panel = Rebuilt(Alerts(dir), state);
        var title = BannerTitle(panel);

        Press(HideControl(panel), key);

        var reopened = Rebuilt(Alerts(dir), state);

        Assert.True(TraySettings.Load(dir.File("settings.json")).OffPlanBannerHidden);
        Assert.Empty(HideControls(reopened));
        Assert.DoesNotContain(reopened.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == title);
    }

    /// <summary>
    /// A control Tab lands on has to show it: this head draws no focus adorner, so the control draws
    /// its own ring while the keyboard holds it, and drops it with the focus.
    /// </summary>
    [Fact]
    public void TheKeyboardFocusDrawsARingThatGoesWithTheFocus()
    {
        var hide = HideControl(Laid());
        Assert.Null(hide.BorderBrush);

        hide.RaiseEvent(new FocusChangedEventArgs(InputElement.GotFocusEvent) { NavigationMethod = NavigationMethod.Tab });
        Assert.NotNull(hide.BorderBrush);
        Assert.True(hide.BorderThickness.Left > 0, "the ring has no width");

        hide.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));
        Assert.Null(hide.BorderBrush);
        Assert.Equal(default, hide.BorderThickness);
    }

    /// <summary>A pointer's focus draws no ring: the same press hides the banner.</summary>
    [Fact]
    public void APointersFocusDrawsNoRing()
    {
        var hide = HideControl(Laid());

        hide.RaiseEvent(new FocusChangedEventArgs(InputElement.GotFocusEvent) { NavigationMethod = NavigationMethod.Pointer });

        Assert.Null(hide.BorderBrush);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────

    private static KeyEventArgs Press(Border control, Key key)
    {
        var press = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key };
        control.RaiseEvent(press);
        return press;
    }

    private static PanelContent Laid()
    {
        UseRealFonts();

        var state = SampleStates.All.Single(s => s.Name == BannerState);
        var panel = new PanelContent(new LinuxPanelTheme(light: false));
        Populate(panel, state, state.View);
        PanelSampleRenderer.LayOut(panel);
        return panel;
    }

    /// <summary>The shell's keeper of the view, over the settings file in the directory and nothing else.</summary>
    private static UsageAlerts Alerts(TempDir dir)
    {
        var path = dir.File("settings.json");

        return new UsageAlerts(
            hasPlanHistory: false,
            () => throw new InvalidOperationException("the view needs no statistics"),
            () => throw new InvalidOperationException("the view needs no extra-usage read"),
            log: null,
            () => TraySettings.Load(path),
            settings => settings.Save(path),
            _ => { });
    }

    /// <summary>The panel as the window builds it at an open: from the view the shell holds, its hide wired back to the shell.</summary>
    private static PanelContent Rebuilt(UsageAlerts alerts, SampleState state)
    {
        UseRealFonts();

        var panel = new PanelContent(new LinuxPanelTheme(light: false));
        panel.BannerHidden += () => alerts.SetPanelView(alerts.PanelView with { BannerHidden = true });
        Populate(panel, state, alerts.PanelView);
        PanelSampleRenderer.LayOut(panel);
        return panel;
    }

    private static void Populate(PanelContent panel, SampleState state, PanelViewChoice view) =>
        panel.Populate(state.Snapshot, state.Stats, state.Account, state.Plan, state.Scope,
            SampleStates.Now, state.Boost, SampleStates.Zone, state.CachedUsage, view);

    private static string? BannerTitle(Control panel) =>
        HideControl(panel).GetVisualParent()!.GetVisualDescendants().OfType<TextBlock>().First().Text;

    private static List<Border> HideControls(Control panel) =>
        panel.GetVisualDescendants().OfType<Border>()
            .Where(b => AutomationProperties.GetName(b) == PanelText.HideBannerLabel)
            .ToList();

    private static Border HideControl(Control panel) => Assert.Single(HideControls(panel));

    /// <summary>The font setup <see cref="PanelShortScreenTests"/> runs once per process, through its own lock.</summary>
    private static void UseRealFonts() =>
        typeof(PanelShortScreenTests)
            .GetMethod("UseRealFonts", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null);
}
