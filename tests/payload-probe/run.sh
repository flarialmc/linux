#!/bin/bash
# Delivery test for the launcher payload (the client reads it from its loader thread description): the real InjectorCore + injector.exe
# inject a probe DLL into a stand-in Minecraft.Windows.exe running in a scratch Wine prefix; the probe reads the description with
# GetThreadDescription in DllMain exactly like the client. Never touches your data directory, prefix or running game.
# Usage: MSVC_BIN=<msvc-wine bin/x64> LAUNCHER=<Flarial.Launcher apphost or dll> ENGINE=<GDK-Proton-xuser dir> tests/payload-probe/run.sh
#   LAUNCHER defaults to src/Flarial.Launcher/bin/Release/linux-x64/Flarial.Launcher (dotnet build -c Release),
#   ENGINE defaults to ~/.local/share/Flarial/Linux/proton/GDK-Proton-xuser, BUILD_WINEPREFIX to /opt/wine (msvc-wine prefix, only used to compile).
set -e
cd "$(dirname "$0")"
ROOT="$(cd ../.. && pwd)"
LAUNCHER="${LAUNCHER:-$ROOT/src/Flarial.Launcher/bin/Release/linux-x64/Flarial.Launcher}"
ENGINE="${ENGINE:-$HOME/.local/share/Flarial/Linux/proton/GDK-Proton-xuser}"
T="$(mktemp -d)"; trap 'rm -rf "$T"' EXIT
export WINEDLLOVERRIDES="mountmgr.sys=d"
( cd "$T"
  cp "$OLDPWD/probe.c" "$OLDPWD/target.c" .
  export WINEPREFIX="${BUILD_WINEPREFIX:-/opt/wine}"
  "$MSVC_BIN/cl" /nologo /O2 /MT /LD probe.c /Fe:probe.dll /link kernel32.lib >/dev/null
  "$MSVC_BIN/cl" /nologo /O2 /MT target.c /Fe:Minecraft.Windows.exe >/dev/null )
mkdir -p "$T/data/Flarial/Linux/proton" "$T/home"
ln -s "$ENGINE" "$T/data/Flarial/Linux/proton/GDK-Proton-xuser"
unset WINEDLLOVERRIDES
env -u WINEPREFIX HOME="$T/home" XDG_DATA_HOME="$T/data" FLARIAL_PAYLOAD_PROBE="$T" "$LAUNCHER" --selftest-payload
