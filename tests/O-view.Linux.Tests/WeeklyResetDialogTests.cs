using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using OView.App;
using OView.Core.Models;
using OView.Core.Providers.PlanHistory;
using OView.Linux.Dialogs;
using OView.Linux.Panel;
using OView.Linux.Platform;
using OView.Presentation.Text;

namespace OView.Linux.Tests;

/// <summary>
/// The Linux weekly-reset dialog refuses, clears and saves exactly as the Windows one does, in
/// Presentation's words, and the head saves what it returns through the shell.
/// </summary>
public class WeeklyResetDialogTests
{
    private static readonly ManualWeeklyReset MondayEvening = new(DayOfWeek.Monday, new TimeOnly(22, 59));

    // ── the form: what Save, Clear and the day arrows mean ─────────────────────────────

    /// <summary>
    /// A first entry starts empty on Monday, with nothing to clear: O-view's own guess offered as
    /// a default would become a fact the user had apparently confirmed.
    /// </summary>
    [Fact]
    public void AFirstEntryStartsEmptyWithNothingToClear()
    {
        var form = new WeeklyResetForm(null);

        Assert.Equal(DayOfWeek.Monday, form.Day);
        Assert.Equal("", form.Time);
        Assert.False(form.CanClear);
        Assert.Equal(WeeklyResetText.FirstEntryHint, form.Hint);
    }

    [Fact]
    public void AnEntryOpensOnItsOwnDayAndTimeAndCanBeCleared()
    {
        var form = new WeeklyResetForm(new ManualWeeklyReset(DayOfWeek.Thursday, new TimeOnly(7, 5)));

        Assert.Equal(DayOfWeek.Thursday, form.Day);
        Assert.Equal("07:05", form.Time);
        Assert.True(form.CanClear);
        Assert.Equal(WeeklyResetText.ExistingEntryHint, form.Hint);
    }

    /// <summary>
    /// What Core's parser refuses, the dialog refuses, quoting what was typed: a 12-hour time, an
    /// hour or minute out of range, a missing separator or a missing leading zero.
    /// </summary>
    [Theory]
    [InlineData("10:59 PM")]
    [InlineData("25:00")]
    [InlineData("22:60")]
    [InlineData("2259")]
    [InlineData("22.59")]
    [InlineData("9:30")]
    [InlineData("noon")]
    public void ATimeCoreCannotParseIsRefusedWithTheReason(string typed)
    {
        var form = new WeeklyResetForm(null) { Time = $" {typed} " };

        Assert.Null(form.Save());
        Assert.Null(ManualWeeklyReset.Parse(form.DayText, typed));   // the parser is the authority
        Assert.Equal(WeeklyResetText.TimeError(typed), form.Error);
    }

    [Fact]
    public void EditingTheTimeWithdrawsTheError()
    {
        var form = new WeeklyResetForm(null) { Time = "10:59 PM" };
        Assert.Null(form.Save());

        form.Time = "22:59";

        Assert.Null(form.Error);
    }

    [Fact]
    public void AValidTimeSavesTheChosenDayAndTime()
    {
        var form = new WeeklyResetForm(null) { Time = " 22:59 " };
        form.StepDay(2);

        var choice = form.Save();

        Assert.Equal(new WeeklyResetChoice(new ManualWeeklyReset(DayOfWeek.Wednesday, new TimeOnly(22, 59))), choice);
        Assert.Null(form.Error);
    }

    /// <summary>Deleting the time is asking to go back to deriving, not a mistake to report.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyTimeClearsTheEntry(string typed)
    {
        var form = new WeeklyResetForm(MondayEvening) { Time = typed };

        Assert.Same(WeeklyResetChoice.Clear, form.Save());
        Assert.Null(WeeklyResetChoice.Clear.Entry);
        Assert.Null(form.Error);
    }

    [Fact]
    public void TheDayStepsRoundTheWeekInTheWindowsListsOrder()
    {
        var form = new WeeklyResetForm(null);

        form.StepDay(-1);
        Assert.Equal(DayOfWeek.Sunday, form.Day);

        form.StepDay(-1);
        Assert.Equal(DayOfWeek.Saturday, form.Day);

        form.StepDay(1);
        Assert.Equal(DayOfWeek.Sunday, form.Day);

        form.StepDay(7);
        Assert.Equal(DayOfWeek.Sunday, form.Day);
    }

    /// <summary>The day shown is the name Core persists, so every one of them saves.</summary>
    [Fact]
    public void EveryDayShownIsOneCoreParsesBack()
    {
        var form = new WeeklyResetForm(null);

        foreach (var _ in Enum.GetValues<DayOfWeek>())
        {
            Assert.Equal(form.Day, ManualWeeklyReset.Parse(form.DayText, "12:00")!.Day);
            form.StepDay(1);
        }
    }

    // ── the pane: the controls on the form ─────────────────────────────────────────────

