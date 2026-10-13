# ValCraft 0.15.2

Minecraft mobs in Valheim now have proper hitboxes, and crafting in Valheim gives you the Minecraft item. **Protocol v27:** Valheim and Minecraft must both be on 0.15.2. The launcher and installer update both sides for you.

## Download
- **ValCraft-Installer.exe** (Windows): installs or updates everything, including the launcher.
- `ValCraft-Launcher.exe`: the launcher. It updates itself and the mod files.
- `valcraft-0.15.2.jar`: the Minecraft mod (Minecraft 1.21.1, NeoForge 21.1.259).
- `ValCraft.Valheim.dll`: the Valheim plugin.

## Changes
- **Fixed mob hitboxes:** Minecraft mobs in Valheim were too small and short to hit (you had to aim at their legs). Each one now has a hit capsule the size of its Minecraft bounding box, centred on the body. It follows baby size, poses, sneaking, slime size and scale as they change. The placeholder's own colliders no longer steal hits, big mobs can't push or trap you, and the debug option `[Mobs] DrawHitboxes` draws the hitboxes.
- **Crafting in Valheim gives the Minecraft item:** what you craft at any station or by hand goes to your Minecraft inventory as the ValCraft item (350 mapped weapons, tools, armour, capes, food and materials). Quality and crafter are kept and shown in the tooltip, and higher quality raises damage, armour and durability. Materials are used exactly once, and nothing is duplicated. Unmapped items stay in Valheim; if the Minecraft inventory is full, the item drops at your feet.
- **Upgrades:** while a crafting station is open, your ValCraft gear (main inventory, not worn armour) shows up in Valheim's upgrade tab. Upgraded items go back to Minecraft at the new quality.
- **New setting:** `PreferMinecraftItems` (default on), also on the launcher's Settings page. Turn it off to keep crafted items in Valheim.

## Notes
- Protocol v27: a 0.15.1 plugin and a 0.15.2 mod (or the other way round) won't connect. Update with the launcher or the installer.
- The installer and launcher are unsigned, so SmartScreen may warn: click *More info*, then *Run anyway*.
