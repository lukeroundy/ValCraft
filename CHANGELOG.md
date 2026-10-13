# Changelog


## 0.15.2
- **Fix: Minecraft mob hitboxes in Valheim were too small and short** (you had to aim at their legs). Each stand-in now has a dedicated hit capsule sized to the MC entity's bounding box (width × height in blocks, 1 block = 1 m, already including baby size, pose/sneaking, slime size and scale) and centred on the body instead of the feet.
- The hit capsule resizes live when the MC dimensions change (EvMobList, every 0.2 s), so no protocol change is needed (still v26).
- The placeholder prefab's own child colliders are removed so they can't steal hits; the movement collider stays small (radius ≤ 0.4 m, height ≤ 1.8 m), and the hit capsule ignores the local player's collider so big mobs can't push or trap you.
- New debug option `[Mobs] DrawHitboxes` (default off, live) draws the hit capsules as red wireframes.
- ipc-test checks the MC box → capsule mapping (zombie, baby, spider, enderman, slimes, ravager, sneaking, dragon) after wire rounding.

## 0.15.1 (not released yet)
- **Item art:** all ~350 Valheim items in the Minecraft mod, and the tin and bronze items, were redrawn as crisp 16x16 pixel art (`tools/pixelart.py`). Each uses a limited hue-shifted palette with a coloured dark outline, light from the top-left, no blur, and nearest-neighbour scaling only. Templates exist per category (swords, greatswords, knives, axes, battleaxes, maces, sledges, pickaxes, spears, atgeirs, bows, crossbows, arrows/bolts, round/kite/tower shields and bucklers, ores, ingots, logs, hides, food, bowls, meads, gems, materials). The trophies are hand-tuned (boar, deer, Eikthyr, skeleton, The Elder, Bonemass, Moder, Yagluth, The Queen, Fader).
- **Norse armour:** the worn armour layers (layer_1/layer_2) and the armour icons now match the Valheim sets:
  - leather: straps and fur trim
  - troll: blue hide hood and cape
  - bronze/tin: banded plate with rivets and nasal helmets
  - iron: scale mail
  - root: bark mask with glowing eyes
  - wolf: silver mail, white fur mantle and cape, drake horns on the helmet
  - fenris: dark fur hood
  - padded: quilted with plates
  - carapace: chitin plates
  - flametal: dark plate with glowing seams
  - eitr-weave/embla: robes with trim
  - ask: hide with a red cape
- **Recipes:** every Valheim weapon, armour piece, shield and cape has a shaped recipe that mirrors Valheim's costs: the main material, a tier wood (stick → fine wood → yggdrasil → ashwood), and a tier binding (leather scraps → linen → eitr → asksvin hide), with set-specific second materials. All 240 ValCraft recipes have recipe-book unlock advancements. Bronze is copper + copper + tin.
- **Cape slot and capes:**
  - New cape slot in the inventory, next to the player preview. It is ValCraft's own implementation, so Curios is not needed; if Curios is installed, a cape in its back slot also counts. Shift-click puts a cape in the slot. The slot is hidden in the creative inventory.
  - The equipped cape renders on the player in third person and in the inventory preview, syncs to other players, and is dropped on death unless keepInventory is on.
  - Stats, shown in the tooltips:
    - deer hide: +3% speed
    - troll: sneak speed and stealth
    - wolf: frost resistance and +1 armour
    - lox: cold resistance and +2 armour
    - linen: +7% speed
    - feather: no fall damage and slow fall
    - asksvin: +6% speed and cheaper sprinting
    - ash: fire resistance
  - The cape is sent to Valheim (new event EvCape 77, additive, protocol stays v26). The Valheim player shows the matching Valheim cape while no Valheim cape is equipped.
- **Mob sounds per family:** zombie, skeleton, creeper, spider, enderman, slime, illager, animal, villager, other. Default on: the Valheim stand-in is silent and the Minecraft sound plays. Off: the Minecraft sound is muted and the Valheim stand-in keeps its sounds. The settings are plugin `[MobAudio] Swap_<family>` and mod `swapSounds.<family>`.
- **Launcher:**
  - Silent auto-update: re-checks every 30 minutes, never downgrades a newer install, and keeps its arguments across a self-update restart. `--api`/`VALCRAFT_RELEASES_API` points it at another releases endpoint (tested 0.15.0 → 0.15.1 against a mock release).
  - World backup: a BACKUP button, plus a prompt before the first launch. It zips the Valheim worlds (.db/.fwl) and the Minecraft saves into `%APPDATA%\ValCraft\backups`.
  - SETTINGS page: mob-sound families, Minecraft music, emerald chance, start Minecraft with Valheim, cross-game combat, and max mobs. It writes the plugin .cfg and `valcraft.properties` in place.
  - Store detection: Steam (`appmanifest_892970.acf`) or Epic (`ProgramData\Epic\...\Manifests`). If Valheim is missing, the launcher asks which store and queues the install (`steam://install/892970` or the Epic store page). It launches through whichever store Valheim is on.
  - Valheim update warning: compares the local appmanifest `buildid` with Steam's public build from api.steamcmd.net and shows a banner.

