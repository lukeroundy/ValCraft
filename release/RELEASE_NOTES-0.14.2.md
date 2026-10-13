# ValCraft 0.14.2

## Download
- **ValCraft-Installer.exe** (Windows): installs or updates everything. Your settings and item mappings are kept.
- `ValCraft.Valheim.dll`: the Valheim BepInEx plugin (`BepInEx/plugins/ValCraft/`).
- `valcraft-0.14.2.jar`: the Minecraft mod (Minecraft 1.21.1, NeoForge 21.1.259).

## Changes since 0.14.1
- **Hugin/Munin can be used from Minecraft.** Right-click the tutorial raven: the dialog (topic and text) opens in a Minecraft screen, the tutorial is marked as seen and the raven flies off as usual. Valheim's own text window no longer grabs input.
- **The raven's own talk reaches Minecraft.** When Hugin speaks as you come near, the text appears in Minecraft chat, with a hint on the action bar.
- Protocol v26 (new EvNpcSay message): update both sides together.
- The installer is now 0.14.2 and contains the new plugin and mod.

Changes in 0.14.1, for reference: instant block updates, smooth rotation for Create kinetic blocks, fixed shading on block-entity captures, the Windows installer, the `[Minecraft] Launcher` setting (Prism / ATLauncher / Custom) and the Prism first-login flow.

## Notes
- The installer is unsigned, so SmartScreen may warn: click *More info*, then *Run anyway*.
- CurseForge can't be started automatically (it has no launch command line). Start Minecraft yourself after Valheim.
