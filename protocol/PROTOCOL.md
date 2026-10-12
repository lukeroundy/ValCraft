# ValCraft shared-memory protocol v2 (ValCraft 0.2.0)

File-backed memory map. Windows: `%LOCALAPPDATA%\ValCraft\valcraft.shm`; override with env `VALCRAFT_SHM`.
Size: **0xFF4000 bytes (16,728,064)**. Little-endian. Valheim is the **host**: it creates/sizes the file, zeroes
everything below 0x22000, writes a new random non-zero **session id**, and writes `magic` last (release).
Minecraft opens the file and waits until magic+version match. v1 peers are rejected by version.

## Header
| Offset | Type | Field | Writer |
|---|---|---|---|
| 0x000 | u32 | magic `0x434C4156` ("VALC") | V |
| 0x004 | u32 | version = **2** | V |
| 0x008 | u64 | file size | V |
| 0x010 | u32 | Valheim PID | V |
| 0x014 | u32 | Minecraft PID | MC |
| 0x018 | i64 | Valheim heartbeat (Unix ms, every 100 ms) | V |
| 0x020 | i64 | Minecraft heartbeat (Unix ms, every 100 ms) | MC |
| 0x028 | u32 | Valheim flags: 1 in-game, 2 menu open, 4 loading, **8 input forwarded (F7)**, **16 quitting** | V |
| 0x02C | u32 | Minecraft flags: 1 world loaded, 2 window hidden, **4 screen open / mouse not grabbed** | MC |
| 0x030 | u32 | **session** (new random value each time Valheim initialises the file) | V |
| 0x034 | u32 | session Minecraft has been acked for | MC |

## Connection, reconnect, shutdown
1. MC sends `Hello {u32 pid, u32 protocolVersion, u32 sessionSeen}` every 500 ms until acked.
2. Valheim answers a Hello whose `sessionSeen` equals its session with `HelloAck {u32 session, u32 mcPid}`.
   Stale Hellos (old session) are ignored; MC re-sends with the new one.
3. MC is "active" only after the ack: hides its window, skips world rendering, accepts input, exports frames.
4. **Valheim restart:** the session changes → MC drops to disconnected, releases input, re-Hellos. Ring
   counters reset to 0; readers that see `tail > head` or `head - tail > 256` resync to head.
5. **MC restart:** Valheim sees the heartbeat go stale (2 s), turns F7 forwarding off, and acks the new Hello.
6. **Valheim quits:** sets flag 16 and sends `Quit`. MC calls `Minecraft.stop()` (saves the world). Valheim waits
   `QuitGraceSeconds` (10) for the MC process to exit, then kills it. If Valheim dies without Quit, MC exits
   after the Valheim PID has been gone 5 s (`-Dvalcraft.exitWithValheim=false` to keep it running).
7. On start Valheim reads the old MC heartbeat before re-initialising; if < 3 s old it does not launch Prism again.

## PlayerState @0x100 (seqlock, MC writes once per rendered frame)
`u32 seq` (odd while writing), pad, @+8 `i64 frameId`, `f64 x,y,z`, `f32 yaw,pitch,roll,fovDeg`, `u32 flags`.

## Frames (MC writes, Valheim reads)
| Offset | Type | Field |
|---|---|---|
| 0x200/0x204 | u32 | requested width/height (Valheim: its screen size scaled to fit 1920x1080, aspect kept) |
| 0x208/0x20C | u32 | max width/height = 1920/1080 |
| 0x210 | u64 | latest published frame number (0 = none) |
| 0x218 | u32 | index (0/1) of the buffer holding it |
| 0x240 + i*0x40 | | buffer i header: `u64 seq` (2n-1 while writing frame n, 2n when done), `u32 w, h, stride, format` (1 = RGBA8, rows bottom-up) |
| 0x22000 | | buffer 0 pixels, 0x7E9000 bytes (1920*1080*4) |
| 0x80B000 | | buffer 1 pixels |

