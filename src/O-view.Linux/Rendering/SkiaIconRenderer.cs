using OView.App.Rendering;
using OView.Core.Models;
using SkiaSharp;

namespace OView.Linux.Rendering;

/// <summary>
/// The Linux drawing backend for the tray icon: Skia over the shared measurements in
/// <see cref="TrayIconGeometry"/>.
///
/// <para>The mark itself — ring and pupil ratios, stroke width, arc start angle, colour
/// bands, and the rule that an estimate renders no fill — is defined once in
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
    /// on HiDPI — larger than the 16/20/24 Windows uses, which is why legibility has to be
    /// checked at these sizes rather than assumed from the Windows evidence.
    /// </summary>
    public static readonly int[] CommonSizes = [16, 22, 24, 32, 48];

    /// <summary>
    /// Renders the snapshot as a PNG. Unknown or estimated data (no authoritative percent)
    /// renders an empty ring — never a fabricated fill (CLAUDE.md rule 6).
    /// </summary>
    public static byte[] RenderPng(int size, UsageSnapshot snapshot, bool lightPanel)
    {
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        Draw(surface.Canvas, size, snapshot, lightPanel);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
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

        // Pupil: the brand "eye", centred in the ring's hole. Takes the arc's colour, so
        // the icon stays a single-colour signal rather than gaining a second one.
        using var pupil = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = colour };
        canvas.DrawCircle(centre, centre, TrayIconGeometry.PupilRadius(size), pupil);
    }

    private static SKColor ToSkia(IconColor c) => new(c.R, c.G, c.B, c.A);
}
