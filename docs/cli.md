# Command-line flags

O-view is a tray app, so almost everything happens from the icon. These flags exist for two reasons: to get a bug report out of a machine in one round trip, and to render surfaces offscreen so a change can be checked from an image rather than by watching a screen. Both heads parse flags the same way (`CommandLine` in `src/O-view.App`): a flag followed by a non-flag token takes it as a value, and flags are case-insensitive.

The Windows executable is `O-view.Tray.exe`; the Linux one is `o-view`.

## Diagnostics, either platform

| Flag | What it does |
|---|---|
| `--diagnose [path]` | Writes the support bundle: platform, resolved data roots, which locations were searched, transcript counts, the rollup store's state, the weekly-reset anchor and the tail of the log. Contains no token and no conversation content. On Windows a path is required, because a windowed app has no console. On Linux it prints to stdout unless a path is given. Runs without disturbing a running instance. |
| `--log <path>` | Where the running app writes its log, instead of the default file under O-view's data folder. |
| `--interval-ms <n>` | The full poll interval for this run, 50 ms or more; the default is 60 seconds. For watching a change land quickly. |
| `--startup-on`, `--startup-off` | Enable or disable run at startup and report the state as it stands after the write. On Windows this runs inside the live instance and logs the result; on Linux it runs before the single-instance guard, prints the state and the autostart file's path, and exits non-zero if the write did not take. |
| `--startup-status` | Linux only. Reports the autostart state without changing it. |
| `--probe` | Linux only. Asks the session bus whether a notification-area host exists and names the desktop and session type. This is the answer to "why can't I see the icon". |

## Offscreen renders

These run before the single-instance guard, need no display or bus, and exit when done, so they can be run beside a live instance. Each takes a directory and writes PNGs into it.

| Flag | Head | What it renders |
|---|---|---|
| `--samples <dir>` | both | The tray icon in every state (measured zero, 6%, 47%, 58%, 72%, 91%, 100%, estimate, no data), at each size the head draws, in both themes. |
| `--popup-samples <dir>` | Windows | The detail panel in every shared sample state, both themes, plus the short-display render and the graph's hover cards. These are the images `tools/check.ps1` compares with `tests/golden/windows`. |
| `--panel-samples <dir>` | Linux | The same panel states for the Linux head; compared with `tests/golden/linux`. |
| `--menu-samples <dir>` | Windows | The tray menu flyout in every state, both themes. |
| `--tile-samples <dir>` | Windows | The stat tiles across one, two, three, five-model and unpriced cases, both views and themes, plus the hover cards and a `hover-timing.txt` recording the tooltip timings that actually resolved. |
| `--dialog-samples <dir>` | Windows | Both dialog variants in both themes. A modal cannot be screenshotted by the run that would take the screenshot, so it has to be rendered. |

## Live checks, Windows

These drive the real surfaces on the real desktop and write a report, because the regressions they exist for have all been stateful: they appear on the second open, not the first, or they differ between what the logic reports and what is drawn.

| Flag | What it does |
|---|---|
| `--menu-check [report]` | Opens, toggles and closes the real flyout over about 25 seconds and writes what it found. Runs before the single-instance guard. |
| `--fold-check <dir>` | Plays the tokens-used disclosure fold on screen and writes a per-frame geometry trace with filmstrips grabbed from the compositor. Runs before the single-instance guard. |
| `--popup-check [report]` | Drives the real tray-click path to open the panel and reports the panel's state. Needs the assembled app, so it cannot run beside a live instance; if one is running it writes a report saying so rather than nothing. |
| `--toggle-check [report]` | Drives the icon's open, close, open sequence and reports the panel's state after each step. |
| `--show-popup`, `--popup-pin`, `--popup-theme light\|dark` | Opens the panel at once; pinned means auto-hide is off so a screenshot can be taken; the theme can be forced. |
| `--show-menu`, `--menu-pin`, `--menu-theme light\|dark` | The same for the menu flyout, which is the only way to check its docked placement against a live taskbar. |
| `--simulate-divergence <n>` | Feeds the real off-plan detector a simulated divergence so the banner states can be seen; the live state cannot be produced on demand. |
| `--check-updates` | Runs an interactive update check as if chosen from the menu. |
| `--test-notify` | Shows a test balloon notification. |
| `--stress <n>` | Refreshes the tray icon `n` times with GDI object counts sampled before and after, the leak self-check. |

`--restarted-native` is an internal flag the app passes to itself when it has to relaunch after a missing native library; it is not for use by hand.
