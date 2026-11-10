using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using OView.Core.Providers.PlanHistory;
using OView.Linux.Panel;
using OView.Presentation.Text;

namespace OView.Linux.Dialogs;

/// <summary>
/// What the user asked the weekly-reset dialog for: an entry to save, or none, which clears the
/// entry and goes back to deriving. Cancelling produces no choice at all, which is why clearing
/// needs a value of its own rather than a null.
/// </summary>
public sealed record WeeklyResetChoice(ManualWeeklyReset? Entry)
{
    public static WeeklyResetChoice Clear { get; } = new((ManualWeeklyReset?)null);
}

/// <summary>
/// The dialog's rules, apart from the window that shows them, so they are the same rules the
/// Windows dialog applies and can be held to that without a display.
///
/// <para><b>Nothing is pre-filled from the derived value.</b> Offering O-view's own guess as a
/// default turns an inference the panel marks approximate into a fact the user has apparently
/// confirmed. The time starts empty unless the user entered one before, and the day starts on
/// Monday, as on Windows.</para>
/// </summary>
public sealed class WeeklyResetForm
{
    /// <summary>The order the Windows list offers the days in, which is the enum's.</summary>
    private static readonly DayOfWeek[] Days = Enum.GetValues<DayOfWeek>();

    private string _time;

    public WeeklyResetForm(ManualWeeklyReset? current)
    {
        Current = current;
        Day = current?.Day ?? DayOfWeek.Monday;
        _time = current?.TimeText ?? "";
    }

    /// <summary>The entry in effect when the dialog opened, or null while O-view derives the reset.</summary>
    public ManualWeeklyReset? Current { get; }

    public DayOfWeek Day { get; private set; }

    /// <summary>
    /// The day as the dialog shows it: the name Core persists and parses back, which is also
    /// what the Windows list shows.
    /// </summary>
    public string DayText => Day.ToString();

    /// <summary>
    /// The time as typed. Editing it withdraws the error, because an error that outlives the
    /// thing it described reads as a bug.
    /// </summary>
    public string Time
    {
        get => _time;
        set
        {
            _time = value;
            Error = null;
        }
    }

    /// <summary>Why the last save was refused, or null.</summary>
    public string? Error { get; private set; }

    /// <summary>Clearing is offered only when there is an entry to clear, as on Windows.</summary>
    public bool CanClear => Current is not null;

    /// <summary>What the field wants; once something is entered, how to get back to deriving.</summary>
    public string Hint => Current is null ? WeeklyResetText.FirstEntryHint : WeeklyResetText.ExistingEntryHint;

    /// <summary>Moves the day forward or back, round the week rather than stopping at its ends.</summary>
    public void StepDay(int by) =>
        Day = Days[(((Array.IndexOf(Days, Day) + by) % Days.Length) + Days.Length) % Days.Length];

    /// <summary>
    /// The entry Save asks for, or null when it is refused, with <see cref="Error"/> saying why.
    ///
    /// <para>An empty time clears rather than refuses: a user who deletes the value is asking to
    /// go back to deriving. Anything else goes through Core's own parser, so this refuses exactly
    /// what Windows refuses and what the settings file would refuse on its next read.</para>
    /// </summary>
    public WeeklyResetChoice? Save()
    {
        var time = Time.Trim();
        if (time.Length == 0)
        {
            return WeeklyResetChoice.Clear;
        }

        if (ManualWeeklyReset.Parse(DayText, time) is not { } parsed)
        {
            Error = WeeklyResetText.TimeError(time);
            return null;
        }

        return new WeeklyResetChoice(parsed);
    }
}

/// <summary>
/// The dialog's content: the source line, the day and the time, the hint, the error and the
/// three buttons, laid out as the Windows dialog lays them out and in its words.
///
/// <para><b>Every control here draws itself.</b> The head registers no toolkit theme, so a stock
/// text box or button has no template and shows nothing. Each one is given its own, in the panel's
/// palette, with its words in the template for the reason the panel's switch keeps them there.
/// The day is a pair of arrows round the day's name rather than a drop-down: a list would need a
/// popup window, which on this head opens white, and seven days are close enough to step
/// through.</para>
/// </summary>
public sealed class WeeklyResetPane : Border
{
    private const double FieldHeightDip = 30;
    private const double ButtonHeightDip = 32;
    private const double LabelFontSize = 11;
    private const double FieldFontSize = 13;

