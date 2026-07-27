# Finding — Cowork writes its transcript somewhere else

Verified 2026-07-27 on the dev machine (GitHub issue #44).

## Where the files are

Cowork runs each session in a **sandboxed Claude home**:

```
%APPDATA%\Claude\local-agent-mode-sessions\<org>\<user>\<session>\
├── .claude\
│   ├── projects\        ← exists, ALWAYS EMPTY
│   └── ...
└── audit.jsonl          ← the transcript
```

The empty `.claude\projects` directory is why this went unnoticed for so long: the
folder `ClaudeProjectsLocator` looks for *is* present inside the sandbox, it simply
never holds anything. Scanning it succeeds and returns nothing, which is
indistinguishable from "no usage".

Claude Code, by contrast, writes to `%USERPROFILE%\.claude\projects` — including
sessions **hosted inside the Desktop app**. Those are recorded under
`%APPDATA%\Claude\claude-code-sessions\...\local_*.json`, but that file holds only
metadata (`sessionId`, `cliSessionId`, `cwd`, `model`, `title`); its `cliSessionId`
names a transcript in the normal user-profile location. Verified 9 of 9 on this
machine. So "Desktop" is not the distinction — **Cowork** is.

## The record format

`audit.jsonl` assistant records carry the **identical** usage schema:

```
input_tokens, cache_creation_input_tokens, cache_read_input_tokens, output_tokens
```

plus `message.model` and an ISO-8601 `timestamp`. Same models
(`claude-opus-4-8`, `claude-sonnet-5`, `<synthetic>`).

**One difference, and it is silent:** the id is `request_id`, not `requestId`.
Keying only on the camelCase spelling parses zero rows — no exception, no partial
total, just a permanently empty tile. `TranscriptReader.ReadRequestId` accepts both.

De-duplication by request id applies exactly as it does to Claude Code transcripts
(CLAUDE.md rule 4); streaming writes the same id repeatedly here too.

Other keys present but not consumed today, worth knowing about:
`total_cost_usd` (a **reported** cost, not an estimate), `modelUsage`,
`estimated_tokens`, and `rate_limit_info` as its own event type — potentially a more
direct session-% source than `plan-usage-history.json`.

## The junction trap

The Cowork tree contains a **broken directory junction** — a `…-outputs` link whose
target no longer exists. Enumerating it throws `DirectoryNotFoundException`, which
derives from `IOException`.

`Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories)` aborts the whole
walk on that node, and the natural `catch (IOException) → return []` around it then
reports **no transcripts at all** rather than "one folder was skipped". Pointing the
old locator at this root would have zeroed every token in the app, silently.

`TranscriptFileScan` walks directory by directory so a bad node costs only itself.
It also carries a visited-set and a depth ceiling, because a junction can point at
its own ancestor and make the tree infinitely deep.

This is not hypothetical: the same trap produced a false "there is no Cowork token
data anywhere" during the investigation, when a recursive scan with
`-ErrorAction SilentlyContinue` stopped early and the empty result was read as
evidence of absence.

## Measured impact

Deduplicated, across three Cowork sessions on one machine:

| | requests | tokens |
|---|---|---|
| Claude Code | 1,723 | 647,635,028 |
| **Cowork (was invisible)** | **155** | **13,696,086** |

## What is still not measurable

**Chat has no local usage record at all.** `claude.ai` (web or in the Desktop app)
persists conversation *content* — `IndexedDB\https_claude.ai_0.indexeddb.leveldb` and
`Local Storage` — but no token accounting anywhere. Confirmed by byte-scanning the
whole `%APPDATA%\Claude` tree for `output_tokens`, `input_tokens`,
`cache_read_input_tokens`, `usage_limit`, `tokensUsed`.

`plan-usage-history.json` holds only `{t, org, u:{fh, sd}}` — two integers per sample.
That is why the plan meters cover chat correctly while no token figure ever can.
Closing that gap would need the server-side usage API, i.e. credentials, which
[ADR-0007](../adr/0007-plan-history-primary-provider.md) and CLAUDE.md rule 3 rule out
for v1. Chat therefore stays a **labelling** problem, not an ingestion one.
