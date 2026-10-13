# Smoke fixture: a Claude home

A small, hand-written Claude home that the smoke test (`tests/O-view.App.Tests/SmokeTests.cs`) runs the real engine against, through `UsageEngineOptions.ClaudeData`. It's laid out the way Claude Code and Claude Desktop write it on Windows.

| File | What it holds |
|---|---|
| `.claude/.claude.json` | The account (`oauthAccount`), using the all-zero placeholder organization |
| `AppData/Roaming/Claude/plan-usage-history.json` | Three plan samples ending at 2026-07-28 10:42Z: 25% session, 3% weekly |
| `.claude/projects/C--Users-sample-project/session-a.jsonl` | Transcript records: one request written twice (counted once), one more today, one yesterday, and a `<synthetic>` record that must be dropped |

What the smoke test expects, at 2026-07-28 10:47Z:
- **Meters:** 25% session and 3% weekly, from the plan history.
- **Output tokens today:** 4,000 (1,500 + 2,500; the duplicate isn't counted twice).
- **Output tokens over 31 days:** 8,000 (adds 4,000 from yesterday).

**Rules for editing:**
- Use only placeholder identifiers. CI's `identifiers` job rejects any UUID other than the allowed placeholders, and any request id matching `req_` followed by 8 or more letters or digits. That's why these ids are short (`req_s0001`).
- If you change a figure here, change the expectation in the smoke test and in this README in the same PR.
