# ADR-0002: Dual usage-data providers with graceful fallback

- **Status:** Accepted
- **Date:** 2026-07-20
- **Deciders:** @mlengmark

## Context

O-view needs two things: **how much of the limit is consumed**, and **when it resets**. Two independent sources exist on a Windows machine with Claude Code installed, and neither is sufficient alone.

### Source A — OAuth usage endpoint

`https://api.anthropic.com/api/oauth/usage` returns `five_hour.utilization`, `seven_day.utilization`, and reset timestamps. This is exactly the product requirement.

Risks:
- **Undocumented.** No compatibility guarantee; the shape can change or the endpoint disappear without notice.
- **Aggressively rate-limited.** Publicly reported in [anthropics/claude-code#31021](https://github.com/anthropics/claude-code/issues/31021) and [#31637](https://github.com/anthropics/claude-code/issues/31637), where polling makes usage monitoring unusable.
- **Token location on Windows is unresolved.** Investigation of the dev machine found `%USERPROFILE%\.claude\.credentials.json` contains *only MCP OAuth tokens* — no `claudeAiOauth` entry — and Windows Credential Manager showed no matching entry. Where Claude Code Desktop stores its primary token on Windows is an open question.

### Source B — Local JSONL transcripts

`%USERPROFILE%\.claude\projects\<mangled-path>\<session>.jsonl` contains per-message `usage` objects with `input_tokens`, `cache_creation_input_tokens`, `cache_read_input_tokens`, and `output_tokens`, plus `model`, `requestId`, and a UTC `timestamp`.

Always present, needs no auth, works offline, and cannot be rate-limited. But it yields **raw token counts, not percentage-of-limit** — the plan's allowances are not published, so any percentage derived from it is an estimate.

See [findings/jsonl-schema.md](../findings/jsonl-schema.md) for the verified schema and a critical de-duplication requirement.

## Decision

Ship **both providers in v1** behind a single interface, with explicit precedence and honest labelling.

```
IUsageProvider
 ├─ OAuthUsageProvider   (primary)
 ├─ JsonlUsageProvider   (fallback)
 └─ CompositeUsageProvider (resolution + caching)
```

**Resolution order:**

1. Fresh OAuth snapshot (within TTL) → use it, label **Live**
2. OAuth failed/rate-limited but cached snapshot is recent → use cache, label **As of HH:MM**
3. Otherwise → JSONL, label **Local estimate**
4. No data at all → neutral icon and an explanatory popup, never a fabricated number

**Labelling is mandatory.** Estimated data must never be presented as authoritative. A usage monitor that silently shows a wrong number is worse than one that admits it does not know.

**Polling discipline for OAuth:**
- Default interval no faster than 5 minutes
- Exponential backoff with jitter on `429`, honouring `retry-after`
- Every response field treated as nullable; a schema change degrades to fallback and never crashes the tray icon

### Build order

**`JsonlUsageProvider` is implemented first**, despite OAuth being the primary source. It needs no auth and no network, so it is fully testable offline against fixtures. This lets the rolling-window arithmetic — the most bug-prone part of the system — be proven correct before network and auth complexity are layered on.

## Alternatives considered

| Option | Why rejected |
|---|---|
| **OAuth only** | Single point of failure on an undocumented, rate-limited endpoint, blocked behind an unresolved token-storage question. One upstream change and the product is dead. |
| **JSONL only** | Cannot report true percentage-of-limit or authoritative reset times — the core requirement. Simpler and lower-risk, but the wrong product. Retained as the contingency if the token spike fails. |
| **Scrape browser cookies** (an approach some existing usage monitors take) | Chrome/Edge now combine DPAPI with App-Bound Encryption; extracting cookies means defeating a security control. Rejected on both fragility and propriety. |
| **Ask the user for an API key** | Console API keys measure a different billing surface than Claude Code subscription limits. Wrong data, plus needless credential handling. |

## Consequences

**Positive**
- The unresolved token-storage question **no longer blocks the project**. If the spike fails, v1 ships JSONL-only and OAuth lands later behind the same interface.
- The app is useful offline and during rate-limit storms.
- The interface boundary makes adding a provider a contained change.

**Negative**
- Two code paths to build, test, and maintain in v1.
- Two notions of "usage" must be reconciled into one UI without misleading the user — hence the mandatory source badge.
- The OAuth provider carries a live auth token in memory. It must never be logged, persisted, or included in diagnostics; a security review is required before the repo goes public.
