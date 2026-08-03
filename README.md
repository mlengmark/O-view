# O-view

A notification-area (system tray) app that displays your Claude AI token usage and the time remaining until your next usage-limit reset.

> **Status:** **Windows** is shipped and in use — current release [v0.5.11](https://github.com/mlengmark/O-view/releases/latest).
> **Linux** support is built but **not yet released**: it has never run on a physical Linux desktop, so the rows marked *unverified* in [Platform support](#platform-support) are exactly that. See [`docs/adr/`](docs/adr/) for the decisions that shaped the build.

---

## What it does

O-view sits in the notification area and answers three questions at a glance:

1. **How much of my Claude usage limit have I consumed?** (5-hour rolling window, and 7-day window)
2. **When does it reset?**
3. **Is my work actually drawing from the plan, or billing as extra usage?**

| Surface | Shows |
|---|---|
| Tray icon | A **ring gauge** filled in proportion to 5-hour usage, with the brand "eye" pupil at its centre — no digits. Colour-coded green (<50%) → amber (50–69%) → red (≥70%). A grey, unfilled ring means no authoritative data rather than a fabricated 0%. Geometry scales to the icon size, and the palette switches for a light or dark taskbar. |
| Tooltip | `5h: 47% · resets 16:32 · 7d: 61%` — the exact number lives here, since the icon carries no text. Degrades honestly: `(as of 14:05)` when the data is stale, `local estimate · usage % unknown` on fallback data, `no usage data` when there is none. |
| Detail panel | Left-click. Account header, session/weekly bars with reset times, four clickable stat tiles, a 31-day usage graph, and an off-plan section. Details below. |
| Menu | Right-click. Run at startup · notify-at-threshold toggle · Copy diagnostics · Check for updates… · Exit. Rendered as an on-brand flyout docked to the taskbar corner, not a Win32 context menu, so it matches the panel. |

A balloon notification fires once per session window when usage crosses the threshold — **70% by default**, the point at which the gauge turns red, so the notification and the colour never disagree.

### The detail panel

On Windows both flyouts dock to the taskbar corner like a system flyout and animate open and closed. On Linux the panel is a plain centred window and the menu is the desktop's own — see [Platform support](#platform-support) for why. Both follow the desktop's light/dark theme, re-read on every open, so switching never needs a restart.

- **Account header** — display name, email, and plan-tier badge, read from `~/.claude.json`. No token, no network call.
- **Session and weekly bars** — percentage, proportional fill in the same colour bands as the icon, and the derived reset time for each: `Resets in 2h 14m · 16:32` for the 5-hour window, `Resets in 6d 3h · Tue 06:28` for the weekly one. Reset times are *derived from observed drops*, not reported by any API. Before a drop has been seen the panel says so — the weekly row reads **"Waiting for first reset…"** — rather than guessing. A weekly reset that happened while Claude Desktop was closed is only bracketed to within a few hours, and is shown with a `~` and an explanation on hover instead of a made-up minute.
- **Four stat tiles** — tokens today, Est. value today, tokens over 31 days, Est. value over 31 days. **Click any tile to flip it to a per-model breakdown**: a segmented bar with a consistent colour per model across the whole panel, and per-model token and cost figures on hover. Nothing is fetched on click; the split is already in hand.
- **Usage graph — last 31 days** — daily bars with hover tooltips, and dotted gridlines at each **weekly limit reset** (falling back to Monday, labelled as such, until a reset has been observed). Because a reset happens at a time of day rather than at midnight, a gridline sits at its true position *inside* the day it falls in. Days before O-view's first recorded day are drawn as an explicit empty region, never as zero-height bars, because "no data" and "no usage" are different claims.
- **Off-plan usage — last 31 days** — the estimated API-rate value of usage on models that bill as extra usage (currently Fable) rather than drawing from the plan window.
- **Off-plan warning banner** — appears when the live divergence detector sees substantial local work against a flat plan meter, or when the plan window is exhausted. This exists because the tray once read a comfortable green 6% while ~€86 of credit usage was being billed; see [findings/credit-usage-divergence.md](docs/findings/credit-usage-divergence.md).
- **"No usage data" banner** — when the figures read unknown, the panel states *what* O-view checked and *what it observed*, rather than asserting anything about your machine it hasn't verified.

Figures labelled **Est.** price tokens at published API rates. They are not money charged — within plan limits the marginal cost is zero. Where a model has no published rate, O-view sums what it can price and names the rest (`excludes claude-x (no published rate)`) instead of blanking the tile.

## Platform support

**Windows 11** and **Linux** (Ubuntu 22.04+ / Debian 12+, x64 and arm64 — deliberately the
same matrix [Claude Desktop for Linux](https://code.claude.com/docs/en/desktop-linux)
supports, since a machine that cannot run Claude Desktop has nothing for O-view to read).
**macOS is out of scope.** See [ADR-0012](docs/adr/0012-linux-support.md) and
[ADR-0013](docs/adr/0013-linux-ui-toolkit.md).

**The two are not identical, and this table does not pretend they are.**

| | Windows 11 | Linux |
|---|---|---|
| Tray icon | ✅ | ⚠️ **Needs a notification-area host.** GNOME ships none by default — see below |
| Tooltip | ✅ | ✅ built, *unverified on hardware* |
| Detail panel | ✅ docked flyout at the taskbar corner | ⚠️ a **plain centred window**, not docked — see below |
| Right-click menu | ✅ full: startup, notifications, diagnostics, updates, exit | ⚠️ **Exit only** so far |
| Notifications | ✅ balloon tip | ✅ freedesktop notifications, *unverified on hardware* |
| Run at startup | ✅ registry `Run` key | ✅ XDG autostart `.desktop` |
| Light/dark theme | ✅ follows `AppsUseLightTheme` | ✅ XDG desktop portal, *unverified on hardware* |
| Auto-update | ✅ in-place, one confirmation | ⚠️ **notifies only, by design** — tells you once per version, installs nothing ([ADR-0009](docs/adr/0009-auto-update.md)) |

*"Unverified on hardware" means the code exists and passes its tests, but nobody has yet run
it on a physical Linux desktop. It is recorded that way rather than ticked, because claiming
otherwise would be exactly the kind of unearned assertion the rest of this app refuses to
make.*

### Why the icon may not appear on GNOME

The Linux notification area is a protocol — StatusNotifierItem, over D-Bus — and something
has to implement the *host* side. KDE Plasma, XFCE and Cinnamon do. **GNOME does not, by
default**, having removed tray support deliberately.

Installing the **AppIndicator and KStatusNotifierItem Support** extension adds one, and
O-view picks it up without needing a restart. Until then it will tell you, via a desktop
notification, that it is running but has nowhere to put its icon — because an app that is
silently invisible is indistinguishable from a broken one.

O-view asks the session bus rather than trusting the toolkit, which reports success either
way ([findings](docs/findings/linux-tray-spike.md)). `o-view --probe` reports what it found.

### Why the Linux panel is not docked

SNI provides no way for an application to learn where its own icon was drawn — there is no
`Shell_NotifyIconGetRect` equivalent — and under Wayland a client generally cannot position
its own surface at all. Approximating the Windows docking would put the panel in the wrong
place most of the time, which reads as broken; a plainly-centred window does not.

### No Snap or Flatpak

Both sandbox the filesystem, and O-view's entire function is reading files that belong to
*another* application under `~/.config/Claude` and `~/.claude`. Making that work under
confinement is a project in itself, not a packaging option.

## Provenance — clean-room

O-view is inspired by the *product concept* of macOS menu-bar apps that track AI token usage.

**No third-party usage-monitor code has been read, copied, adapted, or consulted.** Those reference apps are macOS/Swift; O-view is an independent .NET implementation written from scratch against platform documentation and locally observed data formats. See [ADR-0004](docs/adr/0004-clean-room-provenance.md) for the full policy and its practical rules.

## Data sources

O-view handles **no credentials and makes no API calls for usage data.** Everything comes from files Claude's own apps already keep on disk. It reads from two independent providers and falls back gracefully:

| Provider | Role | Reads | Gives |
|---|---|---|---|
| `PlanHistoryProvider` | Primary | `%APPDATA%\Claude\plan-usage-history.json` on Windows, `~/.config/Claude/plan-usage-history.json` on Linux — read-only, the file belongs to Claude Desktop | Authoritative 5-hour and 7-day % utilisation, plus reset times derived from observed drops. Observed weekly resets are logged to O-view's own data directory, since the source file's retention is shorter than a user's history |
| `JsonlUsageProvider` | Fallback | Claude Code transcripts under `%USERPROFILE%\.claude\projects` / `~/.claude/projects`, plus Cowork audit logs | Token counts and per-model breakdown, de-duplicated by `requestId` |

`CompositeUsageProvider` resolves between them by information value rather than list position: any live snapshot beats a stale one, and stale authoritative percentages beat an estimate carrying no percentages at all. The winning snapshot keeps its own label, so the UI shows a visible **"local estimate"** badge whenever it is running on fallback data. A provider that throws is treated as "no data" and the chain falls through rather than blanking the display.

> **The two measure different things,** and the panel says so. The plan bars cover *all* Claude usage; the token tiles only cover usage that leaves a Claude Code transcript. A Desktop-only user therefore sees a non-zero session % beside 0 tokens.

`OAuthUsageProvider` appears in some earlier design docs as the intended primary source. It was **deferred out of v1 and has not been built** — the local file solves the problem without a token ([ADR-0007](docs/adr/0007-plan-history-primary-provider.md)). ADR-0002 is superseded on this point.

O-view polls every 60 seconds (the underlying file only updates every ~300 s), with a fast 3-second warm-up cadence for the first two minutes after launch so a start that beats Claude Desktop to the punch still fills the bars within seconds.

### What O-view writes

Only to its own directory — `%LOCALAPPDATA%\O-view\` on Windows, `~/.local/share/O-view/` on Linux. **Never to Claude's own files**, which are read-only to O-view on both platforms:

- `usage.db` — SQLite daily rollups per model. Claude Code deletes transcripts at 30 days, so the 31-day figures need their own store ([ADR-0006](docs/adr/0006-local-rollup-store.md)). Ingestion is idempotent.
- `settings.json` — notification preferences. Run-at-startup is not stored here; the registry `Run` key (Windows) or the XDG autostart `.desktop` file (Linux) is its single source of truth, so the two can never disagree.

## Install and run

### Linux

> **Read [Why the icon may not appear on GNOME](#why-the-icon-may-not-appear-on-gnome) first
> if you are on GNOME** — including stock Ubuntu. Without an AppIndicator extension there is
> nowhere for the icon to go, and that is a property of the desktop, not a bug in O-view.

**Debian / Ubuntu** — download `o-view_<version>_amd64.deb` (or `_arm64.deb`) from the
[latest release](https://github.com/mlengmark/O-view/releases/latest):

```bash
sudo apt install ./o-view_0.6.0_amd64.deb
```

Self-contained: no .NET runtime needed. It adds an application-menu entry; run-at-startup is
a toggle inside the app, not something the package decides for you. `apt remove` leaves your
settings and — importantly — the weekly-reset log alone, since that one is unrebuildable.

**Anything else** — download the `.tar.gz`, extract, and run `./o-view`. No installation.

**Staying up to date on Linux.** O-view checks for a newer release and tells you once per
version — then leaves it entirely to you. It never downloads or replaces anything, because on
a `.deb` install those files belong to your package manager and overwriting them would be
undone by the next `apt upgrade`.

> There is **no O-view apt repository**, so `apt upgrade` will not find a new version by
> itself. Updating means downloading the next `.deb` or tarball from the releases page and
> installing it the same way you did the first time. This is the one part of the Linux
> experience that is genuinely worse than the Windows one, and it is stated rather than
> glossed.

On every change, the packaging workflow installs the `.deb` in clean **Ubuntu 22.04, Ubuntu
24.04 and Debian 12** containers (plus Ubuntu 24.04 under arm64 emulation) and extracts the
tarball on **Fedora**. Each run checks that the declared dependencies resolve, that the binary
starts and renders its icons, that `--probe` reports rather than crashes when there is no
session bus, and that `apt purge` leaves your data behind.

Those are headless containers, so they prove the package is *sound* — not that the tray icon
appears on your desktop. That is the distinction the ⚠️ rows above are recording.

### Windows

**Recommended — the installer.** Download `O-view-Setup.exe` from the [latest release](https://github.com/mlengmark/O-view/releases/latest) and run it. It installs per-user (no admin rights), adds an **O-view** entry to the Start Menu so you can relaunch it any time, and offers a "start automatically when I sign in" option so it survives reboots. It appears in *Settings → Apps* for a clean uninstall. See [ADR-0008](docs/adr/0008-installer-distribution.md).

**Portable alternative.** Prefer not to install? Download the standalone `O-view.Tray.exe` and run it from anywhere — it is self-contained, no .NET required. Note there is then no Start Menu entry; use the right-click **Run at startup** toggle if you want it to persist.

Either way: the icon lands in the taskbar overflow flyout (the `^` chevron) by default; drag it onto the taskbar to pin it. Left-click opens the panel, right-click the menu; clicking the icon again closes what is open.

**Staying up to date.** O-view checks GitHub for a newer release in the background and shows a one-time balloon when one is available; right-click → **Check for updates…** at any time. For an installed copy it downloads `O-view-Setup.exe`, upgrades in place, and relaunches — all after a single confirmation. Portable copies are pointed at the release page. See [ADR-0009](docs/adr/0009-auto-update.md) and [ADR-0010](docs/adr/0010-post-update-relaunch.md).

> **SmartScreen, honestly:** neither the installer nor the executable is code-signed — a certificate costs more than a free tool justifies, which is also why there is no MSIX package (MSIX cannot install unsigned). Windows SmartScreen will warn on first run ("Windows protected your PC"); *More info → Run anyway* proceeds. The source is in this repository, and both the installer and the binary are built from it by the GitHub Actions workflow — verify rather than trust.

## Troubleshooting

If the panel reads "no usage data", it will state which path it checked and what it found there — what it *observed*, never an assertion about your machine it hasn't verified.

On Windows, right-click → **Copy diagnostics** puts the full report on the clipboard. Either platform can produce it without disturbing a running instance:

```bash
O-view.Tray.exe --diagnose report.txt
```

```bash
o-view --diagnose
```

The report names the platform, the resolved config and data roots, which locations were searched, and how many transcripts were found. On Linux it also reports the desktop, the session type, and **whether a notification-area host was found on the bus** — which is the answer to "why can't I see the icon". For just that question:

```bash
o-view --probe
```

It contains no token and no conversation content.

## Building from source

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

The Linux head, and the `.deb` and tarball around it — the same script CI runs, so a local
build is the artefact, not an approximation of it:

```bash
./packaging/linux/build.sh 0.6.0 linux-x64 dist
```

It needs `dpkg-deb`, so it runs on a Debian-family machine or in a container; `linux-arm64` is
the other supported RID.

**On Linux, build the projects rather than the solution.** `O-view.Tray` is WPF and targets
`net10.0-windows`, so a bare `dotnet build` of the whole solution fails there on that one
project. Everything else is portable:

```bash
dotnet test tests/O-view.Core.Tests tests/O-view.App.Tests tests/O-view.Linux.Tests -c Release
```

That is precisely what the `ubuntu-latest` CI leg runs.

Requires the .NET 10 SDK (10.0.302+). The test suite is 424 xUnit tests across `O-view.Core`, `O-view.App` and `O-view.Linux`; CI builds and tests on **both** windows-latest and ubuntu-latest on every push and pull request, and tagging `v*` publishes all six assets — the Windows installer and portable exe, both `.deb` architectures, and both tarballs — to a single GitHub release.

Runtime prerequisites: Windows 11 or a supported Linux (see [Platform support](#platform-support)), and Claude data present locally — [Claude Desktop](https://claude.ai/download) for authoritative percentages, and/or Claude Code transcripts under `%USERPROFILE%\.claude\` for token counts.

## Documentation

- [Architecture Decision Records](docs/adr/) — why the stack, providers, and constraints are what they are
- [Findings](docs/findings/) — empirical observations about local data formats
- [UI spec](docs/ui-spec.md) — the panel contract
- [CLAUDE.md](CLAUDE.md) — the rules any contributor (human or AI) works under

## Licence

MIT — see [LICENSE](LICENSE).

Not affiliated with, endorsed by, or sponsored by Anthropic.
