# ValCraft 0.15.7

Mobs stand on the Valheim ground again (and you hit what you see), dungeons can be entered, the Pointy Hat, mace-made gravel and sand, clay from sand in water, XP for Valheim kills, and Valheim's Rested in Minecraft. Protocol v27 plus three new events (80 mob offsets, 81 XP, 82 Rested).

## Download
- **ValCraft-Installer.exe** (Windows): installs or updates everything, including the launcher.
- `ValCraft-Launcher.exe`: the launcher. It updates itself and the mod files.
- `valcraft-0.15.7.jar`: the Minecraft mod (Minecraft 1.21.1, NeoForge 21.1.259).
- `ValCraft.Valheim.dll`: the Valheim plugin.

## Changes
- **Mobs sit on the Valheim ground again.** 0.15.5 drew MC mobs at their raw MC height, which sank them into Valheim's smooth terrain. They are re-seated on Valheim's ground as before, and Valheim now sends each mob's offset to Minecraft (new event `EvMobOffsets`), so the crosshair and your sword pick the same shifted box you see. The Valheim stand-in already follows the drawn mesh.
- **Dungeons work.** Your log showed the cause: the crypt doorway teleported the Valheim player inside (`Setting forced environment Crypt`), but ValCraft kept pinning the player to the MC position every frame, so it popped back out 2–4 s later. Now any Valheim teleport (`Player.TeleportTo`) stops the follow at once:
  - Into a dungeon/interior (Valheim y > 3000, beyond MC's build height): Valheim plays it natively, with Valheim input, camera and HUD. The MC player waits at the entrance, and health is not synced meanwhile.
  - Out again, or through a portal: once Valheim has arrived, following resumes with the same mapping and the MC player is teleported to the matching MC position.
- **Pointy Hat** (`HelmetPointyHat` ↔ `valcraft:pointy_hat`): a 3D wizard hat when worn (brim plus a bent 4-tier cone with a gold band), with its own icon. Recipe as in Valheim: 3 deer hide + 5 coal + 1 bronze (shapeless).
- **Mace + cobblestone → gravel, mace + gravel → sand.** The mace stays in the grid with 1 durability used. This works with every mace in the new `#valcraft:maces` tag (all ValCraft maces plus `minecraft:mace`). The tools that are kept are listed in `#valcraft:keep_tools`.
- **Sand in water becomes clay:** a dropped sand item that stays about 2.5 s in water (MC water, or Valheim water while linked) turns into 4 clay balls per sand, with a splash and a sound.
- **XP for Valheim kills:** about 1 point per 10 max HP, times the creature level (stars). Bosses get ×5 and at least 200 (Eikthyr 250); the cap is 5000.
- **Rested:** Valheim's Rested shows as an MC effect "Rested" with an icon, kept in sync with its remaining time. Bonus: a little less hunger and +10% XP.
- Tests: the self-test checks the mace recipes (the mace comes back with damage 1), the pointy hat recipe, the Rested effect, and that 2 sand dropped in water become 8 clay balls. ipc-test covers the mob-offset codec and the XP formula, and the generator now keeps the deer hide first.


## Notes
- The plugin and the mod must both be 0.15.7 (new events).
- The installer and launcher are unsigned, so SmartScreen may warn: click *More info*, then *Run anyway*.
