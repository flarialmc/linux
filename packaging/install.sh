#!/bin/sh
# Flarial Launcher (Linux) installer. No root, no sudo.
#   curl -fsSL https://cdn.flarial.xyz/launcher/linux/install.sh | sh
#   sh install.sh --uninstall [--purge [--yes]]
# Options: --quiet (less output)
# Env: FLARIAL_CDN_BASE (default https://cdn.flarial.xyz/launcher/linux, file:// ok), FLARIAL_PUBKEY_FILE
set -eu

CDN_BASE=${FLARIAL_CDN_BASE:-https://cdn.flarial.xyz/launcher/linux}
CDN_BASE=${CDN_BASE%/}
ARCHIVE=Flarial.Launcher.Linux.tar.zst
MANIFEST=Flarial.Launcher.Linux.json

DATA_HOME=${XDG_DATA_HOME:-$HOME/.local/share}
BIN_HOME=${XDG_BIN_HOME:-$HOME/.local/bin}
ROOT=$DATA_HOME/Flarial/Linux
LAUNCHER=$ROOT/launcher
BIN_LINK=$BIN_HOME/flarial-launcher
DESKTOP_FILE=$DATA_HOME/applications/flarial-launcher.desktop
ICON_DIR=$DATA_HOME/icons/hicolor/256x256/apps
ICON_FILE=$ICON_DIR/flarial-launcher.png

MODE=install PURGE=0 YES=0 QUIET=0
for a in "$@"; do
  case $a in
    --uninstall) MODE=uninstall ;;
    --purge) PURGE=1 ;;
    --yes|-y) YES=1 ;;
    --quiet|-q) QUIET=1 ;;
    -h|--help) sed -n '2,6p' "$0" 2>/dev/null || true; exit 0 ;;
    *) echo "unknown option: $a" >&2; exit 2 ;;
  esac
done

say() { [ "$QUIET" = 1 ] || echo "$*"; }
warn() { echo "warning: $*" >&2; }
die() { echo "error: $*" >&2; exit 1; }

TMP=
cleanup() { [ -z "$TMP" ] || rm -rf "$TMP"; }
trap cleanup EXIT
trap 'exit 130' INT TERM HUP

# ---------------------------------------------------------------- uninstall
if [ "$MODE" = uninstall ]; then
  rm -rf "$LAUNCHER"
  [ -L "$BIN_LINK" ] && rm -f "$BIN_LINK"
  rm -f "$DESKTOP_FILE" "$ICON_FILE"
  command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$DATA_HOME/applications" >/dev/null 2>&1 || true
  command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t "$DATA_HOME/icons/hicolor" >/dev/null 2>&1 || true
  say "Removed launcher, symlink, desktop entry and icon."
  if [ "$PURGE" = 1 ]; then
    echo "WARNING: --purge deletes ALL Flarial Linux data in $ROOT" >&2
    echo "         (installed games, Wine prefix, saved logins). This cannot be undone." >&2
    if [ "$YES" != 1 ] && ( : </dev/tty ) 2>/dev/null; then
      printf 'Type "yes" to continue: ' >&2
      read -r ans </dev/tty || ans=
      [ "$ans" = yes ] || die "purge cancelled"
    fi
    rm -rf "$ROOT"
    say "Purged $ROOT"
  else
    say "Game data, prefix and logins in $ROOT were kept (use --purge to remove them)."
  fi
  rmdir "$DATA_HOME/Flarial" 2>/dev/null || true
  exit 0
fi

# ------------------------------------------------------------------ checks
[ "$(uname -m)" = x86_64 ] || die "unsupported architecture $(uname -m); only x86_64 is supported"

pkgmgr=
if [ -r /etc/os-release ]; then
  ids=$(. /etc/os-release; echo "${ID:-} ${ID_LIKE:-}")
  case " $ids " in
    *" arch "*|*" cachyos "*|*" manjaro "*) pkgmgr=pacman ;;
    *" debian "*|*" ubuntu "*) pkgmgr=apt ;;
    *" fedora "*|*" rhel "*) pkgmgr=dnf ;;
    *" suse "*|*" opensuse"*) pkgmgr=zypper ;;
  esac
