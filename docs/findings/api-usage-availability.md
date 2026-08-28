# Finding: what the Anthropic API will and will not tell us about usage

**Measured:** 2026-08-28 · **Cited by:** [ADR-0015](../adr/0015-no-credential-based-usage-sources.md)

Written so that nobody re-derives this, and so that a future assistant reaching for *"just call the
API"* is stopped by evidence rather than by opinion. Every claim below has a first-party source;
none of it comes from reading another usage monitor ([ADR-0004](../adr/0004-clean-room-provenance.md)).

---

## 1. The consumer plan meters have no public API

The 5-hour and 7-day utilisation percentages — O-view's two headline bars — are served by an
undocumented endpoint authenticated with a **Claude subscription OAuth token**. Claude Desktop and
Claude Code both call it; both cache the answer locally, which is why
[ADR-0007](../adr/0007-plan-history-primary-provider.md) and
[cached-usage-utilization.md](cached-usage-utilization.md) work at all.

There is no documented, supported endpoint that reports plan-window utilisation for a Pro or Max
subscription to a third party.

## 2. Using a subscription OAuth token in a third-party app is prohibited

Anthropic clarified this in **February 2026**, adding explicit authentication and credential-use
language: using OAuth tokens obtained through Claude Free, Pro or Max accounts in any other
product, tool or service is not permitted under the Consumer Terms of Service.

Anthropic's own guidance for third-party software is **API key authentication through the Claude
Console or a supported cloud provider**
([Authentication](https://platform.claude.com/docs/en/manage-claude/authentication)).

**The May 2026 reinstatement does not reopen this.** Third-party agent usage on subscriptions was
restored, but metered against a separate monthly *Agent SDK credit* pool billed at API rates. That
is a mechanism for **spending**, not for **reporting**, and it is not a licence to reuse a
subscription credential to read account data.

**Consequence:** `OAuthUsageProvider` is not deferred. It is prohibited. The ADR index's open
question *"What is the exact response shape of `/api/oauth/usage`?"* should move to **Resolved:
moot** — answering it would not make the provider buildable.

## 3. The Admin API is documented and usable — for a different account and a different number

[Usage and Cost API](https://platform.claude.com/docs/en/manage-claude/usage-cost-api):

- `GET /v1/organizations/usage_report/messages` — token counts (uncached input, cache read, cache
  creation, output), bucketed `1m`/`1h`/`1d`, groupable by model, workspace, API key, service tier,
  `inference_geo`, `speed`.
- `GET /v1/organizations/cost_report` — USD cost, `1d` buckets only, groupable by workspace or
  description. **Priority Tier costs are excluded**; track those through the usage endpoint.

Two constraints decide its role:

> **"The Admin API is unavailable for individual accounts."**

and the data is **API-credit consumption, not plan-window consumption** — a different pool from the
one the bars report. The development account reports `organizationType: claude_pro` (rule 8), a
personal subscription, and gets nothing from these endpoints.

Auth: an Admin API key (`sk-ant-admin01-…`), an OAuth token with `org:admin` scope, or a personal
or service-account key **not scoped to a workspace**. Workspace keys do not work.

Claude Enterprise organizations do not appear in the Console and hold no Admin keys; they use a
separate **Analytics API** with an Analytics key.

### Operational limits, if it is ever built

| | |
|---|---|
| Freshness | data typically appears within ~5 min of a request completing |
| Sustained polling | ~1 request per minute; cache for anything more frequent |
| Bucket caps | `1m` max 1,440 · `1h` max 168 · `1d` max 31 |
| Pagination | `has_more` + `next_page` |
| Etiquette | set a `User-Agent` identifying the integration |

The `1d` cap of 31 buckets happens to match O-view's existing 31-day window exactly.

## 4. Net effect on the three surfaces

| Surface | Local record | API available to us | Net |
|---|---|---|---|
| Claude Code | yes | no | unchanged |
| Cowork, local | yes | no | unchanged |
| Cowork, cloud container | **none** | **none** | still invisible |
| Chat | **none** | **none** | still invisible |

**No API tier closes any of the gaps in the token and cost tiles.** They are gaps in what Anthropic
exposes to third parties, not in how O-view reads it.

## 5. The token location is no longer the barrier — and never will be again

One distinction is worth stating before the rest of this section, because it is the one most often
elided: **reading a cache of an answer is not the same act as replaying a credential.**
`plan-usage-history.json` holds percentages — no token, no network call, nothing replayed. Reading a
stored credential and presenting it to Anthropic's servers attributes a request to the user's
subscription from software Anthropic has not approved. The two are different in kind, and only the
first is available to O-view.

[Claude Code's authentication docs](https://code.claude.com/docs/en/authentication) now document
credential storage outright: `%USERPROFILE%\.claude\.credentials.json` on Windows,
`~/.claude/.credentials.json` at mode `0600` on Linux, macOS Keychain — and `claude setup-token`
mints a one-year OAuth token on demand.

That **resolves ADR-0002's open question** *"Where does Claude Code Desktop store its OAuth token
on Windows?"*, which [ADR-0007](../adr/0007-plan-history-primary-provider.md) recorded as the
project's single largest open risk and then made moot by a different route.

It also removes the last non-policy reason not to build `OAuthUsageProvider`. **The decision now
rests on §2 alone, and must be argued there** — not on the token being hard to find, because it is
not.

## 6. What an Admin-key provider would have unlocked — and why it was rejected

For completeness, since the question will recur.

It would serve users who have a Console organization **and** spend on API credits: accurate,
cloud-side, machine-independent figures, for a population the local-file design serves not at all.

[ADR-0015](../adr/0015-no-credential-based-usage-sources.md) rejected it anyway, on reach and cost.
The short form: it is unavailable to the Pro/Max subscriber the app exists for, it reaches **none** of
the three gaps in §4, and it was the only proposed track carrying a downside — a credential, a
network dependency and rate limiting in an app whose stated advantage is having neither, a second
kind of number that rule 6 must continuously keep from being merged with the first, and new
failure states (401, expired, rate-limited, offline) that users file as bugs.

**Conditions that would justify revisiting:** Anthropic publishing a consumer usage endpoint for
third parties, or evidence that a meaningful share of O-view users hold Console organizations.
Neither is true today.

This section describes availability only. **The decision lives in [ADR-0015](../adr/0015-no-credential-based-usage-sources.md)**
— keep it there, so this file stays evidence rather than argument.

---

## Sources

- [Usage and Cost API — Claude Platform Docs](https://platform.claude.com/docs/en/manage-claude/usage-cost-api)
- [Authentication — Claude Platform Docs](https://platform.claude.com/docs/en/manage-claude/authentication)
- [Claude Code Analytics API](https://platform.claude.com/docs/en/manage-claude/claude-code-analytics-api)
- [Admin API keys](https://platform.claude.com/docs/en/manage-claude/admin-api-keys)
- [Claude Code — Authentication](https://code.claude.com/docs/en/authentication) — credential
  storage paths per platform, authentication precedence, `claude setup-token`
- Feb 2026 Consumer Terms clarification on third-party credential use, as reported by
  [The Register, 2026-02-20](https://www.theregister.com/software/2026/02/20/anthropic-clarifies-ban-on-third-party-tool-access-to-claude/5014546)
- May 2026 reinstatement and the Agent SDK credit pool, as reported by
  [VentureBeat](https://venturebeat.com/technology/anthropic-reinstates-openclaw-and-third-party-agent-usage-on-claude-subscriptions-with-a-catch)

The last two are secondary sources for a policy position; **re-check the Consumer Terms directly
before acting on §2**, since it is the claim the whole PDR turns on.
