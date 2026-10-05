#!/bin/bash
# Builds flarial_bcrypt_shim.dll (x64, static CRT, no dependencies beyond bcrypt/kernel32) from flarial_bcrypt_shim.cpp with msvc-wine.
set -e
cd "$(dirname "$0")"
M="${MSVC_BIN:?set MSVC_BIN to the msvc-wine bin/x64 dir}"
# Strip the msvc-wine root (as wine sees it) from embedded source paths.
R="${MSVC_ROOT:-$(cd "$M/../.." && pwd)}"
R="z:${R//\//\\}\\"
export WINEDLLOVERRIDES="mountmgr.sys=d"
"$M/cl" /nologo /O2 /MT /std:c++20 /EHsc /GS- /Brepro /DUNICODE /D_UNICODE /LD flarial_bcrypt_shim.cpp /Fe:flarial_bcrypt_shim.dll \
  /d1trimfile:"$R" \
  /link /Brepro /NOLOGO bcrypt.lib
rm -f flarial_bcrypt_shim.obj flarial_bcrypt_shim.lib flarial_bcrypt_shim.exp
