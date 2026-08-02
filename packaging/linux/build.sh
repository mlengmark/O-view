#!/usr/bin/env bash
#
# Builds O-view's Linux artifacts for one architecture: a .deb and a portable tarball.
#
#   ./packaging/linux/build.sh <version> <rid> [output-dir]
#   ./packaging/linux/build.sh 0.6.0 linux-x64 dist
#
# Must run on a Debian-family host: it uses dpkg-deb rather than assembling an ar/tar
# archive by hand, so the result is a package lintian is willing to look at.
#
# Reproducible from a clean checkout — the release workflow calls exactly this, so a
# package built locally and one built by CI come out of the same code path.
set -euo pipefail

VERSION="${1:?usage: build.sh <version> <rid> [output-dir]}"
RID="${2:?usage: build.sh <version> <rid> [output-dir]}"
OUT="${3:-dist}"

case "$RID" in
  linux-x64)   DEB_ARCH=amd64 ;;
  linux-arm64) DEB_ARCH=arm64 ;;
  *) echo "unsupported rid: $RID (expected linux-x64 or linux-arm64)" >&2; exit 1 ;;
esac

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

mkdir -p "$OUT"
echo "==> building o-view $VERSION for $RID ($DEB_ARCH)"

# ── publish ─────────────────────────────────────────────────────────────────────────
#
# Self-contained deliberately. Framework-dependent would need a dotnet-runtime-10 package
# that may simply not exist in the user's archive yet, and a tray app that will not start
# because of a missing runtime is indistinguishable from a broken one. Same reasoning as
# the Windows build.
APP="$STAGE/usr/lib/o-view"
mkdir -p "$APP"
dotnet publish "$ROOT/src/O-view.Linux" \
  --configuration Release \
  --runtime "$RID" \
  --self-contained true \
  -p:Version="$VERSION" \
  -p:DebugType=none \
  --output "$APP"

chmod 0755 "$APP/o-view"

# ── package tree ────────────────────────────────────────────────────────────────────

# A shim rather than a symlink into /usr/lib: the single-file host resolves its extraction
# directory from the real path, and a symlinked argv[0] has surprised it before.
mkdir -p "$STAGE/usr/bin"
cat > "$STAGE/usr/bin/o-view" <<'SHIM'
#!/bin/sh
exec /usr/lib/o-view/o-view "$@"
SHIM
chmod 0755 "$STAGE/usr/bin/o-view"

# Desktop entry. This is the APPLICATION entry, describing O-view to the launcher — it is
# root-owned and must never be edited by a settings toggle. Run-at-startup is a separate,
# per-user file in ~/.config/autostart (XdgAutostartRegistration).
mkdir -p "$STAGE/usr/share/applications"
cat > "$STAGE/usr/share/applications/o-view.desktop" <<'DESKTOP'
[Desktop Entry]
Type=Application
Name=O-view
Comment=Claude usage and time until the next limit reset
Exec=/usr/bin/o-view
Icon=o-view
Terminal=false
Categories=Utility;Monitor;
Keywords=claude;usage;tokens;monitor;
StartupNotify=false
DESKTOP

# Icons, from the brand set already in the repo.
for size in 16 24 32 48 64 128 256; do
  src="$ROOT/brand/png/o-view-icon-${size}.png"
  [ -f "$src" ] || continue
  dir="$STAGE/usr/share/icons/hicolor/${size}x${size}/apps"
  mkdir -p "$dir"
  install -m 0644 "$src" "$dir/o-view.png"
done

# Copyright, in the machine-readable format Debian expects.
mkdir -p "$STAGE/usr/share/doc/o-view"
{
  echo "Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/"
  echo "Upstream-Name: O-view"
  echo "Source: https://github.com/mlengmark/O-view"
  echo
  echo "Files: *"
  echo "Copyright: 2026 mlengmark"
  echo "License: MIT"
  sed 's/^/ /; s/^ $/ ./' "$ROOT/LICENSE"
} > "$STAGE/usr/share/doc/o-view/copyright"

# ── control ─────────────────────────────────────────────────────────────────────────
#
# The dependency list is deliberately permissive across releases: ICU and OpenSSL are
# SONAME-versioned differently on Ubuntu 22.04 (libicu70), Debian 12 (libicu72) and
# Ubuntu 24.04 (libicu74 / libssl3t64). Pinning one would make the package uninstallable
# on the others, which is precisely the failure the install checks in CI exist to catch.
#
# The X11 and fontconfig entries are Avalonia's, not .NET's.
INSTALLED_KB="$(du -sk "$STAGE/usr" | cut -f1)"
mkdir -p "$STAGE/DEBIAN"
cat > "$STAGE/DEBIAN/control" <<CONTROL
Package: o-view
Version: $VERSION
Section: utils
Priority: optional
Architecture: $DEB_ARCH
Maintainer: Maximilian Lengmark <noreply@github.com>
Homepage: https://github.com/mlengmark/O-view
Installed-Size: $INSTALLED_KB
Depends: libc6, libgcc-s1, libstdc++6, zlib1g, libx11-6, libice6, libsm6, libfontconfig1,
 libicu74 | libicu72 | libicu71 | libicu70 | libicu69, libssl3t64 | libssl3
Description: Claude usage in the notification area
 O-view shows how much of your Claude plan you have used and how long until the
 next limit resets, from a tray icon and a detail panel.
 .
 It reads only files Claude already writes on this machine. No account, no token,
 and no network access except an optional check for a newer release.
 .
 A notification-area host is required for the icon to appear. GNOME does not
 provide one by default; an AppIndicator/KStatusNotifierItem extension adds it.
CONTROL

# Deliberately no postrm cleanup of user data.
#
# O-view's state lives in ~/.local/share/O-view, which is per-user. A root-run maintainer
# script cannot know which users have it, and guessing at home directories to delete files
# is exactly the kind of thing that goes wrong loudly. Removal therefore leaves settings,
# the rollup store and — most importantly — the weekly-reset log alone. That log is
# unrebuildable and costs a week to re-observe (ADR-0011).

# ── build ───────────────────────────────────────────────────────────────────────────

find "$STAGE" -type d -exec chmod 0755 {} +
DEB="$OUT/o-view_${VERSION}_${DEB_ARCH}.deb"
dpkg-deb --root-owner-group --build "$STAGE" "$DEB"

# ── tarball ─────────────────────────────────────────────────────────────────────────
#
# For distributions the .deb does not target — the same set Claude Desktop for Linux does
# not support either. Extract and run; no installation, no auto-update.
TARDIR="$STAGE/tar/o-view-${VERSION}-${RID}"
mkdir -p "$TARDIR"
cp -a "$APP/." "$TARDIR/"
install -m 0644 "$ROOT/LICENSE" "$TARDIR/LICENSE"
cat > "$TARDIR/README" <<README
O-view ${VERSION} (${RID})

  ./o-view              run it
  ./o-view --probe      report what this desktop looks like to O-view
  ./o-view --samples d  render the tray icon at every size a panel might request

The icon needs a notification-area host. GNOME provides none by default; an
AppIndicator/KStatusNotifierItem extension adds one. Without a host O-view still
runs and will say so via a desktop notification.

Reads only files Claude already writes on this machine. No account, no token.
README

TAR="$OUT/o-view-${VERSION}-${RID}.tar.gz"
tar -czf "$TAR" -C "$STAGE/tar" "o-view-${VERSION}-${RID}"

echo "==> $DEB"
echo "==> $TAR"
