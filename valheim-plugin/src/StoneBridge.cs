using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using ValCraft.Protocol;

namespace ValCraft
{
    /// <summary>
    /// 0.11.0: Vegvisirs / rune stones interacted from Minecraft. Valheim's own Interact opens the large map or the
    /// TextViewer, which take input while MC has focus (felt like a freeze). Instead: reveal the map pin without opening
    /// the map, and show the rune text as a Minecraft screen (EvLoreText). Anything else that pops up a Valheim
    /// TextViewer / large map right after an MC-originated interact is closed again (guard).
    /// </summary>
    internal static class StoneBridge
    {
        static Type _vegT, _runeT, _gameT, _locT, _textViewerT, _minimapT;
        static MethodInfo _discover, _localize, _addKnown, _addUnique, _setGlobal, _randomText, _tvHide, _tvVisible, _setMapMode;
        static bool _init;
        static float _guardUntil;
        static ValCraftLink _link;

        static void Init()
        {
            _init = true;
            _vegT = AccessTools.TypeByName("Vegvisir"); _runeT = AccessTools.TypeByName("RuneStone"); _gameT = AccessTools.TypeByName("Game");
            _locT = AccessTools.TypeByName("Localization"); _textViewerT = AccessTools.TypeByName("TextViewer"); _minimapT = AccessTools.TypeByName("Minimap");
            _discover = _gameT == null ? null : AccessTools.Method(_gameT, "DiscoverClosestLocation", new[] { typeof(string), typeof(Vector3), typeof(string), typeof(int), typeof(bool), typeof(bool) });
            _localize = _locT == null ? null : AccessTools.Method(_locT, "Localize", new[] { typeof(string) });
            var playerT = AccessTools.TypeByName("Player");
            _addKnown = playerT == null ? null : AccessTools.Method(playerT, "AddKnownText", new[] { typeof(string), typeof(string) });
            _addUnique = playerT == null ? null : AccessTools.Method(playerT, "AddUniqueKey", new[] { typeof(string) });
            var zs = AccessTools.TypeByName("ZoneSystem");
            _setGlobal = zs == null ? null : AccessTools.Method(zs, "SetGlobalKey", new[] { typeof(string) });
            _randomText = _runeT == null ? null : AccessTools.Method(_runeT, "GetRandomText");
            _tvHide = _textViewerT == null ? null : AccessTools.Method(_textViewerT, "Hide");
            _tvVisible = _textViewerT == null ? null : AccessTools.Method(_textViewerT, "IsVisible");
            _setMapMode = _minimapT == null ? null : AccessTools.Method(_minimapT, "SetMapMode");
            ValCraftPlugin.Log.LogInfo($"[lore] Vegvisir={_vegT != null} RuneStone={_runeT != null} DiscoverClosestLocation={_discover != null} Localize={_localize != null}");
        }

        internal static void SetLink(ValCraftLink l) { _link = l; }

        // 0.12.3: reflection lookups cached (Guard runs every frame for 2 s after any MC interact, e.g. doors)
        static readonly System.Collections.Generic.Dictionary<Type, Func<object>> _instCache = new System.Collections.Generic.Dictionary<Type, Func<object>>();
        static System.Reflection.FieldInfo _modeF;
        static object Inst(Type t, string name = "instance")
        {
            if (t == null) return null;
            if (!_instCache.TryGetValue(t, out var get))
            {
                var p = AccessTools.Property(t, name);
                System.Reflection.FieldInfo f = null;
                if (p == null) foreach (var fi in AccessTools.GetDeclaredFields(t)) if (fi.IsStatic && fi.FieldType == t) { f = fi; break; }
                get = p != null ? (Func<object>)(() => p.GetValue(null, null)) : f != null ? (() => f.GetValue(null)) : (Func<object>)(() => null);
                _instCache[t] = get;
            }
            try { return get(); } catch { return null; }
        }
        static string L(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            try { var loc = Inst(_locT); if (loc != null && _localize != null) return (string)_localize.Invoke(loc, new object[] { s }); } catch { }
            return s;
        }
        static T F<T>(object o, string n) { try { var f = AccessTools.Field(o.GetType(), n); return f == null ? default : (T)f.GetValue(o); } catch { return default; } }

        /// <summary>True if handled (no Valheim Interact call).</summary>
        internal static bool TryInteract(GameObject go, Component player)
        {
            if (!_init) Init();
            _guardUntil = Time.unscaledTime + 2f;
            if (go == null) return false;
            var veg = _vegT == null ? null : go.GetComponentInParent(_vegT);
            if (veg != null) { Vegvisir(veg, player); return true; }
            var rune = _runeT == null ? null : go.GetComponentInParent(_runeT);
            if (rune != null) { Rune(rune, player); return true; }
            return false;
        }

        static void Discover(string loc, Vector3 pos, string pin, int pinType, bool all)
        {
            if (string.IsNullOrEmpty(loc) || _discover == null) return;
            var game = Inst(_gameT);
            if (game != null) _discover.Invoke(game, new object[] { loc, pos, pin, pinType, false /* never open the map */, all });
        }

