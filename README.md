# O-view

A system tray app for Windows 11 and Linux that shows how much of your Claude usage limit you have used, when it resets, and whether your work is drawing from the plan or billing as extra usage. It reads the files Claude's own apps keep on your machine. It handles no credentials and sends nothing anywhere.

**Current release: [v1.0.0](https://github.com/mlengmark/O-view/releases/latest)**, with a Windows installer, a portable Windows exe, `.deb` packages for amd64 and arm64, and portable Linux tarballs under one tag.

## What it shows

| Surface | What you see |
|---|---|
| Tray icon | A ring gauge filled in proportion to your 5-hour usage, with the brand "eye" at its centre and no digits. Green below 50%, amber from 50%, red from 70%. A grey, unfilled ring means O-view has no authoritative data, which is not the same as 0%. |
| Tooltip | The exact numbers: `5h: 47% · resets 16:32 · 7d: 61%`. When the data is stale it says so (`as of 14:05`); on fallback data it says `local estimate`; with nothing to read it says `no usage data`. |
| Detail panel | Left-click the icon. Your account, the session and weekly bars with their reset times, four stat tiles you can flip to a per-model breakdown, a tokens-used section, a 31-day graph, and an off-plan section. |
| Menu | Right-click the icon. Reset panel position, Run at startup, Update automatically, Notify at 70/80/90%, Weekly reset, Copy diagnostics, Check for updates, Exit. On Windows it is a flyout docked to the taskbar corner, matching the panel. |

A notification fires once per session window when usage crosses the threshold you chose. The default is 70%, the point at which the gauge turns red, so the colour and the notification agree out of the box.

### The detail panel

Both heads follow the desktop's light or dark theme, re-read every time the panel opens. On Windows the panel docks to the taskbar corner like a system flyout. On Linux it opens in the same work-area corner, and on X11 or XWayland you can drag it by its header, nudge it with Alt and an arrow key, and it reopens where you left it.

- **Account header.** Your display name, email and plan tier, read from Claude Code's own config file.
- **Session and weekly bars.** The percentage used and a reset time for each. The weekly reset is the exact instant Claude Code reports, stored once and projected forward. The 5-hour reset is derived by watching the percentage fall, so before the first drop the panel says the time is unknown rather than guessing, and a drop seen across a gap in sampling is shown with a `~`.
- **Four stat tiles.** Output tokens and their estimated value, today and over 31 days. The tiles headline output alone, because output is the only billed quantity that tracks work done; the rest is prompt re-sends against the cache. Click any tile to flip it to a per-model bar with the same colour per model across the whole panel. A window O-view has not been installed for the whole of says so: `12 of 31 days recorded`.
- **Tokens used.** Two bars split by token kind, today and over 31 days, with a hover card per segment and a breakdown table behind a switch. This is everything billed, where the tiles are output only.
- **Usage graph.** A bar per day for the last 31 days, with dotted gridlines at each weekly reset. Days before O-view's first recorded day are drawn empty, never as zero, because no data and no usage are different claims.
- **Off-plan usage.** The estimated API-rate value of usage on models that bill as extra usage rather than drawing from the plan, and a banner when the live detector sees substantial work against a flat plan meter or an exhausted window. The banner says whether your account has extra usage switched on, as Claude Code last reported it, rather than asserting a charge.
- **No usage data.** When the figures are unknown, the panel states what O-view checked and what it found, never something about your machine it has not verified.

Figures labelled **Est.** price tokens at published API rates. They are not money charged; within plan limits the marginal cost is zero. Where a model has no published rate, O-view sums what it can price and names what it left out.

### Why these totals are lower than Claude Code's own

Claude Code writes one line to a transcript per content block, and every line repeats the same `usage` object, so counting lines multiplies a response by the number of blocks it had. O-view keeps one row per request id. Measured on one machine, Claude Code's own stats file ran between 1.5 and 2.6 times O-view's figure for the same day, and the gap was duplicated lines rather than usage O-view missed.

### What O-view cannot see

Everything here is read from this machine, which is the same scope Claude Code states for its own usage summary. Work that runs elsewhere writes no local transcript and is absent from the tiles while still drawing on the plan window: cloud and remote agents, other machines, and claude.ai itself. Sub-agents run locally and are counted.

## Platform support

| | Supported since | Targets |
|---|---|---|
| Windows 11 | v0.1.0 | x64 |
| Linux | v0.6.0 | Ubuntu 22.04+ and Debian 12+ (`.deb`), x64 and arm64; portable tarball elsewhere |

The Linux targets mirror what Claude Desktop for Linux supports, since a machine that cannot run Claude Desktop has nothing for O-view to read. macOS is out of scope.

Supported does not mean identical. The table below says what each platform does; [docs/platform-status.md](docs/platform-status.md) says what has actually been seen working on Linux, which is less than what ships.

| | Windows 11 | Linux |
|---|---|---|
| Reading Claude's data | Yes | Yes |
| Tray icon | Yes | Yes, where the desktop has a notification-area host. GNOME ships none by default; see below |
| Tooltip | Yes | Not on GNOME. The exact figures are in the panel instead |
| Detail panel | Docked flyout at the taskbar corner | A window in the work area's corner, not docked to the icon |
| Right-click menu | All rows | Show usage, Reset panel position (X11 and XWayland only), Run at startup, Resume usage refresh when needed, Exit. Diagnostics and updates have command-line equivalents |
| Notifications | Balloon tip | Freedesktop notifications |
| Run at startup | Registry `Run` key, from the menu | XDG autostart file, from the menu or `o-view --startup-on` |
| Light and dark theme | Follows the app theme setting | Follows the desktop portal. GNOME's default "no preference" renders dark; prefer-light renders light |
| Auto-update | In place, after one confirmation, or unattended if you turn Update automatically on | Notifies once per version and installs nothing |
| Remembered panel position | Drag or Alt+arrow; reopens where left | The same on X11 and XWayland. A native Wayland session cannot place its own window, so it keeps the corner |

### Why the icon may not appear on GNOME

The Linux notification area is a protocol, StatusNotifierItem over D-Bus, and something has to implement the host side. KDE Plasma, XFCE and Cinnamon do. GNOME removed tray support on purpose. Install the **AppIndicator and KStatusNotifierItem Support** extension and O-view picks it up without a restart. Until then it tells you by notification that it is running but has nowhere to put its icon, because an app that is silently invisible looks exactly like a broken one. `o-view --probe` reports what it found on the bus.

### Why there is no tooltip on GNOME

Nothing publishes one and nothing would read one. Avalonia's tray backend answers the `ToolTip` property with an empty structure, and the AppIndicator extension GNOME needs before it has a tray at all has tooltips disabled in its own interface description. The panel carries the exact percentages instead. What KDE Plasma does with the empty tooltip is not known; neither report mentioned it.

### Why the Linux panel is not docked to the icon

StatusNotifierItem gives an application no way to learn where its icon was drawn, so the panel cannot appear under it. It opens in the work-area corner beside the panel bar, computed by the same rule the Windows flyout docks with. Placing a window is a request a window manager may ignore and a native Wayland compositor refuses, so O-view logs both the position it asked for and the one it got.

### No Snap or Flatpak

Both sandbox the filesystem, and O-view's whole job is reading files that belong to another application. Making that work under confinement is a project of its own.

## Where the numbers come from

O-view reads files Claude's own apps already keep and never writes to them. Three providers, with a composite that picks between them.

| Provider | Reads | Gives |
|---|---|---|
| Plan history | Claude Desktop's `plan-usage-history.json` | The 5-hour and 7-day percentages, sampled while Desktop runs |
| Cached utilization | The `cachedUsageUtilization` block in Claude Code's `.claude.json` | The same two percentages plus the exact weekly reset instant. This fills the bars on a machine with no Claude Desktop |
| Transcripts | Claude Code transcripts and Cowork audit logs | Token counts and the per-model breakdown, one row per request id |

The composite takes any live reading over a stale one, and a stale authoritative percentage over an estimate with none. Between the two primaries it takes whichever carries more meters, and among equals the one captured most recently. A snapshot is used whole; two sources are never merged field by field. When it is running on transcript data alone the panel says **local estimate**.

The plan bars cover all your Claude usage. The token tiles cover only usage that leaves a transcript on this machine, so a Desktop-only user sees a non-zero session percentage beside zero tokens.

### One thing O-view runs

Claude Code refreshes its cached percentages only when `/usage` runs, so on a machine without Claude Desktop they can be days stale. O-view runs `claude /usage` itself, when you open the panel and at most every 15 minutes in the background, then reads the file as it always has. Claude Code authenticates as itself; O-view holds no token. The command costs no tokens, and O-view checks after every run that it stayed free. If one is ever billed, the refresh stops itself and the menu offers a **Resume usage refresh** row.

### What O-view writes

Only to its own folder: `%LOCALAPPDATA%\O-view` on Windows, `~/.local/share/O-view` on Linux.

- `usage.db`, daily rollups per model, so the 31-day figures survive Claude Code deleting old transcripts. History from before O-view was installed cannot be recovered.
- `weekly-reset.json`, the weekly reset instant Claude Code reported, projected forward by whole weeks.
- `settings.json`, the notification threshold and whether automatic updates are on. Run at startup is not stored here; the registry key or the autostart file is the only record of it, so the two can never disagree.
- `panel-position.json`, where you left the panel, per display.
- `logs/oview.log`.

## Install

### Windows

Download `O-view-Setup.exe` from the [latest release](https://github.com/mlengmark/O-view/releases/latest) and run it. It installs per user with no admin rights, adds a Start Menu entry, offers to start at sign-in, and appears in Settings under Apps for a clean uninstall.

Prefer not to install? Download `O-view.Tray.exe` and run it from anywhere. It is self-contained. There is then no Start Menu entry; use the **Run at startup** row in the menu if you want it to persist.

The icon lands in the taskbar overflow by default; drag it onto the taskbar to pin it. Left-click opens the panel, right-click the menu, and clicking the icon again closes whichever is open.

**Updates.** O-view checks GitHub for a newer release and shows a balloon once when one exists; **Check for updates** in the menu does it on demand. An installed copy downloads the installer, upgrades in place after one confirmation, and relaunches. Tick **Update automatically** and it does this on its own, still telling you before it downloads. Portable copies are pointed at the release page.

**SmartScreen.** Neither the installer nor the exe is code-signed; a certificate costs more than a free tool justifies. Windows will warn on first run. More info, then Run anyway. Every release carries a checksum file and a Sigstore build attestation so you can verify what you downloaded came out of this repository's workflow.

### Linux

If you are on GNOME, including stock Ubuntu, read [Why the icon may not appear on GNOME](#why-the-icon-may-not-appear-on-gnome) first.

**Debian or Ubuntu:**

```bash
sudo apt install ./o-view_1.0.0_amd64.deb
```

Self-contained, no .NET runtime needed. It adds an application-menu entry and does not enable run at startup for you. `apt remove` leaves your settings and your weekly-reset anchor alone.

**To start with your session**, tick **Run at startup** in the menu or run:

```bash
o-view --startup-on
```

`--startup-off` disables it and `--startup-status` reports it. All three print the state as it stands after the write and exit non-zero if the write did not take. They work whether or not O-view is running, which matters on a desktop where the menu is not available.

**Anything else:** download the `.tar.gz`, extract it and run `./o-view`.

**Updates.** O-view tells you once when a newer release exists and installs nothing, because on a `.deb` install those files belong to your package manager. There is no apt repository, so `apt upgrade` will not find a new version. Download the next package from the releases page and install it the same way.

Every change installs the `.deb` in clean Ubuntu 22.04, Ubuntu 24.04 and Debian 12 containers and runs the tarball on Fedora. Those are headless, which proves the package is sound and not that the icon appears on your desktop.

## Troubleshooting

If the panel reads "no usage data" it says which path it checked and what it found. For the full report, right-click and choose **Copy diagnostics** on Windows, or run:

```bash
O-view.Tray.exe --diagnose report.txt
```

```bash
o-view --diagnose
```

The report names the platform, the resolved data roots, which locations were searched and how many transcripts were found. On Linux it also names the desktop, the session type, and whether a notification-area host was found, which answers "why can't I see the icon". It contains no token and no conversation content. Every flag is listed in [docs/cli.md](docs/cli.md).

## Versioning

Tags are `vMAJOR.LARGE.MINOR`: a different product generation, a substantial change, a fix. Higher is newer, compared number by number, so `0.6.10` is newer than `0.6.9` and `1.0.0` is newer than `0.99.99`. There is no cap on any number and no leading zeros. Releases are published in ascending order, because the updater reads the most recently published release. This is not semantic versioning: O-view is an application and nothing consumes it as an API.

## Building from source

Requires the .NET 10 SDK.

```bash
dotnet build
```

```bash
dotnet test
```

The Windows head:

```bash
dotnet publish src/O-view.Tray -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

The Linux head, the `.deb` and the tarball, with the same script CI runs (needs `dpkg-deb`, so a Debian-family machine or container):

```bash
./packaging/linux/build.sh 1.0.0 linux-x64 dist
```

On Linux, build the projects rather than the solution: `O-view.Tray` is WPF and does not build there. Everything else is portable, and CI tests it on both Windows and Ubuntu on every push.

Before opening a pull request run the full check, which builds, runs every test, renders both panels in every sample state and compares them pixel by pixel with `tests/golden`:

```bash
powershell -File tools/check.ps1
```

## Documentation

- [docs/architecture.md](docs/architecture.md): the layers and the rules the code follows.
- [docs/platform-status.md](docs/platform-status.md): what has been seen working on each platform.
- [docs/cli.md](docs/cli.md): every command-line flag.
- [docs/ui-spec.md](docs/ui-spec.md): the panel contract.
- [docs/adr/](docs/adr/): the decisions and their reasons.
- [docs/findings/](docs/findings/): what was measured about Claude's local data formats.
- [CLAUDE.md](CLAUDE.md): the rules any contributor, human or AI, works under.

## Licence

MIT. See [LICENSE](LICENSE).

Not affiliated with, endorsed by, or sponsored by Anthropic.
