using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using ValCraft.Protocol;

namespace ValCraft
{
    /// <summary>
    /// Milestone 1: create the shared-memory link, heartbeat, log incoming events, answer pings.
    /// No gameplay patches yet. Startup logs the environment checks the plan marked as unverified.
    /// </summary>
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public class ValCraftPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        Harmony _harmony;
        ValCraftLink _link;
        float _nextHeartbeat, _nextStatus;
        bool _peerWasAlive;

        void Awake()
        {
            Log = Logger;
            LogEnvironmentChecks();
            try
            {
                _link = new ValCraftLink(ValCraftLink.DefaultPath(), isHost: true);
                Log.LogInfo($"[link] created {_link.Path} ({ValCraftLink.FileSize} bytes), protocol v{ValCraftLink.Version}");
            }
            catch (Exception e)
            {
                Log.LogError($"[link] could not create shared memory: {e}");
            }
            _harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            _harmony.PatchAll(); // no patches in milestone 1
        }

        void LogEnvironmentChecks()
        {
            Log.LogInfo($"[check] Unity version: {Application.unityVersion}");
            Log.LogInfo($"[check] Runtime: {Environment.Version}, Mono: {Type.GetType("Mono.Runtime") != null}");
            var rp = GraphicsSettings.currentRenderPipeline;
            Log.LogInfo(rp == null
                ? "[check] Render pipeline: BUILT-IN (GraphicsSettings.currentRenderPipeline == null)"
                : $"[check] Render pipeline: SRP asset '{rp.name}' type {rp.GetType().FullName} (URP/HDRP)");
            Log.LogInfo($"[check] Graphics API: {SystemInfo.graphicsDeviceType}, {SystemInfo.graphicsDeviceName}, reversedZ={SystemInfo.usesReversedZBuffer}");
            Log.LogInfo($"[check] Game version: {GameVersionString()}");

            // MemoryMappedFile self-test on a scratch file (does Mono's implementation work in this game?)
            string scratch = Path.Combine(Path.GetTempPath(), "valcraft-mmf-selftest.bin");
            try
            {
                using (var fs = new FileStream(scratch, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    fs.SetLength(4096);
                    using (var mmf = MemoryMappedFile.CreateFromFile(fs, null, 4096, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, true))
                    using (var acc = mmf.CreateViewAccessor(0, 4096))
                    {
                        acc.Write(128, 0x12345678);
                        acc.Flush();
                    }
                    fs.Position = 128;
                    var b = new byte[4]; fs.Read(b, 0, 4);
                    bool ok = BitConverter.ToInt32(b, 0) == 0x12345678;
                    Log.LogInfo($"[check] MemoryMappedFile (file-backed): {(ok ? "OK" : "MISMATCH")}");
                }
                File.Delete(scratch);
            }
            catch (Exception e)
            {
                Log.LogError($"[check] MemoryMappedFile FAILED: {e.GetType().Name}: {e.Message}");
            }
        }

        static string GameVersionString()
        {
#if HAS_VALHEIM
            try { return global::Version.GetVersionString(); } catch (Exception e) { return "unknown (" + e.Message + ")"; }
#else
            return "unknown (built without game DLLs)";
#endif
        }

        void Update()
        {
            if (_link == null) return;
            float t = Time.unscaledTime;
            if (t >= _nextHeartbeat) { _link.Heartbeat(); _nextHeartbeat = t + 0.1f; }

            while (_link.TryReceive(out var ev))
            {
                switch (ev.Type)
                {
                    case ValCraftLink.EvHello:
                        Log.LogInfo($"[link] Hello from Minecraft pid {BitConverter.ToUInt32(ev.Payload, 0)}");
                        _link.TrySend(ValCraftLink.EvPing, BitConverter.GetBytes(1L));
                        break;
                    case ValCraftLink.EvPing:
                        _link.TrySend(ValCraftLink.EvPong, ev.Payload);
                        break;
                    case ValCraftLink.EvPong:
                        Log.LogInfo($"[link] Pong {BitConverter.ToInt64(ev.Payload, 0)} — round trip OK");
                        break;
                    default:
                        Log.LogDebug($"[link] event type {ev.Type}, {ev.Payload.Length} bytes");
                        break;
                }
            }

            bool alive = _link.PeerAlive();
            if (alive != _peerWasAlive) { Log.LogInfo(alive ? "[link] Minecraft connected" : "[link] Minecraft heartbeat lost"); _peerWasAlive = alive; }
            if (alive && t >= _nextStatus && _link.TryReadPlayerState(out var s))
            {
                Log.LogInfo($"[link] MC player frame {s.FrameId} pos ({s.X:F2},{s.Y:F2},{s.Z:F2}) yaw {s.Yaw:F1} pitch {s.Pitch:F1} fov {s.FovDeg:F0}");
                _nextStatus = t + 5f;
            }
        }

        void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            _link?.Dispose();
        }
    }
}
