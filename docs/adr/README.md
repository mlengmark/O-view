# Architecture Decision Records

Records of significant architectural decisions for O-view: the context, the choice, the alternatives, and the consequences.

These are **decided, not drafts.** To change one, add a new ADR that supersedes it rather than editing history.

| # | Title | Status | Summary |
|---|---|---|---|
| [0001](0001-tech-stack.md) | Technology stack — .NET 10 + WPF | Accepted *(partly superseded by 0005)* | C# / .NET 10 LTS / WPF. Rejected Rust+Tauri, Python, Electron, WinUI 3. |
| [0002](0002-usage-data-providers.md) | Dual usage-data providers with graceful fallback | Accepted | OAuth endpoint primary, local JSONL fallback, both in v1. Mandatory data-source labelling. |
| [0003](0003-windows-tray-constraints.md) | Windows-only target and notification-area design constraints | Accepted *(icon design revised)* | Windows 11 only. The tray icon cannot show text — information tiers across icon, tooltip, popup. |
| [0004](0004-clean-room-provenance.md) | Clean-room provenance policy | Accepted | No ReferenceApp source is read or copied. Implementation derives from platform docs and local observation. |
| [0005](0005-native-tray-integration.md) | Native tray integration — drop H.NotifyIcon | Accepted | Use first-party `System.Windows.Forms.NotifyIcon`. **Zero third-party runtime dependencies.** |

## Findings

Empirical results from spikes, cited by the ADRs above:

- [jsonl-schema.md](../findings/jsonl-schema.md) — local transcript format; the `requestId` de-duplication requirement
- [tray-icon-rendering.md](../findings/tray-icon-rendering.md) — icon legibility measurements; why the ring gauge was dropped

## Open questions

Tracked here until resolved by a spike, then folded into an ADR:

| Question | Blocks | Contingency |
|---|---|---|
| Where does Claude Code Desktop store its OAuth token on Windows? `.credentials.json` held only MCP tokens; Credential Manager showed no match. | `OAuthUsageProvider` | Ship JSONL-only v1 ([ADR-0002](0002-usage-data-providers.md)) |
| What is the exact response shape of `/api/oauth/usage`? | `OAuthUsageProvider` parsing | Defensive nullable parsing regardless |
| Does icon legibility hold on a real taskbar at 125/150/175% scaling and in high-contrast themes? | Final icon polish | Auto-fitted font already adapts; verify on-device |

### Resolved

| Question | Outcome |
|---|---|
| ~~Are 2 digits legible at 16×16 px?~~ | **Yes** — 13.5 px font, crisp. But the *ring* had to go: it starved the digits. → [tray-icon-rendering.md](../findings/tray-icon-rendering.md), revises [ADR-0003](0003-windows-tray-constraints.md) |
| ~~Is a third-party tray library required?~~ | **No** — `System.Windows.Forms.NotifyIcon` is first-party and sufficient. → [ADR-0005](0005-native-tray-integration.md) |

## Format

Each record carries: Status · Date · Deciders · Context · Decision · Alternatives considered · Consequences (positive **and** negative).
