using System;
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
            void ApplyColl() { ColliderExport.Enabled = _cfgCollision.Value; ColliderExport.Radius = Mathf.Clamp(_cfgCollRadius.Value, 8f, 96f); }
            ApplyColl();
            Config.SettingChanged += (_, __) => ApplyColl();
            void ApplySync() { PlayerSync.Enabled = _cfgFollow.Value; PlayerSync.HidePlayer = _cfgHidePlayer.Value; PlayerSync.HideHud = _cfgHideHud.Value; PlayerSync.ViewBob = _cfgViewBob.Value; PlayerSync.Vertical = _cfgVertical.Value; }
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
            string args = $"--launch \"{_cfgInstance.Value}\" {_cfgExtraArgs.Value}".Trim();
            try
            {
                Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
                Log.LogInfo($"[launch] started {exe} {args}");
            }
            catch (Exception e) { Log.LogError($"[launch] failed: {e.Message}"); }
        }

        void Update()
        {
            if (Link == null) return;
            float t = Time.unscaledTime;
            if (t >= _nextHeartbeat) { Link.Heartbeat(); _nextHeartbeat = t + 0.1f; }
            if (t >= _nextSizeRequest) { Link.RequestFrameSize(Screen.width, Screen.height); _nextSizeRequest = t + 1f; }

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
                        _connected = true; _mcPid = pid;
                        break;
                    case ValCraftLink.EvPing:
                        Link.TrySend(ValCraftLink.EvPong, ev.Payload);
                        break;
                }
            }

            bool alive = Link.PeerAlive();
            if (_connected && !alive)
            {
                Log.LogInfo("[link] Minecraft heartbeat lost; waiting for it to reconnect");
                _connected = false;
                SetInputActive(false);
                PlayerSync.Stop("Minecraft disconnected");
            }

            var kb = UnityInput.Current;
            if (kb.GetKeyDown(_cfgToggleInput.Value))
            {
                if (_connected) SetInputActive(!_inputActive);
                else Log.LogInfo("[input] Minecraft not connected; F7 ignored");
            }
            if (kb.GetKeyDown(_cfgToggleOverlay.Value)) _overlayVisible = !_overlayVisible;
            if (kb.GetKeyDown(_cfgReAnchor.Value)) PlayerSync.ReAnchor();
            PlayerSync.Update(Link, _connected);
            ColliderExport.Update(Link, PlayerSync.Anchored);

            uint flags = 0;
            if (InputBlock.LocalPlayerExists()) flags |= ValCraftLink.VFlagInGame;
            if (_inputActive) flags |= ValCraftLink.VFlagInputActive;
            Link.Flags = flags;

            if (_inputActive) _input.Forward((Link.PeerFlags & ValCraftLink.McFlagScreenOpen) != 0);
            _overlay.Update(Link, _connected && _overlayVisible, _inputActive);

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
            PlayerSync.Stop("plugin unloading");
            _harmony?.UnpatchSelf();
            _overlay?.Dispose();
            Link?.Dispose();
        }
    }
}
