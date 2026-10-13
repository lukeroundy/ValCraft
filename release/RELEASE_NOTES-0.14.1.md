# ValCraft 0.14.1

Play Minecraft inside Valheim: one world, both games.

## Download
- **ValCraft-Installer-0.14.1.exe** (Windows): the easiest way to install. It finds Valheim, installs BepInEx if it's missing, installs the plugin and sets up Minecraft (a portable Prism Launcher, or your own launcher).
- `ValCraft.Valheim.dll`: the Valheim BepInEx plugin, for manual installs (put it in `BepInEx/plugins/ValCraft/`).
- `valcraft-0.14.1.jar`: the Minecraft mod (Minecraft 1.21.1, NeoForge 21.1.259).

Requirements: Valheim (Steam), BepInEx 5.4.23.x (BepInExPack_Valheim) and Minecraft Java Edition (Microsoft account). Java 21 is downloaded automatically by Prism.

## Changes
- Placed blocks show up in Valheim right away. Block changes are pushed at once, ahead of other traffic.
- Create water wheels, cogs and shafts rotate smoothly in Valheim, with no more periodic snap-backs. Newly placed shafts start spinning once they get speed.
- Create block-entity shading is fixed: no more dark or greenish sun-facing sides.
- Valheim plugin: new `[Minecraft] Launcher` setting (Prism / ATLauncher / Custom). With a fresh Prism, the first start opens Prism so you can log in, then launches Minecraft.
- New Windows installer: install, update, repair and uninstall. Settings and item mappings are kept. It can also check for updates and check the link.
- Protocol v25: update both sides together.

## Notes
- The installer is not code-signed, so Windows SmartScreen may show "Windows protected your PC". Click "More info", then "Run anyway".
- CurseForge has no command line for launching a profile. Start Minecraft yourself after Valheim (see the setup guide).