fi
hint() { # $1 pacman pkg, $2 apt, $3 dnf, $4 zypper
  case $pkgmgr in
    pacman) echo "sudo pacman -S $1" ;;
    apt) echo "sudo apt install $2" ;;
    dnf) echo "sudo dnf install $3" ;;
    zypper) echo "sudo zypper install $4" ;;
    *) echo "install: $1 (Arch) / $2 (Debian) / $3 (Fedora) / $4 (openSUSE)" ;;
  esac
}

missing=
need() { # $1 command, rest hint args
  c=$1; shift
  command -v "$c" >/dev/null 2>&1 || missing="$missing
  $c missing -> $(hint "$@")"
}
HAVE_CURL=0; command -v curl >/dev/null 2>&1 && HAVE_CURL=1
if [ "$HAVE_CURL" = 0 ]; then
  case $CDN_BASE in file://*) ;; *) command -v wget >/dev/null 2>&1 || missing="$missing
  curl or wget missing -> $(hint curl curl curl curl)" ;; esac
fi
need tar tar tar tar tar
command -v zstd >/dev/null 2>&1 || tar --help 2>/dev/null | grep -q zstd || missing="$missing
  zstd (or a tar with zstd support) missing -> $(hint zstd zstd zstd zstd)"
need openssl openssl openssl openssl openssl
need python3 python python3 python3 python3
if [ -n "$missing" ]; then
  echo "error: missing required dependencies:$missing" >&2
  exit 1
fi
command -v xprop >/dev/null 2>&1 || warn "xprop not found; the launcher cannot detect a game process that outlived its window ($(hint xorg-xprop x11-utils xprop xprop))"
if command -v vulkaninfo >/dev/null 2>&1; then
  vulkaninfo --summary >/dev/null 2>&1 || warn "vulkaninfo failed; Vulkan may be unusable (games need a working Vulkan driver, e.g. $(hint vulkan-icd-loader libvulkan1 vulkan-loader vulkan-loader))"
else
  warn "vulkaninfo not found; cannot verify Vulkan support (optional: $(hint vulkan-tools vulkan-tools vulkan-tools vulkan-tools))"
fi

# ---------------------------------------------------------------- download
TMP=$(mktemp -d "${TMPDIR:-/tmp}/flarial-install.XXXXXX")

fetch() { # url out
  case $1 in
    file://*) cp "${1#file://}" "$2" ;;
    *) if [ "$HAVE_CURL" = 1 ]; then curl -fsSL --retry 3 -o "$2" "$1"; else wget -q -O "$2" "$1"; fi ;;
  esac
}

if [ -n "${FLARIAL_PUBKEY_FILE:-}" ]; then
  cp "$FLARIAL_PUBKEY_FILE" "$TMP/pub.pem"
else
  cat > "$TMP/pub.pem" <<'KEY'
-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEyE2XQRgyEM/Gy+/MITOSFhmEFoDY
/8mmGUIAdy66nSToJU7whtSWcAjZDojWUhB29xSvmAM+bX1h5eknNadUUQ==
-----END PUBLIC KEY-----
KEY
fi

say "Fetching manifest from $CDN_BASE ..."
fetch "$CDN_BASE/$MANIFEST" "$TMP/manifest.json" || die "could not download manifest"
python3 - "$TMP/manifest.json" "$TMP" <<'PY' || die "invalid manifest"
import json, sys, re
m = json.load(open(sys.argv[1]))
v = str(m["version"])
if not re.fullmatch(r"[0-9A-Za-z._-]+", v) or v.startswith("."):
    sys.exit("bad version")
if not re.fullmatch(r"[0-9a-fA-F]{64}", m["sha256"]):
    sys.exit("bad sha256")
for k, val in (("version", v), ("sha256", m["sha256"].lower()), ("size", int(m["size"])), ("signature", m["signature"])):
    open(f"{sys.argv[2]}/m.{k}", "w").write(str(val))
