using System;
using HarmonyLib;
using UnityEngine;
namespace ValCraft
{
    /// <summary>0.15.7: Valheim teleports (dungeon doorways = Teleport.Interact, portals, /goto) while following Minecraft.
    /// 0.15.6 log: "[interact] Gateway" -> "Setting forced environment Crypt" -> "Setting forced environment" 2-4 s later:
    /// Valheim moved the player into the crypt (y ~ 5000), but PlayerSync kept pinning the player to the MC position every
    /// frame (MC never moved), so Valheim's teleport was undone and the player popped back out at the doorway.
    /// Now: a teleport of the followed player stops following at once (Valheim keeps the player where it teleports it).
    ///  - Destination is a dungeon / interior (y above <see cref="InteriorY"/>, far outside MC's -64..320 build range):
    ///    Valheim plays the dungeon natively (Valheim input, camera and HUD); MC waits at the entrance.
    ///  - Destination is normal ground (portal, or the dungeon exit): once Valheim has finished teleporting, following
    ///    resumes, the mapping is kept, and the MC player is teleported to the matching MC position (EvTeleport).</summary>
    internal static class DungeonLink
    {
        internal const float InteriorY = 3000f;
        static Type _playerT; static System.Reflection.MethodInfo _isTeleporting;
        internal static bool InInterior { get; private set; }
        static float _busyUntil;
        /// <summary>PlayerSync must not (re)start following while true.</summary>
        internal static bool Busy
        {
            get
            {
                if (InInterior) return true;
                if (_busyUntil <= 0) return false;
                var p = PlayerSync.LocalPlayerNow();
                bool tp = false; try { tp = p != null && _isTeleporting != null && (bool)_isTeleporting.Invoke(p, null); } catch { }
                if (Time.unscaledTime < _busyUntil || tp) return true;
                _busyUntil = 0; PlayerSync.AlignMcOnStart(); return false;
            }
        }

        internal static void Apply(Harmony h)
        {
            _playerT = Refl.T("Player");
            _isTeleporting = AccessTools.Method(_playerT, "IsTeleporting", Type.EmptyTypes);
            var m = AccessTools.Method(_playerT, "TeleportTo");
            if (m == null) { ValCraftPlugin.Log.LogWarning("[teleport] Player.TeleportTo not found: dungeons/portals stay unsupported"); return; }
            h.Patch(m, postfix: new HarmonyMethod(typeof(DungeonLink), nameof(TeleportPost)));
            ValCraftPlugin.Log.LogInfo($"[teleport] hooked {m} (IsTeleporting={_isTeleporting != null})");
        }

        static void TeleportPost(object __instance, Vector3 pos, bool __result)
        {
            try
            {
                if (!__result || !ReferenceEquals(__instance, PlayerSync.LocalPlayerNow())) return;
                bool interior = pos.y > InteriorY;
                if (interior)
                {
                    InInterior = true; _busyUntil = 0;
                    PlayerSync.Stop("Valheim teleported into a dungeon/interior (Valheim plays it natively; Minecraft waits at the entrance)");
                    ValCraftPlugin.Log.LogInfo($"[teleport] into interior at {pos}");
                }
                else
                {
                    bool was = InInterior; InInterior = false;
                    _busyUntil = Time.unscaledTime + 1.5f; // then until Player.IsTeleporting() is false
                    PlayerSync.Stop(was ? "leaving the dungeon" : "Valheim teleport (portal)");
                    ValCraftPlugin.Log.LogInfo($"[teleport] {(was ? "out of interior" : "teleport")} to {pos}: Minecraft follows once Valheim has arrived");
                }
            }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning("[teleport] " + e.GetBaseException().Message); }
        }

        /// <summary>Unit-testable rule.</summary>
        internal static bool IsInterior(float y) => y > InteriorY;
    }
}
