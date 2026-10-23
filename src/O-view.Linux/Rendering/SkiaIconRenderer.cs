using OView.App.Rendering;
using OView.Core.Models;
using SkiaSharp;

namespace OView.Linux.Rendering;

/// <summary>
/// What a render actually produced, beside the percentage it was asked for: the pixmap's
/// own size, and how many of its pixels carry any paint. Two renders can share a snapshot
/// and still disagree here, that disagreement is the point of logging this.
/// </summary>
public readonly record struct IconRenderDiagnostics(int Width, int Height, int PaintedPixelCount);

/// <summary>
/// The Linux drawing backend for the tray icon: Skia over the shared measurements in
/// <see cref="TrayIconGeometry"/>.
///
/// <para>The mark itself, ring and pupil ratios, stroke width, arc start angle, colour
/// bands, and the rule that an estimate renders no fill, is defined once in
/// <see cref="TrayIconGeometry"/> and shared with the Windows head, so the two cannot
/// drift. Only the drawing differs, which is a view and may reasonably differ per platform
/// (ADR-0013).</para>
///
/// <para>Skia's <c>DrawArc</c> takes the same degrees-clockwise-from-three-o'clock
/// convention as GDI+'s, so the geometry's angles carry across unchanged.</para>
/// </summary>
public static class SkiaIconRenderer
{
    /// <summary>
    /// Sizes an SNI host may ask for. Linux panels commonly request 22 or 24 px, and 48 px
    /// on HiDPI, larger than the 16/20/24 Windows uses, which is why legibility has to be
    /// checked at these sizes rather than assumed from the Windows evidence.
    /// </summary>
    public static readonly int[] CommonSizes = [16, 22, 24, 32, 48];

    /// <summary>
    /// Renders the snapshot as a PNG. Unknown or estimated data (no authoritative percent)
    /// renders an empty ring, never a fabricated fill (CLAUDE.md rule 6).
    /// </summary>
    public static byte[] RenderPng(int size, UsageSnapshot snapshot, bool lightPanel) =>
        RenderPng(size, snapshot, lightPanel, out _);

    /// <summary>
    /// As <see cref="RenderPng(int, UsageSnapshot, bool)"/>, and also reports what was
    /// actually handed to the pixmap, so a caller that logs the render can tell a ring that
    /// drew from one that silently didn't, which the PNG bytes alone don't say.
    /// </summary>
    public static byte[] RenderPng(int size, UsageSnapshot snapshot, bool lightPanel, out IconRenderDiagnostics diagnostics)
    {
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        Draw(surface.Canvas, size, snapshot, lightPanel);

        diagnostics = new IconRenderDiagnostics(size, size, CountPaintedPixels(surface, size));

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// How many pixels of the <paramref name="size"/>×<paramref name="size"/> pixmap carry
    /// any alpha. Cheap on purpose: a 24 px icon, the only size the tray actually uses, is
    /// 576 reads off the surface already in memory, measured at ~6 microseconds against a
    /// render that happens once a minute, nothing a VM run could notice.
    /// </summary>
    public static int CountPaintedPixels(SKSurface surface, int size)
    {
        using var pixmap = surface.PeekPixels();
        var painted = 0;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (pixmap.GetPixelColor(x, y).Alpha > 0)
                {
                    painted++;
                }
            }
        }

        return painted;
    }

    private static void Draw(SKCanvas canvas, int size, UsageSnapshot snapshot, bool lightPanel)
    {
        canvas.Clear(SKColors.Transparent);

        var stroke = TrayIconGeometry.Stroke(size);
        var inset = TrayIconGeometry.Inset(size);
        var diameter = TrayIconGeometry.RingDiameter(size);
        var rect = new SKRect(inset, inset, inset + diameter, inset + diameter);
        var centre = TrayIconGeometry.Center(size);
        var colour = ToSkia(TrayIconGeometry.Foreground(snapshot, lightPanel));

        // Track: the full circle, faint, so an empty gauge still reads as "a gauge".
        using (var track = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = stroke,
            Color = ToSkia(TrayIconGeometry.Track(lightPanel)),
        })
        {
            canvas.DrawOval(rect, track);
        }

        // Fill arc: clockwise from 12 o'clock, proportional. Rounded caps read cleaner
        // than square ones at small sizes.
        if (TrayIconGeometry.FillPercent(snapshot) is { } percent && percent > 0)
        {
            using var arc = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = stroke,
                StrokeCap = SKStrokeCap.Round,
                Color = colour,
            };
            canvas.DrawArc(
                rect,
                TrayIconGeometry.StartAngleDegrees,
                TrayIconGeometry.SweepDegrees(percent),
                useCenter: false,
                arc);
        }

        // Pupil: the brand "eye", centred in the ring's hole. Takes the arc's colour, so the
        // icon stays a single-colour signal rather than gaining a second one, except at a
        // measured 0%, where there is no arc and it fades to the track instead (issue #139).
        using var pupil = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = ToSkia(TrayIconGeometry.Pupil(snapshot, lightPanel)),
        };
        canvas.DrawCircle(centre, centre, TrayIconGeometry.PupilRadius(size), pupil);
    }

    private static SKColor ToSkia(IconColor c) => new(c.R, c.G, c.B, c.A);
}
