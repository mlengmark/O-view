using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using OView.Core.Models;

namespace OView.Tray.Tray;

/// <summary>
/// Rasterises the tray icon as the O-view brand mark: a proportional ring gauge with a
/// filled centre pupil — the "eye" the wordmark is built on. No digits (GitHub issue #1).
///
/// The pupil is drawn at every size, including 16 px. This revisits issue #1's ring-only
/// note, deliberately: the pupil unifies the live tray icon with the static exe icon
/// (the brand mark) so the app reads as one thing everywhere. It does NOT reopen the
/// original spike (docs/findings/tray-icon-rendering.md), which rejected ring *plus
/// digits* — two competing signals starving each other. A pupil is not a second signal:
/// it sits in the ring's empty hole and carries no data, so the 16 px legibility finding
/// for digits still stands. The exact percentage lives in the tooltip.
///
/// Colour bands come from OView.Core.UsageLevels (issue #2): green &lt;50, amber 50–69,
/// red ≥70 — shared with the popup so they cannot drift apart. The pupil takes the same
/// band colour as the arc, so the icon stays a single-colour signal.
/// </summary>
public static class IconRenderer
{
    private static readonly Color DarkGreen = Color.FromArgb(64, 200, 110);
    private static readonly Color DarkAmber = Color.FromArgb(240, 170, 40);
    private static readonly Color DarkRed = Color.FromArgb(232, 72, 72);
    private static readonly Color DarkNeutral = Color.FromArgb(150, 150, 150);

    // Darker shades for a light taskbar, where the bright set washes out.
    private static readonly Color LightGreen = Color.FromArgb(24, 122, 62);
    private static readonly Color LightAmber = Color.FromArgb(179, 116, 0);
    private static readonly Color LightRed = Color.FromArgb(197, 30, 30);
    private static readonly Color LightNeutral = Color.FromArgb(110, 110, 110);

    /// <summary>Small-icon size for the current DPI (16 px at 100%, 24 px at 150%).</summary>
    public static int CurrentIconSize()
    {
        var size = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON);
        return size > 0 ? size : 16;
    }

    /// <summary>
    /// Render the snapshot into a size×size bitmap. Caller owns the bitmap. Unknown or
    /// estimated data (no authoritative percent) renders an empty ring — never a
    /// fabricated fill (CLAUDE.md rule 6).
    /// </summary>
    public static Bitmap Render(int size, UsageSnapshot snapshot, bool lightTaskbar)
    {
        var percent = snapshot.Source is DataSource.Live or DataSource.Stale
            ? snapshot.SessionPercent
            : null;

        if (percent is null)
        {
            return RenderRing(size, fillPercent: 0, lightTaskbar ? LightNeutral : DarkNeutral, lightTaskbar);
        }

        var color = LevelColor(UsageLevels.Classify(percent.Value), lightTaskbar);
        return RenderRing(size, Math.Clamp(percent.Value, 0, 100), color, lightTaskbar);
    }

    private static Color LevelColor(UsageLevel level, bool light) => level switch
    {
        UsageLevel.Critical => light ? LightRed : DarkRed,
        UsageLevel.Warning => light ? LightAmber : DarkAmber,
        _ => light ? LightGreen : DarkGreen,
    };

    private static Bitmap RenderRing(int size, int fillPercent, Color color, bool lightTaskbar)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Stroke ~1/7 of the icon: bold enough to read at 16 px, thin enough to leave
        // a clear hole. Inset by half the stroke so the ring sits fully inside the
        // canvas rather than clipping at the edges.
        var stroke = Math.Max(2f, size / 7f);
        var inset = stroke / 2f + 0.5f;
        var rect = new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset);
        var center = size / 2f;

        // Track: the full circle, faint, so an empty gauge still reads as "a gauge".
        var trackColor = lightTaskbar ? Color.FromArgb(90, 60, 60, 60) : Color.FromArgb(90, 200, 200, 200);
        using (var track = new Pen(trackColor, stroke))
        {
            g.DrawEllipse(track, rect);
        }

        // Fill arc: clockwise from 12 o'clock, proportional. Rounded caps read cleaner
        // than square ones at small sizes.
        if (fillPercent > 0)
        {
            using var arc = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(arc, rect, -90f, 360f * fillPercent / 100f);
        }

        // Pupil: the brand "eye", centred in the ring's hole. Radius is 0.405× the ring
        // radius — the ratio taken from the master mark (pupil 30 / ring 74 at 256 px) so
        // the tray icon and the exe icon are the same shape at every scale.
        var ringRadius = (size - 2f * inset) / 2f;
        var pupilRadius = 0.405f * ringRadius;
        using (var pupil = new SolidBrush(color))
        {
            g.FillEllipse(pupil, center - pupilRadius, center - pupilRadius, pupilRadius * 2f, pupilRadius * 2f);
        }

        return bmp;
    }
}
