#!/bin/bash
# Builds flarial_vault.dll (x64, static CRT, C++/WinRT) from flarial_vault.cpp with msvc-wine.
set -e
cd "$(dirname "$0")"
M="${MSVC_BIN:?set MSVC_BIN to the msvc-wine bin/x64 dir}"
# Strip the msvc-wine root (as wine sees it) from embedded source paths.
R="${MSVC_ROOT:-$(cd "$M/../.." && pwd)}"
R="z:${R//\//\\}\\"
export WINEDLLOVERRIDES="mountmgr.sys=d"
"$M/cl" /nologo /O2 /MT /std:c++20 /EHsc /Brepro /DUNICODE /D_UNICODE /LD flarial_vault.cpp /Fe:flarial_vault.dll \
  /d1trimfile:"$R" \
  /link /Brepro /EXPORT:DllGetActivationFactory,PRIVATE /EXPORT:DllCanUnloadNow,PRIVATE runtimeobject.lib ole32.lib oleaut32.lib
rm -f flarial_vault.obj flarial_vault.lib flarial_vault.exp
