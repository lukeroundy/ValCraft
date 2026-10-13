# ValCraft multiplayer (0.13.0, v1)

Players only join the **Valheim** server. Minecraft rides on that connection; no extra ports, no MC server list.

## Setup
1. Everyone installs ValCraft 0.13.0 (Valheim DLL + `valcraft-0.13.0.jar`) — protocol v21, older builds refuse to link.
2. Host: Valheim → Start game → pick the world → **Start server** ticked (listen host). The host's Minecraft opens the MC world of that
   Valheim world (as in single player) and publishes it on a random **127.0.0.1-only** port (offline mode, flight allowed, no LAN broadcast).
3. Others: join the host's Valheim server normally. Their Minecraft leaves/doesn't open a local world and connects automatically to
   Valheim's local tunnel port (retries every 8 s until the host's world is up).
4. Config (`BepInEx/config/...ValCraft.cfg`, section `[Multiplayer]`): `Enabled` (default true), `MaxSendQueueKB` (256),
   `DedicatedMinecraftServer` (dedicated Valheim servers only, see below).

MC identities: offline-mode names = each player's Minecraft username (must differ between players; inventories/positions are per name).
Lower MC render/simulation distance on the host if joins are slow: the initial chunk download goes through Valheim's connection.

## How it works
- Tunnel: Valheim routed RPC `ValCraft_MC` (≤ 32 KB messages, reliable/ordered via Valheim's Steam/PlayFab socket), 1 MB flow-control window
  per stream and direction, paused while Valheim's own send queue is above `MaxSendQueueKB`. Client: local listener on 127.0.0.1:random;
  host: each stream is dialled to its MC's published port. Valheim peer leaves → its streams close.
- Server actions that used to touch the integrated server directly (Valheim damage, item give/consume, chest deposit, MC attack damage,
  hoe wear, teleports, Valheim monster proxies, terrain tiles) run on the host's server for the requesting player via a `valcraft:op` payload;
  answers for that player's Valheim come back via `valcraft:to_valheim`.
- Authority: fixed shared anchor (MC x = Valheim x, MC z = −Valheim z); time of day and weather from the host only; terrain edits from MC go to
  the editing player's Valheim (Valheim syncs the change to everyone); each Valheim monster / MC-mob stand-in is mirrored by the nearest Valheim player only.
  Block rendering: each client scans its own client world around its player.

## Dedicated Valheim servers (not finished)
`DedicatedMinecraftServer=host:port` makes a dedicated Valheim server with the plugin tunnel players to that Minecraft server (offline mode,
ValCraft jar installed). The ValCraft server-side logic (terrain mirroring, proxies, ops) currently lives in client-dist code, so a headless
MC dedicated server only gives plain shared MC for now — follow-up.

## Known v1 limits
- MC entities near a *joined* player that the host's Valheim never loads don't collide with Valheim buildings on the server side (the server
  uses the host's Valheim collision cache); players themselves are fine (client-side physics).
- Server-side player hitbox scale follows the host's config.
- Switching the single-player MC world to the shared anchor: the first MP session teleports your MC player to Valheim coordinates.
