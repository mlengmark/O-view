# O-view

A Windows notification-area (system tray) app that displays your Claude AI token usage and the time remaining until your next usage-limit reset.

> **Status:** Working. All five build phases are complete — tray icon, popup panel, usage history, notifications, and publish pipeline. See [`docs/adr/`](docs/adr/) for the decisions that shaped the build.

---

## What it does

O-view sits in the Windows 11 notification area and answers two questions at a glance:

1. **How much of my Claude usage limit have I consumed?** (5-hour rolling window, and 7-day window)
2. **When does it reset?**

| Surface | Shows |
|---|---|
| Tray icon | 2 digits + a proportional % fill bar (e.g. `47` over a half-full bar), colour-coded green → amber → red; full-ring `!` at 100% |
| Tooltip | `5h: 47% · resets 16:32 · 7d: 61%` |
| Popup panel | Session/weekly bars, token counts, estimated API-equivalent value, 31-day usage graph, data-source badge. Docks to the taskbar corner like a system flyout. |
| Right-click menu | Run at startup · threshold notification toggle · exit |

A balloon notification fires once per session-window crossing of the threshold (default 85%).

## Platform

**Windows 11 only.** This is a deliberate constraint, not a temporary one — see [ADR-0003](docs/adr/0003-windows-tray-constraints.md). The app targets Windows-native APIs (Shell notification area, DPAPI, per-monitor DPI, registry startup) and there is no macOS or Linux target.

## Provenance — clean-room

O-view is inspired by the *product concept* of [CodexBar](https://github.com/steipete/codexbar), a macOS menu-bar usage monitor.

**No CodexBar code has been read, copied, adapted, or consulted.** CodexBar is a macOS/Swift application; O-view is an independent Windows/.NET implementation written from scratch against Windows platform APIs and locally observed data formats. See [ADR-0004](docs/adr/0004-clean-room-provenance.md) for the full policy and its practical rules.

## Data sources

O-view reads usage from two independent providers and falls back gracefully:

| Provider | Role | Gives | Notes |
|---|---|---|---|
| `OAuthUsageProvider` | Primary | Authoritative % utilisation + reset timestamps | Undocumented endpoint; aggressively rate-limited |
| `JsonlUsageProvider` | Fallback | Token counts from local `~/.claude` logs | Always available, offline, no auth |

When operating on fallback data the UI shows a visible **"local estimate"** badge. See [ADR-0002](docs/adr/0002-usage-data-providers.md).

## Install and run

**Users:** download `O-view.Tray.exe` from the latest release and run it. It is self-contained — no .NET install required. The icon lands in the taskbar overflow flyout (the `^` chevron) by default; drag it onto the taskbar to pin it. Left-click opens the panel, right-click the menu.

> **SmartScreen, honestly:** the executable is not code-signed — a certificate costs more than a free tool justifies. Windows SmartScreen will warn on first run ("Windows protected your PC"); *More info → Run anyway* proceeds. The source is in this repository, and the release binary is built from it by the GitHub Actions workflow — verify rather than trust.

**Building from source:**

```
dotnet build          # requires .NET 10 SDK 10.0.302+
dotnet test           # 72 tests
dotnet publish src/O-view.Tray -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Runtime prerequisites: Windows 11, and Claude data present locally — [Claude Desktop](https://claude.ai/download) for authoritative percentages, and/or Claude Code transcripts under `%USERPROFILE%\.claude\` for token counts.

## Documentation

- [Architecture Decision Records](docs/adr/) — why the stack, providers, and constraints are what they are
- [Findings](docs/findings/) — empirical observations about local data formats

## Licence

MIT — see [LICENSE](LICENSE).

Not affiliated with, endorsed by, or sponsored by Anthropic.
