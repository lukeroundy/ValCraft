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

## v17 (0.9.7) — reach, biomes
| Event | Dir | Payload |
|---|---|---|
| 23 EvTerrainTile | V→MC | + 4 trailing bytes: biome code per 4×4-column quadrant (q = qi·2+qj): 1 Meadows 2 Swamp 3 Mountain 4 BlackForest 5 Plains 6 AshLands 7 DeepNorth 8 Ocean 9 Mistlands, 0 unknown. Older 200-byte tiles still accepted. |
| 62 EvReach | MC→V | f32 blockInteractionRange, f32 entityInteractionRange, every 2 s. Valheim interact/hover + build placement use block reach, hits use entity reach; [Interact] MaxDistance > 0 overrides. |

## v18 (0.10.0) — chests, shields, tin/bronze
| Event | Dir | Payload |
|---|---|---|
| EvDamage | V→MC | + u32 attacker (Valheim Character instance id, 0 = none) → 21 bytes. MC resolves the attacker's proxy entity so shields/armor/knockback treat it as a mob attack; unknown attacker with a direction → positional mob-attack source. Old 17-byte payloads still accepted. |
| 63 EvChestContents | V→MC | Split over messages: u8 flags (1 first, 2 last), u32 serial; first msg: u8 w, u8 h (w=0 = chest closed), u8 nameLen, name; then entries to the end: u8 slot (y·w+x), u16 count, u16 dur (0xFFFF none), u8 idLen, mc id ("" = unmapped), u8 nameLen, Valheim item name. Re-sent on change (0.25 s poll); closes when out of reach. |
| 64 EvChestOp | MC→V | u32 serial, u8 op (1 take, 2 deposit, 3 close), u8 slot, u16 count, u16 dur, utf8 mc id (deposit). Stale serial ignored; a failed deposit is given back to MC. |

Block-set notes (no wire change): minecraft:stone and minecraft:farmland are no longer terrain blocks (placeable/visible as normal blocks). Mirrored deep layers use deepslate, mat3 surface andesite, cultivated ground dirt; stone in old saves below the column top is still hidden until the column is rewritten. Mob (non-TNT) explosions no longer remove terrain blocks or edit Valheim terrain; TNT terrain edits save once per frame per TerrainComp.
Item map: one-time migration of the old tin/bronze placeholder ids (only entries still exactly at the old default) to valcraft:* items.

### 0.10.1 (still v18, no layout change)
- EvEntUpdate entity flag 16 = the local MC player (third person). Valheim places that mesh at its own player transform every frame (no 10 Hz lag); MC no longer draws the player into the overlay frame.
- Captured meshes: grey vertex colours (baked shading) no longer mint atlas tiles; coplanar layer quads (villager clothes, wool, armour) are pushed out 0.004 blocks per layer; up to 2048 quads per mesh; big kinetic meshes (>300 quads) re-captured every 1.5 s.

## v19 (0.11.0) — lore stones, MC combat damage, all Valheim items
| Event | Dir | Payload |
|---|---|---|
| 65 EvLoreText | V→MC | Split over messages: u8 flags (1 first, 2 last), utf8 chunk ≤ 247 B (cut on a char boundary). Joined text = "title\ntext". Sent when MC right-clicks a rune stone / boss stone / vegvisir (Valheim marks the location on its map, AddKnownText, and suppresses its own TextViewer / large map for 2 s so input never moves to Valheim). MC shows a non-pausing lore screen (chat line if another screen is open). |
| EvAttack | MC→V | + 4 bytes → 20: u32 flags. Flag 1 = the f32 damage is the final MC damage (attack attribute × cooldown² curve + enchantment bonus × cooldown, ×1.5 on a vanilla crit); Valheim applies damage × [Combat] DamageScale with no further curve. 16-byte payloads still use the old curve. |
| 57 EvVMobHurt | MC→V | attacker id 0xFFFFFFFF = the MC player: Valheim sets its own player as HitData attacker (aggro, XP, death credit), pierce damage, direction from the player. |