Writer: pick the buffer **not** named by `latest index`; set its seq odd; copy; write w/h/stride/format; seq even;
then `latest index`, then `latest frame` (release). Reader: read latest frame/index, check buffer seq even, copy,
then re-read buffer seq: if it changed, the writer lapped the reader → discard (never shown torn). MC resizes its
hidden window to the requested size; frames always carry their real size, so the reader adapts to anything ≤ max.
Pixels are MC's main render target after GUI: straight RGBA where the world is not drawn (alpha 0 background).

## Rings (SPSC, fixed 256 × 256-byte slots)
Ring A Valheim→MC @0x1000, Ring B MC→Valheim @0x11080. Header: `u64 head` @+0, `u64 tail` @+0x40; slots @+0x80.
Slot: `u16 type, u16 0, u32 length, u8 payload[248]`. Full when head − tail == 256.

## Event types (payloads little-endian)
| Type | Name | Dir | Payload |
|---|---|---|---|
| 1 | Hello | MC→V | u32 pid, u32 version, u32 sessionSeen |
| 2 / 3 | Ping / Pong | both | opaque, echoed |
| 4 | HelloAck | V→MC | u32 session, u32 mcPid |
| 5 | Quit | V→MC | – |
| 10 | Key | V→MC | i32 glfwKey, i32 scancode (0), i32 action (0 release, 1 press), i32 glfwMods |
| 11 | Char | V→MC | u32 codepoint, i32 mods |
| 12 | MouseMove | V→MC | f32 dx, f32 dy (pixels, +y down), f32 nx, f32 ny (0..1, top-left origin) |
| 13 | MouseButton | V→MC | i32 button (0 left, 1 right, 2 middle), i32 action, i32 mods |
| 14 | Scroll | V→MC | f32 dx, f32 dy |
| 15 | ReleaseAll | V→MC | – (F7 off: MC releases every key/button it holds) |

MC applies MouseMove as relative (dx/dy) while the mouse is grabbed (gameplay) and absolute (nx/ny × window size)
while a screen is open.


## v3 changes (ValCraft 0.3.0)
PlayerState slot (0x100, seqlocked as before) extended. Offsets relative to slot start:

| off | type | field |
|---|---|---|
| 0 | u32 | seq (odd = writing) |
| 8 | i64 | frameId |
| 16/24/32 | f64 | camera eye x/y/z (MC coords) |
| 40/44/48/52 | f32 | yaw, pitch, roll, effective vertical FOV (deg) |
| 56 | u32 | flags: 1 onGround, 2 flying, 4 crouching, 8 inWater, 16 thirdPerson, 32 hasPlayer, 64 viewBob |
| 60/64 | f32 | view-bob translation x/y (MC view space, applied to the world) |
| 68/72 | f32 | view-bob roll (Z) / pitch (X) degrees (applied to the world) |
| 80/88/96 | f64 | player feet x/y/z (interpolated) |
| 104 | f32 | eye height |

### Coordinate mapping
MC: X east, Y up, Z south, right-handed; yaw 0 = facing +Z (south), +90 = facing -X (west); pitch + = down.
Valheim/Unity: X east, Y up, Z north, left-handed. 1 block = 1 m.
`unity = anchor + (x, y, -z)`, `unityYaw = mcYaw + 180`, `unityPitch = mcPitch`. Implemented once in `CoordMap` (ValCraftLink.cs) and unit-tested by the IPC test.


## v4 changes (ValCraft 0.4.0)
File grows to 0x1174040 bytes. Collision region at `OffColl = 0xFF4000` (Valheim writes, MC reads):

| off | type | field |
|---|---|---|
| +0 | i32 | latest buffer index (0/1, -1 = none) |
| +4 | i32 | snapshot counter (0 = nothing published yet) |
| +16 + i*16 | u32, i32 | buffer i: seq (odd = writing), box count |
| +0x40 + i*32768*24 | f32[6] x count | AABBs in MC coords: minX minY minZ maxX maxY maxZ |

