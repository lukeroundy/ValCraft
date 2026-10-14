# ValCraft 0.15.6

Shields sit on the forearm, bows face forward, Tab cancels building, O opens the Valheim menu, deer drop deer hide, and dirt + hoe makes farmland. Protocol v27 (unchanged; build action 6 = exit build, map-toggle payload 1 = Valheim menu).

## Download
- **ValCraft-Installer.exe** (Windows): installs or updates everything, including the launcher.
- `ValCraft-Launcher.exe`: the launcher. It updates itself and the mod files.
- `valcraft-0.15.6.jar`: the Minecraft mod (Minecraft 1.21.1, NeoForge 21.1.259).
- `ValCraft.Valheim.dll`: the Valheim plugin.

## Changes
- **Shields** are real 3D models: the art is extruded 2 px thick with a handle, and they sit exactly where the vanilla shield model sits, so the vanilla transforms put them on the forearm, idle and blocking, in first and third person. In 0.15.5 the plate was offset by half a block, which made it float away from the arm and show its edge in first person. Tower shields are 14×26 px, round shields 12×22 and bucklers 12×12.
- **Bows** are no longer held backwards. The vanilla bow transforms are now combined with a 135° turn that maps our vertical bow art onto vanilla's diagonal layout. Before, the limbs pointed at the player. All pull stages are fixed too.
- **Tab** while building with the hammer cancels Valheim building (closes the piece menu and leaves placement mode) and no longer opens the Minecraft inventory.
- **O** opens Valheim's Esc menu (`Menu.Show`). It can be rebound under Controls > ValCraft ("Valheim Menu"), and O has no vanilla binding.
- **Deer hide:** Valheim deer hide now becomes `valcraft:deer_hide` in Minecraft. Rabbit hide is still accepted, and existing item maps at the old default are migrated. New shapeless recipes swap 1 deer hide and 1 rabbit hide both ways.
- **Dirt + any hoe (`#minecraft:hoes`) → farmland.** The hoe stays in the grid with 1 durability used (new recipe type `valcraft:crafting_keep_tool`).
- Tests: the self-test checks the hoe recipe (the iron hoe comes back with damage 1), the hide swaps, and that all 243 recipes and their unlock advancements load. It also takes held-item screenshots. ipc-test covers the deer hide mapping and its migration.

## Notes
- The installer and launcher are unsigned, so SmartScreen may warn: click *More info*, then *Run anyway*.
