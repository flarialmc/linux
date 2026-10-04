# License notes

The packaged files (bootstrap script, installer, desktop entry, icon) come from the
Flarial Linux launcher repository and are licensed GPL-3.0-only, matching its LICENSE.
The package ships no launcher binaries: the launcher is downloaded per user at first run
from https://cdn.flarial.xyz/launcher/linux/ and verified with an embedded ECDSA public key.
PKGBUILD itself is 0BSD, as is customary for AUR packaging.
