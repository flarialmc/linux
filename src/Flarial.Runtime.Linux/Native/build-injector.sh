#!/bin/bash
# Builds injector.exe (x64, static CRT) from injector.c. Prefers mingw, falls back to msvc-wine.
set -e
cd "$(dirname "$0")"
if command -v x86_64-w64-mingw32-gcc >/dev/null; then
  x86_64-w64-mingw32-gcc -O2 -municode -s injector.c -o injector.exe
else
  M="${MSVC_BIN:?set MSVC_BIN to the msvc-wine bin/x64 dir}"
  export WINEDLLOVERRIDES="mountmgr.sys=d"
  "$M/cl" /nologo /O2 /MT /Brepro /DUNICODE /D_UNICODE injector.c /Fe:injector.exe /link /Brepro /SUBSYSTEM:CONSOLE
  rm -f injector.obj
fi
