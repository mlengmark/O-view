## Merge order
- Position: <n> of <total> in Phase <key>
- Depends on: PR #<n> "<title>" — merged ✓ | none
- Stacked on: none | PR #<n> (this PR targets its branch; never edits its files)
- In flight alongside: PR #<n> "<title>" (no shared files) | none
- Touches: <the slice's Touches line, exactly, entries separated by commas>

## Summary
Two or three plain-English sentences: what changes for the app or the codebase, and why. No class names unless they are the point.

## Changes
- **<Change, in a few words>** — <what it does / what is different afterwards>
- **<Change>** — <what it does>

## Screenshots
| State | Before (main) | After (this PR) |
|---|---|---|
| Panel · typical · dark | ![](https://raw.githubusercontent.com/mlengmark/O-view/<main-sha>/tests/golden/<file>) | ![](https://raw.githubusercontent.com/mlengmark/O-view/<head-sha>/tests/golden/<file>) |
| Panel · typical · light | ![](…) | ![](…) |

Or: No visual change — all <n> golden states unchanged.

## Checks
- tools/check.ps1: <n> tests passed · golden: unchanged | changed: <states>
- Feature checklist: <F-ids touched> — pass
- Guarded changes: none | <change> — listed in the Phase <key> brief ✓

## Not verified
- <what nobody has run, e.g. "not run on a real Linux desktop">

## Noticed (out of scope)
- <problems spotted but not touched> | None

<details><summary>Technical notes</summary>Optional detail for reviewers.</details>

Slice <issue-key> · type <type> · Phase <key>
