# ADR-0006: Local rollup store for usage history

- **Status:** Accepted
- **Date:** 2026-07-20
- **Deciders:** @mlengmark

## Context

The agreed UI ([ui-spec.md](../ui-spec.md)) requires **token totals and estimated value over the last 31 days**, plus a **usage graph** across that period. Neither of the providers in [ADR-0002](0002-usage-data-providers.md) can supply this.

Measured on the dev machine:

```
firstStartTime    : 2026-07-17
oldest transcript : 2026-07-17
today             : 2026-07-20
→ 3 days of data available
```

And `cleanupPeriodDays` is unset in settings, so **Claude Code's default 30-day cleanup applies — it deletes its own transcripts.** JSONL is a rolling window that garbage-collects at almost exactly the boundary we need to report on. Asking it for 31 days will never work reliably, regardless of how long the machine has been running.

The OAuth usage endpoint reports current-window utilisation, not history. There is no source of historical usage on the machine.

## Decision

O-view maintains **its own persistent rollup store**, independent of Claude Code's retention.

- **Storage:** SQLite at `%LOCALAPPDATA%\O-view\usage.db`
- **Grain:** one row per (UTC date × model), holding summed `input`, `output`, `cache_creation`, `cache_read` tokens and a request count
- **Ingestion:** on each poll, scan JSONL, de-duplicate by `requestId` ([findings/jsonl-schema.md](../findings/jsonl-schema.md)), and upsert daily aggregates
- **Idempotency:** re-ingesting the same transcript must not double-count. Track the highest-watermark `requestId` set per source file, or upsert by natural key — never blind `INSERT`.
- **Retention:** keep daily rollups for at least 400 days. They are tiny (a few hundred rows per year) and cost nothing to hold.

History therefore accumulates from O-view's install date forward and survives Claude Code deleting its transcripts.

### Honesty requirement

The store cannot invent history that predates installation. Consistent with the "never fabricate a number" rule, any window that is not fully covered must be **labelled with its actual coverage** — e.g. `3 of 31 days recorded`. A 31-day tile showing a small number without that caveat reads as low usage rather than short history, which is a materially misleading difference.

## Alternatives considered

| Option | Why rejected |
|---|---|
| **Read JSONL on demand, no store** | Simplest, and the original design. Fails outright: Claude Code deletes transcripts at 30 days, so the 31-day window can never be complete. |
| **Copy raw transcripts into our own archive** | Would preserve full fidelity. Rejected on privacy and size — transcripts contain full conversation content, and O-view has no need for it. Daily token aggregates are sufficient and hold nothing sensitive. |
| **Raise `cleanupPeriodDays` in Claude Code settings** | O-view must not modify another tool's configuration. Also fails to help retroactively, and leaves us dependent on a setting the user may change. |
| **JSON file instead of SQLite** | Viable at this scale. Rejected for concurrent-access and partial-write safety — SQLite ships in the .NET ecosystem and handles crash-consistency for free. |

## Consequences

**Positive**
- 31-day figures and the usage graph become possible at all
- Data survives Claude Code's cleanup, and gets more valuable the longer O-view is installed
- Aggregation happens once at ingest, so the popup opens instantly rather than re-parsing transcripts

**Negative**
- Adds a persistence layer, a schema, and a migration concern to v1 — a real scope increase over ADR-0002
- Introduces `Microsoft.Data.Sqlite`, a dependency. Accepted: unlike the tray library in [ADR-0005](0005-native-tray-integration.md), there is no first-party alternative and no reasonable way to hand-roll it.
- **The first month looks sparse**, and this is unavoidable. The UI must present it as incomplete history, not as low usage.
- Idempotent ingestion is the subtle failure mode here, exactly as `requestId` de-duplication is for parsing. **Both need explicit tests** — double-counting fails silently and produces confident, wrong numbers.
