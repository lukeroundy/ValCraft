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
