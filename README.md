# ValCraft

**Play Minecraft inside Valheim.** A Valheim BepInEx plugin and a Minecraft (NeoForge) mod run side by side and share one world. You build with Minecraft blocks inside your Valheim world, and items, health, sleep, fire, mobs and more are shared between the two games.

- Current version: **0.15.6** (protocol v27; the plugin and the mod must be the same version, and the launcher and installer keep them in sync)
- Valheim (Steam, Windows) with BepInEx 5.4.23.x (BepInExPack_Valheim)
- Minecraft Java Edition 1.21.1 with NeoForge 21.1.259 (21.1.251 or newer loads); Java 21

## Install
Download **ValCraft-Installer.exe** from the [latest release](https://github.com/lukeroundy/ValCraft/releases/latest) and run it. Full guide: <https://lukeroundy.github.io/ValCraft/>.

The installer finds Valheim, installs BepInEx if it's missing, installs the plugin, and either sets up a portable Prism Launcher with a ready "ValCraft" instance or puts the mod into your own launcher's mods folder. Run it again to update or repair; your settings are kept. It also uninstalls.

The installer is not code-signed, so Windows SmartScreen may warn: click *More info*, then *Run anyway*.

## Repository layout
| Path | What |
|---|---|
| `valheim-plugin/` | BepInEx plugin (C#, netstandard2.1) |
| `neoforge-mod/` | NeoForge 1.21.1 mod (Java 21, Gradle) |
| `protocol/` | Shared-memory protocol (C#) and `PROTOCOL.md` |
| `ipc-test/` | Protocol tests and a fake Valheim host for testing the Minecraft side |
| `installer/` | Windows installer (WinForms, .NET Framework 4.8) and its tests |
| `tools/` | Content generators (procedural item textures), `make-release.sh` |
| `docs/` | GitHub Pages site and the development log (`DEVLOG.md`) |

## Building
- **Minecraft mod:** `cd neoforge-mod && ./gradlew build` produces `build/libs/valcraft-<ver>.jar`.
- **Valheim plugin:** `cd valheim-plugin && dotnet build -c Release`. It builds without any game files, because Valheim is accessed through reflection at runtime. Optionally pass `-p:ValheimDir="<your Valheim folder>"` to compile against your own copy of the game's assemblies, for example to try new APIs. Those files come from your own Valheim install; they are not and must not be in this repository.
- **Installer:** `cd installer && dotnet build -c Release`. It embeds the two build outputs above. Tests: `cd installer/tests && dotnet run`.
- **Protocol tests:** `ipc-test/run-linux.sh` or `run-windows.ps1`.
- **Release:** `tools/make-release.sh` stages the assets in `dist/release-<ver>/`.

No Valheim or Minecraft game files, decompiled code or proprietary assets are in this repository. The item textures are generated procedurally by `tools/gen_valheim.py`.

## Credits
- ValCraft by Luke Roundy.
- Built on [BepInEx](https://github.com/BepInEx/BepInEx), [HarmonyX](https://github.com/BepInEx/HarmonyX), [NeoForge](https://neoforged.net/) and [Mixin](https://github.com/SpongePowered/Mixin). The installer downloads [BepInExPack_Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/) (denikson) and [Prism Launcher](https://prismlauncher.org/) from their official sources.
- Tested with the [Create](https://github.com/Creators-of-Create/Create) mod (not included).
- Valheim is a trademark of Iron Gate AB; Minecraft is a trademark of Mojang/Microsoft. This project is not affiliated with either.

## License
No license has been chosen yet. Until one is added, all rights are reserved by the author.
