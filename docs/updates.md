# Launcher updates and releases

## Release (CI)
`.github/workflows/linux.yml` runs on push to `main` (and manual dispatch). It only proceeds when the commit subject starts with `release:`. Steps: bump `version.txt` (`yyyy.M.d.HHmm`, UTC, commit `Version (linux) -> '<v>'`), `dotnet publish -r linux-x64 --self-contained -p:PublishSingleFile=true` (managed code is one file; `libSkiaSharp.so` and `libHarfBuzzSharp.so` stay beside it), package `Flarial.Launcher/` (exe, natives, `version.txt`, `flarial-launcher.png`, `flarial-launcher.desktop`) into `Flarial.Launcher.Linux.tar.zst`, sign with ECDSA P-256/SHA-256, write `Flarial.Launcher.Linux.json`, and push archive, `.sig`, json and `packaging/install.sh` to `flarialmc/newcdn` `launcher/linux/` (commit `Update Linux Launcher -> '<v>'`).

Secrets on this repo: `LINUX_UPDATE_PRIVATE_KEY` (PEM contents of `~/.config/flarial-launcher-signing/linux-update-private.pem`), `APP_CLIENT_ID`, `APP_PRIVATE_KEY` (GitHub App with write access to `newcdn`; the App installation must also include this repo's workflows/secrets setup). The matching public key is embedded in `LauncherUpdater.cs` and `install.sh`.

## Update flow (`src/Flarial.Runtime.Linux/Update/`)
* Active only when the executable runs from `~/.local/share/Flarial/Linux/launcher/versions/<v>/` (dev builds and extracted archives do nothing; `Flarial.Launcher --install` copies a build into that layout and creates `~/.local/bin/flarial-launcher`, desktop entry and icon).
* After "Ready!", if Settings > Automatic Updates is on: fetch the manifest, compare against `version.txt`, wait while the game runs or a version installs, download, verify size + sha256 + signature + that the archive's `version.txt` equals the manifest version, extract, move to `versions/<v>`, swap `current` (temp symlink + `rename`), set `previous`, prune everything else.
* Then the existing "Launcher Update Available" dialog: Update = restart into the new version, Later = it applies on the next start. The launcher never exits on its own.
* Health: `.started` is written at startup, `.healthy` at "Ready!". If `current` has `.started` older than 60 s and no `.healthy`, the next start swaps back to `previous`, records the version in `bad-version` (not offered again) and relaunches.
* Log: `~/.local/share/Flarial/Linux/logs/updater.log`.
* Check: `XDG_DATA_HOME=$(mktemp -d) HOME=$XDG_DATA_HOME Flarial.Launcher --selftest-updater`.
