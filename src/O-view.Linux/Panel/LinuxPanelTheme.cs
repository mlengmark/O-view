using Avalonia.Media;
using OView.App.Rendering;

namespace OView.Linux.Panel;

/// <summary>
/// The shared panel palette as Avalonia brushes.
///
/// <para>Values and the reasoning behind them live in
/// <see cref="PanelPalette"/> — the accent's contrast ratios and the categorical series'
/// colour-vision separation figures are recorded there, shared with the WPF panel. This
/// class only converts. Restating a hex here would carry the colour but not the constraint
/// that produced it.</para>
/// </summary>
public sealed class LinuxPanelTheme
{
    private readonly Dictionary<string, IBrush> _brushes;

    public LinuxPanelTheme(bool light)
    {
        IsLight = light;
        _brushes = PanelPalette.All(light)
            .ToDictionary(e => e.Key, e => (IBrush)new SolidColorBrush(Color.Parse(e.Value)));
    }

    public bool IsLight { get; }

    /// <summary>A palette brush. Throws on an unknown key — a missing colour renders as
    /// transparent, which is far harder to notice than a crash.</summary>
    public IBrush this[string key] => _brushes.TryGetValue(key, out var brush)
        ? brush
        : throw new ArgumentOutOfRangeException(nameof(key), key, "Not a panel palette key.");

    /// <summary>The band colour for a usage percentage, from the shared classifier so the
    /// bars and the tray icon can never disagree about what "amber" means.</summary>
    public IBrush Level(int? percent) => percent is { } p
        ? new SolidColorBrush(ToAvalonia(TrayIconGeometry.LevelColor(
            Core.Models.UsageLevels.Classify(p), lightTaskbar: IsLight)))
        : this["TextMuted"];

    private static Color ToAvalonia(IconColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);
}
