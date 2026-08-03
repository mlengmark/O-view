<!--
  Prepended to every release's auto-generated notes by .github/workflows/release.yml.

  It is deliberately EVERGREEN — facts that stay true release to release: which asset to
  download, which platforms are supported, the GNOME requirement, and how updates arrive on
  each platform. Do not put "what changed in this version" here; that is what the generated
  section below it is for.

  Issue #84 requires that release notes state the supported distributions and architectures,
  the GNOME extension requirement, and that Linux updates come from the package manager
  rather than in-app. Auto-generated notes are a commit list and state none of that, which is
  why this file exists.
-->

## Which file do I want?

| Platform | Download | Notes |
|---|---|---|
| **Windows 11** | `O-view-Setup.exe` | Per-user install, no admin rights. Updates itself in place. |
| Windows 11, portable | `O-view.Tray.exe` | Self-contained, no install, no auto-update. |
| **Ubuntu 22.04+ / Debian 12+**, x64 | `o-view_<version>_amd64.deb` | `sudo apt install ./o-view_<version>_amd64.deb` |
| **Ubuntu 22.04+ / Debian 12+**, arm64 | `o-view_<version>_arm64.deb` | Same, for aarch64 machines. |
| Any other Linux, x64 | `o-view-<version>-linux-x64.tar.gz` | Extract and run `./o-view`. |
| Any other Linux, arm64 | `o-view-<version>-linux-arm64.tar.gz` | Same, for aarch64. |

Every release carries every platform's assets, whether or not that platform's code changed —
that is what keeps "check for updates" correct on both. A release with no visible change for
your platform is expected, not a mistake.

## Linux: read this before you file a bug about a missing icon

**GNOME ships no notification-area support by default** — including stock Ubuntu. The Linux
tray is a protocol (StatusNotifierItem, over D-Bus) and something has to implement the *host*
side; KDE Plasma, XFCE and Cinnamon do, GNOME deliberately does not.

Install the **AppIndicator and KStatusNotifierItem Support** extension and O-view picks it up
without a restart. Until then it will tell you, by desktop notification, that it is running
but has nowhere to put its icon. To check what it found:

```bash
o-view --probe
```

**Linux updates come from your package manager, not from inside the app.** `apt upgrade`
owns the installed copy; overwriting files dpkg owns would be silently reverted by the next
upgrade. O-view will tell you a newer version exists and will not try to install it. Tarball
users update by downloading the next tarball. Only the Windows installer build updates itself.

**macOS is not supported and is out of scope.** There is no Snap or Flatpak either: both
sandbox the filesystem, and reading another application's files is O-view's entire function.

---
