{
  description = "Flarial Client launcher for Minecraft Bedrock Edition on Linux";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixpkgs-unstable";

  outputs =
    { self, nixpkgs }:
    let
      systems = [ "x86_64-linux" ];
      forAllSystems = nixpkgs.lib.genAttrs systems;

      version = builtins.replaceStrings [ "\n" ] [ "" ] (builtins.readFile ./version.txt);
    in
    {
      packages = forAllSystems (
        system:
        let
          pkgs = nixpkgs.legacyPackages.${system};

          unwrapped = pkgs.buildDotnetModule {
            pname = "flarial-launcher";
            inherit version;

            src = pkgs.lib.cleanSource ./.;

            projectFile = "src/Flarial.Launcher/Flarial.Launcher.csproj";
            nugetDeps = ./packaging/nix/deps.json;

            dotnet-sdk = pkgs.dotnetCorePackages.sdk_10_0;
            dotnet-runtime = pkgs.dotnetCorePackages.runtime_10_0;

            selfContainedBuild = true;
            executables = [ "Flarial.Launcher" ];

            meta.description = "Launcher for the Flarial Client (Minecraft Bedrock GDK) on Linux";
          };

          flarial-launcher = pkgs.buildFHSEnv {
            name = "flarial-launcher";
            inherit version;

            runScript = "${unwrapped}/bin/Flarial.Launcher";

            targetPkgs =
              p: with p; [
                bash
                cairo
                coreutils
                curl
                dbus
                fontconfig
                freetype
                gdk-pixbuf
                glib
                gnutar
                gtk3
                gzip
                libGL
                libgcc
                libglvnd
                libsecret
                libsoup_3
                libunwind
                libxcb
                libxcursor
                libxext
                libxfixes
                libxi
                libxkbcommon
                libxrandr
                libxrender
                ncurses
                openssl
                python3
                stdenv.cc.cc.lib
                util-linux
                vulkan-loader
                webkitgtk_4_1
                xdg-utils
                xprop
                zlib
                zstd
              ];

            extraInstallCommands = ''
              install -Dm644 ${./assets/flarial-launcher.desktop} \
                "$out/share/applications/flarial-launcher.desktop"
              substituteInPlace "$out/share/applications/flarial-launcher.desktop" \
                --replace-fail "@EXEC@" "$out/bin/flarial-launcher"

              install -Dm644 ${./assets/flarial-launcher.png} \
                "$out/share/icons/hicolor/256x256/apps/flarial-launcher.png"
            '';

            meta = {
              description = "Launcher for the Flarial Client (Minecraft Bedrock GDK) on Linux";
              homepage = "https://flarial.xyz";
              license = pkgs.lib.licenses.gpl3Only;
              mainProgram = "flarial-launcher";
              platforms = [ "x86_64-linux" ];
            };
          };
        in
        {
          flarial-launcher = flarial-launcher;
          default = flarial-launcher;
        }
      );

      apps = forAllSystems (system: {
        default = {
          type = "app";
          program = "${self.packages.${system}.flarial-launcher}/bin/flarial-launcher";
        };
      });

      devShells = forAllSystems (
        system:
        let
          pkgs = nixpkgs.legacyPackages.${system};
        in
        {
          default = pkgs.mkShell {
            packages = with pkgs; [
              dotnetCorePackages.sdk_10_0
              zstd
              openssl
              python3
              libsecret
              vulkan-tools
              xprop
            ];
          };
        }
      );

      formatter = forAllSystems (system: nixpkgs.legacyPackages.${system}.nixfmt);
    };
}
