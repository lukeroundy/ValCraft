using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ValCraft
{
    /// 0.15.1: the cape in the Minecraft cape slot (EvCape) is shown on the Valheim player, unless a Valheim cape is
    /// equipped. Visual only; Valheim stats come from Valheim's own equipment.
    internal static class CapeLink
    {
        internal static readonly Dictionary<string, string> Prefabs = new Dictionary<string, string>
        {
            ["deer_hide_cape"] = "CapeDeerHide", ["troll_hide_cape"] = "CapeTrollHide", ["wolf_fur_cape"] = "CapeWolf", ["lox_cape"] = "CapeLox",
            ["linen_cape"] = "CapeLinen", ["feather_cape"] = "CapeFeather", ["asksvin_cloak"] = "CapeAsksvin", ["ash_cape"] = "CapeAsh", ["cape_of_odin"] = "CapeOdin",
        };
        static string _want = "", _shown = null; static float _next;
        static FieldInfo _local, _shoulder, _vis; static MethodInfo _setShoulder; static bool _init, _ok;

        internal static void Receive(byte[] p)
        {
            var id = p == null ? "" : System.Text.Encoding.UTF8.GetString(p).Trim();
            var pf = Prefabs.TryGetValue(id, out var v) ? v : "";
            if (pf != _want) { _want = pf; _shown = null; ValCraftPlugin.Log.LogInfo($"[cape] Minecraft cape '{id}' -> Valheim '{(pf == "" ? "none" : pf)}'"); }
        }

        internal static void Update()
        {
            if (Time.time < _next) return; _next = Time.time + 1f;
            if (!_init) Init();
            if (!_ok) return;
            try
            {
                var pl = _local.GetValue(null) as Component; if (pl == null) { _shown = null; return; }
                if (_shoulder.GetValue(pl) != null) { _shown = null; return; } // own Valheim cape wins
                var vis = _vis.GetValue(pl); if (vis == null) return;
                if (_shown == _want) return;
                _setShoulder.Invoke(vis, new object[] { _want, 0 }); _shown = _want;
            }
            catch (Exception e) { Diag.Warn("cape", "[cape] " + e.GetBaseException().Message); }
        }

        static void Init()
        {
            _init = true;
            var pT = Refl.T("Player"); var hT = Refl.T("Humanoid"); var vT = Refl.T("VisEquipment");
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            _local = pT?.GetField("m_localPlayer", F); _shoulder = hT?.GetField("m_shoulderItem", F); _vis = hT?.GetField("m_visEquipment", F);
            _setShoulder = vT?.GetMethod("SetShoulderItem", F, null, new[] { typeof(string), typeof(int) }, null);
            _ok = _local != null && _shoulder != null && _vis != null && _setShoulder != null;
            ValCraftPlugin.Log.LogInfo($"[cape] cape display {(_ok ? "OK" : "unavailable")}");
        }
    }
}