Writer fills the non-latest buffer (seq odd -> data -> count -> seq even), then publishes index and bumps the counter.
Reader copies the latest buffer and re-checks seq (retry if changed). A snapshot with 0 boxes means "Valheim collision off".
MC also converts exported frames to straight (non-premultiplied) alpha by default (`-Dvalcraft.unpremultiply=false` to disable).


## v5 changes (ValCraft 0.7.0): Minecraft blocks rendered in Valheim
File layout unchanged; version = **5**. New ring events (little-endian). MC sends only blocks that are not mirrored
Valheim terrain (terrain-type blocks at/below the column's mirrored terrain top are skipped), in 16³ sections.

| Type | Name | Dir | Payload |
|---|---|---|---|
| 30 | BlockSyncConfig | V→MC | u32 enabled, u32 radius (blocks). Sent after HelloAck and on config change. MC streams nothing until enabled. |
| 31 | BlockPalette | MC→V | u16 paletteId (≥1), u8 flags (1 full opaque cube, 2 has collision, 4 cutout/translucent), u8 nBoxes (≤8), u16 tile[6] (atlas tile per face, MC Direction order down,up,north,south,west,east), then nBoxes × u8[6] (minX,minY,minZ,maxX,maxY,maxZ in 1/16 block) at offset 16 |
| 32 | SectionBegin | MC→V | i32 sx, sy, sz (MC block >> 4), u32 blockCount |
| 33 | SectionBlocks | MC→V | i32 sx, sy, sz, u16 n (≤58), n × (u16 local = y<<8 \| z<<4 \| x, u16 paletteId) |
| 34 | SectionEnd | MC→V | i32 sx, sy, sz — Valheim replaces the section's contents (0 blocks = remove) and re-meshes it + its 6 neighbours |
| 35 | BlockAtlas | MC→V | u32 revision, u32 tilesUsed, u32 tilesPerRow (64), u32 tileSize (16), UTF-8 path of the PNG (`%LOCALAPPDATA%\ValCraft\blocks-atlas.png`); tile t at column t % perRow, row t / perRow from the top; tile 0 = missing texture |
| 36 | BlocksReset | MC→V | – : drop all sections + palette (sent first after every (re)link) |

Palette entries always precede the sections that use them (same ordered ring). Sections that leave the radius are sent
as empty. MC scans changed sections (break/place/neighbour-notify events, terrain-top changes) first, then a
round-robin pass nearest-first (initial sync + anything missed). Mesh coordinates: `unity = anchor + (x, y, -z)`.

### v5 additions (ValCraft 0.7.1, layout/version unchanged)
BlockPalette flags gain **8 translucent** (alpha-blended submesh), **16 cross** (X-shaped double-sided plant quads, face tile = tile[2], no collision),
**32 fluid** (box maxY = fluid height/16; tiles up/down = still, sides = flowing; corner heights averaged with same-fluid
neighbours, full height when the same fluid is above; faces against the same fluid are culled; no collision).
Fluids are identified as "the same fluid" by equal tile[1]. Full-box non-opaque blocks cull faces against the same palette id (glass-glass).
Valheim uses 3 submeshes/materials: 0 opaque, 1 cutout, 2 translucent.


## v6 changes (ValCraft 0.7.2)
Version = **6** (layout unchanged).
- **BlockPalette** gains 4 trailing bytes after the boxes: `u8 lightLevel (0-15), u8 r, g, b` light colour. New flags: **64 shiny** (glossy/reflective submesh), **128 has model quads** (render from EV 37 quads; boxes are then used for collision only).
- **37 BlockQuads** MC→V: `u16 paletteId, u16 firstIndex (0 = replace), u8 n (≤5), u8 last`, then n × 44 bytes:
  `u16 tile, u8 dir (facing, MC Direction ordinal), u8 cull (0-5 cull face, 6 never)`, 4 × (`i16 x,y,z` in 1/1024 block, block-local MC coords; `u16 u,v` sprite fractions × 65535, v down).
  Sent right after the palette entry for non-cube/no-collision models (torches, lanterns, chains, doors, rails, stairs, slabs, ...). Max 255 quads.
- Valheim submeshes: 0 opaque, 1 cutout, 2 translucent, 3 emissive (light > 0, otherwise-opaque), 4 shiny.


## v7 changes (ValCraft 0.7.3)
Version = **7** (layout unchanged).
- **30 BlockSyncConfig** gains a third u32: max entities.
- Block palette: every non-cross, non-fluid block now sends model quads (EV 37); block-entity renderer geometry (chests, beds, signs, banners, skulls, shulker boxes, decorated pots, bells, conduits, ...) is captured and appended as quads whose tiles are crops of the entity textures. Boxes stay the collision shape.
- **38 BlockAnim** MC→V: `u16 displayTile, u16 n (≤61), n × (u16 frameTile, u16 ticks)`. Valheim copies `frameTile` into `displayTile` on the GPU (20 ticks/s).
- **39 EntMesh** MC→V: `u32 meshId, u16 firstIndex (0 = replace), u8 n (≤5), u8 last`, then n quads in the EV 37 44-byte layout, positions relative to the entity origin.
- **40 EntUpdate** MC→V: `u8 first, u8 last, u16 n (≤11)`, n × (`u32 entityId, u32 meshId, f32 x, y, z` MC position, `u8 flags`: 1 = dropped item, Valheim spins/bobs). A first..last run is one complete snapshot (every 100 ms); entities missing from it are removed.
- Explosions: per column, the lowest destroyed terrain-type block is sent as EV 24 BlockChanged (kind 0), so Valheim lowers its terrain.


## v8 changes (ValCraft 0.7.4)
Version = **8** (layout unchanged).
- **30 BlockSyncConfig** gains a fourth u32: max mobs.
- **40 EntUpdate** flags: 2 = mob/NPC/other player. Mesh ids with bit 30 (`0x40000000 | entityId`) are per-entity *dynamic* meshes: MC re-sends them (EV 39, firstIndex 0 = replace) ~10×/s nearby, ~3×/s farther; Valheim reuses the Mesh and frees it when the entity leaves the snapshot.
- Entity ids with bit 31 set are live block entities (chests, ender chests, shulker boxes) at their block position; their mesh is the renderer capture for (state, lid openness in 1/8 steps). Their static palette entry is an invisible placeholder quad (collision boxes unchanged).


## v9 changes (ValCraft 0.7.5)
Version = **9**. **40 EntUpdate** entries grow to 25 bytes: `u32 id, u32 mesh, f32 x,y,z, u8 flags, f32 groundY` (≤9 per message).
Flags: 8 = groundY valid (terrain-type block under the entity, MC Y of its top), 4 = standing on it. Valheim adds
`offset = valheimGround(x,z) - (groundY + anchor.y)` (smoothed, |offset| < 3 m, held while airborne/swimming, faded out over player-placed blocks).

## v10 (0.8.0) — item bridge (Phase C)

All integers are little-endian; ids are UTF-8 Minecraft item ids (`minecraft:oak_log`).

| Type | Dir | Payload |
|---|---|---|
| 41 EvGiveItem | V→MC | u32 reqId, u16 count, utf8 id. MC adds the item to the player's inventory (server thread) and drops any overflow at the player's feet. |
| 42 EvConsumeReq | V→MC | u32 reqId, u16 count, u8 mode (0 = from the held stack, 1 = by id from the whole inventory), utf8 id. |
| 43 EvConsumeAck | MC→V | u32 reqId, u16 removed, utf8 id. `removed` may be less than requested (0 = none). Creative mode: held items aren't used up but are still acked. |
| 44 EvHeldItem | MC→V | u16 count, utf8 id (empty string = empty hand). Sent whenever the main-hand stack changes. |
| 45 EvInvSnapshot | MC→V | u8 first, u8 last, u8 n, n × (u16 count, u8 len, utf8 id). Totals per id; split across messages, `first`/`last` flags mark the run. |
| 46 EvInvReq | V→MC | empty. Asks for EvInvSnapshot. |

Mapping (Valheim side only): `BepInEx/config/valcraft-items.json`, `{ "ValheimPrefab": ["mc:id_given", "mc:also_accepted", ...] }`. Keys starting with `_` are comments; ids without a namespace get `minecraft:`. Reloaded within 2 s of being saved.

## v11 (0.8.1) — health link + combat

| Type | Dir | Payload |
|---|---|---|
| 28 EvAttack (extended) | MC→V | i32 kind, i32 tier, **f32 mcAttackDamage** (ATTACK_DAMAGE attribute incl. held item), **f32 strength** (attack cooldown scale 0..1). On a Valheim Character: damage = dmg × (0.2 + 0.8·str²) × [Combat] DamageScale, slash (sword/axe), pierce (pickaxe) or blunt (other/fist). |
| 47 EvHealth | MC→V | f32 hp, f32 maxHp, u8 food, u8 flags (1 = dead). Sent on change and every 20 ticks. |
| 48 EvDamage | V→MC | f32 amount (MC hp), u8 Valheim HitData.HitType, f32 dirX, f32 dirZ (MC axes), f32 pushForce. |

Valheim player hp = mcHp / mcMax × Valheim max every frame. Damage reaching the hidden player in Character.ApplyDamage (after armor) is cancelled and forwarded as `amount = dmg / valheimMax × mcMax`. Fall hits (HitType 3) are dropped. When MC dies, Valheim gets Character.OnDeath; when MC respawns, Valheim re-anchors.

## v12 (0.8.3) — crafted gear, durability, mace building

Changed layouts (durability u16: 0..10000 = fraction remaining, 0xFFFF = none):

| Type | Dir | Payload |
|---|---|---|
| 41 EvGiveItem | V→MC | u32 reqId, u16 count, **u16 durability**, utf8 id. Damageable MC items get damage = (1 − frac) × maxDamage. |
| 43 EvConsumeAck | MC→V | u32 reqId, u16 removed, **u16 durability** (of the held stack, mode 0), utf8 id. |
| 49 EvBuildMenu | MC→V | empty. Mace right-click not on a Valheim interactable: enter build mode (equip the Valheim hammer, a temporary one if needed) / toggle the piece menu. |
| 50 EvBuildAction | MC→V | u8: 1 place (Player.TryPlacePiece), 2 remove (Player.RemovePiece), 3 rotate (m_placeRotation++). |
| 51 EvBuildState | V→MC | u8 active. While active and the mace is held, MC left-click = place, sneak+left = remove, sneak+right = rotate; MC attack/mining suppressed. |

Mapping file: on load, default entries missing from the user file are appended; `"_known"` records the default keys already offered so deleted entries stay deleted. Only stackable Valheim items (materials, arrows) are moved into Valheim for crafting/building; tools and armour stay in MC.

## v13 (0.8.5) — MC-side build picker

Valheim's own piece window is no longer used (Minecraft has OS focus; in 0.8.4 Valheim's flag said visible but `SelectionWindow` never activated).

