using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ValCraft.Protocol;

namespace ValCraft
{
    /// <summary>
    /// 0.14.2: Hugin/Munin (class Raven) from Minecraft. The raven sits on the "character" layer / a big trigger, so the
    /// interact ray never hit it: picked by proximity to the view ray instead. Right-click -> vanilla Raven.Interact (marks
    /// the tutorial seen, raven leaves as usual); the text it shows (Chat.SetNpcText / TextViewer.ShowText) goes to the
    /// Minecraft lore screen and Valheim's own viewer is closed by the lore guard. Auto-talk (raven speaks when you come
    /// near) goes to Minecraft chat (EvNpcSay).
    /// </summary>
    internal static class RavenBridge
    {
        static Type _ravenT;
        static bool _init;
        static Component[] _ravens = new Component[0];
        static float _nextScan, _mcUntil;
        static string _lastSaid; static float _lastSaidAt;
        static ValCraftLink _link;
        internal static void SetLink(ValCraftLink l) { _link = l; }

        static void Init() { _init = true; _ravenT = Refl.T("Raven") ?? AccessTools.TypeByName("Raven"); }

        public static void Apply(Harmony h)
        {
            if (!_init) Init();
            int n = 0;
            var chat = AccessTools.TypeByName("Chat");
            if (chat != null) foreach (var m in AccessTools.GetDeclaredMethods(chat)) if (m.Name == "SetNpcText") { h.Patch(m, postfix: new HarmonyMethod(typeof(RavenBridge), nameof(NpcTextPost))); n++; }
            var tv = AccessTools.TypeByName("TextViewer");
            if (tv != null) foreach (var m in AccessTools.GetDeclaredMethods(tv)) if (m.Name == "ShowText") { h.Patch(m, postfix: new HarmonyMethod(typeof(RavenBridge), nameof(ViewerPost))); n++; }
            ValCraftPlugin.Log.LogInfo($"[raven] Raven={_ravenT != null}, {n} text hooks");
        }

        /// <summary>The raven closest to the view ray within reach (perpendicular distance &lt; 1 m), or null.</summary>
        public static Component Pick(Vector3 eye, Vector3 dir, float range)
        {
            if (!_init) Init();
            if (_ravenT == null) return null;
            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + 1f;
                try { var all = UnityEngine.Object.FindObjectsOfType(_ravenT); _ravens = Array.ConvertAll(all, o => o as Component); } catch { _ravens = new Component[0]; }
            }
            Component best = null; float bestD = 1f;
            foreach (var r in _ravens)
            {
                if (r == null || !r.gameObject.activeInHierarchy) continue;
                var c = Center(r);
                var v = c - eye; float along = Vector3.Dot(v, dir);
                if (along < 0 || along > range + 0.75f) continue;
                float perp = (v - dir * along).magnitude;
                if (perp < bestD) { bestD = perp; best = r; }
            }
            return best;
        }

        static Vector3 Center(Component r)
        {
            var col = r.GetComponentInChildren<Collider>();
            if (col != null && col.bounds.extents.magnitude < 1.5f) return col.bounds.center;
            var rend = r.GetComponentInChildren<Renderer>();
            return rend != null ? rend.bounds.center : r.transform.position + Vector3.up * 0.5f;
        }

        public static bool IsRaven(GameObject go) { if (!_init) Init(); return go != null && _ravenT != null && go.GetComponentInParent(_ravenT) != null; }

        /// <summary>MC right-click on a raven: vanilla Interact; its text is shown in Minecraft.</summary>
        public static bool TryInteract(GameObject go, Component player)
        {
            if (!IsRaven(go)) return false;
            var r = go.GetComponentInParent(_ravenT);
            _mcUntil = Time.unscaledTime + 1.5f;
            StoneBridge.ArmGuard();
            try
            {
                var m = AccessTools.Method(_ravenT, "Interact");
                var ps = m.GetParameters(); var a = new object[ps.Length];
                if (ps.Length > 0) a[0] = player; for (int i = 1; i < ps.Length; i++) a[i] = ps[i].ParameterType == typeof(bool) ? (object)false : null;
                m.Invoke(r, a);
                ValCraftPlugin.Log.LogInfo($"[raven] {r.gameObject.name}: interact from Minecraft");
            }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning($"[raven] interact: {e.GetBaseException().Message}"); }
            return true;
        }

        static void NpcTextPost(object[] __args)
        {
            try
            {
                GameObject talker = null; string topic = null, text = null;
                var strs = new System.Collections.Generic.List<string>();
                foreach (var a in __args) { if (a is GameObject g) talker = g; else if (a is string s) strs.Add(s); }
                if (!IsRaven(talker) || strs.Count == 0) return;
                text = strs[strs.Count - 1]; if (strs.Count >= 2) topic = strs[strs.Count - 2];
                Forward(topic, text);
            }
            catch { }
        }

        static void ViewerPost(object[] __args)
        {
            try
            {
                var strs = new System.Collections.Generic.List<string>();
                foreach (var a in __args) if (a is string s) strs.Add(s);
                if (strs.Count == 0) return;
                // only texts opened right after our raven interact (rune stones are handled by StoneBridge without the viewer)
                if (Time.unscaledTime > _mcUntil) return;
                Forward(strs.Count >= 2 ? strs[0] : "", strs[strs.Count - 1]);
            }
            catch { }
        }

        static void Forward(string topic, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            string t = StoneBridge.Localize(topic ?? ""), body = StoneBridge.Localize(text);
            // Raven.Say prefixes "<color=orange>topic</color>\n" when the topic is folded into the text
            var m = System.Text.RegularExpressions.Regex.Match(body, "^\\s*<color=[^>]*>(.*?)</color>\\s*\\n", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (m.Success) { if (t.Length == 0) t = m.Groups[1].Value; body = body.Substring(m.Length); }
            if (Time.unscaledTime <= _mcUntil)
            {
                _mcUntil = 0; // one screen per right-click
                StoneBridge.SendLore(t.Length > 0 ? t : "Hugin", body);
                ValCraftPlugin.Log.LogInfo($"[raven] '{t}' shown in Minecraft");
                return;
            }
            string key = t + "|" + body;
            if (key == _lastSaid && Time.unscaledTime - _lastSaidAt < 30f) return;
            _lastSaid = key; _lastSaidAt = Time.unscaledTime;
            if (_link == null) return;
            var clean = StoneBridge.StripTags((t.Length > 0 ? t : "") + "\n" + body);
            var b = System.Text.Encoding.UTF8.GetBytes(clean);
            if (b.Length > ValCraftLink.SlotPayload) Array.Resize(ref b, ValCraftLink.SlotPayload);
            _link.TrySend(ValCraftLink.EvNpcSay, b);
            ValCraftPlugin.Log.LogInfo($"[raven] auto-talk '{t}' sent to Minecraft chat");
        }
    }
}
