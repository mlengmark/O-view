# Finding: how Claude Desktop is actually packaged on Linux

**Date:** 2026-08-19 · **Closes out:** [#70](https://github.com/mlengmark/O-view/issues/70) · **Bears on:** [#107](https://github.com/mlengmark/O-view/issues/107), [#108](https://github.com/mlengmark/O-view/issues/108)

O-view's `PlanHistoryLocator` searches Snap and Flatpak sandbox stores as well as the canonical
`~/.config/Claude`, because a sandboxed Claude Desktop would not write to the canonical path and a
canonical-only locator would report "no usage data" on a machine where Claude Desktop was open and
working. That code shipped in #98 resting on **documented convention** — nobody had looked at a
machine with either layout on it.

#70 held that gap open, waiting for someone with a Snap- or Flatpak-packaged Claude Desktop to
confirm the paths by observation. This finding records why that report is never going to arrive.

## Headline

> **No Snap or Flatpak packaging of Claude Desktop exists to test against.** Anthropic ships the
> Linux app as a `.deb` through its own apt repository and nothing else. Neither store carries a
> first-party build, and the two Snap Store hits are unrelated third-party apps — the current one
> is a browser wrapper, which writes no `plan-usage-history.json` at all.

So the redirect paths cannot be confirmed by observation, and not because nobody has got round to
it. There is no machine that could produce the observation.

## Method

Three sources, all checked on 2026-08-19.

### 1. Anthropic's own documentation

[`code.claude.com/docs/en/desktop-linux`](https://code.claude.com/docs/en/desktop-linux) — the
first-party install page, and the only distribution channel it describes:

| | |
|---|---|
| **Supported** | Ubuntu 22.04+, Debian 12+, on `x86_64` or `arm64` |
| **Channel** | Anthropic's apt repository, `https://downloads.claude.ai/claude-desktop/apt/stable` |
| **Alternative** | the same `.deb`, downloaded by hand from the repository's package pool |
| **Snap** | not mentioned |
| **Flatpak** | not mentioned |
| **AppImage** | not mentioned |

The page states that other Debian-based distributions "may work but aren't officially tested", and
under *What's not in the Linux beta yet* names **Fedora and RHEL** explicitly: "only Debian-based
distributions are supported today."

A `.deb` installed by `apt` is not sandboxed. Its config goes to `~/.config/Claude`, which is the
canonical path — the row already confirmed on real hardware by the diagnostics attached to #124,
where all 5,645 plan-history samples parsed.

### 2. The Snap Store

Queried `api.snapcraft.io/v2/snaps/find?q=claude`. Two results resemble the desktop app, and
neither is it:

| Snap | Publisher | What it is |
|---|---|---|
| `claude-ai-desktop` | `simonlinuxcraft`, validation **unproven** | Titled "Desktop for Claude (Unofficial)". Its own summary calls it an "Unofficial Claude AI **Browser Wrapper**". v1.4.16, stable, `strict` confinement. |
| `claudeai-desktop` | `prevailexcel`, validation **unproven** | Titled "Claude Desktop". v1.0.0, last released 2025-10-24. |

The distinction that matters is not "official versus unofficial" — it is **what writes the file**.
`plan-usage-history.json` is written by Anthropic's Electron desktop application. A browser wrapper
puts a webview around `claude.ai`; it has no usage-history file to redirect, so installing it and
pointing O-view at `~/snap/claude-ai-desktop/current/.config/Claude` would confirm nothing except
that the directory is empty. Neither snap is a repackaging of the `.deb`.

### 3. Flathub

Queried the Flathub v2 API's full appstream index. **No application id containing either `claude`
or `anthropic`.** There is no Claude Desktop on Flathub, official or otherwise.

## What this changes in the code

`ClaudeDataRoots.Redirected()` justified the Linux half of its search with:

> It is here for unofficial Snap and Flatpak repackagings, **which exist and are widely used**.

That claim is not supported by any of the three sources above, and it is the kind of assertion
[CLAUDE.md](../../CLAUDE.md) rule 6 exists to prevent — stated confidently, never observed, and in
this case wrong. The comment is corrected rather than the code removed, because the two search
paths are cheap, pure, already unit-tested on both runners, and cost nothing on a machine that has
neither directory. Deleting them would trade a harmless lookup for a re-run of the failure that
motivated `ClaudeDataRoots` in the first place, should packaging ever change.

**The paths stay unconfirmed, and are now labelled as such in the code** rather than in an issue
nobody can close.

## What would reopen the question

Any of:

- Anthropic publishing Claude Desktop to Flathub or the Snap Store.
- Anthropic's supported matrix growing past the Debian family — which is also the stated trigger
  for #108, and the same check answers both.
- A credible third-party **repackaging of the `.deb`** — not a browser wrapper — appearing in
  either store, since that would carry the real application and therefore the real file.

Until one of those happens there is nothing to observe, and the honest position is that the Snap
and Flatpak layouts are conventional, untested against a real install, and harmless.
