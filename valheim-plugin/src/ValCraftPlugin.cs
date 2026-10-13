using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using ValCraft.Protocol;

namespace ValCraft
{
    /// <summary>
    /// Milestone 2: link with acked Hello + reconnect, Prism launch, Minecraft HUD overlay, input passthrough (F7),
    /// Valheim input suppression while active, graceful Minecraft shutdown on exit.
    /// </summary>
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public class ValCraftPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static ValCraftPlugin Instance;
        internal static bool InputActive => Instance != null && Instance._inputActive;

        Harmony _harmony;
        internal ValCraftLink Link;
        Overlay _overlay;
        InputForwarder _input;
        float _nextHeartbeat, _nextStatus, _nextSizeRequest;
        bool _connected, _inputActive, _overlayVisible = true;
        uint _mcPid;

        ConfigEntry<bool> _cfgAutoLaunch, _cfgKillOnExit;
        ConfigEntry<string> _cfgPrismExe, _cfgInstance, _cfgExtraArgs;
        ConfigEntry<KeyCode> _cfgToggleInput, _cfgToggleOverlay;
        ConfigEntry<bool> _cfgAutoInput;
        bool _valheimOverride, _stallLogged, _lastInGame;
        float _nextLinkStatus;
        ConfigEntry<int> _cfgQuitGrace;
        internal ConfigEntry<float> CfgMouseScale;
        ConfigEntry<bool> _cfgFollow, _cfgHidePlayer, _cfgHideHud, _cfgViewBob;
        ConfigEntry<VerticalMode> _cfgVertical;
        ConfigEntry<bool> _cfgCollision;
        ConfigEntry<float> _cfgCollRadius;
        ConfigEntry<KeyCode> _cfgReAnchor;

        void Awake()
        {
            Instance = this;
            Log = Logger;
            BindConfig();
            LogEnvironmentChecks();
            try
            {
                Link = new ValCraftLink(ValCraftLink.DefaultPath(), isHost: true);
                Log.LogInfo($"[link] created {Link.Path} ({ValCraftLink.FileSize} bytes), protocol v{ValCraftLink.Version}, session {Link.Session:x8}");
            }
            catch (Exception e)
            {
                Log.LogError($"[link] could not create shared memory: {e}");
            }
            TryLaunchMinecraft();
            _harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            InputBlock.Apply(_harmony);
            PlayerSync.Apply(_harmony);
            ItemBridge.Apply(_harmony);
            HealthLink.Apply(_harmony);
            try { SleepLink.Apply(_harmony); } catch (Exception e) { Log.LogWarning("[sleep] hook failed: " + e.Message); }
            try { FireLink.Apply(_harmony); } catch (Exception e) { Log.LogWarning("[fire] hook failed: " + e.Message); }
            // 0.12.1: HudWanted was never assigned, so HideValheimHud hid the HUD every frame (build menu, large map)
            Refl.Prewarm();
            PlayerSync.HudWanted = () => ItemBridge.MenuWanted() || MapToggle.Open();
            try { Saplings.Apply(_harmony); } catch (Exception e) { Log.LogWarning("[sapling] hook failed: " + e.Message); }
            try { Emeralds.Apply(_harmony); } catch (Exception e) { Log.LogWarning("[emerald] hook failed: " + e.Message); }
            _overlay = new Overlay(gameObject, Config.Bind("Overlay", "PremultipliedShader", false, "(Minecraft 0.4.0+ already sends straight alpha; leave off.) Use a premultiplied-alpha material for the Minecraft overlay if a suitable shader is found (turn off if the overlay looks wrong or invisible).").Value);
            _input = new InputForwarder(this);
            InputForwarder.ExcludeKeys(_cfgToggleInput.Value, _cfgToggleOverlay.Value, _cfgReAnchor.Value);
        }

