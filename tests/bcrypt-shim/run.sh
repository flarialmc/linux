#!/bin/bash
# Round-trip test for flarial_bcrypt_shim.dll under Wine: ciphertexts produced through the patched bcrypt export must decrypt with OpenSSL.
# Usage: MSVC_BIN=<msvc-wine bin/x64> WINE=<wine binary> WINEPREFIX=<scratch prefix for the test run> [BUILD_WINEPREFIX=<msvc-wine prefix, default /opt/wine>] tests/bcrypt-shim/run.sh
set -e
cd "$(dirname "$0")"
SHIM="$(cd ../../src/Flarial.Runtime.Linux/Native && pwd)/flarial_bcrypt_shim.dll"
T="$(mktemp -d)"; trap 'rm -rf "$T"' EXIT
export WINEDLLOVERRIDES="mountmgr.sys=d" WINEDEBUG="${WINEDEBUG:--all}"
openssl genrsa -out "$T/k.pem" 2048 2>/dev/null
openssl rsa -in "$T/k.pem" -noout -modulus | sed 's/Modulus=//' | xxd -r -p > "$T/mod.bin"
printf '\x01\x00\x01' > "$T/e.bin"
cp shim_test.cpp "$T/"
( cd "$T" && WINEPREFIX="${BUILD_WINEPREFIX:-/opt/wine}" "$MSVC_BIN/cl" /nologo /EHsc /MT shim_test.cpp /Fe:shim_test.exe >/dev/null )
( cd "$T" && "${WINE:-wine}" shim_test.exe "Z:${SHIM//\//\\}" mod.bin e.bin | tr -d '\r' | tee out.txt )
grep -q "RESULT: ALL PASS" "$T/out.txt"
dec() { openssl pkeyutl -decrypt -inkey "$T/k.pem" -in "$T/$1" -pkeyopt rsa_padding_mode:oaep -pkeyopt rsa_oaep_md:$2 ${3:+-pkeyopt rsa_oaep_label:$3} | xxd -p | tr -d '\n'; }
want=$(printf '%s' "$(for i in $(seq 0 31); do printf '%02x' $(( (i*7+1) & 255 )); done)")
[ "$(dec ct_sha256.bin sha256)" = "$want" ] && echo "PASS openssl decrypts SHA-256 OAEP"
[ "$(dec ct_sha1.bin sha1)" = "$want" ] && echo "PASS openssl decrypts SHA-1 OAEP"
[ "$(dec ct_sha384_label.bin sha384 "$(printf flarial | xxd -p)")" = "$want" ] && echo "PASS openssl decrypts SHA-384 OAEP with label"
echo "ALL ROUND-TRIPS OK"
