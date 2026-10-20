# xgameruntime certificate crash patch

## Symptom

Selecting Play > Servers closes Minecraft and opens Wine's crash debugger console:

```
wine: Unhandled page fault on read access to 0000000000000020 at address 000000007709E248 ... starting debugger...
```

Flarial's crash log reports exception `0xC0000005` in `xgameruntime.dll` at RVA `0xE248`.

## Cause

The GDK-Proton-xuser builtin `xgameruntime.dll` contains `httpclient_ObtainSecurityInformationForUrl`, which queries the server certificate:

```c
WinHttpQueryOption(..., WINHTTP_OPTION_SERVER_CERT_CONTEXT (0x4e), ...)
```

When this query fails:

1. The function checks `GetLastError()` at virtual address `0x1000E880`. If `LastError <= 0`, execution jumps to `0x1000EB72`.
2. At `0x1000EB72`, `je 1000e233` sends `LastError == 0` back into the success branch with a NULL certificate pointer.
3. At `0x1000E248`, `mov r9, [rdx+0x20]` reads through the NULL pointer and faults.

Engines native18 and native20 have identical binary bytes for this library (SHA-256 `f1d5569ce40ab9b973336a5f3cf2e65b367dc535029d243e3850748b16ceb47e`).

## Fix

At file offset `0xEB72` (ImageBase `0x10000000`), replace `0F 84 BB F6 FF FF` (`je 1000e233`) with `0F 84 1C FD FF FF` (`je 1000e894`).

This routes the zero-error failure to the error exit at `0x1000E894`, setting HRESULT `0x80070000` and proceeding through normal request cleanup (`httpclient_CloseRequest`). The game handles the return as a failed URL security check; at worst, a server banner or icon fails to load.

## Location and hook

Wine loads this builtin from the engine directory rather than the Wine prefix's `system32` copy (confirmed via `/proc/<pid>/maps`):

```
<engine>/files/lib/wine/x86_64-windows/xgameruntime.dll
```

`PrefixManager.PatchEngineXgameruntime()` patches this binary at the start of `EnsureSetupAsync` on every launch and inside `RefreshEngineDlls`.

Symlinks are resolved before opening the file. An engine seeded from a BedrockOnLinux installation is patched in place, fixing the crash for BedrockOnLinux as well.

## Safety

- **Guards**: the file is modified only if its full SHA-256 matches `XgrSha` and the 6 bytes at offset `0xEB72` match `XgrOrig`.
- **Idempotency**: if the patch bytes (`XgrPatch`) are already present at `0xEB72`, the file is left untouched.
- **Atomic write**: the patched data is written to a temporary file (`.tmp-<guid>`) in the same directory and renamed over the original.
- **Fail-safe**: errors are caught and logged without aborting launch.
- **Log entries**: recorded in `~/.local/share/Flarial/Linux/logs/launch.log`:
  - `xgameruntime patch: applied`
  - `xgameruntime patch: already applied`
  - `xgameruntime patch: unknown hash <sha>`
  - `xgameruntime patch: failed <msg>`

## Self-test

Run `--selftest-engine` with `FLARIAL_TEST_XGR` pointing to a copy of the DLL:

```sh
FLARIAL_TEST_XGR=<path-to-dll-copy> Flarial.Launcher --selftest-engine
```

Verify the 6 patched bytes with `xxd`:

```sh
xxd -s 0xeb72 -l 6 <path-to-dll-copy>
```

Expected output:

```
0000eb72: 0f84 1cfd ffff
```

## New engine builds

If an updated engine changes `xgameruntime.dll`, the SHA-256 check fails, logs `xgameruntime patch: unknown hash <sha>`, and makes no changes.

To support the new build:
1. Disassemble `httpclient_ObtainSecurityInformationForUrl` in the new DLL.
2. Confirm the failure branch and target error cleanup offset.
3. Update `XgrSha`, `XgrOffset`, `XgrOrig`, and `XgrPatch` in `src/Flarial.Runtime.Linux/Prefix/PrefixManager.cs`.
