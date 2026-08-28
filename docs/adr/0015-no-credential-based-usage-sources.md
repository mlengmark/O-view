# ADR-0015: No credential-based usage sources

- **Status:** Accepted
- **Date:** 2026-08-28
- **Deciders:** @mlengmark
- **Restates the basis of** CLAUDE.md rule 3 · **Closes** the `OAuthUsageProvider` question left open by [ADR-0002](0002-usage-data-providers.md) and deferred by [ADR-0007](0007-plan-history-primary-provider.md)

## Context

Rule 3 says v1 handles no credentials. It has held since [ADR-0007](0007-plan-history-primary-provider.md),
but **the reason it held has expired**, and the question has now been re-proposed twice in a single
planning session. It needs recording as a decision rather than surviving as an absence.

Three things changed.

**The token is no longer hard to find.** ADR-0002 called its location "the project's single largest
open risk"; the ADR index still carries *"Where does Claude Code store its OAuth token on Windows?"*.
Anthropic now [documents it outright](https://code.claude.com/docs/en/authentication):
`%USERPROFILE%\.claude\.credentials.json` on Windows, `~/.claude/.credentials.json` mode `0600` on
Linux, macOS Keychain — and `claude setup-token` mints a one-year token on demand. **Difficulty was
the load-bearing reason for deferral, and it is gone.** Anything that still forbids this has to
forbid it on other grounds.

**The policy position clarified.** Anthropic's Consumer Terms, clarified February 2026, prohibit
using OAuth tokens from Claude Free, Pro or Max accounts in any other product, tool or service. The
May 2026 reinstatement restored third-party *inference* metered against Agent SDK credits — a
spending mechanism, not a reporting channel, and not a licence to reuse subscription credentials for
account data. Evidence: [findings/api-usage-availability.md](../findings/api-usage-availability.md).

**The technique is well known, and will keep being proposed.** Credential-based approaches are
reachable and will be raised again, usually alongside arguments about how safely the credential
would be stored. Those are arguments about credential **handling**; the question that binds here is
**entitlement**. They are independent axes, and no amount of careful storage answers the second.

There is also no permitted substitute to reach for. Consumer plan-window usage has no public API;
the Admin API is unavailable to individual accounts and reports a different pool. So this decision
is not a choice between two routes — it is the recognition that **the only remaining route is the
prohibited one.**

## Decision

**O-view never handles a Claude subscription credential.** Not a webview login, not a token pasted
by the user, not a token or cookie read out of another application's storage, not a "sign in with
Claude" button, and not a keychain item.

Two source categories are permitted, and both keep the credential inside the vendor's own client:

| Permitted | Example |
|---|---|
| **Read a file the vendor's own client writes** | `plan-usage-history.json`, `~/.claude.json`, transcripts. Read-only, always |
| **Invoke the vendor's own approved client and read what it produces** | `claude -p "/usage"` — see [findings/cli-usage-refresh.md](../findings/cli-usage-refresh.md) |

The second is new and is what makes this ADR a decision rather than a restatement. It reaches
server-fresh data **without O-view ever holding a credential**: Claude Code authenticates itself, as
the client Anthropic approves, and O-view reads the cache it maintains. Measured cost: zero tokens.

`OAuthUsageProvider` is **deleted from the provider design**, not deferred. "Deferred" reads as
"planned"; this is closed.

The ADR index's open question *"What is the exact response shape of `/api/oauth/usage`?"* moves to
**Resolved: moot** — answering it would not make the provider permissible.

## Consequences

**Positive**

- The entire class of credential-leak risk stays out of the codebase. No DPAPI, no libsecret, no
  keychain, no diagnostics-redaction burden, no crash-dump exposure — none of it applies to a
  credential that is never held.
- **Rule 3 survives intact**, now on a durable basis rather than an expiring one.
- No user's account is exposed to action by using O-view.
- Zero-dependency and zero-network guarantees are preserved on the primary path.

**Negative**

- **Cloud-container Cowork and chat usage stay unattributable, permanently.** Measured directly on
  2026-08-28: a cloud Cowork session writes no registration and no transcript on the machine. The
  plan meters still include it — the bars remain correct — but the token and cost tiles cannot
  itemise it, and no permitted source can.
- **No credit balance.** The ADR index's open question about a balance source stays open, and is now
  expected to stay open.
- **Percentages only, and only as fresh as the vendor's client makes them.** The invocation route
  mitigates this but does not remove the dependency.
- O-view will be compared unfavourably to tools that show more, and the difference will not be
  visible to users as a policy choice. The README should say plainly what is not shown and why.

## Alternatives considered

| Option | Why rejected |
|---|---|
| **Read Claude Code's OAuth token** from `.credentials.json` or the keychain and call `/api/oauth/usage` | Prohibited by the Consumer Terms. The exposure lands on the user's account, not ours. Also brittle: the shape of that credential store is undocumented and has already changed within the 2.1.x line |
| **Read the `sessionKey` cookie** from browser stores and call `claude.ai/api/organizations/{id}/usage` | Same prohibition, plus a far larger privacy surface — decrypting a browser cookie jar to read one value |
| **`claude setup-token`** to mint a long-lived token | Still a subscription credential in a third-party product. Minting it deliberately is worse, not better |
| **Admin API with a user-supplied Admin key** | [Unavailable to individual accounts](https://platform.claude.com/docs/en/manage-claude/usage-cost-api), and reports API-credit consumption rather than plan-window usage. Serves neither the user nor the number |
| **Parse the `/usage` TUI output** | Permitted, but couples O-view to undocumented output formatting. Kept as a fallback only; the structured cache is already parsed |
| **Do nothing — accept stale caches** | The status quo, and it leaves Claude-Code-only machines showing *unknown* as their normal state |
