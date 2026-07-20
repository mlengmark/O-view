# CLAUDE.md — O-view

Guidance for AI coding assistants working in this repository. Read this before writing code.

## What this is

A **Windows 11 notification-area (system tray) application** that displays Claude AI token usage and time until the next usage-limit reset.

Status: **planning complete, no application code yet.** The ADRs in `docs/adr/` are decided, not drafts — follow them. If you believe one is wrong, say so and propose superseding it; do not silently deviate.

## Hard rules

### 1. Windows only

Target `net10.0-windows`. Use Win32 and WPF APIs directly. **Do not** add cross-platform abstractions, `#if` platform guards, or macOS/Linux considerations. Windows-only is a decision ([ADR-0003](docs/adr/0003-windows-tray-constraints.md)), not an unfinished state.

Watch for macOS assumptions leaking in from AI training data, because the closest prior art is a Mac app. Specifically **wrong on Windows**:

| Wrong (macOS) | Right (Windows) |
|---|---|
| Tray icon can show a text label | **It cannot.** `Shell_NotifyIcon` takes an `HICON` only. Text must be rasterised into a 16/20/24 px bitmap. |
| Keychain | DPAPI / Windows Credential Manager |
| `~/Library/Application Support` | `%APPDATA%` / `%LOCALAPPDATA%` |
| LaunchAgents / `SMAppService` | `HKCU\...\CurrentVersion\Run` |
| `NSPopover` anchors itself | Position manually via `Shell_NotifyIconGetRect` + work area |
| Menu bar item always visible | Windows 11 hides new tray icons in the overflow flyout |

### 2. Clean-room — do not read CodexBar

This project was inspired by [CodexBar](https://github.com/steipete/codexbar) (macOS/Swift). **Do not read, clone, fetch, or browse its source code**, and do not reproduce its naming or file layout. If asked "how does CodexBar do X", decline and reason from Windows/.NET documentation instead. Full policy: [ADR-0004](docs/adr/0004-clean-room-provenance.md).

Its macOS design is not merely encumbered — it is *incorrect* for this platform.

### 3. Never leak the auth token

`OAuthUsageProvider` handles a live credential. Never log it, persist it, include it in exception messages, or write it to diagnostics. Memory only. `/security-review` is required before this repo goes public.

### 4. De-duplicate JSONL by `requestId`

Non-negotiable, and the single easiest way to ship a silently broken app. Assistant records are written multiple times as responses stream; the sample file had **28 records for 12 distinct `requestId`s** — a naive sum overcounts by ~2.3×. Group by `requestId`, keep the last occurrence. Details: [docs/findings/jsonl-schema.md](docs/findings/jsonl-schema.md).

### 5. Never fabricate a number

If data is unavailable, show a neutral icon and explain in the popup. If data is estimated (JSONL-derived), label it **"local estimate"**. A monitoring tool that confidently displays a wrong number is worse than one that admits uncertainty.

## Prerequisites

**The .NET 10 SDK is not installed on the dev machine** — only the 3.1 and 6.0 runtimes. Install it before attempting a build; `dotnet build` will fail with "No .NET SDKs were found" until then.

Available: `git`, `gh` (authenticated as `mlengmark`). Not available: Node, Rust. The `python` on PATH is the non-functional Microsoft Store alias stub — do not use it for tooling scripts; use PowerShell or C#.

## Intended structure

```
O-view.sln
├── src/
│   ├── O-view.Core/      # Providers, models, window math — no UI, no Win32
│   └── O-view.Tray/      # WPF + H.NotifyIcon, icon rendering, popup
└── tests/
    └── O-view.Core.Tests/  # xUnit
```

Keep `Core` free of UI and Win32 dependencies so the accounting logic stays testable without a desktop session.

### Provider design

```
IUsageProvider
 ├─ OAuthUsageProvider   (primary)   → authoritative %, reset times
 ├─ JsonlUsageProvider   (fallback)  → local token counts, offline
 └─ CompositeUsageProvider           → resolution, caching, source labelling
```

Resolution: fresh OAuth → cached OAuth (labelled with age) → JSONL (labelled estimate) → no data. See [ADR-0002](docs/adr/0002-usage-data-providers.md).

## Build order

Deliberately **not** in order of importance — in order of ascending unknowns:

1. **Icon rasterisation spike** — can 2 digits be read at 16×16? If not, ADR-0003's design changes. Do this before any other UI work.
2. **Token-discovery spike** — where does Claude Code Desktop store its OAuth token on Windows? Unresolved: `.credentials.json` held only MCP tokens; Credential Manager had no match. Timebox it; JSONL fallback means failure here is not fatal.
3. **`JsonlUsageProvider`** — no auth, no network, fully testable. First test is the `requestId` de-duplication test.
4. **Rolling-window math** — 5h window rolls from first use, not a wall clock. UTC throughout.
5. **Tray shell** — icon, tooltip, popup positioning, `TaskbarCreated` re-registration, single-instance mutex.
6. **`OAuthUsageProvider`** — backoff with jitter, `retry-after`, nullable-everything parsing, ≥5 min polling.
7. **Polish** — notifications, startup registration, settings, publish.

## Conventions

- C# nullable reference types enabled; treat warnings as errors in `Core`
- All external data (JSONL fields, HTTP responses) parsed defensively — assume every field can be absent
- Times stored and computed in UTC; converted to local only at the display edge
- Tests use fixtures derived from real session files, with any identifying content scrubbed

## Reference docs

- [docs/adr/](docs/adr/) — decisions and their rationale
- [docs/findings/jsonl-schema.md](docs/findings/jsonl-schema.md) — verified local data schema
