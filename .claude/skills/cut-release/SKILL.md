---
name: cut-release
description: Cut and publish an O-view release (mlengmark/O-view). Use this whenever the user wants to release, tag, ship, publish or "cut" a version, bump the version, write or check release notes, verify a release's assets or attestation, or asks whether main is ready to release. Use it even when they only say "let's do 1.2.0" or "ship it", and before pushing any v* tag, because a tag ruleset blocks tags and the release workflow has preconditions worth checking first.
---

# Cut an O-view release

A release is a `v*` tag on `main`. Pushing the tag runs `.github/workflows/release.yml`, which
builds every asset for both platforms, prepends `.github/release-notes-header.md` to the notes,
publishes the GitHub release with a `SHA256SUMS` file and a Sigstore attestation per asset.
The two things that go wrong are a tag on a red or stale `main`, and the tag being refused by
the ruleset that protects `v*` tags. The steps below are in the order that avoids both.

## 1. Pick the version

Tags are `vMAJOR.LARGE.MINOR` as the README defines them: a different product generation, a
substantial change, a fix. Compared number by number, no leading zeros, no cap, not semantic
versioning. Releases must be published in ascending order because the updater reads the most
recently published release. Confirm the latest tag with `git tag --sort=-v:refname | head -3`
and `gh release list -R mlengmark/O-view --limit 3`.

## 2. Check main is releasable

- `gh run list -R mlengmark/O-view --branch main --limit 5`: the latest CI run is green.
- `gh pr list -R mlengmark/O-view --state open`: nothing the user expects in this release is
  still open. Ask if any PR looks like it belongs.
- `git fetch origin && git status`: you are on `main`, clean, and at `origin/main`.
- Optional dry run when the pipeline itself changed since the last release: trigger
  `release.yml` with `workflow_dispatch` and a version input. It builds and verifies every
  artifact without creating a release.

## 3. Release notes and the README line

Both land through a normal PR, because `main` is protected. Follow the `resolve-issue` skill's
PR rules (template sections, Merge order, Touches). The previous one was
"Cut v1.1.0: release notes and the README release line (OVIAA-203)".

- `docs/release-notes/v<version>.md`, modelled on the previous file: a heading of the form
  "What's new in vX, for someone upgrading from vY", then one section per change a user can
  see, in plain English, with a "Smaller things" list at the end. Build it from
  `git log v<previous>..origin/main --merges --format=%s`, but write what the user notices,
  not the commit list. Refactors that change nothing visible get one sentence, as v1.1.0 did.
- The evergreen material (which asset to download, GNOME, how updates arrive, the off-plan
  banner) lives in `.github/release-notes-header.md` and is prepended automatically. Do not
  repeat it in the version file; edit the header only when a standing fact has changed.
- README line 5, "Current release: [vX](...)", to the new version.
- No em dashes. Nothing in CI checks this, so grep the two files before opening the PR.

Merge the PR, then `git pull` so the tag lands on the merge commit.

## 4. Tag

Ruleset 24988076, "Protect release tags (v*)", blocks creating, moving or deleting `v*` tags.
Switch it off for the push and back on straight after. Both calls need the user's `gh` login.

```bash
gh api -X PUT repos/mlengmark/O-view/rulesets/24988076 -f enforcement=disabled
git tag v<version> && git push origin v<version>
gh api -X PUT repos/mlengmark/O-view/rulesets/24988076 -f enforcement=active
gh api repos/mlengmark/O-view/rulesets/24988076 --jq .enforcement
```

The last line must print `active`. Re-enabling is not optional: the ruleset is what stops a
mistaken `git push --tags` from moving a published version.

## 5. Watch and verify

```bash
gh run list -R mlengmark/O-view --workflow release.yml --limit 1
gh run watch <id> -R mlengmark/O-view
gh release view v<version> -R mlengmark/O-view --json assets --jq '.assets[].name'
```

Expect seven assets: `O-view-Setup.exe`, `O-view.Tray.exe`, two `.deb` files, two tarballs,
`SHA256SUMS`. Then prove provenance on one of them, which is what the README tells users to do:

```bash
gh release download v<version> -R mlengmark/O-view -p O-view-Setup.exe -D <temp dir>
gh attestation verify <temp dir>/O-view-Setup.exe --repo mlengmark/O-view
```

The release workflow bakes the version into the binaries from the tag, so also confirm the
Windows updater sees the new release: `O-view.Tray.exe --check-updates` from an installed copy,
or watch for the balloon. Report the release URL, the asset count, and anything you could
not verify (a Linux install, for example).

## If the workflow fails

Do not delete or move the tag to retry: the ruleset exists so tags are permanent. Fix `main`,
cut the next MINOR, and say in its notes that the previous tag has no release.
