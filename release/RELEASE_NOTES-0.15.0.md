# ValCraft 0.15.0

The new ValCraft Launcher, a new icon, docs pages, and silent mob stand-ins. Protocol v26 (unchanged).

## Download
- **ValCraft-Installer.exe** (Windows): installs or updates everything, including the launcher. Your settings are kept.
- `ValCraft-Launcher.exe`: the launcher on its own. The installer already includes it.
- `valcraft-0.15.0.jar`: the Minecraft mod (Minecraft 1.21.1, NeoForge 21.1.259).
- `ValCraft.Valheim.dll`: the Valheim plugin.

## Changes
- New **ValCraft Launcher**. It checks GitHub for updates, downloads and verifies (SHA-256) the plugin and mod with live progress, and starts Valheim through Steam with one click.
- The Valheim creatures standing in for Minecraft mobs are now silent: no idle, footstep, alert, attack, hit or death sounds. Visual effects remain, and you hear the Minecraft mob sounds.
- The launcher shows news from the release notes and has links to the guides. It updates itself and still works offline using the last news it saved.
- The installer puts the launcher in `%LOCALAPPDATA%\Programs\ValCraft`, adds a Start Menu entry and creates a desktop shortcut (optional, on by default). Uninstall removes all three.
- New blocky Norse ValCraft icon, used for the launcher, the installer, the shortcuts and the website favicon.
- New docs pages: [Getting Started](https://lukeroundy.github.io/ValCraft/getting-started.html), [Commands](https://lukeroundy.github.io/ValCraft/commands.html), [FAQ](https://lukeroundy.github.io/ValCraft/faq.html).

## Notes
- The installer and launcher are unsigned, so SmartScreen may warn: click *More info*, then *Run anyway*.
- The launcher can update itself starting with the next release.
