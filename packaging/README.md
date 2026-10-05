# Linux packaging

## Install (no root)

    curl -fsSL https://cdn.flarial.xyz/launcher/linux/install.sh | sh

Arch/CachyOS (AUR): `yay -S flarial-launcher-bin`. The package only ships a bootstrap
(`/usr/bin/flarial-launcher`) that runs the same installer on first launch; the launcher
then self-updates in `~/.local/share/Flarial/Linux/launcher`.

Re-running the installer updates/repairs. Downloads are verified (size, sha256, ECDSA P-256 signature).
The installer checks curl or wget, tar, gzip, zstd, openssl, python3, bash, script, setsid, stty, xdg-open and xprop. It explains missing tools and asks before installing distro packages. Piped installs read the answer and password from the terminal; without a terminal they print a command and exit. Launcher files stay in your home directory.

Existing installs also get a setup dialog at startup and before downloads, launches or Microsoft sign-in. It offers package installation through the system password prompt, a copyable command, and Later. Without pkexec it offers Check again after a manual install. Package output is saved to `~/.local/share/Flarial/Linux/logs/dependencies.log`. Immutable systems get manual setup guidance.

Check or repair tools without downloading the launcher: `sh install.sh --check-dependencies`.

A working Vulkan driver is still required. GPU drivers are hardware-specific and are not installed automatically; vulkan-tools and secret-tool remain optional.
Env overrides for testing: `FLARIAL_CDN_BASE` (may be `file://...`), `FLARIAL_PUBKEY_FILE`.

## Uninstall

    sh install.sh --uninstall            # keeps games, prefix and logins
    sh install.sh --uninstall --purge    # ALSO deletes ~/.local/share/Flarial/Linux (asks to confirm; --yes skips)

AUR: `sudo pacman -R flarial-launcher-bin`, then the command above for the per-user copy.

## Publishing the AUR package

`aur/flarial-launcher-bin/install.sh` and `flarial-launcher.png` are git-ignored copies of
`packaging/install.sh` and `assets/flarial-launcher.png` (one source of truth). Bump the AUR
package only when the bootstrap, installer, icon or desktop file changes (pkgver is static;
launcher updates come from the self-updater).

    cd packaging/aur/flarial-launcher-bin
    cp ../../install.sh ../../../assets/flarial-launcher.png .
    updpkgsums && makepkg --printsrcinfo > .SRCINFO
    makepkg -f            # local test build
    git clone ssh://aur@aur.archlinux.org/flarial-launcher-bin.git /tmp/aur-flb
    cp PKGBUILD .SRCINFO flarial-launcher flarial-launcher.desktop flarial-launcher.png install.sh /tmp/aur-flb/
    cd /tmp/aur-flb && git add -A && git commit -m "update" && git push

The CI release also publishes `packaging/install.sh` to the CDN as `install.sh`.
