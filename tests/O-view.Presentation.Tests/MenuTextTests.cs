using OView.Presentation.Text;

namespace OView.Presentation.Tests;

/// <summary>
/// The tray menu's rows and the weekly-reset dialog's words, extracted so both heads say the
/// same thing.
///
/// <para>These assert the <b>exact strings the two heads produced before the extraction</b>,
/// character for character — the same contract <see cref="PanelTextTests"/> holds for the
/// panel, and for the same reason: the menu's only other verification is an offscreen render,
/// which cannot tell a wording change from a layout one.</para>
/// </summary>
public class MenuTextTests
{
    // ── the rows ────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheToggleRowsReadAsTheyDid()
    {
        Assert.Equal("Run at startup", MenuText.RunAtStartupRow);
        Assert.Equal("Update automatically", MenuText.UpdateAutomaticallyRow);
    }

    [Fact]
    public void TheActionRowsReadAsTheyDid()
    {
        Assert.Equal("Weekly reset…", MenuText.WeeklyResetRow);
        Assert.Equal("Copy diagnostics", MenuText.CopyDiagnosticsRow);
        Assert.Equal("Check for updates…", MenuText.CheckForUpdatesRow);
        Assert.Equal("Exit O-view", MenuText.ExitRow);
    }

    /// <summary>
    /// The threshold row reads as one sentence across three elements. Asserted as the
    /// assembled line, because that is the thing a reader sees and the thing a change to any
    /// one of the three could break.
    /// </summary>
    [Fact]
    public void TheThresholdRowStillReadsAsOneSentence() =>
        Assert.Equal(
            "Notify at 70% usage",
            $"{MenuText.NotifyPrefix} {MenuText.ThresholdPercent(70)} {MenuText.NotifySuffix}");

    /// <summary>
    /// The pill renders the setting as it actually stands rather than snapping to one of the
    /// three offers: <c>TraySettings</c> accepts any 1–100 value and a hand-edited
    /// settings.json is a legitimate way to hold one. Showing 75 as "80%" would be the menu
    /// asserting something about the machine that is not true (rule 6).
    /// </summary>
    [Fact]
    public void AnOffMenuThresholdIsShownAsItIs()
    {
        Assert.Equal("75%", MenuText.ThresholdPercent(75));
        Assert.Equal("100%", MenuText.ThresholdPercent(100));
    }

    // ── the two values beside a row ─────────────────────────────────────────────────

    [Fact]
    public void TheVersionCarriesItsV() =>
        Assert.Equal("v0.7.1", MenuText.VersionLabel("0.7.1"));

    /// <summary>
    /// With nothing entered, the row states the answer in effect rather than only offering to
    /// change it — a user who has never opened the dialog still learns what O-view is doing.
    /// </summary>
    [Fact]
    public void TheWeeklyResetRowSaysDerivedWhenNothingWasEntered() =>
        Assert.Equal("derived", MenuText.WeeklyResetDerived);

    /// <summary>
    /// An entry is abbreviated to three letters and the time, because the row has its label
    /// beside it and no room for more. The two used to be assembled in the Windows shell,
    /// which is also the only place the slicing could have gone wrong.
    /// </summary>
    [Fact]
    public void AnEnteredResetIsAbbreviatedToFitTheRow()
    {
        Assert.Equal("Mon 22:59", MenuText.WeeklyResetValue("Monday", "22:59"));
        Assert.Equal("Wed 09:00", MenuText.WeeklyResetValue("Wednesday", "09:00"));
    }

    /// <summary>
    /// The exit row names the app. A bare "Exit" in a menu the desktop renders — which is
    /// what Linux gets — does not say what it is exiting.
    /// </summary>
    [Fact]
    public void TheExitRowNamesTheApp() =>
        Assert.Contains(OView.Presentation.Panel.PanelHeaderView.AppName, MenuText.ExitRow, StringComparison.Ordinal);
}
