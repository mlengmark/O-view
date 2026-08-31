# ADR-0016: Published reference data is fetchable on the release feed's terms

- **Status:** Accepted
- **Date:** 2026-08-31
- **Deciders:** @mlengmark
- **Amends** [ADR-0009](0009-auto-update.md) (the outbound-call precedent) · **Does not touch** [ADR-0015](0015-no-credential-based-usage-sources.md) or CLAUDE.md rule 3

## Context

O-view's "Est. value" tiles price local token counts at Anthropic's published API rates. That
table lives in `ModelCatalog`, is maintained by hand, and **has been wrong twice**:

- [#255](https://github.com/mlengmark/O-view/issues/255) — every cache write priced at the
  5-minute rate (1.25× base input) while the transcripts were almost entirely 1-hour (2×).
  Cache-write value understated by 37.5% of its true amount.
- [#256](https://github.com/mlengmark/O-view/issues/256) — Sonnet 5 recorded at $3/$15 against a
  published $2/$10. Every Sonnet 5 figure 50% high.

Nothing in the app knew either. The table's currency lived in a prose comment — *"as cached
2026-06-24"* — invisible to every figure derived from it.

**The two failures defeat different checks, which is what shapes the answer.** #255 was a
published price collapsed into a constant; no amount of checking the table's *date* would have
found it. #256 was worse: the row recorded a price increase scheduled for 2026-09-01 that was
later cancelled, so **it was wrong on the day it was written**. A freshness mechanism that only
asks *how old is this table* would never have caught it either. The check has to compare values.

Comparing values means reading the published table, which means an outbound request. That was
previously assumed to need its own decision. Checking the code, it does not:

- [`ReleaseFeed.cs:24`](../../src/O-view.App/Updates/ReleaseFeed.cs) already makes an
  unauthenticated outbound HTTPS GET to `api.github.com`, on a jittered six-hour timer, through a
  15-second-timeout `HttpClient`.
- `SECURITY.md` already states it: *"One request, to `api.github.com`, to check for a newer
  release."*

A weekly GET of a public pricing page is **the same category of call**: no credential, no user
data, a public endpoint, nothing sent about the machine or its usage. It is an amendment to an
existing decision, not a new decision class.

## Decision

**Public reference data may be fetched on exactly the terms ADR-0009 already established for the
release feed**, subject to four constraints.

**1. Detect, never install.** The fetch returns a *difference list*, never a rate card. The
bundled table stays authoritative until a human confirms a change. This asymmetry is the whole
decision: a broken parser then produces a false "check pricing" line in the log, which is noisy
and harmless, where a parser that broke while *writing* rates would produce confident wrong money
— the exact failure this mechanism exists to catch.

**2. Failure is reported as failure.** Offline, timeout, non-success status or a page that did not
parse all return `null` — an honest "did not check". Reporting agreement because the request
failed is the one outcome that would make this worse than not having it (rule 6). A partial parse
is a failure, not a partial card.

**3. Weekly, and no faster.** Published rates change on the order of months; the failure this
guards against took two months to surface. One ~50 KB request per week, zero tokens, jittered
through the existing `UpdateSchedule.Jittered` so instances that start together do not stay in
step. A daily check would buy nothing but requests against a page nobody else is paying for.

**4. Nothing is persisted.** The result is re-derivable in one request. A last-checked timestamp
in memory plus the weekly timer is enough. `weekly-resets.json` exists because a missed weekly
reset costs a week to re-observe; nothing here has that property.

The implementation mirrors `ReleaseFeed` rather than inventing a second shape: same client, same
timeout, same swallow-and-report-unknown handling, same split between the IO (`RateCardFeed`, in
`App`) and the comparison (`PublishedRates`, in `Core`, testable against a fixture).

**Rule 3 and ADR-0015 are untouched.** They govern subscription credentials. None is involved
here, and this decision creates no route to one — a public documentation page is not an account
endpoint. `SECURITY.md` gains a row on the network line and an entry under *Known and accepted*.

**What this does not authorise.** Fetching *account* data of any kind, sending anything about the
machine or its usage, and any fetch that writes what it fetched into a figure the user sees. Those
would each need their own decision.

## Consequences

**Positive**

- The table's two failure modes are now both covered, and by different mechanisms: an `AsOf` date
  read as data covers ageing, and a value comparison covers a row that was wrong when written.
- The precedent is bounded and stated, so "can we fetch X?" has an answer rather than being
  re-argued each time.
- No new dependency, no new client shape, no new schedule primitive.

**Negative**

- **A second outbound host to state and defend.** Someone reading a network trace now sees two.
  The mitigation is that `SECURITY.md` names both and says what each is for.
- **The parse is against someone else's page.** It parsed cleanly when checked, and that is
  convenient rather than guaranteed. The floor — a header naming all five columns, and at least
  one model row — is deliberately crude, because the failure being guarded against is the page's
  shape changing rather than a row going missing. When it does change, the check goes quiet and
  says so, which is the right direction to fail.
- **A drift report is a log line, not a fix.** Someone has to read it and edit the table. That is
  the point of constraint 1, and it means a difference can sit unactioned between releases.
- **It catches rates only.** A wrong modifier, an unread field or a de-duplication fault is
  invisible to it. `CostEstimator.RelativeError` against a figure Claude Code reported catches all
  of those and is strictly better evidence — but it cannot be automated (no local file carries a
  reported dollar total), so the two are complements rather than alternatives. See
  [reference/pricing.md](../reference/pricing.md).

## Alternatives considered

| Option | Why rejected |
|---|---|
| **Fetch and install the published rates automatically** | A parser that breaks while writing rates produces confident wrong money, silently, on every user's panel. The whole value of the check is that its worst failure is noise |
| **Ask Claude Code for the rates** (`claude -p`) | A model asked for a price returns a plausible number with no provenance — precisely what rule 6 prohibits. And it is not free: an unrecognised argument through that entry point was measured at 49,094 cache-write + 97,456 cache-read + 470 output, ~$0.37–0.55 per call, spent against the user's own allowance |
| **Date-only freshness** — warn when the table is old | Necessary but not sufficient. It would not have caught #256, which was wrong on the day it was written |
| **Ship the rates as a user-editable JSON file** | A separate question, and a bigger one: a user-editable pricing file is a fabricated-number vector unless the panel names its provenance. `RateCardSource` is in place for it; nothing loads one today |
| **Do nothing — rely on manual review** | The status quo through both #255 and #256. Two months and a 50% error, with nothing in the app able to notice |
| **Check daily** | Published rates move on the order of months. Noise with no gain, against a page provided as a courtesy |