    /// <summary>
    /// Every word on the dialog is Presentation's, or Core's name for a day. The buttons' words
    /// are drawn by their templates, which only build inside a window, so their content, which
    /// carries the same word for a screen reader, is what is read here.
    /// </summary>
    [Fact]
    public void EveryWordOnTheDialogComesFromPresentation()
    {
        AvaloniaDispatcherTestSupport.ClaimUiThreadForThisThread();

        string[] allowed =
        [
            WeeklyResetText.Source, WeeklyResetText.DayLabel, WeeklyResetText.TimeLabel,
            WeeklyResetText.FirstEntryHint, WeeklyResetText.ExistingEntryHint,
            WeeklyResetText.SaveButton, WeeklyResetText.CancelButton, WeeklyResetText.ClearButton,
            WeeklyResetText.TimeError("10:59 PM"),
            .. Enum.GetNames<DayOfWeek>(),
        ];

        foreach (var current in new[] { null, MondayEvening })
        {
            var pane = Pane(current);
            pane.TimeBox.Text = "10:59 PM";
            pane.TrySave();

            var words = pane.GetLogicalDescendants()
                .Select(c => c switch
                {
                    TextBlock block => block.Text,
                    Button button => button.Content as string,
                    _ => null,
                })
                .Where(w => !string.IsNullOrEmpty(w))
                .ToArray();

            Assert.NotEmpty(words);
            Assert.All(words, w => Assert.Contains(w, allowed));
        }
    }

    [Fact]
    public void ARefusedSaveShowsTheReasonAndChoosesNothing()
    {
        AvaloniaDispatcherTestSupport.ClaimUiThreadForThisThread();
        var pane = Pane(null);
        var chosen = new List<WeeklyResetChoice>();
        pane.Chosen += chosen.Add;

        pane.TimeBox.Text = "10:59 PM";
        Click(pane.SaveButton);

        Assert.Empty(chosen);
        Assert.Equal(WeeklyResetText.TimeError("10:59 PM"), pane.ErrorShown);

        pane.TimeBox.Text = "22:59";
        Assert.Null(pane.ErrorShown);
    }

    [Fact]
    public void SaveChoosesTheDayTheArrowsReachedAndTheTimeTyped()
    {
        AvaloniaDispatcherTestSupport.ClaimUiThreadForThisThread();
        var pane = Pane(null);
        var chosen = new List<WeeklyResetChoice>();
        pane.Chosen += chosen.Add;

        Click(pane.NextDayButton);
        Click(pane.NextDayButton);
        Click(pane.PreviousDayButton);
        Assert.Equal(nameof(DayOfWeek.Tuesday), pane.DayShown);

        pane.TimeBox.Text = "06:30";
        Click(pane.SaveButton);

        Assert.Equal([new WeeklyResetChoice(new ManualWeeklyReset(DayOfWeek.Tuesday, new TimeOnly(6, 30)))], chosen);
    }

    [Fact]
    public void ClearIsOfferedOnlyForAnEntryAndChoosesNoEntry()
    {
        AvaloniaDispatcherTestSupport.ClaimUiThreadForThisThread();
        Assert.False(Pane(null).ClearButton.IsVisible);

        var pane = Pane(MondayEvening);
        var chosen = new List<WeeklyResetChoice>();
        pane.Chosen += chosen.Add;

        Assert.True(pane.ClearButton.IsVisible);
        Assert.Equal("22:59", pane.TimeBox.Text);

        Click(pane.ClearButton);

        Assert.Equal([WeeklyResetChoice.Clear], chosen);
    }

    [Fact]
    public void CancelLeavesTheEntryAsItWas()
    {
        AvaloniaDispatcherTestSupport.ClaimUiThreadForThisThread();
        var pane = Pane(MondayEvening);
        var chosen = new List<WeeklyResetChoice>();
        var cancelled = 0;
        pane.Chosen += chosen.Add;
        pane.Cancelled += () => cancelled++;

        Click(pane.CancelButton);

        Assert.Empty(chosen);
        Assert.Equal(1, cancelled);
    }

    // ── the head: the row, the save and the conflict notice ───────────────────────────

    /// <summary>The row says what is in effect: "derived", or the entry as the Windows row shows it.</summary>
    [Fact]
    public void TheRowShowsDerivedOrTheEntryInPresentationsWords()
    {
        Assert.Equal($"{MenuText.WeeklyResetRow}  {MenuText.WeeklyResetDerived}", RowHeader(null));
        Assert.Equal(
            $"{MenuText.WeeklyResetRow}  {MenuText.WeeklyResetValue("Monday", "22:59")}",
            RowHeader(MondayEvening));
    }

