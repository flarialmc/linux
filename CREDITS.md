# Credits

This launcher stands on a lot of other people's work. Licenses were checked against each project's repository; "unclear" means no license could be confirmed.

## Flarial

| Project | License | Use |
|---|---|---|
| [flarialmc/Flarial.Launcher](https://github.com/flarialmc/Flarial.Launcher) | GPL-3.0 | The Avalonia UI, view models and runtime are ported from the upstream launcher. This repository is GPL-3.0 accordingly. |

## Backend approach and native helpers

| Project | License | Use |
|---|---|---|
| [BedrockOnLinux](https://github.com/Wyze3306/BedrockOnLinux) by Wyze3306 | MIT (`LICENSE` file; GitHub reports it as unrecognized) | The overall approach to running Minecraft GDK on Linux; `injector.c`; the engine tarball and xodus-cli builds are downloaded from its releases. Notice: `src/Flarial.Runtime.Linux/Native/LICENSE-BedrockOnLinux`. |
| [xodus](https://github.com/xodus-gaming/xodus) (`xodus-cli`) | GPL-3.0 | Store login, game download and per-launch decryption. Downloaded and run as a separate process, never linked or embedded. Pinned build and source: see `src/Flarial.Runtime.Linux/Xodus/NOTICE.txt`. |
| [umu-launcher](https://github.com/Open-Wine-Components/umu-launcher) (Open Wine Components) | GPL-3.0 | `umu-run` starts the game in the Steam Linux Runtime. Downloaded, run as a separate process. |
| [mcpelauncher-gdk-dependencies](https://github.com/minecraft-linux/mcpelauncher-gdk-dependencies) | MIT | Replacement `XCurl.dll` and `libHttpClient.GDK.dll` placed in the game directory. Notice: `src/Flarial.Runtime.Linux/Prefix/LICENSE-mcpelauncher-gdk-dependencies`. |
| [GdkLinks](https://github.com/MinecraftBedrockArchiver/GdkLinks) (MinecraftBedrockArchiver) | MIT | Index of historical Minecraft GDK package URLs (pointers to Microsoft's CDN only). |
| `flarial_bcrypt_shim.dll` (this repository) | GPL-3.0 (original code) | Wine bcrypt RSA-OAEP compatibility shim; implements RFC 8017 EME-OAEP/MGF1 and a small bignum RSA public operation. See `docs/wine-bcrypt-oaep.md`. |
| [curl CA bundle](https://curl.se/docs/caextract.html) (Mozilla CA certificates) | MPL-2.0 | `cacert.pem` for the game's TLS stack. Downloaded. |

## Wine engine and runtime (downloaded, not redistributed)

| Project | License | Use |
|---|---|---|
| [Weather-OS/GDK-Proton](https://github.com/Weather-OS/GDK-Proton) | License unclear (no license file) | Base of the GDK-Proton-xuser engine, via BedrockOnLinux releases. |
| [Weather-OS/WineGDK](https://github.com/Weather-OS/WineGDK) | License unclear on GitHub; Wine fork, so LGPL-2.1 by inheritance | GDK support in Wine. |
| [Wine](https://www.winehq.org/) | LGPL-2.1 | Windows compatibility layer. |
| [Proton](https://github.com/ValveSoftware/Proton) / [Proton-GE](https://github.com/GloriousEggroll/proton-ge-custom) scaffolding | Mixed (BSD-style and others; see upstream) | Compatibility tooling the engine is based on. |
| [vkd3d-proton](https://github.com/HansKristian-Work/vkd3d-proton) | LGPL-2.1 | Direct3D 12 over Vulkan (BedrockOnLinux build with device generated commands). |
| [DXVK](https://github.com/doitsujin/dxvk) | zlib | Direct3D 9/10/11 over Vulkan. |
| [Steam Linux Runtime](https://gitlab.steamos.cloud/steamrt/steamrt) (Valve) | Valve terms / mixed open source | Container runtime fetched from Valve by umu. Never bundled. |

The engine tarball carries its own license and provenance files under `files/share/bedrock-on-linux/licenses-and-provenance/`.

## .NET dependencies

| Package | License |
|---|---|
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) (Avalonia.Skia, Avalonia.X11, Avalonia.HarfBuzz) | MIT |
| [ReactiveUI.Avalonia](https://github.com/reactiveui/ReactiveUI.Avalonia) | MIT |
| [ReactiveUI.SourceGenerators](https://github.com/reactiveui/ReactiveUI.SourceGenerators) | Unrecognized by GitHub; confirm (ReactiveUI projects are MIT) |
| [SkiaSharp](https://github.com/mono/SkiaSharp) (and Linux native assets) | MIT |

## Fonts

| Font | License |
|---|---|
| [Space Grotesk](https://github.com/floriankarsten/space-grotesk) | SIL OFL 1.1; license text: `src/Flarial.Launcher/Resources/OFL-SpaceGrotesk.txt` |

## Not included

Minecraft game files are proprietary to Mojang/Microsoft. They are never bundled and are downloaded from Microsoft's servers under the user's own license.
