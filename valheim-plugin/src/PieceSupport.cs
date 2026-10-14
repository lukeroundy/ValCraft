using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValCraft
{
    /// 0.15.5: Valheim pieces (stations, walls...) built on MC blocks vanished once the player walked away: the MC block
    /// colliders only exist while MC streams those sections (render radius) and while the link is up, so WearNTear lost
    /// its ground support and broke the piece. A piece found resting on MC block colliders is flagged in its ZDO
    /// ("valcraft_mcsup"); while the MC blocks around it aren't loaded, its support is kept as it was instead of recomputed.
    internal static class PieceSupport
    {
        internal const string Key = "valcraft_mcsup";
        static Type _wntT, _znvT; static FieldInfo _nviewF, _supportF; static MethodInfo _getZdo;
        static readonly Dictionary<int, float> _nextCheck = new Dictionary<int, float>();
        static readonly HashSet<int> _marked = new HashSet<int>();
        static readonly Collider[] _buf = new Collider[64];
        static int _mask;
        internal static int Kept, Marked;

        internal static void Apply(Harmony h)
        {
            _wntT = Refl.T("WearNTear"); _znvT = Refl.T("ZNetView");
            if (_wntT == null) { ValCraftPlugin.Log.LogWarning("[support] WearNTear not found"); return; }
            _nviewF = AccessTools.Field(_wntT, "m_nview"); _supportF = AccessTools.Field(_wntT, "m_support"); _getZdo = AccessTools.Method(_znvT, "GetZDO");
            _mask = LayerMask.GetMask("piece", "Default", "static_solid", "terrain");
            int n = 0;
            var us = AccessTools.Method(_wntT, "UpdateSupport");
            if (us != null) { h.Patch(us, prefix: new HarmonyMethod(typeof(PieceSupport), nameof(UpdateSupportPre)), postfix: new HarmonyMethod(typeof(PieceSupport), nameof(UpdateSupportPost))); n++; }
            var hs = AccessTools.Method(_wntT, "HaveSupport");
            if (hs != null) { h.Patch(hs, prefix: new HarmonyMethod(typeof(PieceSupport), nameof(HaveSupportPre))); n++; }
            ValCraftPlugin.Log.LogInfo($"[support] WearNTear hooks {n}/2 (UpdateSupport={us != null}, HaveSupport={hs != null}, m_support={_supportF != null})");
        }

        static object Zdo(Component w) { var nv = _nviewF?.GetValue(w) as Component; try { return nv == null ? null : _getZdo?.Invoke(nv, null); } catch { return null; } }

        static bool IsMarked(Component w)
        {
            int id = w.GetInstanceID();
            if (_marked.Contains(id)) return true;
            if (Visibility.GetFlagPublic(Zdo(w), Key) == 1) { _marked.Add(id); return true; }
            return false;
        }

        /// skip the recompute while the MC blocks under a flagged piece aren't streamed in (keeps the last support value)
        static bool UpdateSupportPre(object __instance)
        {
            try
            {
                if (!(__instance is Component w)) return true;
                if (!IsMarked(w) || BlockRender.McLoadedAt(w.transform.position)) return true;
                Kept++; return false;
            }
            catch { return true; }
        }
        static bool HaveSupportPre(object __instance, ref bool __result)
        {
            try
            {
                if (!(__instance is Component w) || !IsMarked(w) || BlockRender.McLoadedAt(w.transform.position)) return true;
                __result = true; return false;
            }
            catch { return true; }
        }

        /// while loaded: does the piece touch an MC block collider? then flag it (checked at most every 10 s per piece)
        static void UpdateSupportPost(object __instance)
        {
            try
            {
                if (!(__instance is Component w) || !BlockRender.McLoadedAt(w.transform.position)) return;
                int id = w.GetInstanceID(); float t = Time.time;
                if (_nextCheck.TryGetValue(id, out var nt) && t < nt) return;
                _nextCheck[id] = t + 10f;
                if (_marked.Contains(id)) return;
                bool any = false; Bounds b = default; bool have = false;
                foreach (var c in w.GetComponentsInChildren<Collider>()) { if (c.isTrigger) continue; if (!have) { b = c.bounds; have = true; } else b.Encapsulate(c.bounds); }
                if (!have) return;
                int n = Physics.OverlapBoxNonAlloc(b.center, b.extents + Vector3.one * 0.15f, _buf, Quaternion.identity, _mask, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++) if (BlockRender.IsMcCollider(_buf[i])) { any = true; break; }
                if (!any) return;
                _marked.Add(id); Marked++;
                Visibility.SetFlag(Zdo(w), Key, 1);
                Diag.Info("mcsup", $"[support] {w.gameObject.name} rests on Minecraft blocks: keeps its support while they're out of range");
            }
            catch { }
        }
    }
}