    /// <summary>
    /// Saving goes through the shell, then polls, as on Windows; and the row is labelled with
    /// what the shell returned. The recording shell refuses once, as a settings file that cannot
    /// be written would, and the row then still says "derived".
    /// </summary>
    [Fact]
    public void SavingGoesThroughTheShellAndTheRowShowsWhatItReturned()
    {
        AvaloniaDispatcherTestSupport.ClaimUiThreadForThisThread();
        var head = new LinuxApp();
        var shell = WeeklyResetShell.Create();
        var row = WeeklyResetRow(head, shell);

        Apply(head, shell, MondayEvening);
        Assert.Equal(["SetWeeklyReset(Monday 22:59)", "Refresh"], shell.Calls);
        Assert.Equal(RowHeader(MondayEvening), row.Header);

        Apply(head, shell, null);
        Assert.Equal(["SetWeeklyReset(cleared)", "Refresh"], shell.Calls[2..]);
        Assert.Equal(RowHeader(null), row.Header);

        shell.RefuseToSave = true;
        Apply(head, shell, MondayEvening);
        Assert.Equal(RowHeader(null), row.Header);
    }

    /// <summary>
    /// A reported reset that disproves the entry is noticed once, in the Windows notice's words,
    /// and marked told; the same conflict again, or no conflict, says nothing.
    /// </summary>
    [Fact]
    public void AConflictIsNoticedOnceInTheWindowsWords()
    {
        var shell = WeeklyResetShell.Create();
        Assert.Null(Notice(shell));

        var reported = new DateTimeOffset(2026, 11, 13, 6, 0, 0, TimeSpan.Zero);
        shell.Conflict = reported;

        Assert.Equal(
            (NoticeText.WeeklyResetConflictTitle, PanelText.WeeklyResetConflict(reported, TimeZoneInfo.Local)),
            Notice(shell));
        Assert.Equal(reported, shell.Noticed);

        Assert.Null(Notice(shell));
    }

    private static WeeklyResetPane Pane(ManualWeeklyReset? current) =>
        new(new WeeklyResetForm(current), new LinuxPanelTheme(light: false));

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static string RowHeader(ManualWeeklyReset? entry) =>
        (string)typeof(LinuxApp)
            .GetMethod("WeeklyResetRowHeader", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [entry])!;

    private static NativeMenuItem WeeklyResetRow(LinuxApp head, WeeklyResetShell shell)
    {
        var menu = (NativeMenu)typeof(LinuxApp)
            .GetMethod("BuildMenu", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(head, [shell, ClientPlacement.From("x11", null, ":0")])!;

        return Assert.Single(
            menu.Items.OfType<NativeMenuItem>(),
            i => i.Header?.StartsWith(MenuText.WeeklyResetRow, StringComparison.Ordinal) == true);
    }

    private static void Apply(LinuxApp head, WeeklyResetShell shell, ManualWeeklyReset? entry) =>
        typeof(LinuxApp)
            .GetMethod("ApplyWeeklyReset", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(head, [shell, entry]);

    private static (string Title, string Body)? Notice(WeeklyResetShell shell) =>
        ((string, string)?)typeof(LinuxApp)
            .GetMethod("WeeklyResetConflictNotice", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [shell]);

    /// <summary>
    /// A shell holding one weekly-reset entry and one conflict, answering the way the engine does.
    /// Everything else throws: the paths under test must not need it.
    /// </summary>
    public class WeeklyResetShell : DispatchProxy
    {
        public List<string> Calls { get; } = [];
        public ManualWeeklyReset? Entry { get; set; }
        public bool RefuseToSave { get; set; }
        public DateTimeOffset? Conflict { get; set; }
        public DateTimeOffset? Noticed { get; set; }

        public static WeeklyResetShell Create() =>
            (WeeklyResetShell)(object)Create<ISkinToShell, WeeklyResetShell>();

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case nameof(ISkinToShell.SetWeeklyReset):
                    var asked = (ManualWeeklyReset?)args![0];
                    Calls.Add($"SetWeeklyReset({(asked is null ? "cleared" : $"{asked.DayText} {asked.TimeText}")})");
                    if (!RefuseToSave)
                    {
                        Entry = asked;
                    }

                    return Entry;

                case nameof(ISkinToShell.Refresh):
                    Calls.Add("Refresh");
                    return null;

                case "get_" + nameof(ISkinToShell.Settings):
                    return new TraySettings(
                        WeeklyResetDay: Entry?.DayText ?? "", WeeklyResetTime: Entry?.TimeText ?? "");

                case "get_" + nameof(ISkinToShell.WeeklyResetConflict):
                    return Conflict;

                case nameof(ISkinToShell.IsWeeklyResetConflictUnseen):
                    return Noticed != (DateTimeOffset)args![0]!;

                case nameof(ISkinToShell.MarkWeeklyResetConflictNoticed):
                    Noticed = (DateTimeOffset)args![0]!;
                    return null;

                default:
                    throw new NotSupportedException(method.Name);
            }
        }
    }
}
