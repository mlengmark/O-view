# ADR-0017: Remember a dragged panel's position, behind one shared rule

- **Status:** Accepted
- **Date:** 2026-10-16
- **Deciders:** @mlengmark
- **Amends:** [#144](https://github.com/mlengmark/O-view/issues/144)'s corner-only placement, which this extends rather than replaces. `WorkAreaPlacement.Place` is unchanged and stays the fallback.
- **Pattern copied from:** [ADR-0014](0014-weekly-reset-is-a-reported-constant.md)'s anchor file, same shape, same reasons.

## Context

#144 gave both heads a shared rule for *auto*-placing the panel at a work-area corner next to
the taskbar. It never lets the user put the panel anywhere else. The next step both heads need
is letting a drag stick, and a Windows user and a Linux user would expect a dragged panel to
come back exactly where they left it, the same expectation that made #144's placement shared
rather than per-head arithmetic in the first place.

Two things make this harder than it looks:

- **The screen it was dropped on might not be there next time.** A laptop's external monitor
  comes and goes, a resolution changes, a taskbar moves to a different edge. Storing an
  absolute pixel position would be correct for exactly one instant and wrong forever after.
- **Two heads, two storage mechanisms, is how corner placement would have drifted** if it had
  stayed Win32-only arithmetic. A second shared file, decided once, is cheaper than reconciling
  two independent guesses later.

## Decision

**Store a corner and an offset, not a point, and keep the rule that resolves it separate
from the rule that records it.**

1. **What is kept, per display:** the display's own id, the work-area corner the panel was
   released nearest to, and the offset inward from that corner, in device-independent units
   (never pixels, a DPI change between the save and the read must not move the panel).
   `PanelPlacementMemory` (`O-view.App.Rendering`) owns both halves of the rule:
   - `Resolve` answers where the panel opens: it re-clamps the remembered place into the work
     area *as it stands now*, so a shrunk work area or a moved taskbar still shows the whole
     panel. It returns null, "use today's corner placement", when nothing is stored, the
     display was never saved to, or the panel no longer fits the work area at all.
   - `Remember` answers what to store when the user lets go: the nearest corner, with the
     offset snapped flush when it is within 16 px of an edge or a corner.
2. **Where it is kept:** `PanelPositionFile` (`O-view.Core.Storage`), written atomically and
   read defensively, the same shape as `WeeklyResetAnchor` (ADR-0014), for the same reason:
   a value that can only ever be read while the file happens to be valid is a value that is
   usually absent. Keyed per display, so an attached-then-detached monitor does not clobber
   another display's remembered spot.
3. **The seam between them is `IPanelPositionStore`** (`O-view.App.Platform`), not a direct
   dependency on `PanelPositionFile`. Core may not name an App type, so the file stores its
   corner as a bare string for whichever implementation translates it to and from the shared
   `WorkAreaCorner` enum. The same seam lets `PanelPlacementMemory`'s edge cases, an unplugged
   display, nothing stored, be tested against an in-memory fake instead of real file I/O.
   No head constructs an implementation yet; one arrives with whichever head wires dragging up
   first (see *Consequences*).

**Clamping and snapping never see the same value**, which is what keeps the two rules from
being able to disagree: clamping only runs on a value being read back, snapping only on a
value about to be saved. There is no case where "the snap says one corner and the clamp
another" because nothing both snaps and clamps in the same call.

## Alternatives considered

- **Store the absolute window position.** Rejected: correct for one resolution and one set of
  attached displays, wrong the moment either changes, exactly the failure #144 exists to
  avoid for the auto-placed case.
- **One shared position for every display.** Rejected: a laptop and its external monitor are
  different work areas with different proportions; the same offset would be in a different
  place on each, which is not what "remembered" should mean.

## Consequences

- Both heads can wire dragging to the same two calls once they do; neither drag handling, the
  file-backed `IPanelPositionStore`, nor the menu row that resets a remembered place is built
  in this change, the rule and the file it will read and write are ready for them.
- `MenuText.ResetPanelPositionRow` exists so that row's words are shared from the moment either
  head adds it, rather than being copied into a second place first and extracted later.
- A panel on a display too small to hold it at all still falls back to `WorkAreaPlacement`'s
  own answer for that case (pin to the top-left) rather than this ADR inventing a second one.
