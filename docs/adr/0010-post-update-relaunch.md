# ADR-0010: Relaunch through Explorer after a silent update

- **Status:** Accepted
- **Date:** 2026-07-25
- **Deciders:** @mlengmark
- **Amends:** [ADR-0009](0009-auto-update.md) — the relaunch step of the in-app updater

## Context

[ADR-0009](0009-auto-update.md) gave the installer a `/update=1`-gated `[Run]` entry so that a silent in-app update relaunches O-view when it finishes:

```
Filename: "{app}\{#AppExeName}"; Flags: nowait; Check: WantsRelaunch
```

That works — the app does come back — but the relaunched process is a **child of the Inno Setup installer**, and therefore inherits the installer's token and environment.

A user reported that after updating, the popup showed **session and weekly % as "unknown"** while Claude Desktop was open and displaying 62% / 49%. Diagnostics captured **from the failing process** (via the Copy diagnostics menu item added in the same cycle):

```
status      : FileMissing
path        : C:\Users\<user>\AppData\Roaming\Claude\plan-usage-history.json
file exists : False
account file: read ok          <- the same process reads %USERPROFILE%\.claude.json
transcripts : 8 .jsonl found   <- and enumerates %USERPROFILE%\.claude\projects
```

So a single process saw a path that was **correct, present, and readable** as missing — while reading other files in the same user profile — and **never recovered** across hours of 60-second polls. (`File.Exists` returns `false` on access failure as well as absence; it swallows the exception, so the two are indistinguishable from the call site.)

What the investigation ruled out, each by measurement rather than reasoning:

| Hypothesis | How it was eliminated |
|---|---|
| Wrong org filter | v0.4.5 fixed a real cross-account filter bug; the symptom persisted |
| Claude Desktop replacing the file atomically | 11,393 polls over 6 min: **zero** missing, **zero** locked windows |
| Wrong path / `SpecialFolder` resolution | The failing process printed the **correct** path |
| The binary, or where it is installed | Same exe run from the install directory **and** from `%TEMP%`: both `status: Ok`, 756/756 samples |
| File permissions | ACL grants the user `FullControl`; not a junction or reparse point; Controlled Folder Access disabled |
| Claude Desktop or the data being wrong | File contents (`fh=62, sd=49`) matched the Desktop UI **exactly** |

What distinguished the failing instance was **how it was started**: it was the process the installer relaunched at the moment of the update. A fresh launch of the *same installed exe* resolved everything immediately:

```
refresh source=Live session=62 tooltip="5h: 62% · resets 17:10 · 7d: 49%"
```

## Decision

**Hand the post-update relaunch to the shell instead of launching the app directly from the installer:**

```
Filename: "{win}\explorer.exe"; Parameters: """{app}\{#AppExeName}"""; \
    Flags: nowait; Check: WantsRelaunch
```

`explorer.exe` re-parents the new process to the shell, so O-view starts with the ordinary interactive user context rather than whatever it inherits mid-install. Explorer returns immediately, so `nowait` still applies and there is no window flash.

Two supporting changes shipped alongside it:

- **The "no usage data" banner reports the observation, not a conclusion** — it names the path checked, and when Claude Desktop *is* running it tells the user to restart O-view, which is the verified cure. The previous text told a user with Desktop open to "install and run the Claude Desktop app".
- **`--diagnose <file>`**, handled *before* the single-instance mutex, so this state can be captured on a machine where O-view is already running without killing the live instance — the exact thing that made the original report hard to diagnose.

## Alternatives considered

| Option | Assessment |
|---|---|
| **Have the app detect the state and restart itself** | Self-healing is attractive, but the app cannot distinguish "file genuinely absent" from "I cannot see it" — `File.Exists` collapses both to `false`. A restart loop keyed on an ambiguous signal risks restarting forever on a machine that simply has no Claude Desktop. Rejected as a primary fix; the banner now tells the *user* to restart, which is the same remedy with a human in the loop. |
| **Don't relaunch at all; let the user start it** | Removes the trigger entirely and is trivially safe, but it undoes the part of [ADR-0009](0009-auto-update.md) users actually notice — the app coming back by itself after a silent update. Rejected as a regression in the feature's whole point. |
| **Re-resolve the path / retry with a delay** | Assumes the failure is transient. It is not: the process never recovered across hours of polling. Would have shipped a fix for a mechanism that measurement had already contradicted. |
| **`RestartApplications=yes` (Restart Manager)** | Lets Restart Manager bring the app back rather than an explicit `[Run]`. Rejected: it re-parents to the RM service instead of the shell, which is a *different* inherited context rather than a known-good one — swapping one unverified environment for another. |

## Consequences

**Positive**
- Post-update instances start in the same context as a Start Menu launch — the launch path that is demonstrably healthy.
- The failure mode is now **self-describing**: the banner names the path and the remedy, and `--diagnose` captures the state without disturbing a running instance.
- The diagnosis path generalises — the next "it shows nothing" report starts from a paste, not from a guess.

**Negative**
- **The mechanism is not proven.** The correlation (installer-relaunched instance fails; fresh instance of the same exe succeeds) and the cure (restart) are both reproduced, but *which* inherited token or environment property makes `File.Exists` fail was not isolated. This fix targets the trigger, not a root cause established at that level. If the symptom recurs on an Explorer-launched instance, this ADR is wrong and should be superseded rather than patched.
- Launching via `explorer.exe` is a **conventional Windows idiom, not a documented contract**. It is widely used to drop elevation and get a shell-context launch, but it is not an API guarantee, and it means the installer no longer gets a process handle for the app it started (acceptable — `nowait` never used one).
- **One more update cycle is needed to prove it out.** The relaunch after the update that *delivers* this change still runs the old installer; the fix only applies from the following update.
- If `explorer.exe` is unavailable or replaced by a third-party shell, the relaunch silently does nothing. The app is still installed and on the Start Menu, so the degradation is "you start it yourself" rather than a broken install.

## Verification (2026-07-25)

- Diagnostics captured from the failing process (above) and from fresh processes launched from both the install directory and `%TEMP%` (`status: Ok`, 756/756 samples) — the differential that isolated the process rather than the code.
- A fresh launch of the installed v0.4.7 exe logged `source=Live session=62`, matching the Claude Desktop UI (62% / 49%) exactly.
- ISCC compiles the new `[Run]` entry (release workflow's installer step, v0.4.8).
- Suite 177 green.
