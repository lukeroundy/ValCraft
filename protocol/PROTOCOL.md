# ValCraft shared-memory protocol v1

File-backed memory map. Windows: `%LOCALAPPDATA%\ValCraft\valcraft.shm`; override with env `VALCRAFT_SHM`.
Size fixed: 0x22100 bytes (139,520). All values little-endian. Valheim side is the **host**: it creates
the file, sizes it, zeroes it and writes the header last (magic written last, with release ordering).
Minecraft opens it and waits until magic+version match.

| Offset | Size | Field |
|---|---|---|
| 0x000 | u32 | magic `0x434C4156` ("VALC") |
| 0x004 | u32 | version = 1 |
| 0x008 | u64 | total size |
| 0x010 | u32 | Valheim PID |
| 0x014 | u32 | Minecraft PID |
| 0x018 | i64 | Valheim heartbeat (ms since Unix epoch) |
| 0x020 | i64 | Minecraft heartbeat (ms since Unix epoch) |
| 0x028 | u32 | Valheim flags (bit0 in-game, bit1 menu open, bit2 loading) |
| 0x02C | u32 | Minecraft flags (bit0 world loaded) |
| 0x100 | PlayerState slot, **seqlock**, written by Minecraft |
| 0x1000 | Ring A: Valheim → Minecraft |
| 0x11080 | Ring B: Minecraft → Valheim |

## PlayerState @0x100 (seqlock)
`u32 seq` (odd while writing) @+0, pad, then @+8: `i64 frameId`, `f64 x, y, z` (MC block space),
`f32 yaw, pitch, roll, fovDeg`, `u32 flags`. Writer: seq++ (odd), release fence, write fields, seq++ (even, release).
Reader: s1 = seq (acquire); if odd retry; copy; acquire fence; s2 = seq; retry if s1 != s2.

## Rings (SPSC, fixed slots)
Ring header (0x80 bytes): `u64 head` @+0 (next slot to write, producer only), `u64 tail` @+0x40
(next slot to read, consumer only; separate cache line). Monotonic counters; slot = counter % 256.
256 slots × 256 bytes from +0x80. Slot: `u16 type, u16 reserved, u32 length, u8 payload[248]`.
Full when head - tail == 256. Producer writes slot, then head (release). Consumer reads head (acquire), slot, then tail (release).

## Event types
1 Hello (payload: u32 pid) · 2 Ping (u64 nonce) · 3 Pong (u64 nonce) · 100+ reserved for gameplay.