## 0.15.0
- New **ValCraft Launcher** (`ValCraft-Launcher.exe`): a dark, borderless launcher with a news carousel built from GitHub release notes and guide links. On open it checks the latest release, downloads the plugin DLL and mod jar with live speed and time remaining, verifies them against SHA256SUMS and installs them to the same paths the installer uses. It can update itself and starts Valheim through Steam. When offline it falls back to the cached news.
- The installer now installs the launcher to `%LOCALAPPDATA%\Programs\ValCraft`, adds a Start Menu entry, creates a desktop shortcut (optional, on by default) and offers to open the launcher at the end. Uninstall removes the launcher and its shortcuts.
- New docs pages: Getting Started, Commands, FAQ.
- New blocky Norse ValCraft icon (multi-size .ico, 16 to 256 px) for the launcher, installer, window titles, shortcuts and the docs favicon.
- Valheim stand-ins for Minecraft mobs are silent. Their AudioSources/ZSFX are disabled, audio-only entries are removed from their EffectLists, and the EffectList.Create spawns for proxy hit, death, alert, attack and footstep effects are muted. Visual effects stay and the Minecraft sounds still play.
- `make-release.sh` builds the launcher and stages `ValCraft-Launcher.exe` with the other release files.

## 0.14.3 (Minecraft mod + installer; Valheim plugin unchanged, protocol v26)
- A fresh Minecraft no longer gets stuck on the accessibility welcome screen ("Welcome to Minecraft!"). It is closed automatically, the onboarding and narrator are turned off and saved, and the automatic world load continues.
- Minecraft music is off while linked to Valheim (`muteMusic=true` in `config/valcraft.properties`; set it to false to keep the music).
- Installer: the Prism instance gets `options.txt` defaults (`onboardAccessibility:false`, `narrator:0`, `soundCategory_music:0.0`). Only missing keys are added; your own values are kept.

## 0.14.2 (protocol v26)
- Hugin/Munin (tutorial raven): right-click from Minecraft opens the dialog in a Minecraft screen (vanilla Raven.Interact, so the text is marked seen and the raven leaves). Valheim's text viewer is suppressed.
- The raven's auto-talk is forwarded to Minecraft chat and the action bar (new EvNpcSay message).
- The installer is updated to 0.14.2.

## 0.14.1 (protocol v25)
- Block changes are pushed to Valheim immediately (ServerLevel.sendBlockUpdated hook, sections sent ahead of entity traffic, changed section rebuilt first).
- Create kinetic block entities (shafts, cogs, water wheels, ...) are captured once and rotated smoothly in Valheim (new EvKinetic message); speed changes are picked up.
- Block-entity captures: face direction from the geometry, baked shading ignored (fixes dark or greenish faces).
- Plugin: `[Minecraft] Launcher` setting (Prism / ATLauncher / Custom); a fresh Prism opens for the Microsoft login, then Minecraft launches.
- New Windows installer: finds Valheim through Steam, installs BepInEx, sets up a portable Prism instance or your own launcher, and handles update/repair/uninstall and update checks.
- Public repository, GitHub Pages site, releases.

## Earlier
- **0.14.0:** generic block properties for any mod (transparency, emission, reflections, `valcraft-blocks.json`).
- **0.13.x:** multiplayer through the Valheim server; placing on Valheim surfaces; stairs; sleep and spawn across both games; Minecraft campfires warm Valheim and light its cooking stations; respawn order; persistent per-world mapping (`/valcraft offset|nudge`).
- **0.12.x:** Valheim map from Minecraft (M), 24 fps animation capture, weather sync, arrows that stick.
- **0.11.x:** lore stones, every Valheim item in Minecraft (generated), nether/end biomes, ValCraft creative tab, logo.
- **0.10.x:** tin/bronze content, Valheim chests in Minecraft, live rendering for Create block entities, mob collision with Valheim structures.
- **0.9.x:** Valheim fire/lava damage, Minecraft reach caps Valheim interactions, mob names and health.
- **0.8.x:** item bridge (Valheim drops become Minecraft items, crafted gear), the Minecraft-side Valheim build picker.
- **0.7.x:** Minecraft blocks, fluids, block entities and mobs rendered in Valheim.
- **0.5–0.6:** Valheim terrain streamed as Minecraft blocks; Minecraft terrain edits change Valheim.
- **0.2–0.4:** Minecraft HUD in Valheim, input passthrough, Valheim camera and player follow Minecraft, Valheim collision in Minecraft.
