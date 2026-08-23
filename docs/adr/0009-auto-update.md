# ADR-0009: In-app auto-update via the GitHub release + existing installer

- **Status:** Accepted *(relaunch amended by [0010](0010-post-update-relaunch.md); Linux behaviour, the unified-release model, installer verification, and an opt-in automatic install amended below — 2026-07-30, 2026-08-02, 2026-08-03, 2026-08-18 and 2026-08-19)*
- **Date:** 2026-07-24
- **Deciders:** @mlengmark
- **Resolves:** [#18](https://github.com/mlengmark/O-view/issues/18) — "No Auto Update Function"

## Context

O-view ships new versions as tagged GitHub releases ([ADR-0008](0008-installer-distribution.md)), but a running instance has no idea a newer one exists. To upgrade, a user has to happen to revisit the releases page, download the installer, and re-run it. Issue #18 asks for two things:

1. The app should find the newest version and update to it **without the user manually downloading anything**.
2. A **"Check for updates"** item in the right-click menu, sitting **above "Exit O-view"**.

Constraints inherited from earlier decisions shape the solution space:

- **No code signing, ever** (README, [ADR-0008](0008-installer-distribution.md)) — so any framework that requires a signed update feed or a signed delta package is out.
- **Zero third-party runtime dependencies** ([ADR-0005](0005-native-tray-integration.md)) — the runtime footprint is the .NET BCL and SQLite, nothing else.
- **Per-user install, no elevation** ([ADR-0008](0008-installer-distribution.md)) — an updater must work without admin rights.
- The app already produces exactly the artifact needed to upgrade in place: **`O-view-Setup.exe`**, a per-user Inno installer whose Restart Manager integration already closes the running instance and replaces the exe.

## Decision

**Build the updater on the two things that already exist — the GitHub releases API and the installer — and add no new dependency.**

**Discovery (in `O-view.Core`, unit-tested).** [`UpdateCheck`](../../src/O-view.Core/Updates/UpdateCheck.cs) takes the running version and the JSON body of GitHub's `releases/latest` endpoint and decides `UpToDate` / `UpdateAvailable` / `Unknown`. Version comparison is a dedicated [`ReleaseVersion`](../../src/O-view.Core/Updates/ReleaseVersion.cs) — numeric per component (so `0.10.0 > 0.9.0`), tolerant of a leading `v` and a four-part assembly version, pre-release suffixes discarded. An `UpdateAvailable` is only returned when the release also carries the `O-view-Setup.exe` asset, so the offer is never dangling. All of this is pure and network-free, so it is covered by [`UpdateCheckTests`](../../tests/O-view.Core.Tests/UpdateCheckTests.cs).

**IO (in `O-view.Tray`).** [`UpdateService`](../../src/O-view.Tray/Updates/UpdateService.cs) does the HTTP GET (a shared `HttpClient` with a real User-Agent — the GitHub API rejects requests without one — and a 15 s timeout), downloads the installer to a temp file, and launches it. The running build learns its own version from the assembly version stamped at release time (`-p:Version=<tag>` in the release workflow).

**Applying the update.** For an **installed** build, the service runs `O-view-Setup.exe /SILENT /update=1` as an independent process and the app exits so it does not hold the exe locked. The installer's Restart Manager finishes closing the old instance, upgrades in place, and a new `/update=1`-gated `[Run]` entry relaunches the app (the normal post-install "Launch now" checkbox is a `postinstall` action and is skipped under `/SILENT`, so a normal user install never double-launches). For a **portable** build — a loose exe cannot overwrite itself while running, and re-running the installer would create a parallel install — the service opens the release page instead.

**Surfacing it.** A quiet background check runs ~30 s after launch and then daily; when a newer release exists it shows a **balloon once per version** and stops. It never downloads or installs on its own. The **"Check for updates…"** menu item (directly above "Exit O-view", per the issue) runs the same check interactively, always reports an outcome, and — on confirmation — performs the download-and-install. Keeping the actual fetch-and-execute behind an explicit, confirmed user action is deliberate: silently downloading and running an executable from the network in the background is exactly the behaviour a security-conscious user distrusts, and the confirmation costs one click.

## Alternatives considered

| Option | Assessment |
|---|---|
| **Squirrel.Windows / Clowd.Squirrel** | Purpose-built in-app updater with delta packages and background install. **Rejected:** it is a second packaging toolchain and runtime dependency ([ADR-0005](0005-native-tray-integration.md) chose zero), it wants its own `Setup.exe`/`RELEASES` feed rather than the Inno installer we already ship ([ADR-0008](0008-installer-distribution.md)), and its unsigned-update story is awkward. Heavy for a tray utility. |
| **Velopack** (Squirrel's successor) | Same shape of objection: a new dependency and a parallel release format duplicating the installer we already build. The delta-update upside is marginal for a single ~small exe. |
| **MSIX auto-update** | Clean OS-managed updates — but MSIX **cannot install unsigned** ([ADR-0008](0008-installer-distribution.md) rejected it for exactly this), so it is a non-starter here. |
| **Fully silent background auto-install (Chrome-style)** | Closest to a literal reading of "without the user doing anything," but it silently downloads and executes a network binary with no consent, and can restart the app under the user without warning. Rejected in favour of a background *notification* plus a one-click confirmed install. **Partly revisited on 2026-08-19** — still rejected as a default and still never silent, but permitted as an opt-in the user turns on; see the amendment at the end of this file. |
| **Download the portable exe and swap it in** | Avoids the installer, but a running single-file exe cannot replace itself, would need a helper process and hand-rolled file-swap/relaunch, and leaves the installed build's Start Menu entry and uninstall record stale. The installer already does all of this correctly. |
| **Documentation only** ("check the releases page")| Zero engineering; does not resolve the issue's actual request for in-app discovery and update. |

## Consequences

**Positive**
- Updates are **discovered in-app** and installed in **one confirmed click**, with no new dependency and no new release artifact — it reuses the releases API and the existing installer.
- The version-comparison and feed-parsing logic lives in `Core` and is **unit-tested** without a network.
- **Graceful when it cannot check:** any network/HTTP failure degrades to `Unknown` ("Couldn't check for updates") and the background check stays silent — a failed check never crashes or nags.
- Portable users are not left out: they are pointed at the release page rather than given a broken self-replace.

**Negative**
- **Requires the repository (and its releases) to be public.** The endpoint used is unauthenticated `releases/latest`; while the repo is private it returns 404 and every check degrades to `Unknown`. The feature therefore *activates when O-view goes public* (the `docs/generalize-*` work is preparing exactly that) and is inert, but harmless, until then. No token is added to make it work private — that would reintroduce credential handling the project avoids.
- **Unsigned installer, unchanged.** The downloaded `O-view-Setup.exe` triggers SmartScreen exactly as a manual download does; the updater does not and cannot bypass it. Integrity rests on HTTPS to `github.com` plus the release being built from this repo by CI — verify, don't trust (README).
- A **daily background HTTP request** to GitHub. Unauthenticated, well within the 60/hour anonymous rate limit, and skipped entirely on failure.
- One more moving part in the release pipeline: the workflow now stamps the assembly version from the tag (`-p:Version`) so the running build can compare itself. If that stamp is ever dropped, a release build reports `0.0.0` and thinks every release is newer — caught by the version showing in the "up to date" / update balloons.

## Verification (2026-07-24)

- `UpdateCheck` / `ReleaseVersion` covered by 30 unit tests (numeric comparison, 4-part assembly version vs 3-part tag, missing/duplicate installer asset, draft/prerelease, malformed JSON, unparseable current version). Full suite: 151 tests green.
- Installer `[Code] WantsRelaunch` gates the silent relaunch on `/update=1`; a normal install (no `/update`) skips it, so there is no double launch. Compiled by the release workflow's ISCC step.
- Live end-to-end (download + silent upgrade + relaunch) is exercisable only against a **public** release; to be confirmed on-device once the repo is public, tracked below.

## Amendment (2026-07-30): Linux does not self-update

Added with [ADR-0012](0012-linux-support.md) putting Linux in scope. Everything above still
describes the Windows behaviour and is unchanged.

### The decision

**A Linux build never replaces itself.** What it does depends on how it was installed:

| Install kind | On a newer release |
|---|---|
| Windows installer (`%LOCALAPPDATA%\Programs\O-view`) | Download and hand off, as above — **unchanged** |
| Windows portable exe | Open the release page |
| Linux `.deb` (apt/dpkg) | **Say a newer version exists, and stop.** `apt upgrade` does the work |
| Linux tarball | Open the release page |

Encoded once in `OView.App.Updates.UpdatePolicy`, so neither head can quietly disagree with
this table.

### Why an apt install must not self-update

Files installed by dpkg are owned by dpkg. An app that overwrites them is silently reverted
by the next `apt upgrade`, or leaves the package database describing a version that is no
longer on disk. Anthropic's own Claude Desktop for Linux ships through an apt repository and
does not self-update either — this follows the platform's convention rather than fighting it.

Such a build still **checks**, so it can tell the user a newer version exists. What it must
never do is download or execute anything.

### The asset-selection defect this closes

`UpdateCheck` matched a hard-coded `O-view-Setup.exe`. The moment one release carries both
platforms' assets, a Linux build would have found the Windows installer, reported
`UpdateAvailable`, downloaded it, and handed it to `Process.Start` with Inno Setup switches.

`Evaluate` now takes a `ReleaseAssetSelector` **supplied by the caller**, so Core still has no
knowledge of the running platform and stays a pure, unit-testable function. The published
names and the patterns that match them live together in `ReleaseAssets` — one decision, one
home, because the release workflow writes those names and the checker reads them
([#81](https://github.com/mlengmark/O-view/issues/81)).

`O-view-Setup.exe` is **frozen**: every installed Windows build looks for that literal string,
so renaming it would strand existing users on their current version with no way to be told.

### Deliberately not done

This assumes **unified releases** — every tag carrying both platforms' assets — which is the
recommendation in [#81](https://github.com/mlengmark/O-view/issues/81). Under that model
`releases/latest` is always the right thing to read, because there is always an asset for the
checking platform.

If platform-partial releases are ever adopted, two further changes become mandatory *before*
the first such release: walking `/releases` rather than reading `releases/latest` (otherwise a
Linux-only release hides an older Windows one and Windows installs stall silently), and a
distinct outcome for "no build for your platform" so it stops being indistinguishable from a
broken feed. Both are specified in #79 and neither is built, because building half of them
would be worse than building none.

## Amendment (2026-08-02): unified releases

Decided by @mlengmark alongside [#81](https://github.com/mlengmark/O-view/issues/81).

**Every tag carries every platform's assets**, whether or not that platform's code changed.
A release is therefore always complete: `O-view-Setup.exe`, `O-view.Tray.exe`, both `.deb`
architectures and both tarballs, or no release at all.

### Why, rather than publishing only what changed

It makes the update path correct **by construction**. `releases/latest` returns exactly one
release regardless of what is attached to it, so with unified releases there is always an
asset for whichever platform is asking — and `releases/latest` stays the right thing to
read.

Publishing only the changed platform breaks that silently. A Windows user on v0.5.11 facing
`[v0.6.1 (Linux only), v0.6.0 (both)]` reads v0.6.1, finds no `.exe`, and is told nothing —
never learning that v0.6.0 exists and applies to them. They stay a version behind
permanently, with no error to notice. That is why the two further changes specified in #79
(walking `/releases`, and a distinct "no build for your platform" outcome) remain
**deliberately unbuilt**: they are only needed under the model not chosen, and half of that
pair would be worse than neither.

The cost is cosmetic. A Linux-only fix prompts Windows users to reinstall an identical
binary. If that ever grates, the fix is to not rebuild an unchanged platform's asset — not
to publish a partial release.

### One version line, one tag namespace

Separate prefixes (`v*` for Windows, `linux-v*`) are not an alternative route to the same
place. `ReleaseVersion.TryParse` strips a leading `v` only, then truncates at the first
`-`, leaving `"linux"`, which fails to parse — so `Evaluate` returns `Unknown`. Combined
with `releases/latest` ignoring tag prefixes, the first `linux-v*` release would stop every
Windows client being offered updates. It fails safe, in that nothing bad is installed, but
invisibly, which is the worst way to find out.

### Asset names are one decision in two places

The release workflow writes them; `ReleaseAssets` matches them. The workflow asserts every
expected name is present before creating the release, because a rename on either side
otherwise surfaces as an app that quietly stops updating rather than as a build failure.

The Windows names carry no version or architecture and are **frozen** by compatibility; the
Linux ones carry both. That asymmetry is why asset matching cannot be a simple equality
test on both platforms.

---

## Amendment (2026-08-03): correcting "`apt upgrade` does the work"

Found at the v0.6.0 release gate ([#84](https://github.com/mlengmark/O-view/issues/84)), while
re-testing the [#79](https://github.com/mlengmark/O-view/issues/79) regression against the real
published release.

### What was wrong

The 2026-07-30 amendment's table says, of a `.deb` install: *"Say a newer version exists, and
stop. `apt upgrade` does the work."* **Both halves were untrue of the shipped code.**

1. **It did not say a newer version exists.** The Linux head never subscribed to the engine's
   `UpdateCheckDue` event, so it never checked at all. Worse, it *could* not have: an apt build
   was handed `ReleaseAssets.None`, and `UpdateCheck.Evaluate` only reports `UpdateAvailable`
   when the selector matches a published asset. A selector matching nothing returns `Unknown`
   forever. The design had conflated **detection** with **permission** — using "must never
   install this" to mean "must never recognise this".

2. **`apt upgrade` does not do the work.** The `.deb` installs no apt source, and there is no
   O-view repository for one to point at. `apt` cannot learn about a version it has no
   repository for, so it would never have offered the upgrade.

Together those meant a Linux user had **no update path whatsoever**: install once, and never
find out anything had shipped. Neither failure was visible, which is what made it survive to
the release gate.

### The correction

**Detection is separated from permission.** `UpdatePolicy.DetectionAsset` returns the asset a
build would actually install — the `.deb` for its architecture, the tarball for its RID — so
the build can *recognise* a newer release. `UpdatePolicy.MayDownloadAndRun` is unchanged and
remains the only thing deciding whether anything is fetched or executed. A `.deb` build now
sees the `.deb`, says so once, and still touches nothing.

The corrected table row:

| Install kind | On a newer release |
|---|---|
| Linux `.deb` (apt/dpkg) | **Notify once per version, and stop.** The user downloads and installs the next `.deb` themselves |
| Linux tarball | **Notify once per version, and stop.** The user downloads and extracts the next tarball |

"Once per version" is persisted (`TraySettings.LastUpdateNoticeTag`) rather than held in
memory: the check runs every 24 h in an app designed to run for days, and an in-memory flag
would re-nag after every restart.

**The notice must not say "run `apt upgrade`".** That command would report nothing to do, and
a user who ran it and saw nothing would reasonably conclude the notification was wrong — rule
6 applied to our own copy. It names the real step instead.

### What this does not change

Nothing about Windows. And nothing about the prohibition itself: a Linux build still never
downloads, extracts or executes anything. The guard is asserted in `LinuxUpdateNotice` rather
than assumed, so a later edit that made this head "helpfully" install something trips it
instead of shipping.

### Deferred, deliberately

Publishing a real apt repository would make the original sentence true, and is the better
end state. It needs signing keys, hosting and its own ADR, and it is not a prerequisite for
v0.6.0 — a notification the user can act on is a complete answer, just not the most convenient
one. Revisit if `.deb` downloads prove awkward in practice.

---

## Amendment (2026-08-18): the installer is verified before it is run

### What was missing

The original decision reasoned about *which* asset to install and *whether* a given build is
allowed to install it. It never said anything about whether the bytes that arrived are the
bytes the release published — and the implementation did not check. `DownloadInstallerAsync`
fetched `O-view-Setup.exe` and `LaunchInstaller` handed it to `Process.Start` with
`/SILENT /update=1`. Since the installer is deliberately unsigned (ADR-0008), nothing anywhere
in the chain offered a second opinion. The only integrity guarantee was "the bytes arrived
over TLS from api.github.com".

That is a real guarantee and it is not nothing. But it makes the release feed a single point
of trust for code execution on every installed Windows machine, and it costs almost nothing to
add a second check.

### The decision

The release workflow publishes `SHA256SUMS` alongside every asset, and the Windows head
verifies the downloaded installer against it before the installer is ever launched.

**It fails closed.** A missing manifest, an entry that does not parse, an asset the manifest
does not name, or a digest that does not match — each of these refuses the update. Falling
back to "install it anyway" when the manifest is absent would mean an attacker who can replace
the asset simply omits the manifest, and the check would buy nothing.

The cost of failing closed is real and is accepted: a release that forgets to publish
`SHA256SUMS` strands every user on their current version with no way to be told. That is the
same failure mode as renaming a frozen asset name, and it is guarded the same way — the
release job generates the manifest itself rather than staging it, and asserts both that it
carries an entry per asset and that `sha256sum -c` passes against the staged bytes.

Two smaller decisions travel with it, because they are the same question — *does the app
trust what the feed told it?*

- **The download URL is checked against an allowlist of GitHub hosts.** `browser_download_url`
  arrives inside the JSON and the app acts on it by executing what it fetches, so the URL is a
  trust decision, not a detail. `ReleaseDownloadUrl` requires https and an exact host match.
- **The temp filename comes from the parsed version, not the raw tag.** `ReleaseVersion.TryParse`
  truncates at the first `-` or `+`, so a tag of `v9.9.9-../../../../Startup/evil` parsed
  cleanly as 9.9.9 while `AvailableUpdate.Tag` kept the traversal segments — which then went
  into `Path.Combine` and decided where the downloaded executable landed.

### What this does not establish

The manifest ships from the same release as the asset it describes. **It proves the bytes are
the ones that release published; it does not prove the release is honest.** Whoever can
replace `O-view-Setup.exe` can replace `SHA256SUMS` beside it.

What it does defend against: tampering or corruption between the release and the user, a
partially-swapped asset set, and a truncated download that would otherwise have been executed.

The control that *does* cover a compromised account is provenance attestation — Sigstore-backed
`actions/attest-build-provenance`, verifiable with `gh attestation verify`, free for public
repositories. It is the better end state and it is deferred rather than rejected: it needs
`id-token: write` and `attestations: write` on the release job, which pulls against the
permission scoping done at the same time, and it deserves its own decision rather than being
smuggled in here.

### Nothing changes for Linux

A Linux build still downloads and executes nothing (see the 2026-07-30 amendment), so it has
nothing to verify. `AvailableUpdate.ChecksumsUrl` is populated for every platform because
detection is shared, and ignored by the head that never acts on it.

### On the failure message

A verification failure is **not** reported as "couldn't download", and it deliberately does not
open the releases page. Every other failure in this path means "try again by hand"; this one
means the file that arrived was not the file the release published, and routing the user to
download it manually would hand them exactly what the check rejected. `UpdateVerificationException`
exists to make that distinction catchable rather than left to a shared `catch`.

---

## Amendment (2026-08-19): an opt-in "Update automatically", default off

Requested by @mlengmark in [#140](https://github.com/mlengmark/O-view/issues/140).

### What is being reconsidered

The *Alternatives considered* table above rejects **"Fully silent background auto-install
(Chrome-style)"**:

> Closest to a literal reading of "without the user doing anything," but it silently downloads
> and executes a network binary with no consent, and can restart the app under the user
> without warning. Rejected in favour of a background *notification* plus a one-click
> confirmed install.

and the decision body states that keeping fetch-and-execute behind an explicit, confirmed
action is deliberate. This amendment does not pretend that was a mistake. It was the right
call for the **default**, and the default does not change.

**What it got wrong is treating "confirmed" and "per-release" as the same requirement.** The
objection is to acting *without consent*. A toggle the user deliberately turns on is consent —
given once, knowingly, about a standing behaviour, on a row that says what it will do. That is
the same consent model this app already accepts for "Run at startup", which likewise changes
what happens on the machine when nobody is watching.

Two things have also moved since 2026-07-24, and both narrow the original objection rather
than merely re-arguing it:

- **The installer is checksum-verified before it is launched, and fails closed** (amendment of
  2026-08-18). "Silently downloads and executes a network binary" is now "downloads a binary,
  refuses it unless it matches the digest the release published, and only then runs it". Still
  not proof the release is honest — that needs provenance attestation, still deferred — but no
  longer the same sentence.
- **The relaunch is a decided, debugged path** ([ADR-0010](0010-post-update-relaunch.md)),
  not an open question. "Restart the app under the user" was a real unknown when it was
  listed as a cost; it is now a mechanism with a known failure mode and a fix.

### The decision

**A "Update automatically" row is added to the tray menu, beneath "Run at startup", defaulting
to off.** When it is on, the daily background check does the download-verify-install itself,
skipping the confirmation dialog it would otherwise raise.

Four constraints travel with it, and none is optional.

**1. Off by default, and it stays a decision the user made.** A release must never turn this
on, and a build that finds no setting treats it as off. The whole justification for this
amendment is that the user chose it; a default would remove exactly the thing that makes it
acceptable.

**2. Offered only where it can actually work.** `UpdatePolicy.MayDownloadAndRun` is true for
`WindowsInstaller` alone. A portable exe cannot replace itself while running, and a `.deb` or
tarball build must never overwrite files it does not own. On those builds **the row does not
appear** — it is not shown disabled, and it is certainly not shown ticked. A menu row implying
a behaviour the build cannot perform is a fabricated claim about the machine, which rule 6
forbids as firmly for settings as for numbers.

`MayDownloadAndRun` remains the only thing deciding whether anything is fetched or executed.
This amendment adds a second condition on top of it; it does not weaken it, and it does not
touch `DetectionAsset` or the detection/permission separation the 2026-08-03 amendment drew.

**3. It announces itself before it acts, and it says what it did.** Automatic is not silent.
A balloon names the version being installed and warns that O-view will close and reopen,
*before* the installer is launched. The rejected alternative's worst property was
invisibility, not automation, and that half stays rejected.

**4. Every existing failure path is unchanged.** A checksum mismatch still refuses the update,
still says so, and still does **not** open the releases page — routing the user to download by
hand exactly what the check rejected remains wrong whether or not the flow was automatic. A
network failure still degrades quietly. `UpdateVerificationException` remains catchable
separately for precisely this reason.

### Where the setting lives

`TraySettings`, in `settings.json`, alongside the notification preferences — **not** the
registry. Run-at-startup is deliberately kept out of `TraySettings` because the `Run` key is
its single source of truth and Task Manager edits that key directly, so two stores could
disagree. Nothing outside O-view has an opinion about this preference, so that reasoning does
not carry, and inventing a second registry value would create the divergence it avoids.

### What this does not change

- **The default experience.** An untouched install still checks daily, notifies once per
  version, and installs nothing until asked. "Check for updates…" behaves exactly as before,
  including its confirmation.
- **Linux.** Neither build self-updates, so neither gets the row. The Linux head's menu
  carries *Run at startup* and *Exit* and gains nothing here. The 2026-07-30 and 2026-08-03
  amendments stand in full.
- **Windows portable.** Still sent to the release page.
- **Verification.** Unchanged, and now load-bearing for a flow with no human in it —
  see below.

### Consequences

**Positive**
- Answers the half of [#18](https://github.com/mlengmark/O-view/issues/18) that asked to
  update "without the user manually downloading anything" for users who want it, without
  imposing it on users who do not.
- The consent is more informed than the per-release dialog it replaces, not less: a dialog
  reading "Update now?" at a busy moment is approved reflexively, where a menu row is chosen
  deliberately.

**Negative**
- **Checksum verification becomes the only thing standing between the release feed and code
  execution on that machine.** With a confirmation dialog there was at least a human in the
  loop who could decline. There is now a class of user for whom a compromised release is
  installed with nobody watching, and that raises the priority of provenance attestation from
  "better end state" to something worth scheduling.
- The app can close and reopen while the user is working. Mitigated by the announcement in
  constraint 3, not eliminated.
- One more setting, one more menu row, and a menu whose contents now differ by install kind —
  the first time that has been true, and a thing the verification renders must cover.

### What would make this wrong

If a user reports that O-view updated itself when they did not believe they had asked it to,
this amendment has failed, and the failure will be in constraint 1 or 3 rather than in the
principle. Both are cheap to check and worth checking first.

## Amendment (2026-08-23): six-hour cadence with jitter, and a named rate limit

The original decision above costs "a **daily** background HTTP request to GitHub.
Unauthenticated, well within the 60/hour anonymous rate limit". Both halves of that sentence
have since been tested against reality, and each moved in a different direction.

**Twenty-four hours is too slow for the app this became.** O-view is designed to sit in a tray
for days, so the recurring check — not the launch check — is what most users actually rely on.
A release cut an hour after an instance's daily check is invisible to it for the next
twenty-three. That was reported directly: a machine running v0.6.11 had not noticed v0.6.12
ninety minutes after it was published, and would not have for most of a day.

**Ten minutes, the obvious correction, is worse.** Measured against the live API on
2026-08-23, and confirmed against GitHub's documentation:

- "The primary rate limit for unauthenticated requests is 60 requests per hour."
- "Unauthenticated requests are associated with the **originating IP address**, not with the
  user or application that made the request."

The budget is therefore **per address, not per user**, and is shared with every other
unauthenticated GitHub API caller behind the same NAT. At ten minutes each running instance
consumes 6/hour, so ten copies behind one office, lab or VPN exit node exhaust the address's
entire allowance — and leave nothing for anything else on it.

The usual mitigation does not reach us. Conditional requests are exempt only when
authenticated: "Making a conditional request does not count against your primary rate limit if
a `304` response is returned **and the request was made while correctly authorized with an
`Authorization` header**." O-view holds no credentials by [ADR-0007](0007-plan-history-primary-provider.md)
and rule 3, so an ETag would save bandwidth and buy no exemption. Anyone reaching for one
expecting relief from the rate limit should stop here.

### Decision

**Six hours, jittered ±15%, applied once at startup.** Four requests a day per instance, and a
worst case of six hours rather than twenty-four. One `releases/latest` response measured at
17,491 bytes, so the daily cost per instance is ~70 KB.

Jitter is the part that matters and is easy to skip. A fixed interval keeps instances that
started together — a mass reboot, a lab, a fleet behind one VPN — arriving in the same second
for as long as they run. A single offset at startup de-synchronises them permanently, which is
the whole objective; re-rolling per tick would add variance nobody benefits from.

**The launch check is unchanged at 30 s.** It is what catches a release cut while the machine
was off, it costs one request, and it is the half of the schedule that was never the problem.

### A rate limit is now a distinct outcome

The original decision's "graceful when it cannot check" degraded *every* HTTP failure to
`Unknown`. A GitHub throttle is a 403, so it landed there too — indistinguishable from a dead
network, with `x-ratelimit-reset` ignored and the next check retrying straight into the same
wall (issue #176). On a busy shared address that could persist indefinitely while the app said
nothing, which is rule 6 failing quietly.

`UpdateOutcome.RateLimited` now carries the reset instant. `ReleaseFeed` holds the cooldown so
neither head grows the logic, and the interactive message names the shared limit rather than
blaming the user's connection — for most people who hit this, the cause is not their machine.

A 403 must be corroborated by `x-ratelimit-remaining: 0` or a `Retry-After` before it counts:
GitHub uses 403 for causes a cooldown would not fix, and excusing those as "try again later"
would bury a real fault behind a wait.

**Negative**
- Four times the requests of the original decision. Still two orders of magnitude below the
  per-address allowance for a single instance, and the jitter is what keeps that true for a
  fleet.
- One more outcome for both heads to handle, and a cooldown that is invisible in the UI while
  it holds — a check skipped before the reset returns the same answer as the one that set it.

### What would make this wrong

A report of O-view being rate-limited on a normal home connection. At four requests a day that
should be unreachable, and would mean either the jitter is not being applied or something else
on that address is spending the budget — both worth knowing, and distinguishable now that the
log names the throttle instead of calling it a network failure.