        void BindConfig()
        {
            _cfgAutoLaunch = Config.Bind("Minecraft", "StartWithValheim", true, "Start Minecraft through Prism Launcher when Valheim starts (skipped if Minecraft is already connected or PrismExe is empty).");
            _cfgPrismExe = Config.Bind("Minecraft", "PrismExe", "", "Full path to PrismLauncher.exe, e.g. C:\\Users\\Luke\\AppData\\Local\\Programs\\PrismLauncher\\prismlauncher.exe");
            _cfgInstance = Config.Bind("Minecraft", "InstanceName", "ValCraft", "Prism instance name (folder name under Prism's instances directory). It must contain valcraft-<version>.jar in its mods folder.");
            _cfgExtraArgs = Config.Bind("Minecraft", "ExtraArgs", "", "Extra Prism arguments (optional; --profile/--offline are not needed).");
            _cfgKillOnExit = Config.Bind("Minecraft", "StopMinecraftOnExit", true, "When Valheim quits: ask Minecraft to quit (it saves its world), and kill it if it is still running after QuitGraceSeconds.");
            _cfgQuitGrace = Config.Bind("Minecraft", "QuitGraceSeconds", 10, "Seconds Valheim waits on exit for Minecraft to shut down by itself before killing it.");
            _cfgAutoInput = Config.Bind("Controls", "AutoInput", true, "Send input to Minecraft automatically while in-game and no Valheim menu is open. ToggleInputKey then temporarily hands controls to Valheim.");
            _cfgToggleInput = Config.Bind("Controls", "ToggleInputKey", KeyCode.F7, "Toggle sending keyboard/mouse to Minecraft (Valheim ignores input while on).");
            _cfgToggleOverlay = Config.Bind("Controls", "ToggleOverlayKey", KeyCode.F8, "Show/hide the Minecraft overlay.");
            CfgMouseScale = Config.Bind("Controls", "MouseScale", 1.0f, "Multiplier for mouse movement sent to Minecraft (tune if turning is too slow/fast).");
            _cfgReAnchor = Config.Bind("Controls", "ReAnchorKey", KeyCode.F9, "Re-anchor: map Minecraft's current position to where the Valheim player stands now.");
            _cfgFollow = Config.Bind("Sync", "FollowMinecraft", true, "Valheim camera and player follow the Minecraft player while a Minecraft world is loaded.");
            _cfgHidePlayer = Config.Bind("Sync", "HideValheimPlayer", true, "Hide the local Valheim character's body, gear and shadow while following (it stays an invisible proxy for physics/damage/networking).");
            _cfgHideHud = Config.Bind("Sync", "HideValheimHud", true, "Hide Valheim's HUD (health, hotbar, crosshair) while following; Minecraft's HUD is shown instead.");
            _cfgViewBob = Config.Bind("Sync", "ViewBob", true, "Apply Minecraft's view bobbing to the Valheim camera (only if Minecraft's View Bobbing option is on).");
            _cfgVertical = Config.Bind("Sync", "VerticalMode", VerticalMode.FollowMinecraft, "FollowMinecraft (default since 0.4.0): Minecraft's height as-is; MC collides with exported Valheim terrain + buildings. GroundSnap: 0.3.0 behaviour, stand on Valheim's ground + MC jump height (use with ExportCollision=false).");
            _cfgCollision = Config.Bind("Collision", "ExportCollision", true, "Send nearby Valheim colliders (building pieces, rocks, trees, terrain) to Minecraft as invisible collision boxes.");
            _cfgCollRadius = Config.Bind("Collision", "Radius", 32f, "Export radius around the player (m).");
            var cfgTerrain = Config.Bind("Terrain", "TerrainBlocks", true, "Stream Valheim terrain (height + biome) to Minecraft as real blocks (collision, mining). Off = 0.4.x collision boxes.");
            var cfgTerrainR = Config.Bind("Terrain", "Radius", 48f, "Terrain streaming radius around the player (m = blocks).");
            var cfgEdit = Config.Bind("Terrain", "EditTerrain", true, "MC breaking/placing terrain blocks lowers/raises Valheim terrain (Valheim TerrainOp; networked, saved; +-8 m limit).");
            TerrainEdit.Enabled = cfgEdit.Value;
            var cfgHit = Config.Bind("Hits", "Enabled", true, "MC left-click damages Valheim rocks/ore/trees/destructibles the Valheim camera aims at.");
            var cfgTier = Config.Bind("Hits", "ToolTiers", "0,1,2,1,3,4", "Valheim tool tier for MC tool tiers wood,stone,iron,gold,diamond,netherite (Valheim: 0 stone/antler, 1 flint, 2 bronze, 3 iron, 4 black metal; ores/trees need >= their min tier).");
            var cfgDmg = Config.Bind("Hits", "ToolDamage", "10,18,30,22,45,60", "Pickaxe/chop damage per hit for MC tool tiers wood,stone,iron,gold,diamond,netherite.");
            ValheimHit.Enabled = cfgHit.Value;
            try { var t = Array.ConvertAll(cfgTier.Value.Split(','), x => int.Parse(x.Trim())); if (t.Length == 6) ValheimHit.TierMap = t; } catch { Log.LogWarning("[hit] bad ToolTiers config"); }
            try { var d = Array.ConvertAll(cfgDmg.Value.Split(','), x => float.Parse(x.Trim(), System.Globalization.CultureInfo.InvariantCulture)); if (d.Length == 6) ValheimHit.DamageMap = d; } catch { Log.LogWarning("[hit] bad ToolDamage config"); }
            var cfgBr = Config.Bind("Blocks", "RenderBlocks", true, "Draw Minecraft blocks that aren't Valheim terrain (your builds, placed blocks) inside Valheim.");
            var cfgBrR = Config.Bind("Blocks", "Radius", 64, "Radius (m = blocks) around the player in which Minecraft blocks are drawn (16-160).");
            var cfgBrC = Config.Bind("Blocks", "Collision", true, "Give drawn Minecraft blocks Valheim colliders (piece layer), so Valheim physics/raycasts see them.");
            var cfgL = Config.Bind("Blocks", "Lights", true, "Light-emitting Minecraft blocks (torches, lava, glowstone, lanterns...) cast Valheim point lights.");
            var cfgML = Config.Bind("Blocks", "MaxLights", 32, "Maximum number of point lights from Minecraft blocks (nearest/brightest first; nearby light blocks are merged).");
            var cfgRf = Config.Bind("Blocks", "Reflections", true, "Glossy/reflective material for glass, water, ice, metal and polished blocks + one low-res realtime reflection probe at the player (restart to apply).");
            var cfgME = Config.Bind("Blocks", "MaxEntities", 64, "Maximum number of Minecraft entities (dropped items, armor stands, item frames, minecarts, boats, TNT, falling blocks) drawn in Valheim.");
            var cfgMM = Config.Bind("Blocks", "MaxMobs", 24, "Maximum number of Minecraft mobs / villagers / other players drawn (animated, re-captured ~10x/s nearby).");
            var cfgOS = Config.Bind("Blocks", "OpaqueShader", "", "Override the shader for solid blocks (0.7.4 default picks \"Standard\"; set \"Legacy Shaders/Diffuse\" for the 0.7.3 look). Restart to apply.");
            BlockRender.OpaqueShader = cfgOS.Value;
            void ApplyBlocks() { BlockRender.MaxMobs = Mathf.Clamp(cfgMM.Value, 0, 128); BlockRender.MaxEntities = Mathf.Clamp(cfgME.Value, 0, 512); BlockRender.Lights = cfgL.Value; BlockRender.MaxLights = Mathf.Clamp(cfgML.Value, 0, 256); BlockRender.Reflections = cfgRf.Value; BlockRender.Enabled = cfgBr.Value; BlockRender.Radius = Mathf.Clamp(cfgBrR.Value, 16, 160); BlockRender.Collision = cfgBrC.Value; }
            ApplyBlocks();
            Config.SettingChanged += (_, __) => ApplyBlocks();
            var cfgItems = Config.Bind("Items", "Enabled", true, "Phase C item bridge: Valheim resources you gather go to your Minecraft inventory (mapping: valcraft-items.json in the config folder); Minecraft items work in Valheim fires, smelters, cooking stations and crafting.");
            ItemBridge.Enabled = cfgItems.Value;
            HealthLink.Enabled = Config.Bind("Health", "LinkHealth", true, "Minecraft health drives the Valheim player: Valheim damage (mobs, fire, drowning...) goes to Minecraft instead, Valheim fall damage is off, and a Minecraft death kills the Valheim player (tombstone + respawn).").Value;
            DayLink.Enabled = Config.Bind("Time", "SyncDayNight", true, "Valheim's time of day drives Minecraft (MC daylight cycle paused while linked).").Value;
            Hazards.Fire = Config.Bind("Hazards", "Fire", true, "Valheim fire (campfires, burning areas, fire AoE, lava) damages and ignites the Minecraft player.").Value;
            Hazards.Water = Config.Bind("Hazards", "Water", true, "Valheim water and tar act as water for the Minecraft player (swimming, air, drowning; tar slows).").Value;
            MobBridge.Enabled = Config.Bind("Mobs", "CrossGameCombat", true, "Minecraft hostiles and Valheim monsters see and fight each other (invisible proxies on both sides).").Value;
            MobBridge.MaxProxies = Config.Bind("Mobs", "MaxProxies", 24, "Max proxies per side.").Value;
            MobBridge.ProxyPrefab = Config.Bind("Mobs", "ProxyPrefab", "Greyling", "Valheim creature prefab used (invisible, AI off) as the stand-in for Minecraft mobs.").Value;
            MobBridge.HostileFaction = Config.Bind("Mobs", "HostileMcFaction", "Players", "Valheim faction for hostile Minecraft mobs (Players = every Valheim monster attacks them).").Value;
            InteractBridge.MaxDistanceCfg = Config.Bind("Interact", "MaxDistance", 0f, "Max reach (m) for Valheim interact/hover, build placement and hits; 0 = use Minecraft's reach (survival 4.5 m blocks, 3 m entities).").Value; InteractBridge.ApplyReach();
            ValheimHit.Combat = Config.Bind("Combat", "Enabled", true, "Minecraft left-click on Valheim creatures damages them (attacker = your Valheim player, so aggro, skills and drops work).").Value;
            Saplings.Chance = Config.Bind("Items", "SaplingChance", 0.05f, "Chance (0..1) that a chopped-down Valheim tree drops a Minecraft sapling (beech/birch -> birch, pine/fir -> spruce, swamp -> dark oak, others -> oak).").Value;
            Logo.Enabled = Config.Bind("General", "ValCraftLogo", true, "Replace the Valheim main menu title with 'ValCraft'.").Value;
            Emeralds.Chance = Config.Bind("Items", "EmeraldChance", 0.005f, "Chance that a mined-out Valheim rock piece gives a Minecraft emerald (0 = off).").Value;
            WeatherLink.Enabled = Config.Bind("General", "SyncWeather", true, "Valheim weather (rain / thunderstorm / snow) drives Minecraft weather.").Value;
            NetTunnel.Enabled = Config.Bind("Multiplayer", "Enabled", true, "0.13.0: on a Valheim server, Minecraft joins the host's Minecraft world through the Valheim connection (no extra ports). Everyone needs ValCraft 0.13.0+.").Value;
            NetTunnel.DedicatedTarget = Config.Bind("Multiplayer", "DedicatedMinecraftServer", "127.0.0.1:25565", "Dedicated Valheim servers only: address of the (offline-mode) Minecraft server with the ValCraft mod that players are tunnelled to.").Value;
            NetTunnel.MaxSendQueue = Config.Bind("Multiplayer", "MaxSendQueueKB", 256, "Tunnel pauses while Valheim's send queue to a peer is above this (KB), so Minecraft traffic never starves Valheim's own sync.").Value * 1024;
            Logo.ShowVersion = Config.Bind("General", "ValCraftLogoVersion", true, "Show the ValCraft version under the main menu title.").Value;
            ValheimHit.CombatScale = Config.Bind("Combat", "DamageScale", 6f, "Valheim damage per point of Minecraft attack damage at full swing (iron sword 6 -> 36, about a bronze/iron sword).").Value;
            ValheimHit.CombatPush = Config.Bind("Combat", "Knockback", 30f, "Valheim push force of a full-strength hit.").Value;
            var cfgYOff = Config.Bind("Terrain", "YOffset", 32f, "MC Y = Valheim Y + YOffset (sea level 30 -> MC 62; Valheim -96..288 fits MC -64..320).");
            void ApplyTerrain() { TerrainExport.Enabled = cfgTerrain.Value; TerrainExport.Radius = Mathf.Clamp(cfgTerrainR.Value, 16f, 128f); PlayerSync.FixedAnchorY = cfgTerrain.Value ? -cfgYOff.Value : (float?)null; }
            ApplyTerrain();
            Config.SettingChanged += (_, __) => ApplyTerrain();
            void ApplyColl() { ColliderExport.Enabled = _cfgCollision.Value; ColliderExport.Radius = Mathf.Clamp(_cfgCollRadius.Value, 8f, 96f); }
            ApplyColl();
            Config.SettingChanged += (_, __) => ApplyColl();
            var cfgNear = Config.Bind("Sync", "NearClip", 0.05f, "Valheim camera near clip plane while following (m). Must be smaller than half the hitbox width or walls get see-through when you touch them.");
            var cfgHw = Config.Bind("Sync", "HitboxWidth", 0.5f, "Minecraft player width in m (0 = 2 x the Valheim player's CapsuleCollider radius, which is 0.98 m and too wide for doorways; vanilla MC is 0.6).");
            var cfgHh = Config.Bind("Sync", "HitboxHeight", 0f, "Minecraft player height in m (0 = from the Valheim capsule; vanilla MC is 1.8). Eye height = 0.9 x height.");
            var cfgHs = Config.Bind("Sync", "HitboxScale", 1f, "Multiplier on the hitbox width/height above.");
            var cfgVer = Config.Bind("Internal", "ConfigVersion", 0, "Used for one-time config migrations; don't edit.");
            if (cfgVer.Value < 2)
            {
                // 0.4.0 log: an existing 0.3.0 config kept VerticalMode=GroundSnap (BepInEx keeps old values), which fights collision.
                if (_cfgVertical.Value == VerticalMode.GroundSnap) { _cfgVertical.Value = VerticalMode.FollowMinecraft; Log.LogInfo("[config] migrated VerticalMode GroundSnap -> FollowMinecraft (collision needs it)"); }
                cfgVer.Value = 2;
            }
            if (cfgVer.Value < 3)
            {
                // 0.4.2 log: auto width = 0.98 m (capsule radius 0.49) - nearly a whole door opening. Default 0.5 m now.
                if (cfgHw.Value <= 0f) { cfgHw.Value = 0.5f; Log.LogInfo("[config] migrated HitboxWidth auto(0.98) -> 0.5"); }
                cfgVer.Value = 3;
            }
            void ApplySync() {
                PlayerSync.NearClip = Mathf.Clamp(cfgNear.Value, 0.01f, 1f); PlayerSync.HitboxWidth = cfgHw.Value; PlayerSync.HitboxHeight = cfgHh.Value; PlayerSync.HitboxScale = Mathf.Clamp(cfgHs.Value, 0.25f, 4f); PlayerSync.ResendDims(); PlayerSync.Enabled = _cfgFollow.Value; PlayerSync.HidePlayer = _cfgHidePlayer.Value; PlayerSync.HideHud = _cfgHideHud.Value; PlayerSync.ViewBob = _cfgViewBob.Value; PlayerSync.Vertical = _cfgVertical.Value; }
            ApplySync();
            Config.SettingChanged += (_, __) => ApplySync();
        }