| Type | Dir | Payload |
|---|---|---|
| 49 EvBuildMenu | MC→V | empty. Enters build mode if needed (equip hammer, deposit MC materials), then Valheim answers with EvBuildPieces once the deposit acks are in (≤ 1.5 s). |
| 52 EvBuildPieces | V→MC | u8 first, u8 last, u8 n, n × (u16 index, u8 Piece.PieceCategory, u8 flags (1 = can build now), u8 nameLen, utf8 localized name (≤ 60 B), u8 costLen, utf8 cost text (≤ 120 B)). MC opens its picker screen after `last`. |
| 53 EvBuildSelect | MC→V | u16 index → Player.SetSelectedPiece. |

## v14 (0.9.0) — fire, water, cross-game mobs

| Type | Dir | Payload |
|---|---|---|
| 48 EvDamage (use) | V→MC | Valheim fire around the MC player's mapped position (Aoe with fire damage, EffectArea Burning, Character.InLava) is sent as HitType 5 (Burning): 1 hp (4 in lava) per 0.5 s unless Valheim's own burn hit was forwarded in the last second. MC also sets ≥ 4 s of fire ticks. |
| 54 EvMobList | MC→V | u8 first, u8 last, u8 n, n × 20 B (u32 MC entity id, u8 kind 1 hostile/2 passive, f32 x, y, z MC feet, u8 width×10, u8 height×10, u8 hp%). Every 4 ticks, ≤ 24 nearest within 32 m. |
| 55 EvMobHurt | V→MC | u32 MC entity id, f32 damage (MC hp = Valheim ÷ [Combat] DamageScale), f32 dirX, f32 dirZ, u32 attacker vid (0 = none). |
| 56 EvVMobList | V→MC | same layout as 54: Valheim hostile monsters (not tamed, not Players/AnimalsVeg/PlayerSpawned/Dverger) within 32 m, id = Unity instance id (vid). Every 0.2 s, empty list included. |
| 57 EvVMobHurt | MC→V | u32 vid, f32 damage (MC hp; × DamageScale blunt in Valheim), u32 MC attacker entity id (its Valheim proxy becomes the HitData attacker). |
| 58 EvWater | V→MC | f32 x0, f32 z0, u8 step, u8 nx, u8 nz, nx·nz × i16 surface MC Y ×16 (−32768 none), ⌈n/8⌉ tar bits. 9×9 grid, 2 m, every 0.2 s, from Floating.GetLiquidLevel (Water / Tar). |

