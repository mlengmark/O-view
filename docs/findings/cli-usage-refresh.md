# Finding: Claude Code refreshes its usage cache only on `/usage`, and doing so is free

**Measured:** 2026-08-28 · Claude Code `2.1.241`, Windows, account `organizationType: claude_pro`

`CachedUtilizationProvider` exists so a machine with **no Claude Desktop** can fill the two bars
from Claude Code's own cache ([cached-usage-utilization.md](cached-usage-utilization.md)). On the
development machine it could not, and this records why, what fixes it, and what that costs.

---

## 1. The defect

| | |
|---|---|
| `cachedUsageUtilization.fetchedAtMs` | 2026-08-24 02:17 — **4.43 days old** |
| `~/.claude.json` file mtime | 2026-08-28 12:23 — **12 minutes old** |
| Cached figures | `five_hour 14%`, `seven_day 81%` |
| Reality, `plan-usage-history.json` at 12:09 | `fh 44%`, `sd 35%` |
| Both `resets_at` | 2026-08-24 — four days past |

Three consequences, and the third is the defect:

1. **The file is written constantly; the usage block is not.** Project state and history update every
   session, so **mtime says nothing about this block's freshness.** `CachedUtilization` already reads
   `fetchedAtMs` rather than mtime and is correct to; anything added later must do the same.
2. **The stale figures are wrong in both directions** — session far too low, weekly far too high. Not
   drift; a different account state.
3. **CLAUDE.md's stale-window rule correctly rejects them**, because both `resets_at` have passed. So
   O-view reports *unknown*, and a Claude-Code-only machine sits there as its normal state — the very
   population this provider was added to serve.

Nothing in O-view is at fault. Claude Code simply does not refresh the block.

## 2. What refreshes it: `/usage`, and nothing else

| Invocation | `fetchedAtMs` moved |
|---|---|
| `claude --version` | no |
| `claude --help` | no |
| `claude doctor` | no — exits 0, non-interactive, safe |
| **an interactive session** | **no** — a session started that morning left the block 4.43 days old |
| `claude -p "<ordinary prompt>"` | **no** — reaches the model, and still does not refresh |
| **`claude -p "/usage"`** | **yes** — 24 Aug 02:17 → 28 Aug 12:50 |

**Starting Claude Code does not refresh it.** That is the whole explanation for a machine running
Claude Code daily and still holding a four-day-old block.

### `/usage` does not always advance it either

Measured by running the real refresher twice, five minutes apart:

| Run | Outcome | `fetchedAtMs` |
|---|---|---|
| 16:15:38 | `Refreshed` | 12:50:48 → 16:15:38 |
| 16:19:07 | **`Unchanged`** | 16:15:38 → 16:15:38 |

Claude Code has its own internal freshness window and served a cached answer rather than
re-fetching. **`Unchanged` is therefore an ordinary outcome, not a failure**, and must not be
treated as one — it is the expected result of asking twice in quick succession.

Two consequences for anything built on this. Only `Refreshed` should trigger a republish, because
nothing else changed anything. And the refresh floor should stay comfortably longer than whatever
that internal window is; fifteen minutes clears it with room to spare, which is one more reason not
to shorten it.

### The transcript's folder is named after the caller's working directory

Claude Code files each transcript under a project slug derived from the invoking process's working
directory. A run from a temporary path produced
`C--Users-…-Temp-claude-…-spawncheck` — a directory that means nothing to anyone who later finds it,
sitting in another application's data.

So the working directory is **chosen, not inherited**: `ClaudeCliRefresher.WorkingDirectory` points
at O-view's own data directory, which exists on both platforms, so the slug names O-view and a user
who finds the folder can tell what made it. Inheriting would be worse than untidy — for a
startup-registered tray app the working directory is whatever launched it, which differs between a
Start Menu launch, an autostart entry and a post-update relaunch through Explorer
([ADR-0010](../adr/0010-post-update-relaunch.md)).

## 3. Cost: zero — and the failure mode that is not

`claude -p "/usage"` produced a 6-line transcript holding only `queue-operation`, `user`, `system`
and `last-prompt` records. **No `requestId`, no assistant turn, no usage record.** Input, output,
cache-write and cache-read all zero. The slash command is handled locally and never reaches the
model.

**The contrast is the safety requirement.** An ordinary prompt through the same entry point cost
**49,094 cache-write + 97,456 cache-read + 470 output** for one trivial exchange, because Claude
Code rebuilds its entire context per invocation.

