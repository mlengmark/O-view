using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using OView.Core.Providers.PlanHistory;
using OView.Tray.Tray;

namespace OView.Tray.Popup;

/// <summary>
/// Where the user enters the weekly reset Anthropic assigned their account (GitHub issue
/// #186).
///
/// <para>Its own window rather than <see cref="DialogWindow"/>, which confirms and nothing
/// else. This one collects input, so it needs validation and a third action — clearing the
/// entry to go back to deriving.</para>
///
/// <para><b>Nothing is pre-filled from the derived value.</b> Offering O-view's own guess as
/// a default converts an inference the panel honestly marks approximate into a fact the user
/// has apparently confirmed, which is strictly worse than showing it with a "~". The field
/// starts empty unless the user has entered something before.</para>
/// </summary>
public partial class WeeklyResetDialog : Window
{
    private ManualWeeklyReset? _result;
    private bool _saved;
    private bool _cleared;

    /// <summary>Forces a theme for verification screenshots; null follows the OS.</summary>
    public bool? ThemeOverride { get; set; }

    public WeeklyResetDialog()
    {
        InitializeComponent();

        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            DayBox.Items.Add(day.ToString());
        }

        SaveButton.Click += (_, _) => TrySave();
        CancelButton.Click += (_, _) => Close(saved: false, cleared: false);
        ClearButton.Click += (_, _) => Close(saved: false, cleared: true);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(saved: false, cleared: false);
            }
            else if (e.Key == Key.Enter)
            {
                TrySave();
            }
        };

        // Clearing the error as soon as the field is edited, rather than only on the next
        // save attempt — an error that outlives the thing it described reads as a bug.
        TimeBox.TextChanged += (_, _) => ErrorText.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Shows the dialog modally. Returns the entry now in effect, and whether the user asked
    /// to clear it — which is distinct from cancelling, and cannot be expressed by a null
    /// result alone.
    /// </summary>
    public static (ManualWeeklyReset? Entry, bool Changed) Show(
        ManualWeeklyReset? current, bool? themeOverride = null)
    {
        var dialog = new WeeklyResetDialog { ThemeOverride = themeOverride };
        dialog.Populate(current);

        // Same foreground problem as DialogWindow, and the same fix: a tray app owns no
        // activated window, and ContentRendered is the first point this one is genuinely
        // visible. See DialogWindow.Confirm for why SourceInitialized is too early.
        dialog.ContentRendered += (_, _) =>
        {
            ForegroundWindow.Take(new WindowInteropHelper(dialog).Handle);
            dialog.Activate();
            dialog.TimeBox.Focus();
        };

        dialog.ShowDialog();

        return dialog._cleared
            ? (null, true)
            : (dialog._saved ? dialog._result : current, dialog._saved);
    }

    /// <summary>Fills the dialog without showing it — also the verification-render path.</summary>
    public void Populate(ManualWeeklyReset? current)
    {
        PanelTheme.Apply(Resources, ThemeOverride ?? PanelTheme.IsAppsLight());

        DayBox.SelectedItem = (current?.Day ?? DayOfWeek.Monday).ToString();
        TimeBox.Text = current?.TimeText ?? "";

        HintText.Text = current is null
            ? "Local time, 24-hour, e.g. 22:59. Left empty, O-view keeps deriving the reset "
              + "by watching your weekly percentage fall."
            : "Local time, 24-hour. Clearing this returns to the derived reset.";

        ClearButton.Visibility = current is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void TrySave()
    {
        var day = DayBox.SelectedItem as string;
        var time = TimeBox.Text.Trim();

        // Empty is "clear", not an error — a user who deletes the value is asking to go back
        // to deriving, and making them hunt for a separate button for that is friction.
        if (time.Length == 0)
        {
            Close(saved: false, cleared: true);
            return;
        }

        if (ManualWeeklyReset.Parse(day, time) is not { } parsed)
        {
            ErrorText.Text = $"'{time}' is not a 24-hour time. Enter it as HH:mm — 22:59, not 10:59 PM.";
            ErrorText.Visibility = Visibility.Visible;
            TimeBox.Focus();
            TimeBox.SelectAll();
            return;
        }

        _result = parsed;
        Close(saved: true, cleared: false);
    }

    private void Close(bool saved, bool cleared)
    {
        _saved = saved;
        _cleared = cleared;
        DialogResult = saved || cleared;
    }

    /// <summary>Renders the dialog offscreen for the verification hook.</summary>
    internal System.Windows.Media.Imaging.BitmapSource RenderToBitmap(
        ManualWeeklyReset? current, double scale, string? error = null)
    {
        Populate(current);

        if (error is { Length: > 0 })
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
        }

        return VisualRenderer.RenderContent(this, scale);
    }
}
