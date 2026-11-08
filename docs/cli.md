# Command-line flags

O-view is a tray app, so almost everything happens from the icon. These flags exist for two reasons: to get a bug report out of a machine in one round trip, and to render surfaces offscreen so a change can be checked from an image rather than by watching a screen. Both heads parse flags the same way (`CommandLine` in `src/O-view.App`): a flag followed by a non-flag token takes it as a value, and flags are case-insensitive.

The Windows executable is `O-view.Tray.exe`; the Linux one is `o-view`.

## Diagnostics, either platform

| Flag | What it does |
|---|---|
| `--diagnose [path]` | Writes the support bundle: platform, resolved data roots, which locations were searched, transcript counts, the rollup store's state, the weekly-reset anchor, where the session window starts and which source placed it, what Claude Code's cache held for model limits (`limits[]`: rows seen and skipped, then each model's name, percentage and reset, or why there are none) and for `spend` (the amount, currency and `enabled`, or whether it was absent or unreadable), and the tail of the log. Contains no token and no conversation content. On Windows a path is required, because a windowed app has no console. On Linux it prints to stdout unless a path is given. Runs without disturbing a running instance. |
| `--log <path>` | Where the running app writes its log, instead of the default file under O-view's data folder. |
| `--interval-ms <n>` | The full poll interval for this run, 50 ms or more; the default is 60 seconds. For watching a change land quickly. |
| `--startup-on`, `--startup-off` | Enable or disable run at startup and report the state as it stands after the write. On Windows this is an ordinary launch that writes the setting and logs the result, so the app carries on running afterwards, and it does nothing while another O-view is already running; on Linux it runs before the single-instance guard, prints the state and the autostart file's path, and exits non-zero if the write did not take. |
| `--startup-status` | Linux only. Reports the autostart state without changing it. |
| `--probe` | Linux only. Asks the session bus whether a notification-area host exists and names the desktop and session type. This is the answer to "why can't I see the icon". |

## Offscreen renders

These run before the single-instance guard, need no display or bus, and exit when done, so they can be run beside a live instance. Each takes a directory and writes PNGs into it.

| Flag | Head | What it renders |
|---|---|---|
| `--samples <dir>` | both | The tray icon in every state (measured zero, 6%, 47%, 58%, 72%, 91%, 100%, estimate, no data), at each size the head draws, in both themes. |
| `--popup-samples <dir>` | Windows | The detail panel in every shared sample state, both themes, plus the compact render, the Output/Overall switch caught mid-fold, the two short-display renders (`offplan-on-short`, and `offplan-on-models-short`, which only Windows has) and the chart's hover cards. These are the images `tools/check.ps1` compares with `tests/golden/windows`. It also asks every settled panel and every hover card whether anything is clipped, meaning it crosses the panel's edge, is cut to its slot by the layout, or has text that trims, writes the answer to `popup-edge-check.txt` and exits 1 on any miss. That exit code is what `tools/check.ps1` and CI's `visual` job fail on. |
| `--panel-samples <dir>` | Linux | The same panel states for the Linux head; compared with `tests/golden/linux`. |
| `--menu-samples <dir>` | Windows | The tray menu flyout in every state, both themes. |
| `--tile-samples <dir>` | Windows | The model face across one, two, three and four models, an unpriced model and a model with a long id, in both measures and themes, plus the hover card for a long model name and a `hover-timing.txt` recording the tooltip timings that actually resolved on a name cell. The cases are constructed, because a given machine's data may never produce them. The name is kept for the flag's history; the tiles are gone. |
| `--dialog-samples <dir>` | Windows | Both dialog variants in both themes. A modal cannot be screenshotted by the run that would take the screenshot, so it has to be rendered. |

## Live checks, Windows

These drive the real surfaces on the real desktop and write a report, because the regressions they exist for have all been stateful: they appear on the second open, not the first, or they differ between what the logic reports and what is drawn.

`--menu-check`, `--motion-check`, `--fold-check`, `--view-check` and `--still-check` run before the single-instance guard and work beside a running O-view. Every other flag in this section starts the whole app, so while another O-view is running it exits without a word, apart from `--popup-check`, which writes a report saying `NOT RUN`.

| Flag | What it does |
|---|---|
| `--menu-check [report]` | Opens, toggles and closes the real flyout over about 37 seconds (measured on 2026-11-06 at the current motion pace, so a retuned pace changes it) and writes what it found, ending with `RESULT: PASS` or `FAIL`. Runs before the single-instance guard. |
| `--fold-check <dir>` | Presses the real Output/Overall switch, plays the table's fold on screen and writes a per-frame geometry trace with filmstrips grabbed from the compositor. It also checks a second click: one mid-opening must end closed (closed here means the Output table's height, not zero), and one that reverses a close, landing mid-fold, must continue from the height on screen and end open. Then it turns the block from its totals to its models and back on a real panel, and checks that the two faces differ in height, that the panel's bottom edge stays on its docked corner after the turn and on every frame of it, and that the turn back lands on the first height. The report ends with `RESULT: PASS` or `FAIL`; with Windows' animation setting off the claims that need a fold or a turn in flight read `NOT RUN`. Runs before the single-instance guard. |
| `--motion-check [report]` | Turns the local block between its totals and its models and folds the menu's threshold list on the real windows, each to its end, then clicks a second time while the first transition is still running, reversing an opening and reversing a closing, and reads the state each rests in. For the block, each reversal lands while both faces are on show, and the check fails if either face jumps. Ends with `RESULT: PASS` or `FAIL`, or `RESULT: NOT RUN` when Windows' animation setting is off, since nothing is then ever mid-flight. Runs before the single-instance guard. |
| `--view-check [report]` | Presses the Output/Overall switch and the flip tag on a real panel, closes it with the fold still running, and reads the lit segment and the face on show from a panel built from the settings file alone, which is all a restart carries over, for each of the four views. It works on a copy of the settings file in the temp folder, never the reader's own. Ends with `RESULT: PASS` or `FAIL`. Runs before the single-instance guard. |
| `--still-check [report]` | Forces animations off and checks that the panel opens at full opacity at rest, that the switch and the flip leave nothing running, and that closing hides it at once. It forces the setting inside the check, so it runs on a machine whose Animation effects are on. Ends with `RESULT: PASS` or `FAIL`. Runs before the single-instance guard. |
| `--popup-check [report]` | Drives the real tray-click path to open the panel and reports the panel's state. Needs the assembled app, so it cannot run beside a live instance; if one is running it writes a report saying so rather than nothing. |
| `--toggle-check [report]` | Drives the icon's open, close, open sequence and reports the panel's state after each step. |
| `--show-popup`, `--popup-pin`, `--popup-theme light\|dark` | Opens the panel at once; pinned means auto-hide is off so a screenshot can be taken; the theme can be forced. |
| `--show-menu`, `--menu-pin`, `--menu-theme light\|dark` | The same for the menu flyout, which is the only way to check its docked placement against a live taskbar. |
| `--simulate-divergence <n>` | Feeds the real off-plan detector a simulated divergence so the banner states can be seen; the live state cannot be produced on demand. |
| `--check-updates` | Runs an interactive update check as if chosen from the menu. |
| `--test-notify` | Shows a test balloon notification. |
| `--stress <n>` | Refreshes the tray icon `n` times with GDI object counts sampled before and after, the leak self-check. |

`--restarted-native` is an internal flag the app passes to itself when it has to relaunch after a missing native library; it is not for use by hand.
