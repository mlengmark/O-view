# Security policy

## Reporting a vulnerability

**Please do not open a public issue for a security problem.**

Report it through GitHub's private vulnerability reporting:
[**Report a vulnerability**](https://github.com/mlengmark/O-view/security/advisories/new).

That channel is private until an advisory is published, so a flaw in the update path can be
fixed and released before it is described publicly.

Expect an acknowledgement within a week. O-view is a free tool maintained by one person in
spare time — there is no bounty, and there is no on-call rotation. What there is: a fix or an
honest "I am not going to fix this, and here is why", rather than silence.

## Supported versions

Only the **latest release** is supported. O-view auto-updates on Windows and defers to the
package manager on Linux, so there is no back-porting: fixes ship in the next release.

## What O-view handles

Knowing what the app touches is usually enough to judge whether a report is in scope.

| | |
|---|---|
| Credentials | **None.** O-view handles no token and performs no authentication (CLAUDE.md rule 3, [ADR-0015](docs/adr/0015-no-credential-based-usage-sources.md)). |
| Network | Two, both unauthenticated GETs of public pages, sending nothing about you: `api.github.com`, to check for a newer release; and `platform.claude.com`, once a week, to check O-view's built-in price table against Anthropic's published one. Plus the installer download when a Windows user accepts an update. Nothing else **from O-view itself** — but see Subprocesses below. |
| Subprocesses | **One:** `claude /usage`, run at most every 15 minutes and when you open the panel, to make Claude Code refresh its own usage figures. O-view then reads the file, as it always has. See below. |
| Reads | Files Claude already writes on this machine — `~/.claude.json` (account fields only, never a token), plan-usage history, and session transcripts. **Read-only, always.** |
| Writes | Its own state under `%LOCALAPPDATA%\O-view` / `~/.local/share/O-view`, plus the run-at-startup entry when you enable it. |
| Telemetry | None. Nothing is sent anywhere. |

## Known and accepted

Stated here rather than left to be rediscovered.

- **O-view runs `claude /usage`, and that command talks to Anthropic.** O-view itself still sends
  nothing to Anthropic and still holds no credential — Claude Code authenticates as itself, using
  its own stored login, exactly as it does when you run the command yourself. What changed is that
  O-view now *causes* that request rather than only reading the file it leaves behind, and a
  network trace of the machine will show it. It exists because Claude Code refreshes those figures
  **only** when `/usage` runs, so a machine without Claude Desktop otherwise shows "unknown"
  indefinitely — measured at 4.43 days stale on a machine running Claude Code daily.
  It is on by default. Cost was measured at **zero tokens**, and O-view checks after every
  invocation that it stayed that way: if one is ever billed, the feature stops itself and offers a
  **Resume usage refresh** row rather than continuing.
  ([findings/cli-usage-refresh.md](docs/findings/cli-usage-refresh.md))
- **O-view checks its own price table against Anthropic's published one, weekly.** The "Est.
  value" figures price your tokens at published API rates, and that table has been wrong twice —
  once by 50% on one model — with nothing in the app able to notice. So once a week O-view fetches
  <https://platform.claude.com/docs/en/about-claude/pricing.md> and compares. It is the same kind
  of call as the release check above: no credential, no user data, a public page, and nothing sent
  about your machine or your usage. **It never installs a rate**: a difference is written to the
  log for a human to confirm, and a page that does not parse is reported as "could not check"
  rather than as agreement. ([ADR-0016](docs/adr/0016-published-reference-data-is-fetchable.md),
  [reference/pricing.md](docs/reference/pricing.md))
- **The Windows installer and executable are not code-signed.** An Authenticode certificate
  costs more than a free tool justifies ([ADR-0008](docs/adr/0008-installer-distribution.md)).
  SmartScreen will warn on first run. Integrity rests instead on the two checks below.
- **Diagnostics output is redacted, but still describes your machine.** The Copy diagnostics
  bundle replaces your account name in every path with `<user>` and truncates organization
  UUIDs to eight characters, so it carries no token, no conversation content, no account name
  and no full identifier. It does still show your directory layout, which Claude surfaces you
  use and how much data they hold — worth a glance before pasting it into a public issue, but
  it is no longer a disclosure.

## Verifying a release

Two things ship with every release, and they answer different questions.

**`SHA256SUMS`** answers *did my copy arrive intact* — it is published by the same pipeline as
the assets, so it detects corruption or tampering in transit but not a bad release.

```bash
sha256sum -c SHA256SUMS --ignore-missing
```

**Build provenance** answers *did this come out of O-view's release pipeline*. It is signed by
Sigstore against a GitHub OIDC identity that only a run of this repository's release workflow
can obtain, so it cannot be forged by replacing files on the release. Needs the
[GitHub CLI](https://cli.github.com):

```bash
gh attestation verify O-view-Setup.exe --repo mlengmark/O-view
```

Neither establishes that the source is trustworthy — only that the binary is the one this
repository's pipeline produced. Anyone who can push to the repository gets a genuine
attestation for whatever the pipeline builds.

## Out of scope

- An attacker who already has code execution as your user account. O-view installs per-user
  into a user-writable directory and holds no elevated privileges; someone in that position
  can replace the binary regardless.
- Findings in Claude Desktop, Claude Code or Cowork. Report those to Anthropic. O-view only
  reads the files they write.
