# ADR-0009: In-app auto-update via the GitHub release + existing installer

- **Status:** Accepted
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
| **Fully silent background auto-install (Chrome-style)** | Closest to a literal reading of "without the user doing anything," but it silently downloads and executes a network binary with no consent, and can restart the app under the user without warning. Rejected in favour of a background *notification* plus a one-click confirmed install. |
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
