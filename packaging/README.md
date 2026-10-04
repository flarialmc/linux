# Linux packaging

## Install (no root)

    curl -fsSL https://cdn.flarial.xyz/launcher/linux/install.sh | sh

Arch/CachyOS (AUR): `yay -S flarial-launcher-bin`. The package only ships a bootstrap
(`/usr/bin/flarial-launcher`) that runs the same installer on first launch; the launcher
then self-updates in `~/.local/share/Flarial/Linux/launcher`.

Re-running the installer updates/repairs. Downloads are verified (size, sha256, ECDSA P-256 signature).
Needs: curl or wget, tar, zstd, openssl, python3 (and a working Vulkan driver).
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