    private readonly WeeklyResetForm _form;
    private readonly LinuxPanelTheme _theme;
    private readonly TextBlock _day;
    private readonly TextBlock _error;

    public WeeklyResetPane(WeeklyResetForm form, LinuxPanelTheme theme)
    {
        _form = form;
        _theme = theme;

        Background = theme["PanelBg"];
        Padding = new Thickness(18);

        _day = new TextBlock
        {
            Text = form.DayText,
            FontSize = FieldFontSize,
            Foreground = theme["TextPrimary"],
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        TimeBox = new TextBox
        {
            Text = form.Time,
            Height = FieldHeightDip,
            FontSize = FieldFontSize,
            Padding = new Thickness(9, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Foreground = theme["TextPrimary"],
            CaretBrush = theme["TextPrimary"],
            SelectionBrush = theme["AccentBg"],
            SelectionForegroundBrush = theme["AccentText"],
            Background = theme["TileBg"],
            BorderBrush = theme["PanelBorder"],
            Template = FieldTemplate(),
        };
        Light(TimeBox, theme["TileBg"], theme["TileHover"], theme["PanelBorder"], theme["AccentBg"]);
        // On the property itself rather than TextChanged, which did not arrive for a change made
        // in code, so any change to the text withdraws the error, typed or not.
        TimeBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                _form.Time = TimeBox.Text ?? "";
                ShowError();
            }
        };

        _error = Words("", theme["WarnText"]);
        _error.Margin = new Thickness(0, 8, 0, 0);
        _error.IsVisible = false;

        SaveButton = ActionButton(WeeklyResetText.SaveButton, primary: true);
        SaveButton.Click += (_, _) => TrySave();

        CancelButton = ActionButton(WeeklyResetText.CancelButton, primary: false);
        CancelButton.Margin = new Thickness(0, 0, 8, 0);
        CancelButton.Click += (_, _) => Cancelled?.Invoke();

        ClearButton = ActionButton(WeeklyResetText.ClearButton, primary: false);
        ClearButton.HorizontalAlignment = HorizontalAlignment.Left;
        ClearButton.IsVisible = form.CanClear;
        ClearButton.Click += (_, _) => Chosen?.Invoke(WeeklyResetChoice.Clear);

        var source = Words(WeeklyResetText.Source, theme["TextSecondary"]);
        source.Margin = new Thickness(0, 0, 0, 14);

        var hint = Words(form.Hint, theme["TextMuted"]);
        hint.Margin = new Thickness(0, 8, 0, 0);

        var fields = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,96") };
        fields.Children.Add(Labelled(WeeklyResetText.DayLabel, DayStepper(), column: 0));
        fields.Children.Add(Labelled(WeeklyResetText.TimeLabel, TimeBox, column: 2));

        var actions = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        actions.Children.Add(ClearButton);
        actions.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { CancelButton, SaveButton },
        });

        Child = new StackPanel { Children = { source, fields, hint, _error, actions } };
    }

    /// <summary>The user chose to save or to clear. Not raised for a refused save.</summary>
    public event Action<WeeklyResetChoice>? Chosen;

    /// <summary>The user chose to leave the entry as it was.</summary>
    public event Action? Cancelled;

    public TextBox TimeBox { get; }

    public Button SaveButton { get; }

    public Button CancelButton { get; }

    public Button ClearButton { get; }

    public Button PreviousDayButton { get; private set; } = null!;

    public Button NextDayButton { get; private set; } = null!;

    /// <summary>The day on screen, read back by the tests.</summary>
    public string DayShown => _day.Text ?? "";

    /// <summary>The error on screen, or null while there is none.</summary>
    public string? ErrorShown => _error.IsVisible ? _error.Text : null;

    /// <summary>
    /// Save, from the button or from Enter. A refused time stays in the field, selected, with the
    /// reason under it, so the next keystroke replaces it.
    /// </summary>
    public void TrySave()
    {
        _form.Time = TimeBox.Text ?? "";

        if (_form.Save() is { } choice)
        {
            Chosen?.Invoke(choice);
            return;
        }

        ShowError();
        TimeBox.Focus();
        TimeBox.SelectAll();
    }

    private void ShowError()
    {
        _error.Text = _form.Error ?? "";
        _error.IsVisible = _form.Error is not null;
    }

    private void StepDay(int by)
    {
        _form.StepDay(by);
        _day.Text = _form.DayText;
    }

    /// <summary>The day's name between a back and a forward arrow, in the field's box.</summary>
    private Border DayStepper()
    {
        PreviousDayButton = Arrow("M4,0 L0,4 L4,8", -1);
        NextDayButton = Arrow("M0,0 L4,4 L0,8", 1);
        Grid.SetColumn(_day, 1);
        Grid.SetColumn(NextDayButton, 2);

        return new Border
        {
            Height = FieldHeightDip,
            Background = _theme["TileBg"],
            BorderBrush = _theme["PanelBorder"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                Children = { PreviousDayButton, _day, NextDayButton },
            },
        };
    }

    /// <summary>
    /// One of the day's arrows. Drawn rather than written, so it adds no words; named for a
    /// screen reader by the day it moves to, which is Core's own name for it.
    /// </summary>
    private Button Arrow(string data, int by)
    {
        var arrow = new Button
        {
            Width = 28,
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(1),
            Cursor = Hand(),
            Template = new FuncControlTemplate<Button>((_, _) => new Border
            {
                [!Border.BackgroundProperty] = new TemplateBinding(TemplatedControl.BackgroundProperty),
                [!Border.CornerRadiusProperty] = new TemplateBinding(TemplatedControl.CornerRadiusProperty),
                Child = new Avalonia.Controls.Shapes.Path
                {
                    Data = Geometry.Parse(data),
                    Stroke = _theme["TextMuted"],
                    StrokeThickness = 1.3,
                    StrokeLineCap = PenLineCap.Round,
                    StrokeJoin = PenLineJoin.Round,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            }),
        };

        Light(arrow, Brushes.Transparent, _theme["TileHover"]);
        arrow.Click += (_, _) => StepDay(by);
        return arrow;
    }

    /// <summary>Save, Cancel or Clear, in the Windows dialog's two weights.</summary>
    private Button ActionButton(string label, bool primary)
    {
        var rest = primary ? _theme["AccentBg"] : Brushes.Transparent;
        var hot = _theme[primary ? "AccentHover" : "RowHover"];
        var ink = primary ? _theme["AccentText"] : _theme["TextSecondary"];

        var button = new Button
        {
            // The word a screen reader announces; the one on screen is drawn by the template.
            Content = label,
            Height = ButtonHeightDip,
            Padding = new Thickness(16, 0),
            Background = rest,
            BorderBrush = primary ? rest : _theme["PanelBorder"],
            BorderThickness = new Thickness(primary ? 0 : 1),
            CornerRadius = new CornerRadius(6),
            Cursor = Hand(),
            Template = new FuncControlTemplate<Button>((_, _) => new Border
            {
                [!Border.BackgroundProperty] = new TemplateBinding(TemplatedControl.BackgroundProperty),
                [!Border.BorderBrushProperty] = new TemplateBinding(TemplatedControl.BorderBrushProperty),
                [!Border.BorderThicknessProperty] = new TemplateBinding(TemplatedControl.BorderThicknessProperty),
                [!Border.CornerRadiusProperty] = new TemplateBinding(TemplatedControl.CornerRadiusProperty),
                [!Border.PaddingProperty] = new TemplateBinding(TemplatedControl.PaddingProperty),
                Child = new TextBlock
                {
                    Text = label,
                    FontSize = FieldFontSize,
                    Foreground = ink,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            }),
        };

        Light(button, rest, hot);
        return button;
    }

    /// <summary>
    /// The text box drawn as the Windows field is: the tile's fill, a hairline that takes the
    /// accent while it has focus. The presenter is the one part the toolkit's text box needs by
    /// name, and what it shows is bound back to the box, so typing reaches the form.
    /// </summary>
    private static FuncControlTemplate<TextBox> FieldTemplate() => new((_, scope) => new Border
    {
        [!Border.BackgroundProperty] = new TemplateBinding(TemplatedControl.BackgroundProperty),
        [!Border.BorderBrushProperty] = new TemplateBinding(TemplatedControl.BorderBrushProperty),
        [!Border.PaddingProperty] = new TemplateBinding(TemplatedControl.PaddingProperty),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(5),
        Cursor = Application.Current is null ? null : new Cursor(StandardCursorType.Ibeam),
        Child = new TextPresenter
        {
            Name = "PART_TextPresenter",
            [!TextPresenter.TextProperty] = new TemplateBinding(TextBox.TextProperty) { Mode = BindingMode.TwoWay },
            [!TextPresenter.CaretIndexProperty] = new TemplateBinding(TextBox.CaretIndexProperty),
            [!TextPresenter.SelectionStartProperty] = new TemplateBinding(TextBox.SelectionStartProperty),
            [!TextPresenter.SelectionEndProperty] = new TemplateBinding(TextBox.SelectionEndProperty),
            [!TextPresenter.CaretBrushProperty] = new TemplateBinding(TextBox.CaretBrushProperty),
            [!TextPresenter.SelectionBrushProperty] = new TemplateBinding(TextBox.SelectionBrushProperty),
            [!TextPresenter.SelectionForegroundBrushProperty] = new TemplateBinding(TextBox.SelectionForegroundBrushProperty),
            [!Layoutable.VerticalAlignmentProperty] = new TemplateBinding(ContentControl.VerticalContentAlignmentProperty),
        }.RegisterInNameScope(scope),
    });

    /// <summary>
    /// Swaps a control's fill while the pointer is over it or it has focus, the Windows dialog's
    /// hover and keyboard-focus triggers, which a template built in code has no other way to
    /// state. A border colour, when given, does the same for the text box's focus ring.
    /// </summary>
    private static void Light(
        TemplatedControl control, IBrush rest, IBrush hot, IBrush? edge = null, IBrush? focusEdge = null)
    {
        control.PropertyChanged += (_, e) =>
        {
            if (e.Property != IsPointerOverProperty && e.Property != IsFocusedProperty)
            {
                return;
            }

            if (edge is not null && focusEdge is not null)
            {
                // The field: focus shows in its edge, and only the pointer lifts its fill.
                control.Background = control.IsPointerOver ? hot : rest;
                control.BorderBrush = control.IsFocused ? focusEdge : edge;
                return;
            }

            control.Background = control.IsPointerOver || control.IsFocused ? hot : rest;
        };
    }

    private StackPanel Labelled(string label, Control field, int column)
    {
        var stack = new StackPanel { Children = { Words(label, _theme["TextMuted"]), field } };
        stack.Children[0].Margin = new Thickness(0, 0, 0, 4);
        Grid.SetColumn(stack, column);
        return stack;
    }

    private static TextBlock Words(string text, IBrush ink, double size = LabelFontSize) => new()
    {
        Text = text,
        FontSize = size,
        TextWrapping = TextWrapping.Wrap,
        Foreground = ink,
    };

    /// <summary>A cursor can only be made once the windowing platform has started; the pane is built in tests too.</summary>
    private static Cursor? Hand() => Application.Current is null ? null : new Cursor(StandardCursorType.Hand);
}

/// <summary>
/// Where the user enters the weekly reset Anthropic assigned their account (issue #186), the
/// Linux half of the Windows dialog.
///
/// <para><b>A window the desktop decorates.</b> The Windows dialog draws its own frame; this one
/// keeps the desktop's, which is what lets a person move it or close it on every window manager
/// this head runs under, and what names it in the window list. The title bar carries the title,
/// so the content starts at the source line.</para>
///
/// <para><b>Not modal.</b> A tray app owns no window for a dialog to be modal to. The head keeps
/// at most one open and brings that one forward when the row is chosen again.</para>
/// </summary>
public sealed class WeeklyResetDialog : Window
{
    private readonly WeeklyResetPane _pane;

    public WeeklyResetDialog(WeeklyResetForm form, LinuxPanelTheme theme)
    {
        _pane = new WeeklyResetPane(form, theme);
        _pane.Chosen += choice =>
        {
            Chosen?.Invoke(choice);
            Close();
        };
        _pane.Cancelled += Close;

        Title = WeeklyResetText.Title;
        Content = _pane;
        Width = 400;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = theme["PanelBg"];

        // Ahead of the controls, as the Windows dialog's preview handler is: Enter saves from
        // anywhere in the dialog and Escape leaves it, whichever control has focus.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                _pane.TrySave();
            }
        }, RoutingStrategies.Tunnel);

        Opened += (_, _) =>
        {
            Activate();
            _pane.TimeBox.Focus();
        };
    }

    /// <summary>The user saved or cleared. The window closes itself after raising it.</summary>
    public event Action<WeeklyResetChoice>? Chosen;
}
