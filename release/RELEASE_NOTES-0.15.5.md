# ValCraft 0.15.5

A batch of fixes: hidden duplicate bodies, cooking Minecraft meat on Valheim stations, buildings on Minecraft blocks that survive unloading, bow and shield animations, hammer fixes, and materials that come back to the right slot as the right item. Protocol v27 (unchanged; build action 5 = rotate back).

## Download
- **ValCraft-Installer.exe** (Windows): installs or updates everything, including the launcher.
- `ValCraft-Launcher.exe`: the launcher. It updates itself and the mod files.
- `valcraft-0.15.5.jar`: the Minecraft mod (Minecraft 1.21.1, NeoForge 21.1.259).
- `ValCraft.Valheim.dll`: the Valheim plugin.

## Changes
- Other linked players' Viking bodies are hidden in Valheim, since you see their Minecraft player. Mob proxy stand-ins are hidden for every client (flags are synced through the ZDO).
- Cooking: raw Minecraft beef, porkchop, chicken, rabbit, mutton, cod and salmon can go on Valheim cooking stations. What comes off is the cooked Minecraft item.
- Valheim pieces built on Minecraft blocks no longer collapse when you walk away and the blocks unload. Their support is frozen until the blocks are loaded again.
- Item pickup: dropped items within 0.35 m of your aim are picked up even when the exact ray misses them. Auto-pickup range is 3 m while linked.
- Minecraft name tags are not drawn on the captured entity meshes.
- The ValCraft hammer item (`valcraft:valheim_hammer`) builds like the Valheim hammer, the same as the mace.
- Shields hold and raise like vanilla shields. Bows and crossbows show the draw and charge pose (vanilla display transforms and pull/pulling/charged/blocking item properties).
- Mob hits: with the default camera mode, mobs are no longer drawn up to a block higher or lower than the Minecraft hitbox you actually swing at.
- Step height +0.5 (`valcraft:step_bonus`).
- The scroll wheel rotates the piece while you place. Right-click with the hammer only builds and no longer uses the station or item you are aiming at.
- Shift+right-clicking a cooking station with the hammer no longer consumes the hammer.
- Materials you hand over (crafting or building) and get back go to their original inventory slots.
- Materials come back as the item you handed in: planks come back as planks, not logs.
- Tests: ipc-test covers the returned-material ledger and the cookable meat mapping.

## Notes
- The installer and launcher are unsigned, so SmartScreen may warn: click *More info*, then *Run anyway*.
