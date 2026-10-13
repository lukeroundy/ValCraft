# Changelog

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
