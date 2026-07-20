# Architecture Decision Records

Records of significant architectural decisions for O-view: the context, the choice, the alternatives, and the consequences.

These are **decided, not drafts.** To change one, add a new ADR that supersedes it rather than editing history.

| # | Title | Status | Summary |
|---|---|---|---|
| [0001](0001-tech-stack.md) | Technology stack — .NET 10 + WPF | Accepted | C# / .NET 10 LTS / WPF with H.NotifyIcon. Rejected Rust+Tauri, Python, Electron, WinUI 3. |
| [0002](0002-usage-data-providers.md) | Dual usage-data providers with graceful fallback | Accepted | OAuth endpoint primary, local JSONL fallback, both in v1. Mandatory data-source labelling. |
| [0003](0003-windows-tray-constraints.md) | Windows-only target and notification-area design constraints | Accepted | Windows 11 only. The tray icon cannot show text — information tiers across icon, tooltip, popup. |
| [0004](0004-clean-room-provenance.md) | Clean-room provenance policy | Accepted | No ReferenceApp source is read or copied. Implementation derives from platform docs and local observation. |

## Open questions

Tracked here until resolved by a spike, then folded into an ADR:

| Question | Blocks | Contingency |
|---|---|---|
| Where does Claude Code Desktop store its OAuth token on Windows? `.credentials.json` held only MCP tokens; Credential Manager showed no match. | `OAuthUsageProvider` | Ship JSONL-only v1 ([ADR-0002](0002-usage-data-providers.md)) |
| Are 2 digits legible at 16×16 px across DPI scales? | Tray icon visual design | Gauge-only icon; would supersede part of [ADR-0003](0003-windows-tray-constraints.md) |
| What is the exact response shape of `/api/oauth/usage`? | `OAuthUsageProvider` parsing | Defensive nullable parsing regardless |

## Format

Each record carries: Status · Date · Deciders · Context · Decision · Alternatives considered · Consequences (positive **and** negative).