Proxies: Valheim instantiates [Mobs] ProxyPrefab (Greyling) per MC mob with AI/animator/drops/renderers/sound off, kinematic, non-persistent ZDO, faction [Mobs] HostileMcFaction (Players) for hostiles or AnimalsVeg; Character.ApplyDamage on a proxy is cancelled and sent as EvMobHurt. MC spawns `valcraft:valheim_proxy` (no AI, unsaved, not crosshair-pickable, NoopRenderer) per Valheim monster; every MC `Monster` gets a NearestAttackableTargetGoal for proxies; hits on a proxy are sent as EvVMobHurt.

## v15 (0.9.1) — mob names/health, tiled water, hoe levelling, day/night
| Event | Dir | Payload |
|---|---|---|
| 54 EvMobList / 56 EvVMobList | both | **variable length**: u8 first, u8 last, u8 n, n × (u32 id, u8 kind, f32 x, y, z, u8 w×10, u8 h×10, u8 hp%, u16 maxHp, u8 nameLen, utf8 name ≤32 bytes, cut on a char boundary). Fixed part 23 B per mob. MC sends the custom name or the entity type name; Valheim sends GetHoverName(). |
| 58 EvWater | V→MC | layout unchanged; now 18 m tiles (9×9 cells, 2 m): tile (tx,tz) has x0 = tx·18+1, z0 = tz·18+1. Player's tile every 0.2 s + 5 round-robin tiles of the surrounding 5×5 (≈ ±36 m). Dry tiles sent once. MC keys tiles by floor(x/18), floor(z/18); wet tiles expire after 3 s, dry after 15 s. Applies to all MC entities and boats. |
| 59 EvLevelGround | MC→V | empty. Shift + right-click with a hoe. Valheim raycasts the camera to terrain (≤ 8 m), checks wards, levels to the player's feet Y, r 2 m (full inside 1 m, linear falloff), then re-sends the terrain tiles. MC damages the hoe by 1. |
| 60 EvDayTime | V→MC | f32 EnvMan.GetDayFraction(), every second. MC time = (f·24000 + 18000) % 24000 on the current MC day (monotonic). doDaylightCycle off while linked, restored on unlink. |

## v16 (0.9.4) — per-Valheim-world MC worlds
| Event | Dir | Payload |
|---|---|---|
| 61 EvWorldInfo | V→MC | i64 ZNet.m_world.m_uid, u8 len, utf8 world name (≤200 B). Every 2 s while a Valheim world is loaded. MC maps uid → save folder in config/valcraft-worlds.properties ("ValCraft - <name>", sanitized, uid suffix on clash); the legacy "ValCraft" world is adopted by the first Valheim world seen. If MC is in another world it saves, leaves to the title screen and opens the mapped one. |
