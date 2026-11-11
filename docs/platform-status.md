# Platform status

As of 2026-11-11. The latest release is v1.3.1, and the newest report recorded here is the Linux sign-off of 2026-11-11 on the unreleased test build 1.3.2-trial.2. Anything below that names v1.2.1 or earlier has not been re-checked since. What has actually been seen working, on which platform, and what has not. This page exists so that the README, CLAUDE.md and the build plan can point at one record instead of each retelling it. Update it when a report lands; nothing else in the repository should restate it.

The distinction it keeps is between three claims that sound alike:

- **Seen working.** Someone ran it on that platform, in a desktop session, and reported back.
- **Fixed, not re-tested.** A fault was reported, the cause was found and fixed, and tests guard the fix, but nobody has run the fixed build in that situation since.
- **Never observed.** The code ships, its tests pass, and the package installs cleanly in a container, but a container has no desktop, so nobody has seen the surface itself.

Shipping something is not the same as having seen it work, and the app's own rule about never fabricating a number applies to claims about the app too.

## Windows 11

In daily use on the developer's machine since v0.1.0, and the platform every release has been exercised on by hand. Every surface in the README's platform table has been seen working except what the two subsections below list as not seen.

### The model limit bar

`Weekly · Fable` was checked on one real account: a Max account with a Fable limit, compared with Claude's own usage page. It is drawn on both heads, and on the Linux head it has been seen only in the rendered images the test suite compares. How the Linux panel wraps a very long model name has no test. An amount reported as spent past the plan has only ever been seen as zero; any amount above zero, and any currency other than US dollars, rests on tests and the names of the fields Claude Code writes.

### The v1.2.1 motion

The panel and the menu sliding, the folds, the tick and the tile flip were Windows only when this was signed off; the menu is still Windows only, because on Linux the desktop draws it. Linux has since gained the panel's motion, and what has been seen of it is under Linux below. Maximilian signed the motion off on a test build on 2026-11-06 with no notes. No agent tried the following on that build, and whether he did is not recorded:

- A real mouse pointer over the tiles, rows and menu. The agents used keyboard focus and raised clicks.
- Windows' animation setting switched off for real. The agents forced it off in a temporary build.
- Any display scale other than 100%, so whole-pixel movement at a fractional scale has not been seen.
- A taskbar docked at the top.
- A panel dragged away from the taskbar, which should slide out of its own window edge, wherever that is on screen.
- A tile flip inside the real panel, or in the light theme. The probe drove one tile in a plain window, in the dark theme only.
- A slow machine. The per-frame figures were taken on one PC.

What repeats is three command-line checks, `--menu-check`, `--fold-check` and `--motion-check`, which a person runs on a desktop; CI does not run them. `--toggle-check` and `--popup-check` have not been run on this release, because they cannot run while an installed O-view is open. Three small jumps were measured during review and left alone, and nobody looked for them on the test build: the panel's bottom edge lifting for one frame at the end of a closing fold, a menu dismissed while its threshold list is folding open growing past its docked edge, and a step of about 6 pixels at each end of the breakdown's fold.

## Linux

Supported since v0.6.0 (ADR-0012). Two hardware reports and a series of virtual-machine runs are the whole of what has been seen. No hardware report has landed since v1.2.1, and the 2026-11-11 sign-off below did not list that release's Linux change, so it has still not been seen on a desktop.

### Hardware reports

Both came from the same configuration: an Arch-based system, KDE Plasma on Wayland, tarball install.

