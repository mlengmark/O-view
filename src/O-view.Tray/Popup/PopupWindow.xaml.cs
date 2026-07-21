using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using OView.Core.Models;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace OView.Tray.Popup;

/// <summary>
/// The detail panel (ui-spec.md). Times convert to local here, at the display edge.
/// Dismisses on Esc and on deactivation (click-outside). Theme follows
/// AppsUseLightTheme — the app-window setting, distinct from the taskbar's — and is
/// re-read on every open so a theme switch never needs a restart.
/// </summary>
public partial class PopupWindow : Window
{
    private static readonly Color Green = Color.FromRgb(64, 200, 110);
    private static readonly Color Amber = Color.FromRgb(240, 170, 40);
    private static readonly Color Red = Color.FromRgb(232, 72, 72);
    private static readonly Color GraphBar = Color.FromRgb(127, 119, 221);

    /// <summary>Forces a theme for verification screenshots; null follows the OS.</summary>
    public bool? ThemeOverride { get; set; }

    /// <summary>Disables auto-hide for verification screenshots.</summary>
    public bool PinForVerification { get; set; }

    public PopupWindow()
    {
        InitializeComponent();
        Deactivated += (_, _) => { if (!PinForVerification) Hide(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
    }

    public void ShowNearTrayIcon(UsageSnapshot snapshot, PanelStatistics stats, ClaudeAccount? account)
    {
        ApplyTheme(ThemeOverride ?? IsAppsLightTheme());
        Populate(snapshot, stats, account);

        // SizeToContent height is unknown until measured: lay out off-screen, then place.
        Left = -10_000;
        Top = -10_000;
        Show();
        UpdateLayout();
        (Left, Top) = PopupPositioner.Place(ActualWidth, ActualHeight);
        Activate();
    }

    // ── data ───────────────────────────────────────────────────────────────────

    private void Populate(UsageSnapshot snapshot, PanelStatistics stats, ClaudeAccount? account)
    {
        var local = TimeZoneInfo.Local;

        UpdatedText.Text = $"Updated {Now(local):HH:mm} · {SourceLabel(snapshot, local)}";
        NameText.Text = account?.DisplayName ?? "account unknown";
        EmailText.Text = account?.Email ?? "";
        EmailText.Visibility = account?.Email is null ? Visibility.Collapsed : Visibility.Visible;
        // Tier from organizationType — seatTier is empty and would render blank (rule 8).
        TierText.Text = account?.Tier ?? "tier unknown";

        var authoritative = snapshot.Source is DataSource.Live or DataSource.Stale;
        PopulateBar(SessionPctText, SessionBar, SessionBarFill,
            authoritative ? snapshot.SessionPercent : null);
        SessionResetText.Text = snapshot.SessionResetAtUtc is { } reset
            ? $"Resets in {FormatCountdown(reset - Now(TimeZoneInfo.Utc))} · {TimeZoneInfo.ConvertTime(reset, local):HH:mm}"
            : "Reset time unknown (no reset observed yet)";

        PopulateBar(WeeklyPctText, WeeklyBar, WeeklyBarFill,
            authoritative ? snapshot.WeeklyPercent : null);
        WeeklyResetText.Text = "Reset time unknown";  // 7d resets are not derivable yet (ADR-0007)

        TileTokensToday.Text = FormatTokens(stats.TokensToday);
        TileEstToday.Text = FormatUsd(stats.EstTodayUsd);
        TileTokens31.Text = FormatTokens(stats.Tokens31Days);
        TileEst31.Text = FormatUsd(stats.Est31DaysUsd);

        // Partial history states its coverage — a small number without this caveat
        // reads as low usage rather than short history (ADR-0006).
        var coverage = stats.HasPartialHistory
            ? $"{stats.RecordedDays} of {stats.WindowDays} days recorded"
            : "";
        TileCoverage31.Text = coverage;
        TileCoverage31b.Text = coverage;

        BuildGraph(stats);
    }

    private void PopulateBar(TextBlock pctText, Grid bar, Border fill, int? percent)
    {
        System.Windows.Data.BindingOperations.ClearBinding(fill, WidthProperty);

        if (percent is { } p)
        {
            pctText.Text = string.Create(CultureInfo.InvariantCulture, $"{p}% used");
            fill.Background = new SolidColorBrush(p switch { >= 85 => Red, >= 60 => Amber, _ => Green });
            // Fill width tracks the bar's laid-out width × percent.
            fill.SetBinding(WidthProperty, new System.Windows.Data.Binding(nameof(bar.ActualWidth))
            {
                Source = bar,
                Converter = new PercentWidthConverter(),
                ConverterParameter = Math.Clamp(p, 0, 100),
            });
        }
        else
        {
            pctText.Text = "unknown";
            fill.Width = 0;
        }
    }

    private void BuildGraph(PanelStatistics stats)
    {
        GraphHost.Children.Clear();
        GraphHost.ColumnDefinitions.Clear();

        var series = stats.DailySeries;
        var max = Math.Max(1, series.Max(d => d.TotalTokens));
        var preInstallCount = series.TakeWhile(d => d.PreInstall).Count();

        for (var i = 0; i < series.Count; i++)
        {
            GraphHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        // Pre-install days: an explicit empty region, never zero-height bars (rule 6).
        if (preInstallCount > 0)
        {
            var region = new Border
            {
                BorderBrush = (Brush)FindResource("TextMuted"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Opacity = 0.45,
            };
            Grid.SetColumn(region, 0);
            Grid.SetColumnSpan(region, preInstallCount);
            GraphHost.Children.Add(region);
        }

        for (var i = preInstallCount; i < series.Count; i++)
        {
            var bar = new Rectangle
            {
                Fill = new SolidColorBrush(GraphBar),
                Height = Math.Max(series[i].TotalTokens == 0 ? 0 : 2, 56.0 * series[i].TotalTokens / max),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0.5, 0, 0.5, 0),
                RadiusX = 1,
                RadiusY = 1,
            };
            Grid.SetColumn(bar, i);
            GraphHost.Children.Add(bar);
        }

        GraphCaption.Text = preInstallCount > 0
            ? "Outlined region: before O-view install — no data, not zero usage."
            : "";
        GraphCaption.Visibility = preInstallCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── formatting (display edge) ──────────────────────────────────────────────

    private static DateTimeOffset Now(TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);

    private static string SourceLabel(UsageSnapshot s, TimeZoneInfo local) => s.Source switch
    {
        DataSource.Live => "live",
        DataSource.Stale => s.CapturedAtUtc is { } at
            ? $"as of {TimeZoneInfo.ConvertTime(at, local):HH:mm}"
            : "stale",
        DataSource.Estimate => "local estimate",
        _ => "no data",
    };

    private static string FormatCountdown(TimeSpan t) => t.TotalMinutes < 1
        ? "under a minute"
        : t.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}h {t.Minutes}m")
            : string.Create(CultureInfo.InvariantCulture, $"{t.Minutes}m");

    private static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{tokens / 1_000_000.0:0.0}M"),
        >= 1_000 => string.Create(CultureInfo.InvariantCulture, $"{tokens / 1_000.0:0.0}K"),
        _ => tokens.ToString(CultureInfo.InvariantCulture),
    };

    private static string FormatUsd(decimal? usd) => usd is { } v
        ? "$" + v.ToString("0.00", CultureInfo.InvariantCulture)
        : "unknown";

    // ── theming ────────────────────────────────────────────────────────────────

    private static bool IsAppsLightTheme()
    {
        try
        {
            return Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1) is 1;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or System.IO.IOException)
        {
            return true;
        }
    }

    private void ApplyTheme(bool light)
    {
        SetBrush("PanelBg", light ? "#F9F9F9" : "#202020");
        SetBrush("PanelBorder", light ? "#D6D6D6" : "#383838");
        SetBrush("TextPrimary", light ? "#1A1A1A" : "#F0F0F0");
        SetBrush("TextSecondary", light ? "#555555" : "#B5B5B5");
        SetBrush("TextMuted", light ? "#8A8A8A" : "#8A8A8A");
        SetBrush("TileBg", light ? "#EFEFEF" : "#2B2B2B");
        SetBrush("BarTrack", light ? "#DDDDDD" : "#3A3A3A");
        SetBrush("BadgeBg", light ? "#E4DCF5" : "#3A3355");
        SetBrush("BadgeText", light ? "#4A3A85" : "#C7BDEB");
        SetBrush("WarnBg", light ? "#F7EBD4" : "#453A22");
        SetBrush("WarnText", light ? "#8A5D00" : "#E3B858");
    }

    private void SetBrush(string key, string hex) =>
        Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
}

/// <summary>Bar fill width = track ActualWidth × percent.</summary>
internal sealed class PercentWidthConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double actual && parameter is int percent ? Math.Max(percent == 0 ? 0.0 : 4.0, actual * percent / 100.0) : 0.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
