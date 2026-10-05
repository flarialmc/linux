# Wine bcrypt RSA-OAEP shim

## Problem

The Flarial client talks to `api.flarial.xyz` through an encrypted gateway. Every gateway request (presence, heartbeat, events,
crash reports, featured servers) first wraps a random session key with RSA-OAEP (SHA-256) using
`BCryptEncrypt(key, ..., &BCRYPT_OAEP_PADDING_INFO, ..., BCRYPT_PAD_OAEP)`.

Wine's bcrypt (verified on wine-11.1 / GDK-Proton) answers the size query (`pbOutput == NULL`) correctly but fails the real call
with `STATUS_INVALID_PARAMETER (0xC000000D)`; with `WINEDEBUG=warn+bcrypt` it logs `key_asymmetric_encrypt padding info not found`.
SHA-1 OAEP fails the same way, PKCS#1 v1.5 works. The client treated that as "gateway unavailable", so under Linux no presence or
heartbeat ever left the machine. (TLS, the vault, the OAuth refresh and `/api/v2/account` all work; only the gateway seal failed.)

## Fix

`Native/flarial_bcrypt_shim.dll` is injected into Minecraft before the client (`LinuxInjector.Inject` prepends it to the library
list). On load it redirects the **export table entry** of `bcrypt.dll!BCryptEncrypt` to a small stub (above bcrypt's base, as an
export RVA is unsigned 32-bit) that jumps to the shim's function. Modules that bind to bcrypt afterwards, i.e. the client, get
the shim; modules already bound (Minecraft itself) are untouched.

The shim calls the original `BCryptEncrypt` first and returns its result unchanged unless **all** of these hold:

- `BCRYPT_PAD_OAEP` with a padding-info pointer and a real output buffer,
- the original returned `STATUS_INVALID_PARAMETER`,
- the key exports as an RSA public blob (`BCRYPT_RSAPUBLIC_BLOB`, 1024..8192-bit modulus, exponent up to 8 bytes).

Then it performs RSA-OAEP itself: EME-OAEP encoding with MGF1 (any hash bcrypt supports, optional label; hashing and the random seed
come from bcrypt) followed by the raw RSA public operation with a small bignum. Output is identical in format to Windows (modulus-sized
big-endian block, `*pcbResult` set; too-small buffer gives `STATUS_BUFFER_TOO_SMALL`). Size queries, AES-GCM, HMAC, PKCS#1 and
everything else are passed through untouched. If a future Wine fixes OAEP, the original call succeeds and the shim is a no-op.

Fail open: if `bcrypt.dll` has no usable `BCryptEncrypt` export entry (missing, forwarded, unexpected image), the stub memory cannot be
allocated, or the patch faults, the shim logs one line via `OutputDebugStringA` and leaves bcrypt alone. `DllMain` always returns
success. It never logs keys, plaintext or ciphertext.

## Build and test

```
MSVC_BIN=<msvc-wine>/bin/x64 src/Flarial.Runtime.Linux/Native/build-bcrypt-shim.sh   # reproducible (/Brepro, trimmed paths)
MSVC_BIN=... WINE=<engine wine> WINEPREFIX=<scratch prefix> tests/bcrypt-shim/run.sh  # OpenSSL round trip
```

`run.sh` loads the shim in a Wine process, encrypts through the patched export (SHA-256, SHA-1, SHA-384 with label), checks size
query / short buffer / oversize message / PKCS#1 / AES-GCM behaviour, and decrypts the ciphertexts with OpenSSL.