| Report | Build | What it confirmed | What it found |
|---|---|---|---|
| First, 2026-08-18 | v0.6.1 | Every data path resolved; 5,645 of 5,645 plan-history samples parsed; Cowork logs and the account tier read; the tray icon appeared | The first left click deadlocked the app (#124); the menu took tens of seconds to open (#125) |
| Second | v0.6.5 | The same data resolution and the icon | The left click crashed the app outright with a segmentation fault (#143) |

Reading the code after the first report turned up a third fault nobody had hit: the panel dismissed itself when a compositor refused to focus it (#129).

### Where the four fixes stand

| Fault | Fixed in | Re-tested? |
|---|---|---|
| #124, the deadlocking click | v0.6.4 | Yes, by CI's `linux-runtime` job, which drives a real left click over a real session bus and a stand-in notification area under a virtual display |
| #125, the slow menu | v0.6.4 | Yes, by the same job, within its time budget |
| #129, dismissal on refused focus | v0.6.5 | No. The job has no live case for a compositor refusing focus; `PanelDismissalTests` guards it structurally |
| #143, the crash on the D-Bus thread | after the second report | No. In the CI job the click already arrives on the UI thread, so there is no wrong-thread moment to catch; the mocked `BusCallbackTests` guard it by choosing the thread themselves |

None of the four has been run on physical hardware since it was fixed.

### Virtual-machine runs

An Ubuntu 24.04 VM, GNOME on Wayland through XWayland, has had the panel opened five times, most recently on 2026-10-19. Seen working there: dragging the panel by its header and reopening where it was left; "Reset panel position"; moving it with Alt and an arrow key; a drop that lands off-screen, past an edge or within 16 px of the dock being pulled back onto the work area or snapped flush while the panel stays open; the tile flip, the graph's hover card, the off-plan section, and the Bars/Breakdown switch's labels.

Theme following was seen on the 2026-10-15 run: GNOME's default "no preference" stays dark, and an explicit prefer-light renders light (#316, a deliberate choice). An explicit prefer-dark and a live switch while the panel is open were not exercised.

One defect was seen and is open: the first Alt+arrow press right after clicking the header does nothing; the presses after it work.

A native Xorg GNOME session was run on 2026-10-15 against an earlier build and has not been re-tested against any fix made since.

### The 2026-11-11 sign-off

Maximilian signed off the Linux panel brought level with Windows on the Ubuntu 24.04 VM, on the test build 1.3.2-trial.2, at 08:23 UTC. His prompt had him confirm that he stopped the old copy, installed the build, and saw the version line say 1.3.2-trial.2, and that every check on the card held. He wrote no notes. Each of these was on the card he said Yes to:

- The panel slides up out of its edge when it opens and back when it closes.
- Clicking the tray icon again while the panel is open closes it.
- The block turns when "by model" or "totals" is pressed, and the Output | Overall switch slides and folds the table, without the panel jumping.
- The chart's bars rise and the doughnut rings sweep in when the panel opens.
- Hovering a chart bar or a model's name shows a card like the doughnut sections' cards.
- The right-click menu has "Notify at" with a tick on the setting in use, "Weekly reset…" opening a small window, and "Copy diagnostics".
- With animations switched off in the Ubuntu settings, all of the above still works and nothing moves.

The card made one check conditional: if his account showed the off-plan banner, Tab reaches its ×, Enter hides it, and it folds away. Whether his account showed the banner is not recorded, so that check is not called seen.

The card recorded only that this was the VM. It did not record the session type, the display scale, a physical machine, or whether the pointer was real, and none of those is claimed here. It had no check for the tray icon's first reading, the tooltip, notifications, a theme setting, or a window dragged away from the panel's edge, so those are unchanged below.

### Never observed

- The panel on a physical Linux desktop. Every panel observation above, motion included, is from the VM.
- The tooltip. On GNOME it cannot appear at all (the README explains why); what KDE Plasma does with it is not known, and neither hardware report mentioned it.
- Notifications.
- The tray icon showing real usage the first time it is drawn (v1.2.1). It waits up to 5 seconds for the first reading before it is put on the desktop, and shows the grey ring if none is ready. The fix is shown against a stand-in tray host and by CI's headless job, not on the GNOME desktop where a grey icon was reported, so whether the grey icon is gone there is not known. The cause is the best route found, not an observed one.
- The model limit bar on a physical or virtual Linux desktop; it has been seen only in rendered images.
- The theme switch and the single tray icon after the AppIndicator extension is toggled (v1.2.0), which are proven by tests and by CI's headless job.
- GNOME without an AppIndicator extension, other than in containers.
- The `.deb` package on hardware; both reports were tarball installs.
- An explicit prefer-dark theme setting.

### What CI proves instead

On every change the packaging workflow installs the `.deb` in clean Ubuntu 22.04, Ubuntu 24.04 and Debian 12 containers (plus Ubuntu 24.04 under arm64 emulation), extracts the tarball on Fedora, checks the declared dependencies resolve, starts the binary, renders its icons, confirms `--probe` reports rather than crashes without a session bus, and checks that `apt purge` leaves user data behind. The `linux-runtime` job runs the Linux head under a virtual display with a real session bus and a stand-in tray host, and proves a left click opens the panel and the menu answers within its budget. All of that is headless, which is why it proves the package is sound and not that a panel is legible on a desktop.

## Reporting back

A report from any Linux desktop moves rows on this page. The two commands that make a report useful in one round trip:

```bash
o-view --diagnose
```

```bash
o-view --probe
```

Neither contains a token or conversation content. Attach the output to a GitHub issue along with the desktop, the session type (Wayland, XWayland or Xorg) and how O-view was installed.
