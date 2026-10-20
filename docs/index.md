# Documentation Index

The Flarial Linux launcher is an Avalonia UI port of the Windows Flarial launcher, backed by a C# Linux runtime that runs Minecraft Bedrock Edition (Windows GDK) under GDK-Proton via umu-launcher and injects the Flarial client DLL.

## Repository layout

The source tree under `src/` is divided into four components:

- `src/Flarial.Launcher`: Avalonia UI and view models, ported from the upstream Windows launcher.
- `src/Flarial.Runtime`: Cross-platform core runtime providing interfaces, models, client payload preparation, version registry, and settings.
- `src/Flarial.Runtime.Linux`: Linux backend implementing `Platform.cs` interfaces (Wine prefix management, engine installation, umu launch execution, xodus authentication, DLL injection, update verification, and engine binary patches).
- `src/Flarial.Runtime.Windows`: Upstream Windows runtime sources, retained for parity reference and not built on Linux.

## Build, run, and test

### Prerequisites

- .NET 10 SDK
- Host packages: `python3`, `zstd`, `openssl`, `tar`, `gzip`, `bash`, `script`, `setsid`, `stty`, `xdg-open`, `xprop`, and `curl` or `wget`

### Build and run

```sh
# Build launcher
dotnet build src/Flarial.Launcher/Flarial.Launcher.csproj

# Run from source
dotnet run --project src/Flarial.Launcher

# Single-file self-contained release publish
dotnet publish src/Flarial.Launcher/Flarial.Launcher.csproj -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o publish
```

### Self-tests

Self-tests run before the single-instance mutex and can be tested against an isolated data directory:

```sh
T=$(mktemp -d); XDG_DATA_HOME=$T HOME=$T Flarial.Launcher --selftest-<name>
```

Available self-test targets:

- `--selftest-dependencies`: Verifies host CLI dependencies and tools.
- `--selftest-engine`: Validates engine range downloads, SHA-256 checksumming, and optional xgameruntime patching (`FLARIAL_TEST_XGR`).
- `--selftest-xodus`: Tests Xbox device and title authentication against xodus-cli.
- `--selftest-updater`: Validates archive signature verification, staging, atomic symlink swap, and automatic rollback using a mock local HTTP listener.
- `--selftest-payload`: Validates JSON formatting and string boundary rules for client account payloads.

Additional automated integration test scripts:

- `tests/bcrypt-shim/run.sh`: Tests the RSA-OAEP bcrypt shim under Wine against OpenSSL decryption.
- `tests/payload-probe/run.sh`: Tests thread description delivery in a scratch Wine prefix by injecting a probe DLL.

## Documents

- [account-payload.md](account-payload.md): JSON account payload format and thread description injection mechanism for the client.
- [linux-presence.md](linux-presence.md): Runtime detection of Wine in the client DLL to report Linux platform presence.
- [reference/README.md](reference/README.md): Upstream reference UI frame captures and pixel difference comparisons against Linux builds.
- [ui-deviations.md](ui-deviations.md): Detailed record of all UI and configuration adjustments made relative to upstream Windows Flarial.Launcher.
- [updates.md](updates.md): Self-updater architecture, cryptographic archive signing, GitHub Actions CI release workflow, and health verification.
- [wine-bcrypt-oaep.md](wine-bcrypt-oaep.md): Injected Wine bcrypt export hook providing RSA-OAEP encryption for client gateway communication.
- [xgameruntime-cert-patch.md](xgameruntime-cert-patch.md): Binary patch for GDK-Proton xgameruntime.dll to prevent NULL pointer dereference crashes on the Play Servers screen.
