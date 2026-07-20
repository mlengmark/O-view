# O-view

A Windows notification-area (system tray) app that displays your Claude AI token usage and the time remaining until your next usage-limit reset.

> **Status:** Planning / pre-implementation. No application code yet — see [`docs/adr/`](docs/adr/) for the decisions that shape the build.

---

## What it does

O-view sits in the Windows 11 notification area and answers two questions at a glance:

1. **How much of my Claude usage limit have I consumed?** (5-hour rolling window, and 7-day window)
2. **When does it reset?**

| Surface | Shows |
|---|---|
| Tray icon | Ring gauge + 2 digits (e.g. `47`), colour-coded green → amber → red |
| Tooltip | `5h: 47% · resets 16:32 · 7d: 61%` |
| Popup panel | Full breakdown, token counts, model split, data-source badge |

## Platform

**Windows 11 only.** This is a deliberate constraint, not a temporary one — see [ADR-0003](docs/adr/0003-windows-tray-constraints.md). The app targets Windows-native APIs (Shell notification area, DPAPI, per-monitor DPI, registry startup) and there is no macOS or Linux target.

## Provenance — clean-room

O-view is inspired by the *product concept* of [ReferenceApp](https://github.com/example/reference-app), a macOS menu-bar usage monitor.

**No ReferenceApp code has been read, copied, adapted, or consulted.** ReferenceApp is a macOS/Swift application; O-view is an independent Windows/.NET implementation written from scratch against Windows platform APIs and locally observed data formats. See [ADR-0004](docs/adr/0004-clean-room-provenance.md) for the full policy and its practical rules.

## Data sources

O-view reads usage from two independent providers and falls back gracefully:

| Provider | Role | Gives | Notes |
|---|---|---|---|
| `OAuthUsageProvider` | Primary | Authoritative % utilisation + reset timestamps | Undocumented endpoint; aggressively rate-limited |
| `JsonlUsageProvider` | Fallback | Token counts from local `~/.claude` logs | Always available, offline, no auth |

When operating on fallback data the UI shows a visible **"local estimate"** badge. See [ADR-0002](docs/adr/0002-usage-data-providers.md).

## Prerequisites

- Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download) — **not currently installed on the dev machine**; required before first build
- Claude Code installed with data present under `%USERPROFILE%\.claude\`

## Documentation

- [Architecture Decision Records](docs/adr/) — why the stack, providers, and constraints are what they are
- [Findings](docs/findings/) — empirical observations about local data formats

## Licence

MIT — see [LICENSE](LICENSE).

Not affiliated with, endorsed by, or sponsored by Anthropic.
