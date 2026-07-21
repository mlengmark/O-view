using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using OView.Core.Models;

namespace OView.Tray.Tray;

/// <summary>
/// Rasterises the tray icon as a circular gauge — a proportional arc, no digits
/// (GitHub issue #1). The earlier digits-plus-bar design cluttered the ~16 px canvas;
/// a ring-only gauge uses the whole area for one signal. This does NOT contradict the
/// original spike (docs/findings/tray-icon-rendering.md), which rejected ring *plus*
/// digits because they starved each other of space — removing the digits removes that
/// conflict. The exact percentage lives in the tooltip.
///
/// Colour bands come from OView.Core.UsageLevels (issue #2): green &lt;50, amber 50–69,
/// red ≥70 — shared with the popup so they cannot drift apart.
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

        return bmp;
    }
}
