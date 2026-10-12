# ValCraft 0.2.0 — Milestone 2 (Minecraft HUD in Valheim + input passthrough)

Minecraft Java 1.21.1 (NeoForge) runs **hidden** next to Valheim. Valheim shows Minecraft's hand, HUD and screens
as a transparent overlay, and with **F7** your keyboard/mouse drive Minecraft instead of Valheim. No world
blending yet (Minecraft's world is simply not drawn); that's milestone 3. Plan: `/workspace/valheim-minecraft-plan.md`.

| Folder | What |
|---|---|
| `protocol/` | `PROTOCOL.md` (v2 byte layout + connection rules) and `ValCraftLink.cs` (C# side) |
| `valheim-plugin/` | BepInEx 5 plugin (`netstandard2.1`): link host, Prism launch, overlay, input forwarding, Valheim input block |
| `neoforge-mod/` | NeoForge 21.1.259 / MC 1.21.1 mod (ModDevGradle 2.0.148, Java 21) |
| `ipc-test/` | Cross-language protocol test + "fake Valheim" host used to smoke-test the real Minecraft client |
| `docs/smoke-test/` | Frames captured from the real Minecraft client during the Linux smoke test |

## How it works (0.2.0)
- **Link:** 16.7 MB file-backed shared memory at `%LOCALAPPDATA%\ValCraft\valcraft.shm`. Valheim creates it with a new
  session id each start; Minecraft re-sends `Hello` until Valheim acks it, and re-connects automatically if either
  side restarts (see `protocol/PROTOCOL.md`).
- **Minecraft (while linked):** window hidden (GLFW), vsync off, never pauses for lost focus, world render skipped,
  background transparent, alpha fixed up so translucent GUI doesn't punch holes. Each frame its render target is
  read back asynchronously (3 PBOs + fences) into one of two shared-memory frame buffers (≤1920x1080, seq-numbered).
  It resizes its hidden window to the size Valheim requests (Valheim's screen, scaled to fit 1920x1080).
- **Valheim:** uploads the newest complete frame to a `Texture2D` on a top-most screen-space Canvas `RawImage`.
  **F7** toggles input forwarding (Valheim's own input is blocked meanwhile), **F8** shows/hides the overlay.
- **Exit:** quitting Valheim sends `Quit`; Minecraft saves and exits; Valheim kills it after 10 s if it doesn't.
  If Valheim crashes, Minecraft exits 5 s after the Valheim process disappears.

## Windows setup
Needs: Valheim + BepInExPack_Valheim 5.4.2351, Prism Launcher, and (only to build from source) .NET 8 SDK + JDK 21.
Prebuilt files are in `dist/` of `valcraft-m2.zip`: `ValCraft.Valheim.dll` and `valcraft-0.2.0.jar`.

### 1. Minecraft instance in Prism
1. **Add Instance** → Minecraft **1.21.1** → **NeoForge 21.1.259** (or newer 21.1.x). Name it `ValCraft`.
2. Put `valcraft-0.2.0.jar` into the instance's `mods` folder (right-click → *Folder* → `minecraft\mods`). Remove 0.1.0.
3. *Edit* → *Settings* → *Java arguments*: add `-Dvalcraft.startHidden=true` (hides MC as soon as it takes over the
   window from NeoForge's loading screen). Optional: `-Dvalcraft.exitWithValheim=false` to keep MC alive after Valheim.
4. Launch once by hand to sign in, create a singleplayer world, quit.
5. Prism **Settings → Minecraft**: enable closing/minimising the launcher when the game starts (label varies by
   Prism version).
6. Optional, to load straight into the world: Prism instance *Settings → Launch → join world on launch* if your Prism has
   it, or add `--quickPlaySingleplayer "<World Name>"` via the instance's game arguments (unverified in Prism UI).

### 2. Valheim plugin
Copy `ValCraft.Valheim.dll` to `<Valheim>\BepInEx\plugins\ValCraft\` (remove 0.1.0). Start Valheim once, then edit
`BepInEx\config\dev.valcraft.valheim.cfg`:
```ini
[Minecraft]
StartWithValheim = true
PrismExe = C:\Users\<you>\AppData\Local\Programs\PrismLauncher\prismlauncher.exe
InstanceName = ValCraft
StopMinecraftOnExit = true
QuitGraceSeconds = 10
[Controls]
ToggleInputKey = F7
ToggleOverlayKey = F8
MouseScale = 1
```

### 3. Test checklist (please report results + `BepInEx\LogOutput.log` and Prism's MC log)
1. Start Valheim. Log: `[link] created ... protocol v2`, `[launch] started ...`, `[block] N Valheim input methods patched`,
   `[check] Input: legacy=..., inputSystem=..., uGUI RawImage=True`, maybe `[overlay] premultiplied-alpha shader: ...`.
2. Minecraft starts, its window disappears (a NeoForge loading window may show for a few seconds first).
   Valheim log: `[link] Minecraft pid ... connected (Hello acked)`, `[overlay] frame size WxH`.
3. You see Minecraft's title screen over Valheim. Press **F7**, click **Singleplayer**, open your world.
4. In the world you should see only MC's hotbar/hearts/hand over Valheim's world. With F7 on: WASD/jump/mouse look
   move the (invisible) MC player; Valheim's character must not move. `E` opens MC inventory with a free cursor;
   `T` opens chat and typing works; `Esc` opens MC's pause menu.
5. **F7** again: Valheim gets input back, MC releases all keys. **F8** hides/shows the overlay.
6. Close MC by hand → Valheim log `heartbeat lost`; start it again from Prism → it reconnects.
7. Quit Valheim → MC saves and exits within ~10 s (Valheim log `[quit] ...`).

### Building from source
```powershell
cd valcraft\valheim-plugin; dotnet build -c Release            # stubs; add -p:ValheimDir="...\Valheim" for real game DLLs
cd ..\neoforge-mod; .\gradlew build                            # build\libs\valcraft-0.2.0.jar
cd ..\ipc-test; .\run-windows.ps1                              # protocol test: expect "[cs] PASS" and "[java] ... PASS"
```
Smoke test of the real MC client without Valheim (what was run on the Linux box):
```bash
dotnet ipc-test/build/cs/IpcTest.dll host /tmp/link.shm 1500 /tmp/cmd.txt &       # fake Valheim
VALCRAFT_SHM=/tmp/link.shm ./gradlew runClient "-PquickPlay=New World ValCraft"     # Linux: wrap in xvfb-run
echo "click 0.5 0.42" >> /tmp/cmd.txt; echo "dump /tmp/f.pam" >> /tmp/cmd.txt         # see FakeValheim.cs for commands
python3 ipc-test/pam2png.py /tmp/f.pam f.png
```

## Status of 0.2.0
Verified on the Linux build box (real NeoForge 21.1.259 client under Xvfb/Mesa, fake Valheim host):
- all mixins apply; Hello/ack connect; window hidden; resize 854x480 → 1280x720 on request
- frames arrive with transparent background, hand + HUD opaque (`docs/smoke-test/`), 0 torn frames accepted
- injected click, typed characters, Esc (pause menu, screen-open flag), mouse look (yaw), WASD + jump (position) all work
- host restart → MC reconnects to the new session; Quit → MC saves world and exits (~1.5–2.7 s);
  host killed → MC exits after the Valheim PID is gone 5 s
- protocol test: 20k ping/pong, seqlock, frames at 3 sizes incl. clamp, torn-frame detection, 1000 input events, reconnect

Not verified (no Valheim on the box): everything inside Valheim — overlay drawing, alpha look, input reading,
F7/F8, Harmony input-block targets, cursor handling, Prism launch, quit/kill. See the risks list in the hand-off.


## 0.3.0 — Milestone 3: Valheim camera + player follow Minecraft

What's new
- While a Minecraft world is loaded, the Valheim camera takes Minecraft's eye position, yaw/pitch, effective FOV and view bob every frame (Harmony postfix on `GameCamera.LateUpdate`, last priority).
- The local Valheim Player follows Minecraft's feet position, relative to an **anchor** set when following starts (you don't teleport; MC's position maps onto where you stood). **F9** re-anchors.
- The Valheim body is a kinematic, invisible proxy: Rigidbody kinematic + no gravity, `Character.UpdateMotion` skipped for the local player, renderers (body, gear, shadows) disabled. It is still a real networked Character that can be hit.
- Valheim HUD hidden while following (MC's HUD is in the overlay).
- **Vertical interim (VerticalMode=GroundSnap, default):** Valheim height = Valheim ground under you (raycast, fallback `ZoneSystem.GetGroundHeight`) + how high MC's feet are above where MC last stood. So MC jumps/falls show, and MC's own invisible terrain shape doesn't matter. Chosen because it needs no MC-side physics changes and can't drop you under Valheim terrain. Valheim walls/rocks do **not** block you yet (that's milestone 5: Valheim collision into MC). Recommended: play in a **Superflat** MC world so MC terrain never blocks your walking. `FollowMinecraft` uses MC's height directly (good with flying/creative).
- Fixes from Luke's 0.2.0 log: `PrismExe` with surrounding quotes now works; overlay now searches for a usable premultiplied-alpha shader (none of the built-ins exist in Valheim) and logs what it found.

New config (`BepInEx/config/dev.valcraft.valheim.cfg`)
- `[Sync] FollowMinecraft=true`, `HideValheimPlayer=true`, `HideValheimHud=true`, `ViewBob=true`, `VerticalMode=GroundSnap|FollowMinecraft`
- `[Controls] ReAnchorKey=F9`; `[Overlay] PremultipliedShader=true`

Windows test (0.3.0)
1. Replace `ValCraft.Valheim.dll` in `BepInEx/plugins/ValCraft/` and the jar in the Prism instance's `mods` (remove 0.2.0 jar). **Both sides must be 0.3.0** (protocol v3).
2. Fix `PrismExe` path if needed (quotes are fine now). Make a Superflat MC world.
3. Load a Valheim world, load the MC world. Log should show `[sync] reflection: ...` (all True), `[sync] camera hook: GameCamera:LateUpdate`, `[sync] 3 hooks patched`, `[sync] following Minecraft`, `[sync] anchor ...`.
4. F7, walk with WASD: Valheim view moves; looking around turns it; no Valheim body or shadow visible; Valheim HUD gone.
5. Facing check: walk forward in MC — Valheim view must move in the direction you look (not mirrored). Jump: view rises then lands on Valheim ground. Walk up a Valheim hill: view follows the terrain.
6. Sprint: FOV widens like MC. Turn MC View Bobbing on/off: Valheim camera bobs / doesn't.
7. F9 re-anchors. Disconnect MC: body/HUD come back, normal Valheim control.
8. Send the new LogOutput.log (especially the `[overlay] shaders with _SrcBlend...` line).


## 0.4.0 — crash fix + Milestone 5 part 1: Valheim collision in Minecraft

**Fixed:** pressing E (inventory) crashed MC in 0.3.0 (`NullPointerException: this.camera is null` in
`EntityRenderDispatcher.distanceToSqr` from `InventoryScreen.renderEntityInInventory`). Cause: ValCraft cancels
`LevelRenderer.renderLevel`, which is also where MC hands the camera to the entity renderers; the inventory's player doll
then had no camera. ValCraft now does those `prepare()` calls itself. Also fixed from Luke's log: ZoneSystem singleton
lookup; overlay alpha (MC now sends straight alpha because Valheim has no premultiplied UI shader).

**Collision:** Valheim exports colliders within `Radius` (32 m) — building pieces, doors, rocks, trees, static props — plus
a 1 m terrain heightfield (24 m) as boxes in MC coordinates; MC's player collides with them (step-up 0.6 works, so stairs
and 0.5 m steps are walkable) and *ignores Minecraft's own blocks* while linked (no suffocation, no server rubber-banding).
Until the first snapshot arrives the MC player hovers. `VerticalMode` default is now `FollowMinecraft` (height comes from
MC physics on Valheim geometry). Config `[Collision] ExportCollision`, `Radius`.

**Minecraft world:** any world works (MC blocks are ignored for the player), but a *Superflat/Void* world avoids MC mobs,
water and lava (those still act on you). The jar is client/singleplayer only — don't put it on a dedicated server.

Windows test (0.4.0) — both sides must be 0.4.0 (protocol v4):
1. Install DLL + jar. In Valheim, stand inside/near your base, start MC world.
2. Log should show `[collision] masks ...` and `[collision] exported N boxes (...)`; MC latest.log `[collision] N Valheim boxes`.
3. Press E, T (chat), Esc in MC: no crash. Open a crafting table/chest if you have one.
4. Walk into walls (blocked), through open doors (pass) and closed doors (blocked), up stairs (should step), onto floors and
   upper floors, around rocks/trees. Jump onto a 1 m ledge. Walk off a ledge (fall).
5. Open/close a door in Valheim with input back (F7) and check it updates in MC within ~0.25 s.
6. Send both logs; note any place you fall through or get stuck (and the piece type).


## 0.4.1 — fit, see-through fixes, MC world made irrelevant, auto world, auto input

- **Hitbox = Valheim capsule:** Valheim reads the local player's CapsuleCollider (width = 2 x radius, height) and sends it to MC
  (event 20); MC scales every player pose (eye = 0.9 x height), client and integrated server, re-applied after respawn.
  `[Sync] HitboxWidth/HitboxHeight` (0 = auto), `HitboxScale`. Rotated wall slices are now 0.15 m (less inflation).
- **See-through walls:** Valheim camera near clip = `[Sync] NearClip` (0.05 m) while following, restored afterwards.
- **MC terrain irrelevant while linked:** no in-wall/underwater/fire screen overlays, no fluid physics/drowning, no
  fire/cactus/cobweb/portal effects from MC blocks, no suffocation, crosshair never targets MC blocks (entities still pickable),
  hand lit full-bright. Also while waiting for Valheim's first collision snapshot (0.4.0 could suffocate there).
- **No world selection:** linked MC on the title screen opens/creates world `ValCraft` (void generator, survival, cheats on).
  `config/valcraft.properties` in the Prism instance: `autoWorld`, `worldName`, `gameMode`, `generator=void|flat`.
- **No control swapping:** `[Controls] AutoInput=true` — input goes to MC whenever following and no Valheim UI (menu,
  inventory, map, chat, console, text input, trader) is open. F7 now temporarily hands controls to Valheim (press again to return).
  `AutoInput=false` restores the old F7 toggle.
- From Luke's 0.4.0 log: his old config kept `VerticalMode=GroundSnap` -> migrated once to FollowMinecraft; a one-frame
  state blip stopped following and re-anchored (~2 m jump) -> last good state is kept, 1 s debounce, anchor kept per player.

Windows test (0.4.1): install both; delete nothing. Start Valheim -> MC should go straight into world "ValCraft" and you control
MC without pressing F7. Check doorways, walls up close (no see-through), Tab/Esc in Valheim after F7, MC chat/inventory.

## Roadmap: Valheim terrain as real MC blocks (Luke's request (c))
Phase A (next, 0.5.0): Valheim -> MC voxel terrain. Valheim samples heightmap + biome (Heightmap/WorldGenerator) in a
  ring around the player (e.g. 48 m), sends chunks (16x16 columns: height, biome, top material) over a new ring/region; MC
  fills the void world with grass/dirt/stone/sand/snow by biome, 1 block = 1 m, aligned to the anchor (anchor snapped to
  integer + offset so blocks line up). Terrain collision boxes are then dropped (pieces/rocks stay boxes); MC terrain stops
  being ignored for collision (only the old non-ValCraft MC blocks issue disappears because the world is void).
Phase B: MC break/place -> Valheim. MC sends BlockChanged(x,y,z,old,new); Valheim maps to a world point and applies
  TerrainComp/TerrainModifier (lower/raise at that cell, level = new top) — the same op the hoe/pickaxe use, so it's saved and
  networked by Valheim. Rocks: hits on MineRock/Destructible within the box -> Damage(HitData) with pickaxe damage.
Phase C: Valheim -> MC terrain edits (other players digging) re-sync the affected chunks; drops/item mapping.
Risks: heightmap->block quantisation (1 m steps vs smooth slopes: stairs/slabs later), chunk volume (send on demand),
  multiplayer authority (Valheim owns terrain; MC edits are requests).


## 0.4.2 — fixes for the 0.4.1 regression
- **No overlay / Valheim body visible:** MC's render thread is blocked for a few seconds while the auto-world opens. Valheim
  declared MC dead after 2 s without a heartbeat and then only accepted a fresh Hello, which an already-linked MC never sends
  -> stuck unlinked forever. Fixed both ends: MC heartbeats from a background thread (max gap in test: 118 ms during world
  load), Valheim only logs "not responding" after 2 s, drops after 20 s, and re-links automatically when the same MC's
  heartbeat resumes. A `[link] status:` line is logged every 30 s while not connected/following, saying why.
- **Prism opened but didn't launch:** `--launch` takes the instance *ID* = folder name (Luke's is `1.21.1`, display name
  `valcraft`); config said `ValCraft`. The plugin now lists Prism's instances and resolves `PrismInstance` by folder name,
  case-insensitive folder name, display name (instance.cfg `name=`), or the only instance with a valcraft jar, and logs
  `[launch] instance "X" -> ID "Y" (why)`. Honours portable installs and `InstanceDir=` in prismlauncher.cfg.


## 0.4.3
- **Hitbox:** 0.4.2 auto width was 0.98 m (Valheim capsule radius 0.49) — almost a full door opening. Default
  `HitboxWidth=0.5` (existing auto configs migrated once), height still from the capsule (1.85 m). `0` = old auto.
- **Third person (F5):** MC draws only your player entity (model, armour, held items) into the overlay frame, lit full-bright;
  the Valheim camera already follows MC's 3rd-person camera position. The 3rd-person camera distance is limited by the
  Valheim collision boxes (not MC's hidden blocks). Limitation: no depth exchange, so Valheim walls don't hide your MC body.
- **Interact with Valheim objects:** look at a door/chest/bed/station/item: Valheim's hover text appears under MC's
  crosshair (`[E]` shown as `[Right-click]`). Right-click -> Valheim `Player.Interact` (same as E). Priority: an MC entity
  under the crosshair wins, then Valheim's target (nearest along the camera ray within 3.5 m of your eye), then MC's normal
  item use. Holding right-click interacts once per press.
- **Overlay only during gameplay:** hidden in Valheim's main menu/character select/loading (no local player) and the Esc menu.


## 0.5.0 — Phase A: Valheim terrain as real Minecraft blocks
- Valheim samples its terrain around the player's MC position (`[Terrain] Radius`, 48 m) in 8x8-column tiles on the MC
  block grid (1 block = 1 m on the anchor): height by terrain raycast (fallback ZoneSystem), material from biome
  (Meadows/Plains grass, Black Forest podzol, Swamp mud, Mountain/Deep North snow, Ashlands blackstone, Mistlands gravel,
  Ocean/below sea level sand), paint (cultivated -> farmland, cleared -> gravel, paved -> cobblestone) and slope (steep -> stone).
  Nearest tiles first, 6 per frame; tiles are rescanned continuously and re-sent only if changed (hoe/pickaxe edits).
- MC's integrated server writes each column (surface, 3 sub-surface, stone, 16 deep), only changed columns, 24 tiles/tick,
  no neighbour updates. Blocks are not drawn (MC world render stays off): they are collision, step-up and mining.
- Vertical mapping is fixed: **MC Y = Valheim Y + `[Terrain] YOffset` (32)**: sea level 30 -> 62; Valheim -96..288 fits
  MC -64..320. On linking, the MC player is teleported to that height (Valheim waits until MC confirms before following).
- Terrain collision boxes are no longer sent; buildings/rocks/trees remain boxes. MC blocks count for collision again and
  are pickable/minable. MC holds the player until the ground under them has arrived.
- Phase B hook: MC block break/place events are sent to Valheim and logged
  (`[terrain] MC broke minecraft:grass_block at MC (x,y,z) = Valheim (...) — not applied ... yet`).
- `[Terrain] TerrainBlocks=false` returns to 0.4.x behaviour.

## Roadmap
- **Phase B (terrain edits -> Valheim):** apply the logged break/place events via TerrainComp/TerrainModifier (lower/raise
  the cell), damage MineRock/Destructible for rock hits; Valheim-side edits already flow back via tile rescans.
- **Phase C (items, Luke's design):** a configurable two-way item mapping table (e.g. `valcraft-items.cfg`):
  `Wood <-> minecraft:oak_log`, `Stone <-> minecraft:cobblestone`, `CopperOre <-> minecraft:raw_copper`,
  `Coal <-> minecraft:coal`, `TinOre <-> ...`, with counts. Valheim harvesting (trees, rocks, pickables, deposits) gives
  the mapped MC items instead of / in addition to Valheim drops (hook ItemDrop pickup / Pickable / Destructible drops ->
  EvGiveItem to MC, server gives item). Right-clicking a Valheim station (campfire, fireplace, smelter, kiln, charcoal
  kiln, workbench) holding a mapped MC item: MC sends EvUseItemOn(itemId,count) with the interact; Valheim adds the mapped
  Valheim item to the station (Smelter.QueueOre/AddFuel, Fireplace fuel, Container) and MC consumes the stack on ack.
  Risks: duplication on lost acks (use request ids + ack), station APIs differ per type, multiplayer ownership.

## 0.5.1: smooth terrain movement
- Valheim now samples the true ground at 0.5 m (17x17 per tile) and also sends `EvTerrainHeights` (26): 4 m cells of 9x9
  samples (cm above a base). MC's linked player is carried by this bilinear heightfield (feet = true Valheim height,
  sticks to the ground going downhill, counts as on-ground). Terrain blocks are ignored for **movement only**. Anything
  sticking out more than 0.25 m above the smooth surface still blocks (placed blocks, stale blocks). Mining and raycasts still hit the blocks.
- Block tops now use floor(height) instead of round(height), so blocks never stick out above the real ground.
- Stale terrain from an earlier session/anchor (saved in the ValCraft world) is cleared above each column the first
  time it's written in a session (terrain block types only).
- Integrated server no longer rubber-bands the player for overlapping terrain blocks; LocalPlayer push-out disabled on smooth terrain.
- Valheim camera: small vertical steps (0.3–1.1 m, e.g. stair/box edges) are smoothed (decays in ~0.1 s).
- Debug: `VC_DEBUGMOVE=true` (dev run) / `-Dvalcraft.debugMove=true` logs collide in/out vs ground height.

## 0.6.0: Phase B (MC edits and hits change Valheim)
- **Terrain edits:** breaking a terrain-type block (grass/dirt/stone/sand/gravel/snow/mud/...) that lies in Valheim's ground
  levels that 1 m cell down to the block's bottom; placing a terrain-type block resting on the ground levels it up to the
  block's top. Done with Valheim's own `TerrainOp` (level op, radius `TerrainEdit` 0.6 m square, paints dirt when digging)
  -> `TerrainComp.ApplyOperation` RPC: networked and saved with the world like pickaxe/hoe edits. Valheim's +-8 m edit limit
  applies; wards (PrivateArea) block it. The edited tile (and neighbours) is re-sampled 0.3/1.2/3 s later and re-sent,
  so MC blocks and the smooth heightfield follow and you drop into holes. Config `[Terrain] EditTerrain`.
- **Hits:** Valheim rays from its camera every 0.1 s; if the nearest thing within 3.5 m is an `IDestructible` (rocks,
  ore deposits MineRock/MineRock5, trees, logs, stumps, other destructibles) MC is told, and MC left-click then sends a hit
  instead of mining/attacking in MC (one per click, ~0.7 s while held, arm swings). Valheim applies a `HitData` with
  pickaxe (MC pickaxe) or chop (MC axe) damage and tool tier from `[Hits] ToolTiers` / `ToolDamage` (per MC tier
  wood,stone,iron,gold,diamond,netherite; defaults 0,1,2,1,3,4 and 10,18,30,22,45,60) through `IDestructible.Damage` (networked RPC).
  Valheim drops spawn as Valheim items (MC items come with Phase C).
- **Non-terrain MC blocks** (planks, etc.) stay MC-only: they collide in MC but are invisible in Valheim (MC world isn't
  drawn). Next proposed step: render MC-placed blocks in Valheim (mesh per chunk from MC block models/atlas, sent over IPC).

## 0.6.1
- Fix: terrain digging did nothing. Valheim's TerrainComp.ApplyOperation sends the TerrainOp's *prefab hash*; the receiver
  looks the prefab up in ZNetScene for its settings, and our code-made "ValCraft_TerrainOp" wasn't registered ("Failed to
  deserialize TerrainOp settings for prefab hash -1716889444, cancelling TerrainOp"). Now a template prefab
  `ValCraft_TerrainLevel` is registered in ZNetScene (each time it's recreated) and cloned at the edit point.
  Multiplayer: every player needs the plugin so their ZNetScene knows the prefab.
- Diagnostics: `[terrain-edit] check MC (x,z): Valheim ground now .. target .. -> OK / NOT APPLIED` 1 s after each edit;
  `[hit] aiming at <object>` when a hittable Valheim object is targeted; `[hit] MC attack but no Valheim target`.

## 0.6.2
- Fix (for real): digging. 0.6.1's registered prefab was still rejected by TerrainComp's RPC handler ("Failed to
  deserialize TerrainOp settings for prefab hash 131645091"). Terrain edits now call the owner-side
  `TerrainComp.DoOperation(pos, TerrainOp.Settings)` directly on each touched heightmap's TerrainComp (created if needed via
  `Heightmap.GetAndCreateTerrainCompiler`), then poke the heightmap. Saved in the TerrainComp ZDO (persists, replicates).
  Signatures checked against publicized assembly_valheim (ValheimGameLibz 0.221.12). If another peer owns the TerrainComp,
  we claim ownership first (multiplayer caveat).

## 0.6.3
- Fix: digging/placing terrain. 0.6.2 failed because `TerrainComp.DoOperation(Vector3, TerrainOp.Settings)` doesn't exist in
  the current Valheim build (log: `TerrainComp.DoOperation=False`), so every edit was skipped (with a misleading "TerrainOp
  not found" line). Now the plugin writes TerrainComp's per-vertex arrays directly (`m_levelDelta`/`m_smoothDelta`/
  `m_modifiedHeight`, dirt paint via `m_paintMask`/`m_modifiedPaint`), then ClaimOwnership, `m_operations++`, `Save()`,
  `Heightmap.Poke(false)`. That's how JereKuusela's World Edit Commands edits terrain (public domain:
  https://github.com/JereKuusela/valheim-world_edit_commands, TerrainOperations.cs / TerrainSave.cs / TerrainSelect.cs).
- Startup diagnostic lists any missing TerrainComp member; each edit logs `wrote N vertices on K TerrainComp(s) to y=..`.

## 0.6.4
- TerrainComp/Heightmap methods are now resolved by scanning the live types (exact name, then name-contains, any bool/int/float params); full method lists of both types are logged at first edit. A missing Save/Poke no longer disables edits (edits apply in-session; a warning says they won't persist).
- Placement "resting" check relaxed: a terrain block counts as resting when Valheim ground is inside the block or up to ~1 m below its bottom (block tops are floored); terrain is raised to the block top.

## 0.7.0
- Minecraft blocks that aren't Valheim terrain (builds, placed blocks) are drawn inside Valheim: MC streams 16³ sections within `[Blocks] Radius` (64) over the link (protocol v5), Valheim builds face-culled meshes textured from a PNG atlas of MC's block textures (`%LOCALAPPDATA%\ValCraft\blocks-atlas.png`), with MeshColliders on the `piece` layer (`[Blocks] Collision`). Toggle: `[Blocks] RenderBlocks`.
- Terrain edits use the confirmed 1.0.17 API first: `TerrainComp.Save(bool)`, `Heightmap.Poke(int,bool)`.
- IPC test adds a Java→C# block-stream + mesher test (`BlockCodecTest`, `IpcTest blocks`).

## 0.7.1
- Water/lava drawn (sloped surfaces, still/flow textures, water tinted + translucent, no collision); glass/stained glass/ice/slime/honey on a translucent material with same-block face culling; plants (flowers, grass, saplings, crops, sugar cane, ...) as X-shaped cutout quads without collision.
- Valheim picks separate opaque / cutout / translucent shaders and logs every loaded shader name once (for tuning).

## 0.7.2 (protocol v6)
- Non-cube blocks (torches, lanterns, chains, doors, rails, stairs, slabs, ...) use their real Minecraft model quads and UVs; collision still from the block shape.
- Light-emitting blocks: pooled Unity point lights (nearby same-colour light blocks merged per 8³ cell, nearest/brightest `[Blocks] MaxLights`=32, no shadows) + emissive material. `[Blocks] Lights`.
- `[Blocks] Reflections`: glossy material for metal/polished/glass/ice/water and one 64 px realtime reflection probe at the player (every 3 s).

## 0.7.3 (protocol v7)
- Block entities (chests, ender chests, beds, signs, banners, skulls, shulker boxes, decorated pots, bells, conduits...) captured from their renderers; all blocks now use real model quads (fixes piston/rotated textures); beacons render (no beam).
- Animated textures (water, lava, fire, sea lantern, prismarine, magma, portal...) via GPU tile copies.
- Entities: dropped items (spin/bob), armor stands, item frames, falling blocks, primed TNT, minecarts, boats; `[Blocks] MaxEntities`=64.
- TNT/explosions lower Valheim terrain (one dig per column).

## 0.7.4 (protocol v8)
- Mobs, villagers and other players rendered (animated by re-capture, `[Blocks] MaxMobs`=24).
- Chest / ender chest / shulker box lids animate (live block-entity capture per 1/8 openness).
- Decorated pots: logs why capture fails and falls back to a built-in pot mesh.
- Shader pick now honours priority across loaded shaders: solid blocks use `Standard` (override `[Blocks] OpaqueShader`).
