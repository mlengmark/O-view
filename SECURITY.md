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
| Credentials | **None.** v1 handles no token and performs no authentication (CLAUDE.md rule 3). |
| Network | One request, to `api.github.com`, to check for a newer release. Plus the installer download when a Windows user accepts an update. Nothing else. |
| Reads | Files Claude already writes on this machine — `~/.claude.json` (account fields only, never a token), plan-usage history, and session transcripts. **Read-only, always.** |
| Writes | Its own state under `%LOCALAPPDATA%\O-view` / `~/.local/share/O-view`, plus the run-at-startup entry when you enable it. |
| Telemetry | None. Nothing is sent anywhere. |

## Known and accepted

Stated here rather than left to be rediscovered.

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
