# ValCraft — Milestone 1 (shared-memory link)

Minecraft Java 1.21.1 (NeoForge) running next to Valheim, talking over shared memory. This milestone only
proves the link: heartbeats, a seqlocked per-frame player-state slot, and event ring buffers both ways.
Nothing is rendered or patched in either game yet. Plan: `/workspace/valheim-minecraft-plan.md`.

| Folder | What |
|---|---|
| `protocol/` | `PROTOCOL.md` (byte layout) and `ValCraftLink.cs` (C# side, shared by plugin + test) |
| `valheim-plugin/` | BepInEx 5 plugin (`netstandard2.1`), creates the link, logs environment checks |
| `neoforge-mod/` | NeoForge 21.1.259 / MC 1.21.1 mod (ModDevGradle 2.0.148, Java 21); Java side in `dev/valcraft/link/ValCraftLink.java` |
| `ipc-test/` | Standalone cross-language test: C# console (Valheim role) + Java peer (Minecraft role) |

Link file: `%LOCALAPPDATA%\ValCraft\valcraft.shm` (override with env var `VALCRAFT_SHM`). Valheim creates
and initialises it; Minecraft connects once it exists. File-backed map, so no named kernel object and no
JNA/Panama on the Java side (plain `FileChannel.map`).

## What you need on Windows
- Valheim 1.0.17 + **BepInExPack_Valheim 5.4.2351** (Thunderstore / r2modman)
- **.NET 8 SDK** (to build the plugin and the test) — `winget install Microsoft.DotNet.SDK.8`
- **JDK 21** (Temurin) — `winget install EclipseAdoptium.Temurin.21.JDK`
- Minecraft Java 1.21.1 with **NeoForge 21.1.259** (installer from neoforged.net, or Prism Launcher)

## 1. Run the IPC test first (no games needed)
```powershell
cd valcraft\ipc-test
.\run-windows.ps1
```
Expect `[cs] PASS`, `[java] ... PASS`, `csharp exit=0 java exit=0`. Linux: `./run-linux.sh`.

## 2. Build + install the Valheim plugin
Builds without game DLLs (stubs: BepInEx.Core 5.4.21 + UnityEngine.Modules 6000.0.61 from nuget.bepinex.dev).
To also compile against the real game (enables the game-version log line):
```powershell
cd valcraft\valheim-plugin
dotnet build -c Release -p:ValheimDir="C:\Program Files (x86)\Steam\steamapps\common\Valheim"
```
Optional for later milestones: publicize `assembly_valheim.dll` with
[BepInEx.AssemblyPublicizer](https://github.com/BepInEx/BepInEx.AssemblyPublicizer)
(`dotnet tool install -g BepInEx.AssemblyPublicizer.Cli`, then
`assembly-publicizer valheim_Data\Managed\assembly_valheim.dll -o valheim_Data\Managed\publicized_assemblies\assembly_valheim_publicized.dll`);
the csproj picks it up automatically.

Copy `bin\Release\netstandard2.1\ValCraft.Valheim.dll` to `<Valheim>\BepInEx\plugins\ValCraft\` (or your r2modman profile's `BepInEx\plugins`).
Start Valheim and open `BepInEx\LogOutput.log`. Look for:
```
[check] Unity version: ...
[check] Render pipeline: BUILT-IN ...   (or SRP asset ... URP)
[check] Graphics API: Direct3D11 ..., reversedZ=True
[check] MemoryMappedFile (file-backed): OK
[link] created C:\Users\...\AppData\Local\ValCraft\valcraft.shm ...
```
**Please send me those lines** — they settle the unverified items in the plan.

## 3. Build + run the NeoForge mod
```powershell
cd valcraft\neoforge-mod
.\gradlew build           # jar: build\libs\valcraft-0.1.0.jar
.\gradlew runClient       # dev client, or copy the jar into a NeoForge 21.1.259 instance's mods folder
```
With Valheim already running, load any world in Minecraft. Minecraft log: `[link] opened ...`, `[link] Valheim connected`,
`[link] pong from Valheim — round trip OK`. Valheim log: `[link] Hello from Minecraft pid ...`, `Minecraft connected`,
`Pong 1 — round trip OK`, and every 5 s `[link] MC player frame ... pos (...)` that changes as you walk in Minecraft.

## Status
- Built on Linux box: NeoForge mod jar ✅, Valheim plugin DLL against stubs ✅, IPC test ✅ (20,000 ping/pong round trips, ~88k seqlock reads, 0 torn).
- Not yet run inside Valheim or Minecraft ⚠️ (no game files on the build box).
- Unverified: Valheim's Unity version/render pipeline, Mono MemoryMappedFile behaviour in-game, NeoForge
  event names working in the real client (they compiled against 21.1.259), Windows file-sharing behaviour of the map.
- Known limits: if Valheim restarts while Minecraft is connected, Minecraft keeps the old mapping (re-init is
  same file, so it keeps working, but ring counters reset; reconnect logic is milestone 2 work).