> **If `/usage` ever stops being recognised as a slash command, the string falls through to the model
> and every refresh costs ~50K tokens.** On a 15-minute beat that is ~4.8M tokens a day, spent to
> report usage.

So any implementation must: send exactly `/usage` and nothing else; treat unparseable output as
failure and back off rather than retry; and **assert the invocation produced no transcript record
carrying a `requestId`.** That check is cheap and is the only thing between the feature and a
runaway cost bug on a future Claude Code release.

*Measured the hard way:* `-p "/usage"` under Git Bash is path-mangled by MSYS into
`C:/Program Files/Git/usage` and goes to the model. Invoke without a shell, or escape it.

## 4. What a fresh block contains: freshness only, on this account

Populated: `five_hour` 70%, `seven_day` 38%, `nimbus_quill` 0%.

Empty **even when fresh**: `seven_day_cowork`, `seven_day_opus`, `seven_day_sonnet`,
`seven_day_oauth_apps`, `extra_usage`, `spend`, `member_dashboard_available`, and the rest.

**The per-surface Cowork meter is not there.** The field exists in the schema and carries nothing.
Caveat: this is a `claude_pro` account, and some of these plausibly populate where the corresponding
separate limit exists — `seven_day_opus` on Max, for instance. Empty here is not proven empty
everywhere. **Build nothing on them until an account is found where they fill.**

`limits[]` **is** populated, and carries Anthropic's own classification:

```
kind=session     group=session  pct=70  severity=normal  is_active=true
kind=weekly_all  group=weekly   pct=38  severity=normal  is_active=false
```

Two things worth taking. `is_active` marks which limit currently binds — a distinction O-view does
not make. And **`severity` disagrees with `UsageLevels`**: Anthropic calls 70% `normal` where
O-view's bands call it red. O-view's threshold is a deliberate user-facing warning choice, so this
is not a bug — but it should be a recorded decision rather than a surprise.

## 5. The `/usage` text output carries more

Beyond the cached block: request and session counts over 24h and 7d, a context-size distribution,
and a per-skill breakdown. It also carries Anthropic's own coverage disclaimer —

> "Approximate, based on local sessions on this machine — does not include other devices or
> claude.ai."

— which is almost exactly the labelling O-view needs for its own scope problem. Worth aligning to
as a phrasing precedent rather than inventing against. **Parsing that text is still TUI-shaped and
remains a fallback, not the plan**: the structured block is already parsed by
`CachedUtilizationProvider`, and spawning to *refresh* while reading the *file* keeps one parser.

## 6. Cadence — why not faster

Cost is zero, so cost is not the constraint. Two others are:

- **A 429 lands on the user's own credential.** ADR-0002 recorded the usage endpoint as
  [aggressively rate limited](https://github.com/anthropics/claude-code/issues/31637). A refresh
  triggered through Claude Code spends *Claude Code's* budget, so polling hard enough to be
  throttled would degrade the user's real CLI work to keep a tray icon current.
- **Nothing moves fast enough to matter.** `plan-usage-history.json` samples every **15 minutes**
  (measured; see §7), and the session meter moved ~8 points per sample under heavy load. A gauge
  showing an integer percent cannot express finer resolution.

**Prefer event-driven over timer-driven.** Refresh when the panel opens — that is when freshness is
observable — plus a slow background beat for the tray icon's threshold notification. Tie
`DefaultFreshness` to whatever floor is chosen so a reading is refreshed as it goes stale, and never
refresh while a Claude Code session is running, since it maintains the block itself.

## 7. Two ADR figures contradicted by measurement

- **Sampling cadence.** Three consecutive `plan-usage-history.json` samples were **exactly 15:00
  apart**. [ADR-0007](../adr/0007-plan-history-primary-provider.md) records a 5-minute series.
  **Record it as variable, not as a new constant** — it was 5 minutes in July and 15 in August, so a
  fixed figure invites the same error again. Reset-derivation precision claims rest on the finer one.
- **Transcript retention.** Ten Cowork registrations aged 30–90 days, oldest **41.9 days**, all still
  had their transcripts. [ADR-0006](../adr/0006-local-rollup-store.md) justifies the rollup store on
  Claude Code deleting transcripts at 30 days. The store may still be justified; that premise is not
  confirmed here.

## 8. Related

The register also carries unused per-session metadata — `model`, `completedTurns`, `isArchived` —
and `lastActivityAt` / `createdAt` / `lastFocusedAt` are **Int64 epoch milliseconds**, not ISO
strings. `CoworkSessionRegistry` already parses them correctly; anything new must not assume
otherwise.