Biomes (no wire change): AshLands columns map to nether biomes per ~64-block patch (nether wastes, basalt deltas, soul sand valley, crimson/warped forest) with nether spawns; DeepNorth maps to end_highlands (endermen). A rare vanilla ender dragon (config/valcraft.properties dragonChancePerMinute, default 0.002, ≤ 1 per 512 blocks) can appear above Deep North players; it does not break blocks outside the End.
Item map: ~350 generated valcraft:* items for every Valheim item without an exact MC equivalent (tools/valheim_items.py). Where a vanilla stand-in exists it stays first (Valheim→MC gives it) and the ValCraft item is appended as an accepted alternative; keys without a stand-in default to the ValCraft item. One-time migration appends the ValCraft id to user entries still exactly at the old single default.

## v20 (0.12.0) — map key, weather
| Event | Dir | Payload |
|---|---|---|
| 66 EvMapToggle | MC→V | empty. MC key "Valheim Map" (default M). Valheim toggles Minimap large mode; while it is open Valheim owns input (auto-input watches Minimap.IsOpen) and the MC overlay is hidden, so panning/zoom/pins and M/Esc are Valheim's own. |
| 67 EvWeather | V→MC | u8 flags (1 rain, 2 thunder, 4 snow), u8 len, utf8 EnvMan environment name (≤ 64 B). Every 2 s. rain = EnvSetup.m_isWet or name contains rain/snow/thunder/storm; MC sets overworld rain/thunder (snow in cold biomes such as Mountain = snowy slopes); doWeatherCycle off while linked, restored on unlink. |

No wire change: entity animation capture runs at 24 fps (41 ms) within 16 blocks, 12 fps to 32, 4 fps beyond, budget 6 ms per frame and skipped while > 300 events are queued. Projectiles (arrows, tridents, thrown items) are synced entities at their exact position; arrows stuck in Valheim boxes stay stuck. MC fluids do not spread into block cells a Valheim box fills. Emeralds: 0.5 % per broken MC stone block (config/valcraft.properties emeraldChance) and per mined-out Valheim rock piece ([Items] EmeraldChance). Items: TrophyBoar, TrophyDeer, TrophySkeleton added.

