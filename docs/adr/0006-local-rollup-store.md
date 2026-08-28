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

> **Correction (2026-08-19, [#142](https://github.com/mlengmark/O-view/issues/142)) — the implementation had this backwards, and no decision here changes.**
>
> "Coverage" above means *how much of the window predates the store*. What shipped counted **days carrying usage**: a `SELECT COUNT(DISTINCT utc_date)` over the request ledger, in which a day the user spent away from Claude was indistinguishable from a day before O-view existed.
>
> That inverted the paragraph's own purpose. A user who took a week off was told `18 of 31 days recorded` — their history read as *short* when it was complete and their usage was simply low, which is the misreading this requirement exists to prevent, pointed the other way. It also disagreed with the graph rendered directly beneath the label, which had always drawn an idle day as a real day with a zero bar.
>
> Coverage is now derived in `PanelStatistics.Build` from the same first-recorded-day boundary that marks `DayUsage.PreInstall`, so the caveat and the chart are one derivation and cannot disagree. `RollupStore.CountRecordedDays` is deleted rather than left unused — a second, wrong answer to the same question is how it would come back.
>
> Two consequences accepted deliberately: the caveat now disappears for most users once they are past 31 days (correct — it is for short history, and that history is not short), and a store wiped by the corruption guard legitimately reports shrunken coverage if Claude Code has since pruned the transcripts behind it (also correct — the data really is gone, and it is the floor the graph already drew).

> **Amendment (2026-08-26, [#211](https://github.com/mlengmark/O-view/issues/211)) — the grain above is what is *stored*; the grain *reported* is a local day.**
>
> "One row per (UTC date × model)" still describes the ledger, and ingestion is unchanged. What changed is the read: a UTC day is not the day a user means by "today", and one local day straddles two UTC ones, so `utc_date` cannot answer for it however it is indexed. `GetDailyRollups` now takes a UTC instant range and a timezone, buckets each row from its own `last_timestamp`, and returns local dates. The coverage window this section is about is therefore 31 **local** days.
>
> **Storing a local date alongside the UTC one was rejected**, not overlooked. It queries faster, and it bakes the machine's offset into the row at ingest time — wrong for anyone who travels, and wrong for every historical row after a DST change. The offset belongs to the reader, not to the record.
>
> **The cost of giving up `ix_requests_date` was measured, not assumed**, because that was the stated risk. Against a synthetic ledger of 7,000 rows — the size of the development machine's when the issue was written — a 31-day read is **0.63 ms**, against 7.61 ms for the whole ledger; `ix_requests_timestamp` was added to serve the range scan. `RollupStoreQueryCostTests` holds it there. The measurement is deliberately not taken against the real store: opening that file is the operation [#213](https://github.com/mlengmark/O-view/issues/213) is about.
>
> **A local day is 23 or 25 hours twice a year.** Boundaries come from the timezone via `LocalDays`, never from 24-hour arithmetic, and the graph's gridlines are placed in the same frame as its columns. Nothing here touches the plan meters — the five-hour window rolls from first use and the weekly reset is a reported instant (ADR-0014); neither is a calendar day.

> **Amendment (2026-08-26, [#213](https://github.com/mlengmark/O-view/issues/213)) — the store is checked for an orphaned journal before it is opened.**
>
> SQLite recovers from a `-wal` on open and treats its frames as the newest version of the pages they cover, overriding newer content in the main file. That is correct when the journal is a genuine continuation. When it is an orphan, the store presents itself as it stood when that file was written — measured on the development machine, the same database read twice gave **6,917 rows** and **5,072 rows**, differing only in whether a stale journal sat beside it, with `PRAGMA quick_check` returning `ok` both times. Worse, opening it that way makes the rolled-back state the new truth: 1,845 rows, unrebuildable for transcripts Claude Code had since deleted.
>
> `StaleJournal.Guard` runs in the `RollupStore` constructor **before** the connection, because after it there is nothing left to see. Three decisions in it are not obvious:
>
> - **The age is compared against the database, not against now.** A machine switched off for a week has an old journal and an old database and nothing is wrong. What cannot happen is a journal materially *older* than the file it continues — SQLite writes the journal on every commit and the database only on checkpoint.
> - **The timestamps are only read while both files are held exclusively.** Windows does not update a directory entry while a handle is open, so a journal being written right now can report a last-write time from minutes ago; quarantining that one would *cause* the loss this prevents. When the handles cannot be taken the guard reports that nothing was established, which is not the same as reporting the journal healthy. On Unix the probe is weaker — .NET emulates `FileShare` with `flock` while SQLite locks with `fcntl` — so what carries it there is running behind the single-instance guard.
> - **Only the journal is moved aside, never the database.** A deliberate departure from the corruption path above, which quarantines the whole set and rebuilds empty. Here the database is the truth and the journal is the liar; moving the database would discard the history the guard exists to save.
>
> **The threshold is six hours**, argued rather than picked. A live journal is never more than one poll behind, and one that outlives a clean shutdown does not exist — so on the app's own behaviour, minutes would do; the margin above that is for timestamp granularity, NTP steps and tools that rewrite mtimes. The issue offered "a day" as the unambiguous case and this is tighter, because the two mistakes cost very different amounts: acting wrongly costs a re-ingest into a rebuildable cache with the frames still quarantined, and failing to act costs days of history that cannot be rebuilt at all, silently.
>
> **The rollback is reproduced, not assumed.** `StaleJournalTests` captures a real journal mid-life and puts it back after the database has moved on: a plain connection then reports 5 rows where the file holds 25, and `quick_check` still says `ok`. That test asserts the wrong behaviour deliberately, so the guard beside it is demonstrably defending against something real — and so it fails loudly if a future SQLite stops doing this, rather than leaving the guard standing on an expired premise.

> **Amendment (2026-08-28, [#236](https://github.com/mlengmark/O-view/issues/236)) — the 30-day cleanup is not operating as this ADR describes, and the decision stands anyway.**
>
> The Context above infers a 30-day boundary from `cleanupPeriodDays` being unset, and the first row of *Alternatives* treats it as fact. **Measured on the development machine on 2026-08-28: ten Cowork session registrations aged 30–90 days, the oldest 41.9 days, and every one of them still had its transcript on disk.** Cleanup is running — `~/.claude/.last-cleanup` had been touched that morning — so this is not a dormant scheduler.
>
> What that establishes is only that retention exceeds 42 days here. It does **not** establish the real figure, and no number should replace 30 in the text above until one is measured; the same trap as ADR-0007's sampling interval, where a documented constant quietly stopped being true. Treat retention as **unknown and variable**.
>
> **No part of the decision changes, and two of its load-bearing reasons never depended on the figure.** History that predates O-view's install is unrecoverable at any retention length, which is what the *Honesty requirement* and the coverage caveat exist for. And `cleanupPeriodDays` is a setting the user may change at any time — the third row of *Alternatives* already rejects depending on it. A store justified by "their retention is 30 days" would be undermined by this measurement; a store justified by "their retention is not ours to rely on" is not.
>
> The practical consequence is narrow: a machine may hold more recoverable history than the store assumes, so a rebuild after the corruption guard fires may recover more than expected. Nothing needs to change for that to be true.

## Alternatives considered

| Option | Why rejected |
|---|---|
| **Read JSONL on demand, no store** | Simplest, and the original design. Fails outright: Claude Code deletes transcripts at 30 days, so the 31-day window can never be complete. *(The 30-day figure is contradicted by measurement — see the 2026-08-28 amendment. The rejection stands: retention is not ours to rely on, and history predating install is unrecoverable at any length.)* |
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
