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
