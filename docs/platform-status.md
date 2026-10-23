# Platform status

What has actually been seen working, on which platform, and what has not. This page exists so that the README, CLAUDE.md and the build plan can point at one record instead of each retelling it. Update it when a report lands; nothing else in the repository should restate it.

The distinction it keeps is between three claims that sound alike:

- **Seen working.** Someone ran it on that platform, in a desktop session, and reported back.
- **Fixed, not re-tested.** A fault was reported, the cause was found and fixed, and tests guard the fix, but nobody has run the fixed build in that situation since.
- **Never observed.** The code ships, its tests pass, and the package installs cleanly in a container, but a container has no desktop, so nobody has seen the surface itself.

Shipping something is not the same as having seen it work, and the app's own rule about never fabricating a number applies to claims about the app too.

## Windows 11

In daily use on the developer's machine since v0.1.0, and the platform every release has been exercised on by hand. Every surface in the README's platform table is seen working.

## Linux

Supported since v0.6.0 (ADR-0012). Two hardware reports and a series of virtual-machine runs are the whole of what has been seen.

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

### Never observed

- The panel on a physical Linux desktop. Every panel observation above is from the VM.
- The tooltip. On GNOME it cannot appear at all (the README explains why); what KDE Plasma does with it is not known, and neither hardware report mentioned it.
- Notifications.
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
