# ADR-0004: Clean-room provenance policy

- **Status:** Accepted
- **Date:** 2026-07-20
- **Deciders:** @mlengmark

## Context

O-view was conceived after seeing [ReferenceApp](https://github.com/example/reference-app), a macOS menu-bar AI usage monitor. Working "next to" an existing open-source implementation creates a real risk that its code, structure, or naming leaks into the new project — whether deliberately or by osmosis from having read it.

Two independent reasons to prevent this:

1. **Licence and attribution.** Copying source from another project carries that project's licence obligations. Unintentional copying is still copying, and it is difficult to disprove after the fact.
2. **Technical correctness.** ReferenceApp is macOS/Swift. Its design encodes macOS assumptions — `NSStatusItem` text labels, Keychain, LaunchAgents, Safari cookie stores — that are **wrong on Windows** ([ADR-0003](0003-windows-tray-constraints.md)). Borrowing its structure would import defects, not shortcuts.

The second reason is the stronger one. The macOS design is not merely encumbered; it is inapplicable.

## Decision

O-view is developed **clean-room** with respect to ReferenceApp and any other existing usage-monitor implementation.

### Rules

1. **Do not read, clone, or browse ReferenceApp's source.** Not the repository, not individual files, not vendored copies.
2. **Public product description only.** What the product does, as stated on its README and website, is permitted input. How it does it is not.
3. **Derive implementation from primary sources only:** Windows platform documentation, .NET documentation, and empirical observation of local data formats on the developer's own machine (see [findings/](../findings/)).
4. **This rule binds AI coding assistants working on this repo**, which is why it is restated in `CLAUDE.md`. An assistant asked to "look at how ReferenceApp does X" must decline and reason from platform documentation instead.
5. **No copying of naming, file layout, or architecture** from the reference, even where re-derived independently — divergence is the safer default.

### What is *not* restricted

- The **product concept** — a tray-resident AI usage monitor — is an idea, not protectable expression.
- **Facts** about the Claude platform: window durations, endpoint paths, response field names. Facts are not copyrightable, and these are independently observable.
- General patterns (polling with backoff, provider interfaces) that are standard practice.

## Record of research already performed

For transparency, the research conducted during planning was:

| Action | Result |
|---|---|
| Fetched `github.com/example/reference-app` | **README-level description only** — features, platform, provider list. No source files read. |
| Fetched `reference-app.app` | HTTP 403, no content retrieved |
| Web search on Claude rate limits | Public documentation and GitHub issues in `anthropics/claude-code` |
| Inspected local `%USERPROFILE%\.claude\` | Own machine's data — schema, credential layout, volume |
| Inspected local toolchain | `dotnet`, `git`, `gh` availability |

No ReferenceApp source code was accessed at any point.

## Consequences

**Positive**
- The project is defensibly original and can be made public without licence concerns
- Windows-native design decisions are forced, rather than macOS ones being inherited
- The policy is stated where it will actually be read — README and `CLAUDE.md`

**Negative**
- Genuine effort is spent re-deriving solutions to problems already solved elsewhere. Accepted: most of those solutions are macOS-specific and would not transfer.
- No shortcut is available for the fiddly parts (icon rasterisation, popup positioning). These must be solved from Windows documentation.
- Requires ongoing discipline, particularly when debugging — the temptation to peek is highest when stuck.