### 0.12.1 (still v20)
- Valheim: HideValheimHud no longer hides the HUD while the large map (or build menu) is open; the map UI chain is activated; a map that is still not visible after 1.5 s is closed so input returns to Minecraft.
- 0.12.2: while the large map is open Valheim closes it on M or Esc (raw key, Valheim's own Map key is swallowed); MC-side M while open also closes; no per-frame re-activation of the map root.
- 0.12.3: map open requests within 0.6 s of a close are ignored (M flicker); weather: also EnvMan.IsWet()/m_forceEnv, logged on change + every 2 min; MC keeps server weather only (no rain/snow drawing, sky darkening or rain sounds while linked); collision index built off-thread; moving colliders keep cached visibility; lore guard reflection cached and no longer closes an M-opened map.
- 0.12.4: Valheim type lookups cached (AccessTools.TypeByName scanned all assemblies on every interact / chest open / weather poll); interact stage timing (> 5 ms logged) and a [perf] watchdog for ValCraft frames > 15 ms; main-menu logo replacement never touches in-game UI.

## v21 (0.13.0) — multiplayer through the Valheim server
| Event | Dir | Payload |
|---|---|---|
| 68 EvNetRole | V→MC | u8 role (0 single player, 1 Valheim server host, 2 joined a Valheim server), u16 port (role 2: Valheim's local tunnel port on 127.0.0.1), u8 len, utf8 Valheim player name. On change and every 3 s. |
| 69 EvNetReady | MC→V | u8 role (1), u16 port: the host's integrated server is published on 127.0.0.1:port (offline mode, flight allowed, no LAN broadcast). Every 2 s while published. |

Tunnel (Valheim routed RPC `ValCraft_MC`, one ZPackage byte[] per message; protocol/TunnelCore.cs): u8 kind, i32 stream id, payload.
1 Open (client→host: new MC connection), 2 Data (≤ 32 KB of TCP bytes), 3 Close (either side), 4 Ack (u32 bytes written to the local socket).
Flow control: ≤ 1 MB un-acked per stream and direction (the socket reader blocks); sends pause while Valheim's send queue to that peer is above [Multiplayer] MaxSendQueueKB (256).
The host dials 127.0.0.1:<EvNetReady port> per Open (refused → Close); a Valheim peer leaving drops its streams.

MC custom payloads (both optional, channel version "1"): `valcraft:op` C→S {i16 op, byte[]} runs a former integrated-server action for the sending
player on the host (op = the Valheim event id for EV_DAMAGE, EV_GIVE_ITEM, EV_CONSUME_REQ, EV_TERRAIN_TILE, EV_VMOB_LIST (whole snapshot: u16 n, n × (u16 len, part)), EV_MOB_HURT;
1001 chest deposit {i64 serial, i32 slot, u8 half}, 1002 hoe wear, 1003 attack {i32 kind, i32 tier, f32 strength, u8 crit}, 1004 teleport {f32 x,y,z}, 1005 refresh dims, 1006 clear my Valheim proxies);
`valcraft:to_valheim` S→C {i16 event, byte[]}: the server's answer for that player's Valheim (EV_CONSUME_ACK, EV_CHEST_OP, EV_ATTACK, EV_VMOB_HURT, EV_BLOCK_CHANGED).
Authority: multiplayer uses a fixed shared anchor (MC x = Valheim x, MC z = −Valheim z, Y as before); each Valheim monster / MC-mob stand-in is mirrored only by the
nearest Valheim player (4 m hysteresis); MC terrain edits go to the editing (or nearest) player's Valheim only; time/weather come from the host only.
- 0.13.0 (same build): "Loading terrain..." no longer waits 30 s for a section compile that never happens while linked (ready once the player chunk is loaded); tunnel clients are teleported by the server only (no "moved too quickly").
- 0.13.1 (no wire change): collider raster uses 0.25 m cells for small colliders (stairs); MC blocks can be placed against Valheim surfaces (virtual face support); MC waits for a linked Valheim world instead of opening the default world after 20 s.

## v22 (0.13.2) — sleep, spawn points, MC campfires
| Event | Dir | Payload |
|---|---|---|
| 70 EvSleep | MC→V | u8 state (1 MC player went to sleep in an MC bed, 0 woke up), f32 x,y,z (MC bed position, state 1). Valheim checks EnvMan.CanSleep / enemies, then marks its player in bed (ZDO inBed) so Game.UpdateSleeping skips the night; the MC bed also becomes Valheim's custom spawn point. |
| 70 EvSleep | V→MC | u8 state (1 Valheim player is in bed → MC player sleeps where it stands, 0 woke → MC wakes, 2 Valheim refused → MC wakes), state 2: utf8 reason. |
| 71 EvSpawn | V→MC | f32 x,y,z MC coordinates of the Valheim custom spawn point (a Valheim bed was used) → MC respawn position (forced). |
| 72 EvMcFires | MC→V | u16 n, n × i32 x,y,z: lit MC campfires / soul campfires within 32 blocks (on change, every 5 s). Valheim keeps an invisible EffectArea Heat+Fire (radius 3 m, never Burning) and a Fire comfort piece at each. |
EvBuildAction 4: cycle the Valheim placement snap point (Player.m_manualSnapPoint: 0..n−1, then −1 = automatic); MC sends it on Left Alt while building.
MC ops 1007 sleep {u8 on} (server: start sleeping at the player position / wake), 1008 respawn position {f32 x,y,z}.
While Valheim drives the time, MC's own "everyone asleep" skip/wake-up is disabled; MC wakes when Valheim's morning reaches it.
On a death the anchor is kept: the MC player is teleported to where Valheim respawned it (previously the mapping was re-anchored, shifting MC builds).
- 0.13.3 (no wire change, Valheim DLL only): an MC campfire within 1 m of a CookingStation fire check point counts as lit (IsFireLit postfix); CraftingStation.CheckFire (cauldron) accepts one within 1.5 m; a placement status about fire is overridden over an MC campfire. Areas stay non-Burning.

## v23 (0.13.4) — respawn order
| 73 EvRespawnReady | V→MC | u8 (0 Valheim still respawning: keep MC's respawn button disabled, "Waiting for Valheim..."; 1 Valheim's new player is alive, not teleporting/intro → MC respawns itself). Every 0.5 s while MC is dead after a linked kill. No 0 seen → no gate; 60 s timeout re-enables the button. |
Valheim kill: m_lastHit is given a HitData before Player.OnDeath (0.13.3 log: OnDeath NullReferenceException, no tombstone/respawn); fallback lethal ApplyDamage; if both fail MC is released at once. After MC respawns Valheim moves it to the Valheim respawn point (anchor kept, 0.13.2).

## v24 (0.13.5) — persistent mapping
| 74 EvOffset | MC→V | u8 relative (0 set, 1 nudge), f32 x, f32 z: change this Valheim world's stored horizontal mapping offset (MC `/valcraft offset x z`, `/valcraft nudge dx dz`). |
Single player: the horizontal anchor is no longer recomputed from the two saved player positions at link time. It is stored per Valheim world uid in BepInEx/config/valcraft-anchors.txt (default (0,0) = the 0.13.0 mapping MC x = Valheim x, MC z = −Valheim z, same as multiplayer); on link the MC player is teleported to its Valheim position. Death alignment no longer holds the Valheim player forever when the Valheim kill failed.

## 0.14.0 (no new events; palette light byte extended)
Palette light byte: bits 0-3 block light level, bit 0x40 = emissive quads without block light (glow material, no point light).
MC-side classification is property-driven for any mod (BlockProps): render layer from the model's render types (NeoForge ChunkRenderTypeSet / model JSON render_type) + legacy map + texture-alpha fallback; emission from getLightEmission and baked quad lightmaps; light colour from the texture; reflections from tags (#c:glass_blocks, #c:glass_panes, #c:storage_blocks, #minecraft:ice), sound type (glass, metal, netherite, copper, amethyst, chain), friction, polished_/smooth_ names; hot/glowing modded fluids via FluidType temperature/light; campfire warmth for #minecraft:campfires / CampfireBlock subclasses with a "lit" property. Overrides: config/valcraft-blocks.json (block id or #tag → render, emissive, light, lightColor, shiny, smoothness, metallic, warm).

## v25 (0.14.1) — spinning kinetic block entities
- **EvKinetic 75** (MC→V), sent with each entity snapshot while any exist (one empty list after the last one goes):
  `u8 first, u8 last, u16 n, n × (u32 entityId, u8 axis 0=x 1=y 2=z (MC), f32 angleDeg, f32 degPerSec)`; at most 18 per message, first..last = full list.
  The entity's mesh (EvEntMesh) was captured once; `angleDeg` = rotation since that capture about the block centre, +axis, MC right-handed
  (Create: angle = ticks × rpm × 3/10, so degPerSec = rpm × 6). Valheim predicts from the rate every frame and eases toward the sent angle;
  Unity mirrors z, so x/y rotations flip sign. MC re-captures (angle restarts at 0) only when the speed, axis or block state change.
  Whitelisted single-axis Create BEs only; others keep periodic snapshots.
- Block-entity captures: face direction from the quad winding, near-grey/weak vertex colours ignored (no baked shading).
- Sections: block changes are hooked at ServerLevel.sendBlockUpdated and queued ahead of entity traffic; Valheim rebuilds the changed section first.

## v26 (0.14.2): Hugin
- **EvNpcSay 76** (V→MC): UTF-8 `topic\ntext`, with tags stripped and at most one slot long. Sent when a raven (Hugin/Munin) talks by itself (Chat.SetNpcText). MC shows a chat line plus an action-bar hint. Repeats of the same text are suppressed for 30 s.
- A right-click on a raven (picked by its distance from the view ray, because the interact ray misses it) runs the vanilla Raven.Interact. The text it shows (Chat.SetNpcText or TextViewer.ShowText within 1.5 s) is sent as **EvLoreText 65** (the lore screen). Valheim's own text viewer is closed by the lore guard for 2 s.
- **EvCape 77** (MC→V, 0.15.1): UTF-8 ValCraft item id of the cape in the Minecraft cape slot (`wolf_fur_cape`, ...), empty = none. Sent on change and every 5 s. Valheim shows the matching Valheim cape on the player while no Valheim cape is equipped. Additive: older peers ignore it, so the protocol version stays 26.
