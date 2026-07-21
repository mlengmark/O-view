using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using OView.Core.Models;

namespace OView.Tray.Tray;

/// <summary>
/// Rasterises the tray icon. Design is measured, not assumed
/// (docs/findings/tray-icon-rendering.md): digits only, NO ring gauge — the ring
/// starves digits of space at 16 px (13.5 px font vs 9.5 px). Digit colour carries
/// urgency; the digits themselves carry the value, so colour is never the sole
/// signal. At 100% a full-ring "!" replaces three digits, which don't fit legibly.
/// Font auto-fits per icon size — a hard-coded size clips at some DPI scales.
/// </summary>
public static class IconRenderer
{
    // Thresholds per ui-spec.md: green < 60, amber 60–84, red ≥ 85.
    private static readonly Color DarkGreen = Color.FromArgb(64, 200, 110);
    private static readonly Color DarkAmber = Color.FromArgb(240, 170, 40);
    private static readonly Color DarkRed = Color.FromArgb(232, 72, 72);
    private static readonly Color DarkNeutral = Color.FromArgb(160, 160, 160);

    // Darker shades for a light taskbar, where the bright set washes out.
    private static readonly Color LightGreen = Color.FromArgb(24, 122, 62);
    private static readonly Color LightAmber = Color.FromArgb(179, 116, 0);
    private static readonly Color LightRed = Color.FromArgb(197, 30, 30);
    private static readonly Color LightNeutral = Color.FromArgb(96, 96, 96);

    /// <summary>Small-icon size for the current DPI (16 px at 100%, 24 px at 150%).</summary>
    public static int CurrentIconSize()
    {
        var size = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON);
        return size > 0 ? size : 16;
    }

    /// <summary>
    /// Render the snapshot into a size×size bitmap. Caller owns the bitmap. No data
    /// or estimated data (percent unknown) renders the neutral state — never a
    /// fabricated number (CLAUDE.md rule 6).
    /// </summary>
    public static Bitmap Render(int size, UsageSnapshot snapshot, bool lightTaskbar)
    {
        var percent = snapshot.Source is DataSource.Live or DataSource.Stale
            ? snapshot.SessionPercent
            : null;

        if (percent is null)
        {
            return RenderDigitsWithBar(size, "–", lightTaskbar ? LightNeutral : DarkNeutral, fillPercent: 0, lightTaskbar);
        }

        if (percent >= 100)
        {
            return RenderFullRingAlert(size, lightTaskbar ? LightRed : DarkRed);
        }

        var color = percent switch
        {
            >= 85 => lightTaskbar ? LightRed : DarkRed,
            >= 60 => lightTaskbar ? LightAmber : DarkAmber,
            _ => lightTaskbar ? LightGreen : DarkGreen,
        };
        return RenderDigitsWithBar(size,
            percent.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            color, fillPercent: percent.Value, lightTaskbar);
    }

    /// <summary>
    /// Digits with a proportional fill bar along the bottom edge — the % graph.
    /// The bar takes ~3px at 16px, so digits keep most of the canvas; the ring
    /// design the spike rejected consumed the outer 25% and crushed the font.
    /// </summary>
    private static Bitmap RenderDigitsWithBar(int size, string text, Color color, int fillPercent, bool lightTaskbar)
    {
        var bmp = NewCanvas(size, out var g);
        using (g)
        {
            var barHeight = Math.Max(2, (int)Math.Round(size * 3.0 / 16.0));
            var barTop = size - barHeight;

            // Track: full width, subtle; fill: proportional, threshold colour.
            using (var track = new SolidBrush(lightTaskbar ? Color.FromArgb(205, 205, 205) : Color.FromArgb(72, 72, 72)))
            {
                FillRounded(g, track, 0, barTop, size, barHeight);
            }
            var fillWidth = (int)Math.Round(size * Math.Clamp(fillPercent, 0, 100) / 100.0);
            if (fillWidth > 0)
            {
                using var fill = new SolidBrush(color);
                FillRounded(g, fill, 0, barTop, Math.Max(fillWidth, barHeight), barHeight);
            }

            // Digits auto-fit and centre in what remains above the bar.
            DrawAutoFitText(g, size, text, color,
                availableFraction: (barTop - 1f) / size,
                layout: new RectangleF(0, 0, size, barTop));
        }
        return bmp;
    }

    private static void FillRounded(Graphics g, Brush brush, float x, float y, float width, float height)
    {
        var radius = height / 2f;
        using var path = new GraphicsPath();
        path.AddRoundedRectangle(new RectangleF(x, y, width, height), new SizeF(radius, radius));
        g.FillPath(brush, path);
    }

    /// <summary>The 100% state: full ring + "!" — unmistakable at every size (spike-verified).</summary>
    private static Bitmap RenderFullRingAlert(int size, Color color)
    {
        var bmp = NewCanvas(size, out var g);
        using (g)
        {
            var penWidth = Math.Max(1.6f, size / 8f);
            var rect = new RectangleF(penWidth / 2f, penWidth / 2f, size - penWidth, size - penWidth);
            using (var pen = new Pen(color, penWidth))
            {
                g.DrawEllipse(pen, rect);
            }
            DrawAutoFitText(g, size, "!", color, availableFraction: (size - 2 * penWidth - 1) / size);
        }
        return bmp;
    }

    private static Bitmap NewCanvas(int size, out Graphics g)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        return bmp;
    }

    private static void DrawAutoFitText(Graphics g, int size, string text, Color color, float availableFraction, RectangleF? layout = null)
    {
        var avail = size * availableFraction;
        var drawRect = layout ?? new RectangleF(0, 0, size, size);

        // GenericTypographic + NoWrap: default StringFormat wraps and pads —
        // the spike's hard-coded scale clipped "47" to "4" before auto-fit.
        var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
        format.Alignment = StringAlignment.Center;
        format.LineAlignment = StringAlignment.Center;

        var best = 4f;
        for (var fontSize = (float)size; fontSize >= 4f; fontSize -= 0.5f)
        {
            using var probe = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            var measured = g.MeasureString(text, probe, PointF.Empty, format);
            if (measured.Width <= avail && measured.Height <= avail * 1.18f)
            {
                best = fontSize;
                break;
            }
        }

        using var font = new Font("Segoe UI", best, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, drawRect, format);
    }
}
