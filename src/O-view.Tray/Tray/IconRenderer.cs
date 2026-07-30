using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using OView.App.Rendering;
using OView.Core.Models;

namespace OView.Tray.Tray;

/// <summary>
/// The Windows drawing backend for the tray icon: System.Drawing over the shared
/// measurements in <see cref="TrayIconGeometry"/>.
///
/// <para>What the mark <i>is</i> — the ring/pupil ratios, the stroke width, the arc's start
/// angle, the colour bands, and the rule that an estimate renders no fill — lives in
/// <see cref="TrayIconGeometry"/> so it cannot drift from the Linux head's copy. What lives
/// here is only <i>how Windows draws it</i>, which is a view and may reasonably differ
/// (ADR-0013).</para>
///
/// <para>System.Drawing has been Windows-only since .NET 6, so this file can never move to
/// a shared project — and does not need to.</para>
/// </summary>
public static class IconRenderer
{
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
    public static Bitmap Render(int size, UsageSnapshot snapshot, bool lightTaskbar) =>
        RenderRing(
            size,
            TrayIconGeometry.FillPercent(snapshot) ?? 0,
            ToGdi(TrayIconGeometry.Foreground(snapshot, lightTaskbar)),
            lightTaskbar);

    private static Color ToGdi(IconColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    private static Bitmap RenderRing(int size, int fillPercent, Color color, bool lightTaskbar)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var stroke = TrayIconGeometry.Stroke(size);
        var inset = TrayIconGeometry.Inset(size);
        var rect = new RectangleF(inset, inset, TrayIconGeometry.RingDiameter(size), TrayIconGeometry.RingDiameter(size));
        var center = TrayIconGeometry.Center(size);

        // Track: the full circle, faint, so an empty gauge still reads as "a gauge".
        using (var track = new Pen(ToGdi(TrayIconGeometry.Track(lightTaskbar)), stroke))
        {
            g.DrawEllipse(track, rect);
        }

        // Fill arc: clockwise from 12 o'clock, proportional. Rounded caps read cleaner
        // than square ones at small sizes.
        if (fillPercent > 0)
        {
            using var arc = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(arc, rect, TrayIconGeometry.StartAngleDegrees, TrayIconGeometry.SweepDegrees(fillPercent));
        }

        // Pupil: the brand "eye", centred in the ring's hole.
        var pupilRadius = TrayIconGeometry.PupilRadius(size);
        using (var pupil = new SolidBrush(color))
        {
            g.FillEllipse(pupil, center - pupilRadius, center - pupilRadius, pupilRadius * 2f, pupilRadius * 2f);
        }

        return bmp;
    }
}
