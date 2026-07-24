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

Also present on `usage`, not currently needed: `server_tool_use`, `service_tier`, `cache_creation`, `inference_geo`, `iterations`, `speed`.

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
