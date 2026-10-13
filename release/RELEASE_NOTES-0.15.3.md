# ValCraft 0.15.3

Minecraft mobs in Valheim can now be hit where you see them, and their name tags sit above their heads. Protocol v27 (unchanged from 0.15.2).

## Download
- **ValCraft-Installer.exe** (Windows): installs or updates everything, including the launcher.
- `ValCraft-Launcher.exe`: the launcher. It updates itself and the mod files.
- `valcraft-0.15.3.jar`: the Minecraft mod (Minecraft 1.21.1, NeoForge 21.1.259).
- `ValCraft.Valheim.dll`: the Valheim plugin.

## Changes
- **Fixed mob hitboxes and name tags:** in 0.15.2, name tags and health bars still floated at the zombies' waists and hits had to go low. Each mob's hitbox, aim point and name tag now come from the size of the rendered Minecraft model, with the Minecraft bounding box only as a fallback. They also stand on the model's feet, so the name tag sits just above the head and hits land on the body.
- Debug: the log lists the computed sizes for each mob, and `[Mobs] DrawHitboxes` draws the hitboxes.

## Notes
- Protocol v27, same as 0.15.2: update both sides with the launcher or the installer.
- The installer and launcher are unsigned, so SmartScreen may warn: click *More info*, then *Run anyway*.
