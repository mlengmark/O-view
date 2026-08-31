# Finding: Claude Code JSONL transcript schema (Windows)

**Observed:** 2026-07-20 · **Machine:** Windows 11 Pro 26200 · **Source:** `%USERPROFILE%\.claude\projects\`

Empirical observation of the developer's own machine. Undocumented and subject to change without notice — the parser must treat every field as optional.

## Location and path mangling

```
%USERPROFILE%\.claude\projects\<mangled-cwd>\<session-uuid>.jsonl
```

The working directory is mangled into a directory name by replacing path separators and colons with `-`:

```
C:\Users\Maximilian   →   C--Users-Maximilian
```

Windows mangling differs from POSIX (drive letter plus colon produces the leading `C--`). **Write the resolver for Windows; do not adapt a POSIX implementation.**

## Record types

One JSON object per line. Observed `type` values in a single 61-line session:

| `type` | Count | Relevance |
|---|---|---|
| `assistant` | 27 | **Carries `message.usage`** — the only type that matters for token accounting |
| `user` | 13 | — |
| `attachment` | 7 | — |
| `queue-operation` | 4 | — |
| `last-prompt` | 4 | — |
| `ai-title` | 4 | — |
| `system` | 1 | — |
| `custom-title` | 1 | — |

Filter to `type == "assistant"`. Do not assume this list is exhaustive; unknown types must be skipped silently.

## Fields required for accounting

| Field | Example | Notes |
|---|---|---|
| `timestamp` | `2026-07-20T12:58:16.640Z` | **ISO-8601 UTC.** Do window arithmetic in UTC; convert to local only for display. |
| `requestId` | `req_011ExampleRequestId000` | **De-duplication key — see below** |
| `message.model` | `claude-opus-4-8` | For per-model breakdown |
| `message.usage.input_tokens` | `2` | |
| `message.usage.cache_creation_input_tokens` | `14226` | |
| `message.usage.cache_read_input_tokens` | `25061` | |
| `message.usage.output_tokens` | `120` | |

### Pricing modifiers — also on `usage`, and all three are load-bearing

This line used to read *"Also present on `usage`, not currently needed: `server_tool_use`,
`service_tier`, `cache_creation`, `inference_geo`, `iterations`, `speed`."* Three of those six
appear in Anthropic's published pricing formula, and they were judged against what the panel
**displayed** rather than against what it **computes**. Two defects trace to that one line
([#255](https://github.com/mlengmark/O-view/issues/255), [#257](https://github.com/mlengmark/O-view/issues/257)).

**Any field that appears in a pricing formula is load-bearing whether or not today's build reads
it.** Full structure: [docs/reference/pricing.md](../reference/pricing.md).

| Field | Shape | Why it matters | State |
|---|---|---|---|
| `usage.cache_creation` | `{"ephemeral_5m_input_tokens": 0, "ephemeral_1h_input_tokens": 16719}` | The two TTLs bill at **1.25×** and **2×** base input. The flat `cache_creation_input_tokens` beside it carries the same total with no attribution. | **Active** — read since #255 |
| `usage.speed` | `"standard"` \| `"fast"` \| absent \| `null` | Fast mode is its own published rate row: Opus 5 / 4.8 at **$10/$50**. | Inactive here — read anyway |
| `usage.inference_geo` | `"global"` \| `"us"` \| `"not_available"` \| `null` | `"us"` applies **1.1×** to every category. | Inactive here — read anyway |

**Coverage, measured 2026-08-31 on this machine.** `cache_creation` is present on **15,851 of
15,851** Claude Code assistant records and on **296 of 296** Cowork `audit.jsonl` assistant
records — both sources, checked separately, because rule 9 says these are two sources and not
one. `speed` is `"standard"` on 15,817 of 15,817 Claude Code records and absent from all but one
Cowork record; `inference_geo` is `"not_available"` on 15,851 of 15,851 and on 295 of 296.

**Inactive is not absent, and absent is not standard.** A record whose `cache_creation` is missing
keeps its writes in a third, unattributed bucket rather than being assigned the TTL the rest of
the file happens to use. A `speed` or `inference_geo` value this build does not recognise makes
the request **unpriceable** rather than standard — pricing an unknown modifier at the cheaper
standard rate is the silent downgrade #257 is about.

Still not needed, and still only display-adjacent: `server_tool_use`, `service_tier` (uniformly
`"standard"`, including on requests known to have billed to credits), `iterations`,
`output_tokens_details`.

## ⚠️ Critical: records are duplicated by `requestId`

Measured on the sample file:

```
assistant records : 28
distinct requestId: 12
```

The same request is written **multiple times** as the response streams and is updated. Records are append-only, so earlier partial rows remain in the file alongside the final one.

**A naive `SUM(usage.output_tokens)` over assistant records overcounts by roughly 2.3×.**

This fails silently — there is no error, only a confidently wrong number, which is the worst outcome for a monitoring tool.

### Required handling

> Group assistant records by `requestId` and keep **only the last occurrence** (highest `timestamp`, or last line wins on ties, since the file is append-ordered). Sum token fields across the de-duplicated set only.

The duplication ratio is not fixed — it varies with response length and streaming behaviour — so it cannot be corrected with a constant factor.

**This is the first unit test to write**, using a fixture derived from a real session file.

## Other parser requirements

- **Multiple sessions.** Scan all `*.jsonl` under all project directories, not just the current one. Usage limits are account-wide.
- **Concurrent writes.** Claude Code appends while O-view reads. Open with `FileShare.ReadWrite` and tolerate a truncated final line — a partially flushed last line is normal, not corruption.
- **Malformed lines.** Skip and continue. One bad line must never fail a scan.
- **Incremental reads.** Track file offsets rather than re-parsing everything each poll. Sample volume was small (2 files, 0.2 MB) but this grows without bound.
- **Rolling window.** The 5-hour window is *rolling from first use*, not aligned to a wall clock — filter to `timestamp >= now - 5h` rather than bucketing by clock hour.

## What this source cannot provide

Raw token counts only. The subscription plan's token allowance is not published, so **no true percentage-of-limit can be derived** from JSONL alone. Any percentage shown from this source is an estimate and must be labelled as such — see [ADR-0002](../adr/0002-usage-data-providers.md).
