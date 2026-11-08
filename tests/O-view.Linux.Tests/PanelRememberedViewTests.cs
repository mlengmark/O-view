using System.Reflection;

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using OView.App;
using OView.Core.Models;
using OView.Linux.Panel;
using OView.Presentation.Panel;
using OView.Presentation.Samples;
using OView.Presentation.Text;

namespace OView.Linux.Tests;

/// <summary>
/// The view the reader leaves the panel in is the view it opens on, after the app has been
/// restarted: what a press on the switch, the flip tag and the banner's hide control hands back
/// is stored through the shell, and a panel built fresh from nothing but the settings file shows
/// it.
///
/// <para>The window's half, that a press reaches the shell at all, is held structurally in
/// <see cref="PanelMeasureSwitchTests"/>; this is the other half, from the press to the file and
/// from the file to the panel. The presses are the routed events a click raises, because Avalonia
/// raises no pointer events without a windowing subsystem.</para>
/// </summary>
public class PanelRememberedViewTests
{
    /// <summary>Off-plan, so there is a banner to hide, on the totals face and the Output measure.</summary>
    private const string State = "offplan-on";

    // ── each choice ─────────────────────────────────────────────────────────────────

    [Fact]
    public void TheMeasureChosenIsTheMeasureAFreshPanelOpensOn()
    {
        using var dir = new TempDir();
        var (panel, state) = Opened(dir);

        Segments(panel).Overall.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var reopened = Reopened(dir, state);
        var (output, overall) = Segments(reopened);

        Assert.Equal(PanelMeasure.Overall, ViewIn(dir).Measure);
        Assert.False(output.IsChecked);
        Assert.True(overall.IsChecked);
    }

    [Fact]
    public void TheFaceChosenIsTheFaceAFreshPanelOpensOn()
    {
        using var dir = new TempDir();
        var (panel, state) = Opened(dir);

        Tag(panel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var reopened = Reopened(dir, state);

        Assert.Equal(PanelFace.Models, ViewIn(dir).Face);
        Assert.Equal(2, reopened.GetVisualDescendants().OfType<Canvas>().Count(c => c.Children.OfType<Avalonia.Controls.Shapes.Path>().Any()));
        Assert.Equal(TokenText.FlipTagLabel(PanelFace.Models), Tag(reopened).Content);
    }

    [Fact]
    public void ABannerHiddenStaysHiddenOnAFreshPanel()
    {
        using var dir = new TempDir();
        var (panel, state) = Opened(dir);
        var banner = HideControl(panel).GetVisualParent()!.GetVisualDescendants().OfType<TextBlock>().First().Text;

        Press(HideControl(panel), panel);

        var reopened = Reopened(dir, state);

        Assert.True(ViewIn(dir).BannerHidden);
        Assert.DoesNotContain(reopened.GetVisualDescendants().OfType<Border>(), b => AutomationProperties.GetName(b) == PanelText.HideBannerLabel);
        Assert.DoesNotContain(reopened.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == banner);
    }

    // ── all three, across a restart ─────────────────────────────────────────────────

    /// <summary>
    /// One panel is used to choose all three, one press after another, each rebuilt from what the
    /// shell kept as the window rebuilds it. Then nothing of it is left but the settings file, and
    /// a fresh shell and a fresh panel built from that alone open on the same view.
    /// </summary>
    [Fact]
    public void ARestartOpensOnTheMeasureTheFaceAndTheBannerAllChosenBefore()
    {
        using var dir = new TempDir();
        var (panel, state) = Opened(dir);
        var alerts = Alerts(dir);

        Segments(panel).Overall.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        panel = Rebuilt(alerts, state);
        Tag(panel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        panel = Rebuilt(alerts, state);
        Press(HideControl(panel), panel);

        var chosen = new PanelViewChoice(PanelMeasure.Overall, PanelFace.Models, BannerHidden: true);
        Assert.Equal(chosen, ViewIn(dir));

        var reopened = Reopened(dir, state);

        Assert.Equal(chosen, Alerts(dir).PanelView);
        Assert.True(Segments(reopened).Overall.IsChecked);
        Assert.Equal(TokenText.FlipTagLabel(PanelFace.Models), Tag(reopened).Content);
        Assert.DoesNotContain(reopened.GetVisualDescendants().OfType<Border>(), b => AutomationProperties.GetName(b) == PanelText.HideBannerLabel);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────

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

    /// <summary>What the settings file holds, read without the shell.</summary>
    private static PanelViewChoice ViewIn(TempDir dir)
    {
        var settings = TraySettings.Load(dir.File("settings.json"));
        return new PanelViewChoice(settings.PanelMeasure, settings.PanelFace, settings.OffPlanBannerHidden);
    }

    /// <summary>
    /// A panel opened on a fresh install's view, with its presses wired to the shell the way the
    /// window wires them: the choice is stored, and what the shell hands back is kept.
    /// </summary>
    private static (PanelContent Panel, SampleState State) Opened(TempDir dir)
    {
        var state = SampleStates.All.Single(s => s.Name == State);
        var alerts = Alerts(dir);

        Assert.Equal(default, alerts.PanelView);
        return (Rebuilt(alerts, state), state);
    }

    /// <summary>The panel as the window rebuilds it after a press: from the view the shell holds now.</summary>
    private static PanelContent Rebuilt(UsageAlerts alerts, SampleState state)
    {
        UseRealFonts();

        var panel = new PanelContent(new LinuxPanelTheme(light: false));
        panel.ViewChosen += view => alerts.SetPanelView(view);
        panel.BannerHidden += () => alerts.SetPanelView(alerts.PanelView with { BannerHidden = true });
        Populate(panel, state, alerts.PanelView);
        PanelSampleRenderer.LayOut(panel);
        return panel;
    }

    /// <summary>A restart: a new shell over the same file, and a panel built from what it reads.</summary>
    private static PanelContent Reopened(TempDir dir, SampleState state) => Rebuilt(Alerts(dir), state);

    private static void Populate(PanelContent panel, SampleState state, PanelViewChoice view) =>
        panel.Populate(state.Snapshot, state.Stats, state.Account, state.Plan, state.Scope,
            SampleStates.Now, state.Boost, SampleStates.Zone, state.CachedUsage, view);

    private static (RadioButton Output, RadioButton Overall) Segments(Control panel)
    {
        var segments = panel.GetVisualDescendants().OfType<RadioButton>().ToList();
        Assert.Equal(2, segments.Count);
        return (segments[0], segments[1]);
    }

    private static Button Tag(Control panel) =>
        panel.GetVisualDescendants().OfType<Button>().Single(b => b is not RadioButton);

    private static Border HideControl(Control panel) =>
        panel.GetVisualDescendants().OfType<Border>().Single(b => AutomationProperties.GetName(b) == PanelText.HideBannerLabel);

    private static void Press(Border control, Visual root) =>
        control.RaiseEvent(new PointerPressedEventArgs(
            control,
            new Avalonia.Input.Pointer(1, PointerType.Mouse, isPrimary: true),
            root,
            new Point(1, 1),
            timestamp: 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None));

    private static void UseRealFonts() =>
        typeof(PanelShortScreenTests)
            .GetMethod("UseRealFonts", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null);
}