PY
VERSION=$(cat "$TMP/m.version")
SHA=$(cat "$TMP/m.sha256")
SIZE=$(cat "$TMP/m.size")

say "Downloading Flarial Launcher $VERSION ..."
fetch "$CDN_BASE/$ARCHIVE" "$TMP/$ARCHIVE" || die "could not download archive"

[ "$(wc -c < "$TMP/$ARCHIVE" | tr -d ' ')" = "$SIZE" ] || die "size mismatch; aborting"
got=$(openssl dgst -sha256 "$TMP/$ARCHIVE" | sed 's/.*= *//')
[ "$got" = "$SHA" ] || die "sha256 mismatch; aborting"
openssl base64 -d -A < "$TMP/m.signature" > "$TMP/sig.der" 2>/dev/null || die "bad signature encoding"
openssl dgst -sha256 -verify "$TMP/pub.pem" -signature "$TMP/sig.der" "$TMP/$ARCHIVE" >/dev/null 2>&1 \
  || die "signature verification FAILED; aborting"
say "Checksum and signature OK."

# ----------------------------------------------------------------- install
mkdir -p "$LAUNCHER/versions" "$BIN_HOME" "$DATA_HOME/applications" "$ICON_DIR"
STAGE=$LAUNCHER/versions/.stage.$$
rm -rf "$STAGE"; mkdir "$STAGE"
if command -v zstd >/dev/null 2>&1; then
  zstd -dc "$TMP/$ARCHIVE" | tar -xf - -C "$STAGE" || { rm -rf "$STAGE"; die "extraction failed"; }
else
  tar --zstd -xf "$TMP/$ARCHIVE" -C "$STAGE" || { rm -rf "$STAGE"; die "extraction failed"; }
fi
SRC=$STAGE/Flarial.Launcher
[ -f "$SRC/Flarial.Launcher" ] || { rm -rf "$STAGE"; die "archive has unexpected layout"; }
chmod +x "$SRC/Flarial.Launcher"

DEST=$LAUNCHER/versions/$VERSION
rm -rf "$DEST"
mv "$SRC" "$DEST"
rm -rf "$STAGE"

old=
[ -L "$LAUNCHER/current" ] && old=$(readlink "$LAUNCHER/current") || true
if [ -n "$old" ] && [ "$old" != "versions/$VERSION" ]; then
  ln -s "$old" "$LAUNCHER/previous.new"
  mv -fT "$LAUNCHER/previous.new" "$LAUNCHER/previous"
fi
ln -s "versions/$VERSION" "$LAUNCHER/current.new"
mv -fT "$LAUNCHER/current.new" "$LAUNCHER/current"

# keep only current + previous
keep1=$VERSION keep2=
[ -L "$LAUNCHER/previous" ] && keep2=$(basename "$(readlink "$LAUNCHER/previous")") || true
for d in "$LAUNCHER"/versions/*; do
  [ -d "$d" ] || continue
  b=$(basename "$d")
  [ "$b" = "$keep1" ] || [ "$b" = "$keep2" ] || rm -rf "$d"
done

ln -sfn "$LAUNCHER/current/Flarial.Launcher" "$BIN_LINK"

[ -f "$DEST/flarial-launcher.png" ] && cp "$DEST/flarial-launcher.png" "$ICON_FILE"
cat > "$DESKTOP_FILE" <<DESK
[Desktop Entry]
Type=Application
Name=Flarial Launcher
Comment=Minecraft Bedrock client launcher
Exec=$BIN_LINK
Icon=flarial-launcher
Terminal=false
Categories=Game;
StartupWMClass=Flarial.Launcher
DESK
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$DATA_HOME/applications" >/dev/null 2>&1 || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t "$DATA_HOME/icons/hicolor" >/dev/null 2>&1 || true

say "Installed Flarial Launcher $VERSION to $LAUNCHER"
case ":$PATH:" in
  *":$BIN_HOME:"*) ;;
  *) warn "$BIN_HOME is not in your PATH; add it to run 'flarial-launcher' from a terminal" ;;
esac
say "Run: flarial-launcher (or use the app menu entry)"
