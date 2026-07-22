# Finding: tray icon legibility spike

**Run:** 2026-07-20 · **Machine:** Windows 11 Pro 26200 · .NET SDK 10.0.302
**Resolves:** open question *"Are 2 digits legible at 16×16 px?"* from the [ADR index](../adr/README.md)

![Tray icon legibility test sheet](images/tray-icon-legibility.png)

Each design rendered at 16/20/24 px on both dark and light taskbar colours, shown 1:1 and at 8× nearest-neighbour magnification. Font auto-fitted to the largest size that fits the available box.

## Result: digits are legible — but the ring is what hurts them

**The spike inverted the assumption in [ADR-0003](../adr/0003-windows-tray-constraints.md).** It assumed a ring gauge *plus* digits. Measured, the ring is the problem: it consumes the outer ~25% of an already tiny canvas, forcing the font down and turning the digits to mush at 16 px.

| Design | 16 px | 20 px | 24 px |
|---|---|---|---|
| **Digits only** | **✅ crisp** (13.5 px font) | ✅ excellent (17 px) | ✅ excellent (20.5 px) |
| Ring + digits | ⚠️ mushy, marginal (9.5 px font) | ✅ good (12 px) | ✅ good (14.5 px) |
| Ring only | ✅ clean, but conveys no number | ✅ | ✅ |
| 3 digits (`100`) | ⚠️ cramped but readable (9 px) | ✅ (11.5 px) | ✅ (13.5 px) |
| Full ring + `!` symbol | ✅ unmistakable | ✅ | ✅ |

16 px is the default at 100% display scaling and therefore the case that must work. **Digits-only at 13.5 px is markedly more legible than ring-plus-digits at 9.5 px.**

## Revised icon design

> **Revised again 2026-07-21 (product direction).** The product owner requires the
> icon to carry a **% graph**, not digits alone. The implemented design adds a
> **proportional fill bar along the bottom edge** (~3 px at 16 px) under the
> digits: the bar is the graph, digits keep ~80% of the canvas, and re-rendered
> samples at 16/24 px on both themes confirmed digits remain legible with the bar
> present. This preserves the spike's core result — the *ring* remains rejected
> because it consumed the outer 25% and crushed the font; a 3 px bottom bar does
> not.
>
> **Superseded 2026-07-21 (GitHub issue #1): ring-only.** Digits-plus-bar read as
> cluttered at 16 px in practice. The icon is now a **ring gauge with no digits**.
> The spike's finding still holds and is *why this works*: the spike rejected ring
> **plus** digits, where the two competed for a 16 px canvas. A ring **alone** has
> no digits to crush — it uses the full area for the arc, and re-rendered samples
> confirm it is clean and legible at 16/24 px across all three colour bands (green
> <50, amber 50–69, red ≥70) on both themes. The exact number lives in the tooltip.
> Below this line, "digits-only is the design" is historical.

> **Superseded 2026-07-22 (brand unification): ring + centre pupil.** The static exe
> icon (`<ApplicationIcon>`) is the O-view brand mark — a ring gauge with a filled
> centre pupil (the "eye"). The live tray icon now renders that same mark so the app
> reads as one thing everywhere: `IconRenderer` draws the pupil in the ring's hole at
> **every size, including 16 px**, in the same colour band as the arc. This does **not**
> reopen the spike's core result. The spike rejected ring **plus digits** — two data
> signals competing for a 16 px canvas. The pupil is **not** a second signal: it carries
> no number, sits in the otherwise-empty hole, and takes the arc's colour, so the icon
> stays a single-colour gauge. Pupil radius is 0.405× the ring radius (the mark's
> 30/74-at-256 px ratio). Renders through the real `IconRenderer` at 16/24/32 px on both
> themes confirm the pupil stays a distinct eye with a clear gap to the ring. See
> [IconRenderer.cs](../../src/O-view.Tray/Tray/IconRenderer.cs).

> **Digits only. No ring.** _(Historical — superseded twice, see above: ring-only per issue #1, then ring + pupil per the 2026-07-22 block.)_ The two digits are the primary signal; **the digit colour** carries urgency (green → amber → red).

This is not a downgrade. The ring was only ever a redundant encoding of the number already displayed — dropping it buys ~40% more font size for the signal users actually read.

Colour is not the sole signal: the digits themselves state the value, satisfying the accessibility requirement in ADR-0003.

### The 100% case

Three digits at 16 px are cramped. At 100% use the **full-ring `!` symbol**, which the spike shows is unmistakable at every size — a distinct visual state for "limit reached" is more useful than squeezing in a third digit.

### Confirmed parameters

- Font **Segoe UI Bold**, auto-fitted per icon size rather than hard-coded — DPI scaling changes the canvas, so a fixed size will clip. The 8×-magnified `47` in the first sheet clipped to `4` at a hard-coded scale; this is exactly the bug auto-fitting prevents.
- `SmoothingMode.AntiAlias` + `TextRenderingHint.AntiAliasGridFit`
- Measure with `StringFormat.GenericTypographic` + `NoWrap`; default `StringFormat` wrapped `100` onto two lines
- Render at the real pixel size — never render large and downscale

## Related platform facts confirmed

- `NotifyIcon.Text` (tooltip) caps at **127 characters**, not 128. Planned tooltip is 32.
- `Bitmap.GetHicon()` → `Icon.FromHandle()` works, but **`DestroyIcon` must be called** on every refresh or the process leaks a GDI handle per update. See [ADR-0005](../adr/0005-native-tray-integration.md).

## Still open

- Not yet tested on a real taskbar at 125%/150%/175% scaling — the spike rendered canonical 16/20/24 px sizes. Verify on-device during the tray shell phase.
- High-contrast themes not covered.
