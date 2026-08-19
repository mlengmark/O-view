using OView.Core.Models;

namespace OView.App.Rendering;

/// <summary>An RGBA colour, so the shared palette owes nothing to a drawing library.</summary>
public readonly record struct IconColor(byte R, byte G, byte B, byte A = 255);

/// <summary>
/// Every measurement and colour the tray icon is made of — the O-view brand mark, a
/// proportional ring gauge with a filled centre pupil (the "eye"), and **no digits**
/// (GitHub issue #1).
///
/// <para>This holds the <b>facts</b>; each platform holds its own ten lines of drawing.
/// That split is deliberate: a rasteriser is a view and may reasonably differ per
/// platform, but the stroke ratio, the pupil ratio, the arc's start angle and the colour
/// bands are measured decisions, and two copies of a measured decision is how they drift
/// (issues #55, #56). Windows draws these with System.Drawing; the Linux head draws them
/// with Skia (ADR-0013).</para>
///
/// <para>The pupil is drawn at every size, including 16 px. This revisits issue #1's
/// ring-only note deliberately: the pupil unifies the live tray icon with the static exe
/// icon so the app reads as one thing everywhere. It does NOT reopen the original spike
/// (docs/findings/tray-icon-rendering.md), which rejected ring *plus digits* — two
/// competing signals starving each other. A pupil is not a second signal: it sits in the
/// ring's empty hole and carries no data. The exact percentage lives in the tooltip.</para>
///
/// <para><b>Colour is never the sole signal.</b> The arc's sweep encodes the percentage
/// independently of its colour, for colour-blind users, for monochrome and high-contrast
/// taskbars, and — on Linux — for SNI hosts that recolour icons themselves.</para>
/// </summary>
public static class TrayIconGeometry
{
    // Bands come from OView.Core.UsageLevels (issue #2): green <50, amber 50-69, red >=70.
    private static readonly IconColor DarkGreen = new(64, 200, 110);
    private static readonly IconColor DarkAmber = new(240, 170, 40);
    private static readonly IconColor DarkRed = new(232, 72, 72);
    private static readonly IconColor DarkNeutral = new(150, 150, 150);

    // Darker shades for a light taskbar, where the bright set washes out.
    private static readonly IconColor LightGreen = new(24, 122, 62);
    private static readonly IconColor LightAmber = new(179, 116, 0);
    private static readonly IconColor LightRed = new(197, 30, 30);
    private static readonly IconColor LightNeutral = new(110, 110, 110);

    /// <summary>
    /// Stroke width: ~1/7 of the icon, floored at 2 px. Bold enough to read at 16 px, thin
    /// enough to leave a clear hole for the pupil. <b>Scaled, never hard-coded</b> — a fixed
    /// width clips at some DPI scales.
    /// </summary>
    public static float Stroke(int size) => Math.Max(2f, size / 7f);

    /// <summary>
    /// Distance from the canvas edge to the ring's centreline: half the stroke, plus half a
    /// pixel so the ring sits fully inside the canvas rather than clipping at the edges.
    /// </summary>
    public static float Inset(int size) => Stroke(size) / 2f + 0.5f;

    /// <summary>Diameter of the ring's centreline circle.</summary>
    public static float RingDiameter(int size) => size - (2f * Inset(size));

    public static float RingRadius(int size) => RingDiameter(size) / 2f;

    public static float Center(int size) => size / 2f;

    /// <summary>
    /// Pupil radius: 0.405x the ring radius. Taken from the master mark (pupil 30 / ring 74
    /// at 256 px) so the tray icon and the exe icon are the same shape at every scale.
    /// </summary>
    public static float PupilRadius(int size) => 0.405f * RingRadius(size);

    /// <summary>Twelve o'clock, in the degrees-clockwise-from-3-o'clock convention both backends use.</summary>
    public const float StartAngleDegrees = -90f;

    /// <summary>How far the fill arc sweeps, clockwise from twelve o'clock.</summary>
    public static float SweepDegrees(int fillPercent) => 360f * fillPercent / 100f;

    /// <summary>
    /// The faint full circle behind the arc, so an empty gauge still reads as *a gauge*
    /// rather than as a missing icon.
    /// </summary>
    public static IconColor Track(bool lightTaskbar) =>
        lightTaskbar ? new IconColor(60, 60, 60, 90) : new IconColor(200, 200, 200, 90);

    /// <summary>
    /// The percentage to draw, or null when there is nothing authoritative to draw.
    ///
    /// <para>Only <see cref="DataSource.Live"/> and <see cref="DataSource.Stale"/> count.
    /// A JSONL-derived estimate is <b>not</b> rendered as a fill: the icon has no room to
    /// label itself "estimate", and an unlabelled fill would be a fabricated number
    /// (CLAUDE.md rule 6). It gets the neutral empty ring instead.</para>
    /// </summary>
    public static int? FillPercent(UsageSnapshot snapshot) =>
        snapshot.Source is DataSource.Live or DataSource.Stale && snapshot.SessionPercent is { } p
            ? Math.Clamp(p, 0, 100)
            : null;

    /// <summary>The arc colour for a snapshot — neutral when there is no authoritative percentage.</summary>
    public static IconColor Foreground(UsageSnapshot snapshot, bool lightTaskbar) =>
        FillPercent(snapshot) is { } percent
            ? LevelColor(UsageLevels.Classify(percent), lightTaskbar)
            : (lightTaskbar ? LightNeutral : DarkNeutral);

    /// <summary>
    /// The pupil's colour: the arc's, except at a measured zero, where it takes the
    /// <see cref="Track"/>'s so an empty gauge is uniformly empty (GitHub issue #139).
    ///
    /// <para>At 0% there is no arc, and a full-saturation green pupil inside a faint grey ring
    /// made the icon read as two states at once — an empty gauge wrapped around a live dot.
    /// <c>UsageLevels.Classify(0)</c> is <c>Normal</c>, so the pupil was simply taking the
    /// green it would have at 1%.</para>
    ///
    /// <para><b>Only an authoritative zero fades.</b> <see cref="FillPercent"/> returns null
    /// rather than 0 for unknown and estimated data, so those keep the opaque neutral pupil —
    /// which is the whole of what still distinguishes "O-view has no number" from "the number
    /// is zero" once both rings are drawn faint. Collapsing the two would trade one wrong icon
    /// for another.</para>
    ///
    /// <para>Here rather than in either renderer because it is a decision about what the mark
    /// <i>is</i>, and two copies of one of those is how the heads drift (issues #55, #56).</para>
    /// </summary>
    public static IconColor Pupil(UsageSnapshot snapshot, bool lightTaskbar) =>
        FillPercent(snapshot) is 0
            ? Track(lightTaskbar)
            : Foreground(snapshot, lightTaskbar);

    public static IconColor LevelColor(UsageLevel level, bool lightTaskbar) => level switch
    {
        UsageLevel.Critical => lightTaskbar ? LightRed : DarkRed,
        UsageLevel.Warning => lightTaskbar ? LightAmber : DarkAmber,
        _ => lightTaskbar ? LightGreen : DarkGreen,
    };
}