        static void Vegvisir(Component v, Component player)
        {
            var sb = new StringBuilder();
            try
            {
                var locs = F<IList>(v, "m_locations");
                if (locs != null)
                    foreach (var l in locs)
                    {
                        string name = F<string>(l, "m_locationName"), pin = F<string>(l, "m_pinName");
                        int type = Convert.ToInt32(AccessTools.Field(l.GetType(), "m_pinType")?.GetValue(l) ?? 0);
                        Discover(name, v.transform.position, pin, type, F<bool>(l, "m_discoverAll"));
                        if (!string.IsNullOrEmpty(pin)) sb.Append(L(pin)).Append('\n');
                    }
                var gk = F<string>(v, "m_setsGlobalKey"); if (!string.IsNullOrEmpty(gk)) { var zs = Inst(AccessTools.TypeByName("ZoneSystem")); if (zs != null) _setGlobal?.Invoke(zs, new object[] { gk }); }
                var pk = F<string>(v, "m_setsPlayerKey"); if (!string.IsNullOrEmpty(pk) && player != null) _addUnique?.Invoke(player, new object[] { pk });
            }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning($"[lore] vegvisir: {e.GetBaseException().Message}"); }
            string title = L(F<string>(v, "m_name"));
            Send(string.IsNullOrEmpty(title) ? "Vegvisir" : title, sb.Length > 0 ? "Revealed on your map:\n" + sb.ToString().TrimEnd() : "A location was revealed on your map.");
            ValCraftPlugin.Log.LogInfo($"[lore] vegvisir {v.gameObject.name}: map pin revealed (map not opened)");
        }

        static void Rune(Component r, Component player)
        {
            string topic = F<string>(r, "m_topic"), label = F<string>(r, "m_label"), text = F<string>(r, "m_text");
            try
            {
                var rnd = F<IList>(r, "m_randomTexts");
                if (rnd != null && rnd.Count > 0 && _randomText != null)
                {
                    var t = _randomText.Invoke(r, null);
                    if (t != null) { topic = F<string>(t, "m_topic"); label = F<string>(t, "m_label"); text = F<string>(t, "m_text"); }
                }
                if (player != null && !string.IsNullOrEmpty(label)) _addKnown?.Invoke(player, new object[] { label, text });
                string loc = F<string>(r, "m_locationName");
                int type = Convert.ToInt32(AccessTools.Field(r.GetType(), "m_pinType")?.GetValue(r) ?? 0);
                Discover(loc, r.transform.position, F<string>(r, "m_pinName"), type, false);
            }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning($"[lore] rune stone: {e.GetBaseException().Message}"); }
            string title = L(string.IsNullOrEmpty(topic) ? F<string>(r, "m_name") : topic);
            Send(string.IsNullOrEmpty(title) ? "Rune stone" : title, L(text));
            ValCraftPlugin.Log.LogInfo($"[lore] rune stone {r.gameObject.name}: '{title}' shown in Minecraft");
        }

        static readonly System.Text.RegularExpressions.Regex Tags = new System.Text.RegularExpressions.Regex("<[^>]+>");
        static void Send(string title, string text)
        {
            if (_link == null) return;
            var all = Encoding.UTF8.GetBytes(Tags.Replace(title ?? "", "") + "\n" + Tags.Replace(text ?? "", ""));
            if (all.Length > 4000) Array.Resize(ref all, 4000);
            int chunk = ValCraftLink.SlotPayload - 1;
            for (int o = 0; o < all.Length || o == 0; o += chunk)
            {
                int n = Math.Min(chunk, all.Length - o);
                var b = new byte[n + 1];
                b[0] = (byte)((o == 0 ? 1 : 0) | (o + n >= all.Length ? 2 : 0));
                Array.Copy(all, o, b, 1, n);
                _link.TrySend(ValCraftLink.EvLoreText, b);
                if (n <= 0) break;
            }
        }

        /// <summary>Every frame: close a Valheim TextViewer / large map that an MC-originated interact opened.</summary>
        internal static void Guard()
        {
            if (Time.unscaledTime > _guardUntil) return;
            if (!_init) Init();
            try
            {
                var tv = Inst(_textViewerT);
                if (tv != null && _tvVisible != null && (bool)_tvVisible.Invoke(tv, null)) { _tvHide?.Invoke(tv, null); ValCraftPlugin.Log.LogInfo("[lore] closed a Valheim text viewer opened by an MC interact"); }
                var mm = Inst(_minimapT);
                if (mm != null && _setMapMode != null)
                {
                    if (_modeF == null) _modeF = AccessTools.Field(_minimapT, "m_mode");
                    var mode = _modeF?.GetValue(mm);
                    if (mode != null && Convert.ToInt32(mode) == 2 && !MapToggle.OpenedByUs) { _setMapMode.Invoke(mm, new[] { Enum.ToObject(_setMapMode.GetParameters()[0].ParameterType, 1) }); ValCraftPlugin.Log.LogInfo("[lore] closed the Valheim large map opened by an MC interact"); }
                }
            }
            catch { }
        }
    }
}
