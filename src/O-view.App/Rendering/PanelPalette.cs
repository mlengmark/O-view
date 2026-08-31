namespace OView.App.Rendering;

/// <summary>
/// Every colour the detail panel paints, as hex, for both themes.
///
/// <para><b>These are measured, not chosen.</b> The accent pair clears 4.5:1 against white
/// label text while staying distinguishable from both panel surfaces; the categorical
/// series were validated for colour-vision deficiency separation against the exact tile
/// backgrounds they sit on. The reasoning is recorded against each entry because it is the
/// part that would be lost first — a second copy of a hex string carries the value but not
/// the constraint that produced it.</para>
///
/// <para>Shared so the two heads cannot drift. Each platform converts these to its own
/// colour type and applies them its own way; only the values and the reasons live here.
/// Restating them as literals elsewhere is exactly the failure issue #52 was about.</para>
/// </summary>
public static class PanelPalette
{
    /// <summary>The hex for one palette key in one theme. Unknown keys throw — a silently
    /// missing colour renders as transparent, which is far harder to notice than a crash.</summary>
    public static string Get(string key, bool light) =>
        All(light).TryGetValue(key, out var hex)
            ? hex
            : throw new ArgumentOutOfRangeException(nameof(key), key, "Not a panel palette key.");

    /// <summary>The whole palette for one theme. A superset — a surface simply does not
    /// reference the keys it has no use for.</summary>
    public static IReadOnlyDictionary<string, string> All(bool light) => new Dictionary<string, string>
    {
        ["PanelBg"] = light ? "#F9F9F9" : "#202020",
        ["PanelBorder"] = light ? "#D6D6D6" : "#383838",
        ["TextPrimary"] = light ? "#1A1A1A" : "#F0F0F0",
        ["TextSecondary"] = light ? "#555555" : "#B5B5B5",
        ["TextMuted"] = light ? "#8A8A8A" : "#8A8A8A",
        ["TileBg"] = light ? "#EFEFEF" : "#2B2B2B",
        ["BarTrack"] = light ? "#DDDDDD" : "#3A3A3A",
        ["BadgeBg"] = light ? "#E4DCF5" : "#3A3355",
        ["BadgeText"] = light ? "#4A3A85" : "#C7BDEB",
        ["WarnBg"] = light ? "#F7EBD4" : "#453A22",
        ["WarnText"] = light ? "#8A5D00" : "#E3B858",

        // Menu rows: one step off the panel for hover, two for pressed — the same
        // separation Windows 11's own flyouts use for list rows.
        ["RowHover"] = light ? "#ECECEC" : "#2E2E2E",
        ["RowPressed"] = light ? "#E0E0E0" : "#3A3A3A",

        // Clickable stat tiles (issue #37): same one-step/two-step separation off
        // TileBg, so "this responds to a click" reads the same way everywhere.
        ["TileHover"] = light ? "#E5E5E5" : "#343434",
        ["TilePressed"] = light ? "#DADADA" : "#3E3E3E",

        // Brand accent, for the one primary action on a dialog. Taken from the mark's
        // gradient (brand/o-view-mark.svg), but stepped darker than its #D9603A end:
        // that end gives white label text only 3.69:1, short of the 4.5 a 12px label
        // needs. One pair serves both themes, because each step has to clear white text
        // AND stay distinguishable from whichever panel it sits on. Measured:
        //
        //   #BE4E29  white 4.87:1 · light panel 4.63:1 · dark panel 3.34:1
        //   #B84A27  white 5.19:1 · light panel 4.93:1 · dark panel 3.14:1
        //
        // Do not darken the hover further to make it "more obvious": the next step down
        // (#B44726) lands on exactly 3.00:1 against the dark panel, and anything past it
        // fails — the button would stop reading as a button on hover in dark mode.
        ["AccentBg"] = "#BE4E29",
        ["AccentHover"] = "#B84A27",
        ["AccentText"] = "#FFFFFF",

        // Hover cards float ABOVE both the panel and the tiles, so they step away from
        // each rather than matching either — otherwise a card over a tile reads as part
        // of it. Light goes brighter than the panel, dark goes lighter than the tile.
        ["TooltipBg"] = light ? "#FFFFFF" : "#333333",
        ["TooltipBorder"] = light ? "#CFCFCF" : "#4A4A4A",

        // ── categorical series, for the per-model tile charts (issue #37) ─────────
        //
        // Not decoration and not free choice: these are validated, and the dark column
        // is the same three hues re-stepped for the dark surface, not an auto-flip.
        // Checked with the six-check validator against THESE surfaces (TileBg), on the
        // all-pairs list — segment order follows the data, so any two can end up
        // adjacent:
        //
        //   light #EFEFEF : CVD ΔE 9.2 (target 8) · normal-vision ΔE 17.3 (floor 15)
        //   dark  #2B2B2B : CVD ΔE 9.4            · normal-vision ΔE 16.5
        //
        // On the LIGHT surface, Series2 (2.78:1), Series3 (2.45:1) and SeriesOther
        // (3.00:1) sit at or below the 3:1 contrast line. That is a documented relief
        // case, not a dismissable warning: the tile MUST ship visible labels, which is
        // why the breakdown always draws a legend naming each model and its value, and
        // a tooltip carrying every model exactly. Identity never rests on hue alone.
        //
        // Do not add a fourth chromatic slot. The next hue in the validated order is
        // yellow, which fails the all-pairs floors beside Series2's orange — that is
        // exactly why ModelBreakdown folds a fourth model into "Other" instead.
        ["Series1"] = light ? "#2A78D6" : "#3987E5",
        ["Series2"] = light ? "#EB6834" : "#D95926",
        ["Series3"] = light ? "#1BAF7A" : "#199E70",

        // Neutral by design — it is a residual bucket, not an identity, so it is the one
        // slot deliberately below the chroma floor. Its separation IS still verified:
        // worst CVD ΔE 11.0 light / 12.2 dark against the two named slots it appears with.
        ["SeriesOther"] = "#8A8A8A",

        // ── token-kind ramp, for the composition bars (issue #253) ───────────────
        //
        // Deliberately ACHROMATIC, and that is the whole design. The categorical trio
        // above means "which model", panel-wide and by decision — ModelBreakdown
        // computes one slot order so a model wears one colour everywhere. Reusing
        // those hues for token kinds would put blue on "Opus 5" in a flipped tile and
        // on "cache read" in the bar directly beneath it, on screen together.
        //
        // Brightness carries the encoding instead, ordered by how much the reader
        // cares: output is the loudest step, cache read the quietest. That ordering is
        // real information rather than decoration, it needs no hue, and it leaves the
        // three chromatic slots to mean exactly one thing.
        //
        // Steps run away from the surface in each theme — lighter on dark, darker on
        // light — so the ramp reads the same way round in both. Identity never rests
        // on the step alone: every segment is named in the legend and carries a hover
        // card, which is the same relief the series colours run on.
        ["TokenKindOutput"] = light ? "#1F1F1F" : "#F0F0F0",
        ["TokenKindInput"] = light ? "#656565" : "#A0A0A0",
        ["TokenKindCacheWrite"] = light ? "#9A9A9A" : "#6C6C6C",
        ["TokenKindCacheRead"] = light ? "#C4C4C4" : "#4A4A4A",

        // Not a panel surface: a desktop stand-in, used only by the verification renders
        // to composite a transparent-cornered flyout onto something, so its corner radius
        // and border are visible. One step OUTSIDE the palette in each theme — light greys
        // down, dark greys up — because a backdrop matching PanelBg would hide the very
        // edge the render exists to show.
        ["Backdrop"] = light ? "#DEDEDE" : "#101010",
    };
}
