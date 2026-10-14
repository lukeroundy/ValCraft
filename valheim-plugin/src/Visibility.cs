using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValCraft
{
    /// 0.15.5 multiplayer visibility: (1) a linked player's Valheim body is hidden for EVERYONE (others see only its MC
    /// model, captured like any MC entity); (2) the Greyling stand-ins of MC mobs are hidden for every client, not just the
    /// one that spawned them. Both are flagged in the ZDO ("valcraft_linked" on the player, "valcraft_proxy" on stand-ins),
    /// and every ValCraft client hides the renderers of flagged characters.
    internal static class Visibility
    {
        internal const string LinkedKey = "valcraft_linked", ProxyKey = "valcraft_proxy";
        static bool _init, _ok; static Type _charT, _playerT, _znvT, _zdoT;
        static MethodInfo _getAll, _getZdo, _setInt, _getInt, _isOwner; static FieldInfo _nviewF;
        static float _next; static int _sentLinked = -1; static object _sentZdo;
        static readonly Dictionary<Renderer, bool> _hidden = new Dictionary<Renderer, bool>();
        internal static float PickupRange = 3f; static float _pickOrig; static FieldInfo _pickF;
        static readonly HashSet<int> _hiddenChars = new HashSet<int>();

        static void Init()
        {
            _init = true;
            _charT = Refl.T("Character"); _playerT = Refl.T("Player"); _znvT = Refl.T("ZNetView"); _zdoT = Refl.T("ZDO");
            _getAll = AccessTools.Method(_charT, "GetAllCharacters"); _getZdo = AccessTools.Method(_znvT, "GetZDO"); _isOwner = AccessTools.Method(_znvT, "IsOwner");
            _nviewF = AccessTools.Field(_charT, "m_nview");
            _setInt = _zdoT?.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m => m.Name == "Set" && m.GetParameters().Length >= 2 && m.GetParameters()[0].ParameterType == typeof(string) && m.GetParameters()[1].ParameterType == typeof(int));
            _getInt = _zdoT?.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m => m.Name == "GetInt" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(string));
            _ok = _getAll != null && _getZdo != null && _setInt != null && _getInt != null;
            ValCraftPlugin.Log.LogInfo($"[visibility] ZDO flags {(_ok ? "OK" : "unavailable")} (Set(string,int)={_setInt != null}, GetInt={_getInt != null})");
        }

        static object Zdo(Component c)
        {
            var nv = (_nviewF?.GetValue(c) as Component) ?? c.GetComponent(_znvT);
            return nv == null ? null : _getZdo.Invoke(nv, null);
        }
        internal static void SetFlag(object zdo, string key, int v)
        {
            if (!_init) Init();
            if (zdo == null || _setInt == null) return;
            var ps = _setInt.GetParameters(); var args = new object[ps.Length]; args[0] = key; args[1] = v;
            for (int i = 2; i < ps.Length; i++) args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
            try { _setInt.Invoke(zdo, args); } catch { }
        }
        internal static int GetFlagPublic(object zdo, string key) { if (!_init) Init(); return _getInt == null ? 0 : GetFlag(zdo, key); }
        static int GetFlag(object zdo, string key) { try { return zdo == null ? 0 : (int)_getInt.Invoke(zdo, new object[] { key, 0 }); } catch { return 0; } }

        internal static void Update()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;
            if (!_init) Init();
            if (!_ok) return;
            try
            {
                var me = PlayerSync.LocalPlayerNow();
                if (me != null)
                {
                    var zdo = Zdo(me); int want = PlayerSync.Following ? 1 : 0;
                    if (zdo != null && (want != _sentLinked || !ReferenceEquals(zdo, _sentZdo))) { SetFlag(zdo, LinkedKey, want); _sentLinked = want; _sentZdo = zdo; }
                }
                // 0.15.5: auto-pickup a bit wider while following (the hidden Viking walks MC's blocky ground; drops often sat just out of reach)
                if (me != null)
                {
                    _pickF = _pickF ?? AccessTools.Field(_playerT, "m_autoPickupRange");
                    if (_pickF != null) { float cur = (float)_pickF.GetValue(me); if (PlayerSync.Following && cur < PickupRange) { _pickOrig = cur; _pickF.SetValue(me, PickupRange); } else if (!PlayerSync.Following && _pickOrig > 0 && Math.Abs(cur - PickupRange) < 0.01f) _pickF.SetValue(me, _pickOrig); }
                }
                if (!(_getAll.Invoke(null, null) is System.Collections.IList all)) return;
                var still = new HashSet<int>();
                foreach (Component c in all)
                {
                    if (c == null || ReferenceEquals(c, me)) continue;
                    var zdo = Zdo(c);
                    bool hide = GetFlag(zdo, ProxyKey) == 1 || (_playerT != null && _playerT.IsInstanceOfType(c) && GetFlag(zdo, LinkedKey) == 1);
                    int id = c.GetInstanceID();
                    if (hide) { still.Add(id); foreach (var r in c.GetComponentsInChildren<Renderer>(true)) if (r.enabled) { r.enabled = false; _hidden[r] = true; } if (_hiddenChars.Add(id)) Diag.Info("vis", $"[visibility] hiding Valheim body of {c.gameObject.name} ({(GetFlag(zdo, ProxyKey) == 1 ? "MC mob stand-in" : "linked player")})"); }
                }
                // un-hide players that unlinked
                foreach (var id in _hiddenChars.ToList()) if (!still.Contains(id)) _hiddenChars.Remove(id);
                foreach (var r in _hidden.Keys.ToList())
                {
                    if (r == null) { _hidden.Remove(r); continue; }
                    var ch = r.GetComponentInParent(_charT);
                    if (ch == null || !still.Contains(ch.GetInstanceID())) { r.enabled = true; _hidden.Remove(r); }
                }
            }
            catch (Exception e) { Diag.Warn("vis", "[visibility] " + e.GetBaseException().Message); }
        }
    }
}
