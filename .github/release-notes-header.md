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

  The off-plan banner section is here on the same terms, and it is worth saying why it is not
  a changelog entry in disguise. What it states is standing behaviour — what the banner claims
  and what it deliberately does not — which is true of every release from v0.9.1 on. The one
  backward-looking sentence in it stays true forever too: someone on an older build reading a
  much later release still needs to know that the message they are looking at is wrong for
  them. If it ever needs a "and in this version we changed…", that belongs in the release body,
  not here.
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

### Verifying your download

`SHA256SUMS` lists a checksum for each of the six assets above. It is the seventh file on
this release, and you do not need it for a normal install.

```bash
sha256sum -c SHA256SUMS --ignore-missing
```

```powershell
(Get-FileHash .\O-view-Setup.exe -Algorithm SHA256).Hash
```

O-view is **not code-signed** — an Authenticode certificate costs more than a free tool
justifies — so Windows SmartScreen will warn on first run. The checksum is what you have
instead: it confirms the file you have is the file this release published. It cannot tell you
anything about a release itself being wrong, only that your copy arrived intact.

Every asset also carries **build provenance**, signed by Sigstore. This is the stronger check:
it proves the file came out of O-view's own release workflow rather than being attached to the
release by some other route. It needs the [GitHub CLI](https://cli.github.com):

```bash
gh attestation verify O-view-Setup.exe --repo mlengmark/O-view
```

A file that fails this did not come from this pipeline, whatever else it claims.

## The off-plan banner is about your plan window, not your bill

O-view raises an amber banner when your 5-hour window is exhausted, or when substantial local
work runs against a plan meter that is not moving. Both say the same thing: **your work has
stopped drawing from the plan allowance.** Whether it is being *charged* is a separate
question, and the answer is your account's extra-usage setting rather than anything the meter
can tell you.

So O-view reads that setting from Claude Code's own local cache and says which way it is set —
stamped with the time Claude Code last read it, because that cache refreshes only when `/usage`
runs and can be days old. Where there is nothing to read it says so instead of guessing. It
never asserts a charge it cannot see, and the banner links to your usage settings in Claude for
the figure it will not state.

**Before v0.9.1 the banner asserted "usage is billing beyond your plan" at everyone whose
window ran out**, including accounts with auto-billing switched off, which could not be billed
anything. If you are on an older build, that message is wrong for you and upgrading is the fix.

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

**Linux builds notify; they never update themselves.** When a newer release exists O-view
tells you once, and stops there — overwriting files dpkg owns would be silently reverted by
the next `apt upgrade`, so it does not touch them.

Note that **there is no O-view apt repository**, so `apt upgrade` will *not* find a new
version on its own. Updating means downloading the next `.deb` (or tarball) from the releases
page and installing it, exactly as you did the first time. Only the Windows installer build
updates itself.

**macOS is not supported and is out of scope.** There is no Snap or Flatpak either: both
sandbox the filesystem, and reading another application's files is O-view's entire function.

---
