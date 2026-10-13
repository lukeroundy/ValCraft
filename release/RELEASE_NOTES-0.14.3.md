# ValCraft 0.14.3

Minecraft mod and installer update. The Valheim plugin is unchanged from 0.14.2 (protocol v26).

## Download
- **ValCraft-Installer.exe** (Windows): installs or updates everything. Your settings are kept.
- `valcraft-0.14.3.jar`: the Minecraft mod (Minecraft 1.21.1, NeoForge 21.1.259).
- `ValCraft.Valheim.dll`: the Valheim plugin (same as 0.14.2).

## Changes
- A fresh Minecraft no longer gets stuck on the accessibility welcome screen ("Welcome to Minecraft!"). It is closed automatically, the onboarding and narrator are turned off and saved, and the automatic world load continues.
- Minecraft music is off while linked to Valheim (`muteMusic=true` in `config/valcraft.properties`; set it to false to keep the music).
- Installer: the Prism instance gets `options.txt` defaults (`onboardAccessibility:false`, `narrator:0`, `soundCategory_music:0.0`). Only missing keys are added; your own values are kept.

## Notes
- The installer is unsigned, so SmartScreen may warn: click *More info*, then *Run anyway*.