        void LogEnvironmentChecks()
        {
            Log.LogInfo($"[check] Unity version: {Application.unityVersion}");
            Log.LogInfo($"[check] Runtime: {Environment.Version}, Mono: {Type.GetType("Mono.Runtime") != null}");
            var rp = GraphicsSettings.currentRenderPipeline;
            Log.LogInfo(rp == null
                ? "[check] Render pipeline: BUILT-IN (GraphicsSettings.currentRenderPipeline == null)"
                : $"[check] Render pipeline: SRP asset '{rp.name}' type {rp.GetType().FullName} (URP/HDRP)");
            Log.LogInfo($"[check] Graphics API: {SystemInfo.graphicsDeviceType}, {SystemInfo.graphicsDeviceName}, reversedZ={SystemInfo.usesReversedZBuffer}, colorSpace={QualitySettings.activeColorSpace}");
            Log.LogInfo($"[check] Input: legacy={InputForwarder.LegacyInputWorks()}, inputSystem={InputForwarder.InputSystemPresent()}, uGUI RawImage={(Overlay.RawImageType != null)}");
            string scratch = Path.Combine(Path.GetTempPath(), "valcraft-mmf-selftest.bin");
            try
            {
                using (var fs = new FileStream(scratch, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    fs.SetLength(4096);
                    using (var mmf = MemoryMappedFile.CreateFromFile(fs, null, 4096, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, true))
                    using (var acc = mmf.CreateViewAccessor(0, 4096)) { acc.Write(128, 0x12345678); acc.Flush(); }
                    fs.Position = 128;
                    var b = new byte[4]; fs.Read(b, 0, 4);
                    Log.LogInfo($"[check] MemoryMappedFile (file-backed): {(BitConverter.ToInt32(b, 0) == 0x12345678 ? "OK" : "MISMATCH")}");
                }
                File.Delete(scratch);
            }
            catch (Exception e) { Log.LogError($"[check] MemoryMappedFile FAILED: {e.GetType().Name}: {e.Message}"); }
        }

        void TryLaunchMinecraft()
        {
            if (!_cfgAutoLaunch.Value) return;
            string exe = (_cfgPrismExe.Value ?? "").Trim().Trim('"', '\'').Trim(); // users often paste the path with quotes
            if (string.IsNullOrWhiteSpace(exe)) { Log.LogInfo("[launch] PrismExe not set in config; start Minecraft yourself"); return; }
            if (!File.Exists(exe)) { Log.LogWarning($"[launch] PrismExe not found: {exe}"); return; }
            if (Link != null && Link.PreviousPeerHeartbeatMs != 0 && ValCraftLink.NowMs() - Link.PreviousPeerHeartbeatMs < 3000)
            { Log.LogInfo("[launch] a ValCraft Minecraft is already running; it will reconnect, not launching another"); return; }
            string id = PrismInstances.Resolve(exe, (_cfgInstance.Value ?? "").Trim());
            if (id == null) return;
            string args = $"--launch \"{id}\" {_cfgExtraArgs.Value}".Trim();
            try
            {
                Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
                Log.LogInfo($"[launch] started {exe} {args}");
            }
            catch (Exception e) { Log.LogError($"[launch] failed: {e.Message}"); }
        }

        // 0.12.4: frame watchdog: a ValCraft Update over 15 ms is logged (rate-limited) to find hitches
        float _nextSlowLog;
        void Update()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            UpdateInner();
            double ms = sw.Elapsed.TotalMilliseconds;
            if (ms > 15 && Time.unscaledTime >= _nextSlowLog)
            {
                _nextSlowLog = Time.unscaledTime + 5f;
                // 0.13.1: which parts
                var parts = new List<KeyValuePair<string, double>>(_parts); parts.Sort((x, y) => y.Value.CompareTo(x.Value));
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < parts.Count && i < 4; i++) sb.Append(i == 0 ? "" : ", ").Append(parts[i].Key).Append(' ').Append(parts[i].Value.ToString("F1"));
                Log.LogWarning($"[perf] ValCraft frame update took {ms:F1} ms ({sb})");
            }
            _parts.Clear();
        }
        readonly Dictionary<string, double> _parts = new Dictionary<string, double>();
        readonly System.Diagnostics.Stopwatch _partSw = new System.Diagnostics.Stopwatch();
        void Part(string name) { double ms = _partSw.Elapsed.TotalMilliseconds; _partSw.Restart(); _parts.TryGetValue(name, out var o); _parts[name] = o + ms; }
        void UpdateInner()
        {
            _partSw.Restart();
            try { NetTunnel.Update(Link, _connected); } catch (Exception e) { Diag.Warn("net", "[net] update error: " + e.GetBaseException().Message); }
            Part("NetTunnel");
            if (Link == null) return;
            float t = Time.unscaledTime;
            if (t >= _nextHeartbeat) { Link.Heartbeat(); _nextHeartbeat = t + 0.1f; }
            if (t >= _nextSizeRequest) { Link.RequestFrameSize(Screen.width, Screen.height); _nextSizeRequest = t + 1f; }

            Part("start");
            while (Link.TryReceive(out var ev))
            {
                switch (ev.Type)
                {
                    case ValCraftLink.EvHello:
                        uint pid = Payload.GetU32(ev.Payload, 0), ver = Payload.GetU32(ev.Payload, 1), seen = Payload.GetU32(ev.Payload, 2);
                        if (ver != ValCraftLink.Version) { Log.LogWarning($"[link] Minecraft speaks protocol v{ver}, we speak v{ValCraftLink.Version}: update the mod jar"); break; }
                        if (seen != Link.Session) break; // stale Hello from before our restart; MC will re-send
                        Link.TrySend(ValCraftLink.EvHelloAck, Payload.U32(Link.Session, pid));
                        if (!_connected || pid != _mcPid) Log.LogInfo($"[link] Minecraft pid {pid} connected (Hello acked)");
                        BlockRender.OnConnected();
                        _connected = true; _mcPid = pid;
                        break;
                    case ValCraftLink.EvPing:
                        Link.TrySend(ValCraftLink.EvPong, ev.Payload);
                        break;
                    case ValCraftLink.EvBlockChanged:
                        TerrainExport.OnBlockChanged(ev.Payload);
                        break;
                    case ValCraftLink.EvAttack:
                        ValheimHit.Attack(ev.Payload);
                        break;
                    case ValCraftLink.EvInteract:
                        InteractBridge.Interact();
                        break;
                    default:
                        if (ev.Type == ValCraftLink.EvHealth) HealthLink.Receive(ev.Payload);
                        else if (ev.Type == ValCraftLink.EvBuildMenu) ItemBridge.BuildMenu();
                        else if (ev.Type == ValCraftLink.EvBuildSelect) ItemBridge.BuildSelect(ev.Payload);
                        else if (ev.Type == ValCraftLink.EvMobList) MobBridge.Receive(ev.Payload);
                        else if (ev.Type == ValCraftLink.EvVMobHurt) MobBridge.VMobHurt(ev.Payload);
                        else if (ev.Type == ValCraftLink.EvLevelGround) TerrainEdit.LevelGround();
                        else if (ev.Type == ValCraftLink.EvMapToggle) MapToggle.Toggle();
                        else if (ev.Type == ValCraftLink.EvNetReady) NetTunnel.Ready(ev.Payload);
                        else if (ev.Type == ValCraftLink.EvOffset) { if (ev.Payload != null && ev.Payload.Length >= 9) PlayerSync.SetOffset(BitConverter.ToSingle(ev.Payload, 1), BitConverter.ToSingle(ev.Payload, 5), ev.Payload[0] != 0); }
                        else if (ev.Type == ValCraftLink.EvSleep) SleepLink.Receive(Link, ev.Payload);
                        else if (ev.Type == ValCraftLink.EvMcFires) FireLink.Receive(ev.Payload);
                        else if (ev.Type == ValCraftLink.EvReach) InteractBridge.SetReach(ev.Payload);
                        else if (ev.Type == ValCraftLink.EvBuildAction) { if (ev.Payload != null && ev.Payload.Length > 0) ItemBridge.BuildAction(ev.Payload[0]); }
                        else if (!ItemBridge.Handle(ev)) BlockRender.Handle(ev);
                        break;
                }
            }

            // 0.4.1 bug: MC's render thread stalls for >2 s while opening/joining a world. Valheim dropped the link after
            // 2 s and only re-linked on a new Hello, but MC (still acked) never re-sends one -> no overlay, no follow, forever.
            // Now: a stall is only reported; the link drops after 20 s; and a resumed heartbeat from the same acked MC
            // re-links without a Hello.
            long hbAge = Link.PeerHeartbeatMs == 0 ? long.MaxValue : ValCraftLink.NowMs() - Link.PeerHeartbeatMs;
            bool alive = hbAge < 20000;
            if (_connected && hbAge > 2000 && !_stallLogged) { _stallLogged = true; Log.LogInfo("[link] Minecraft not responding (busy loading?) — keeping the link"); }
            if (hbAge < 1000 && _stallLogged) { _stallLogged = false; if (_connected) Log.LogInfo("[link] Minecraft responding again"); }
            if (!_connected && hbAge < 1000 && _mcPid != 0 && Link.McAckedSession == Link.Session)
            {
                _connected = true;
                Log.LogInfo($"[link] Minecraft pid {_mcPid} heartbeat resumed; re-linked (same session)");
            }
            if (_connected && !alive)
            {
                Log.LogInfo("[link] Minecraft heartbeat lost for 20 s; waiting for it to reconnect");
                _connected = false; ValheimHit.Reset(); HealthLink.Reset(); ItemBridge.ResetBuild(); ItemBridge.ChestReset(); BlockRender.Clear();
                SetInputActive(false);
                PlayerSync.Stop("Minecraft disconnected");
            }

            if (t >= _nextLinkStatus)
            {
                _nextLinkStatus = t + 30f;
                if (!_connected)
                    Log.LogInfo($"[link] status: not connected (MC heartbeat age {(Link.PeerHeartbeatMs == 0 ? "never" : (ValCraftLink.NowMs() - Link.PeerHeartbeatMs) + " ms")}, MC acked session {Link.McAckedSession:x8}, ours {Link.Session:x8})");
                else if (!PlayerSync.Following)
                    Log.LogInfo($"[link] status: connected, not following (MC world loaded={(Link.PeerFlags & ValCraftLink.McFlagWorldLoaded) != 0}, Valheim player={(PlayerSync.HasLocalPlayer ? "yes" : "no")}, FollowMinecraft={PlayerSync.Enabled})");
            }

            Part("events");
            var kb = UnityInput.Current;
            if (kb.GetKeyDown(_cfgToggleInput.Value))
            {
                if (!_connected) Log.LogInfo("[input] Minecraft not connected; F7 ignored");
                else if (_cfgAutoInput.Value) { _valheimOverride = !_valheimOverride; Log.LogInfo(_valheimOverride ? "[input] F7: controls to Valheim (press F7 again to return to Minecraft)" : "[input] F7: back to automatic (Minecraft)"); }
                else SetInputActive(!_inputActive);
            }
            if (kb.GetKeyDown(_cfgToggleOverlay.Value)) _overlayVisible = !_overlayVisible;
            if (kb.GetKeyDown(_cfgReAnchor.Value)) PlayerSync.ReAnchor();
            try { PlayerSync.Update(Link, _connected); } catch (Exception e) { Diag.Warn("sync", "[sync] update error: " + e.GetBaseException().Message); }
            Part("PlayerSync");
            try { TerrainExport.Update(Link, _connected); } catch (Exception e) { Diag.Warn("terrain", "[terrain] update error: " + e.GetBaseException().Message); }
            Part("TerrainExport");
            try { BlockRender.Update(Link, _connected); } catch (Exception e) { Diag.Warn("blocks", "[blocks] update error: " + e.GetBaseException().Message); }
            Part("BlockRender");
            try { ItemBridge.Update(Link, _connected); } catch (Exception e) { Diag.Warn("items", "[items] update error: " + e.GetBaseException().Message); }
            Part("ItemBridge");
            try { if (_connected) WorldInfo.Update(Link); } catch (Exception e) { Diag.Warn("world", "[world] " + e.GetBaseException().Message); }
            Part("WorldInfo");
            try { if (_connected && PlayerSync.Following) WeatherLink.Update(Link); } catch { }
            Part("WeatherLink");
            try { if (_connected && PlayerSync.Following) DayLink.Update(Link); } catch (Exception e) { Diag.Warn("day", "[time] update error: " + e.GetBaseException().Message); }
            Part("DayLink");
            try { if (_connected) Hazards.Update(Link); } catch (Exception e) { Diag.Warn("hazard", "[hazard] update error: " + e.GetBaseException().Message); }
            Part("Hazards");
            try { if (_connected) MobBridge.Update(Link); else MobBridge.Clear("disconnected"); } catch (Exception e) { Diag.Warn("mobs", "[mobs] update error: " + e.GetBaseException().Message); }
            Part("MobBridge");
            try { HealthLink.Update(Link); } catch (Exception e) { Diag.Warn("health", "[health] update error: " + e.GetBaseException().Message); }
            Part("HealthLink");
            try { if (_connected) SleepLink.Update(Link); else SleepLink.Reset(); } catch (Exception e) { Diag.Warn("sleep", "[sleep] update error: " + e.GetBaseException().Message); }
            Part("SleepLink");
            try { FireLink.Update(_connected); } catch (Exception e) { Diag.Warn("fire", "[fire] update error: " + e.GetBaseException().Message); }
            Part("FireLink");
            try { Logo.Tick(PlayerSync.HasLocalPlayer); } catch { }
            Part("Logo");
            try { MapToggle.Update(); } catch { }
            Part("MapToggle");
            StoneBridge.SetLink(Link); try { StoneBridge.Guard(); } catch { }
            try { if (_connected) InteractBridge.Update(Link, PlayerSync.Following && _inputActive); } catch (Exception e) { Diag.Warn("interact", "[interact] update error: " + e.GetBaseException().Message); }
            Part("InteractBridge");
            try { if (_connected) ValheimHit.Update(Link, PlayerSync.Following && _inputActive); } catch (Exception e) { Diag.Warn("hit", "[hit] update error: " + e.GetBaseException().Message); }
            Part("ValheimHit");
            if (_cfgAutoInput.Value)
            {
                // Automatic: Minecraft gets input whenever we're in-game following MC and no Valheim UI is open.
                bool want = _connected && PlayerSync.Following && !_valheimOverride && !ValheimUi.AnyOpen() && !ItemBridge.BuildMenuShown();
                if (want != _inputActive) SetInputActive(want);
            }
            try { ColliderExport.Update(Link, PlayerSync.Anchored); } catch (Exception e) { Diag.Warn("collision", "[collision] update error: " + e.GetBaseException().Message); }
            Part("ColliderExport");

            uint flags = 0;
            if (InputBlock.LocalPlayerExists()) flags |= ValCraftLink.VFlagInGame;
            if (_inputActive) flags |= ValCraftLink.VFlagInputActive;
            if (TerrainExport.Active) flags |= ValCraftLink.VFlagTerrainBlocks;
            Link.Flags = flags;

            if (_inputActive) _input.Forward((Link.PeerFlags & ValCraftLink.McFlagScreenOpen) != 0);
            // Only show MC over gameplay: not in the main menu / character select / loading (no local player), nor the Esc menu.
            bool inGame = PlayerSync.HasLocalPlayer && !ValheimUi.MenuOpen();
            if (inGame != _lastInGame) { _lastInGame = inGame; Log.LogInfo(inGame ? "[overlay] Valheim gameplay: overlay on" : "[overlay] Valheim menu/loading: overlay hidden"); }
            // 0.10.1: the MC frame is only HUD + hand now (world/player are in-world meshes): hide it behind Valheim inventory/crafting UI
            bool invOpen = (ValheimUi.InventoryOpen() || MapToggle.Open()) && (Link.PeerFlags & ValCraftLink.McFlagScreenOpen) == 0; // 0.12.0: + large map
            _overlay.Update(Link, _connected && _overlayVisible && inGame && !invOpen, _inputActive);

            if (_connected && t >= _nextStatus && Link.TryReadPlayerState(out var s))
            {
                Log.LogDebug($"[link] MC camera frame {s.FrameId} pos ({s.X:F2},{s.Y:F2},{s.Z:F2}) yaw {s.Yaw:F1} pitch {s.Pitch:F1}");
                _nextStatus = t + 5f;
            }
        }

        void LateUpdate()
        {
            if (_inputActive) _input.ApplyCursor((Link.PeerFlags & ValCraftLink.McFlagScreenOpen) != 0);
        }

        void SetInputActive(bool on)
        {
            if (on == _inputActive) return;
            _inputActive = on;
            if (!on) { Link?.TrySend(ValCraftLink.EvReleaseAll, null); _input.Reset(); }
            Log.LogInfo(on ? "[input] forwarding input to Minecraft (Valheim input suppressed)" : "[input] input back to Valheim");
        }

        void OnApplicationQuit()
        {
            if (Link == null || !_cfgKillOnExit.Value) return;
            Link.SetFlag(ValCraftLink.VFlagQuitting, true);
            Link.TrySend(ValCraftLink.EvQuit, null);
            uint pid = _mcPid != 0 ? _mcPid : Link.PeerPid;
            if (pid == 0) return;
            try
            {
                var p = Process.GetProcessById((int)pid);
                Log.LogInfo($"[quit] asked Minecraft (pid {pid}) to quit; waiting up to {_cfgQuitGrace.Value}s");
                if (!p.WaitForExit(_cfgQuitGrace.Value * 1000)) { Log.LogWarning("[quit] Minecraft still running; killing it"); p.Kill(); }
            }
            catch (ArgumentException) { /* already gone */ }
            catch (Exception e) { Log.LogWarning($"[quit] {e.Message}"); }
        }

        void OnDestroy()
        {
            NetTunnel.Shutdown();
            PlayerSync.Stop("plugin unloading");
            try { MobBridge.Clear("plugin unloading"); } catch { }
            _harmony?.UnpatchSelf();
            _overlay?.Dispose();
            Link?.Dispose();
        }
    }
}
