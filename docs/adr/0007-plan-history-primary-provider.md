# ADR-0007: `PlanHistoryProvider` becomes the primary usage source

- **Status:** Accepted
- **Date:** 2026-07-20
- **Deciders:** @mlengmark
- **Amends:** provider precedence in [ADR-0002](0002-usage-data-providers.md) (that ADR's dual-provider principle and labelling rules stand)

## Context

[ADR-0002](0002-usage-data-providers.md) made `OAuthUsageProvider` the primary source, accepting three known liabilities: an undocumented endpoint, [documented aggressive rate limiting](https://github.com/anthropics/claude-code/issues/31637), and an **unresolved question about where the OAuth token is stored on Windows** — the project's single largest open risk.

Investigation of that question found something better. `%APPDATA%\Claude\plan-usage-history.json`, written by Claude Desktop, contains a 5-minute-interval time series of exactly the two values the UI needs:

```json
{ "t": 1784535700086, "org": "…", "u": { "fh": 5, "sd": 3 } }
```

`fh` = five-hour utilisation %, `sd` = seven-day utilisation %. **Claude Desktop already polls the OAuth endpoint and caches the answer.** Reset times are derivable from drops in `fh`, measured at exactly 5.00014 hours apart.

Full measurements: [findings/plan-usage-history.md](../findings/plan-usage-history.md).

## Decision

Add **`PlanHistoryProvider`** and make it the **primary** source. Revised precedence:

```
CompositeUsageProvider
 1. PlanHistoryProvider   ← primary: %APPDATA%\Claude\plan-usage-history.json
 2. OAuthUsageProvider    ← enhancement, if a token is ever located
 3. JsonlUsageProvider    ← fallback: token counts, labelled "local estimate"
```

Resolution: fresh plan-history sample (within staleness threshold) → OAuth if available → JSONL estimate → no data.

**Reset times** are derived by detecting decreases in `fh` of ≥2 points and anchoring `next = last drop + 5h`, re-anchoring on each new drop. Before any drop is observed, reset time is reported as **unknown**, never guessed.

`OAuthUsageProvider` is **deferred out of v1**, and the token-discovery spike is dropped from the critical path. It remains worth building later for credit balances and for users without Desktop.

> **Amendment (2026-08-28, [#236](https://github.com/mlengmark/O-view/issues/236)) — the sampling interval is not a constant, and 5 minutes is no longer what it does.**
>
> The Context above describes "a 5-minute-interval time series". **Measured on 2026-08-28: three consecutive samples exactly 15:00 apart** — 11:39:51, 11:54:51, 12:09:51 — and 939 samples over 30 days is consistent with the slower rate rather than the faster one.
>
> **Record the cadence as variable, not as a new constant.** It was 5 minutes in July 2026 and 15 minutes in August. Writing 15 into this ADR would repeat the error that made the amendment necessary; the interval belongs to Claude Desktop, changes without notice, and must be derived from the samples themselves wherever it matters.
>
> **This does not touch the figures that look similar.** The five-hour window measured at 5.00014 hours apart is a *window length*, not a sample interval, and is unaffected. So is the seven-day window length. Only the rate at which Desktop writes samples has changed.
>
> Two consequences sharpen. **"Data is only as fresh as Desktop's last sample" now means up to three times staler than this ADR assumed**, so the staleness labelling this section already calls "more important, not less" matters more again.
>
> And **the session reset bracket widens threefold**. An `fh` drop is bracketed by the samples either side of it, so a 15-minute interval triples that bracket's width. This lands on the **session** row only: [ADR-0014](0014-weekly-reset-is-a-reported-constant.md) superseded weekly derivation entirely, so the weekly reset is now a reported constant carrying zero uncertainty and its `~` is unreachable. The session window still rolls from first use and still derives, so its `~` and its narrowing by local activity are what this coarser sampling degrades. Anything that assumed a 5-minute bracket there should be re-read.

## Consequences

**Positive**
- **The project's largest open risk is eliminated.** The token question no longer gates anything.
- **No credential handling in v1.** The entire class of token-leak risk — CLAUDE.md rule 3, and the main driver for a pre-release security review — does not apply to the shipping code path.
- **No network calls and no rate limiting.** The `429` problem that made the endpoint "unusable for monitoring" is Desktop's problem, not ours.
- Authoritative percentages *and* derived reset times, entirely from local files
- Integer percent maps exactly onto the 2-digit tray icon
- Build order simplifies: no auth, no HTTP, no backoff state machine in v1

**Negative**
- **Hard dependency on Claude Desktop being installed and running.** Terminal-only users get the JSONL fallback with estimated figures. This is a real product limitation and must be stated in the README, not buried.
- Data is only as fresh as Desktop's last sample; staleness labelling becomes more important, not less.
- Reset time is **derived, not reported**. It is unknown until the first drop is observed — typically within one 5-hour cycle of install.
- Reading another application's private file. It is undocumented, `version: 2` proves the shape already changed once, and it could break without notice. Mitigated by defensive parsing and two working fallbacks. **O-view must never write to it.**
- Three providers to maintain instead of two.

## Alternatives considered

| Option | Why rejected |
|---|---|
| **Keep OAuth primary, use plan-history as fallback** | Inverts the risk. OAuth needs a token we cannot locate, hits documented rate limits, and requires handling a live credential — to obtain data already sitting in a local file. |
| **Use plan-history but keep the token spike on the critical path** | No longer justified. The spike blocks nothing now; it belongs in a later enhancement phase. |
| **Poll the endpoint ourselves to avoid depending on Desktop** | Duplicates work Desktop already does, and reintroduces every liability this ADR removes. |
