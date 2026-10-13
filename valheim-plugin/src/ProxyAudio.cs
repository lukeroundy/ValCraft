using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValCraft
{
    /// 0.15.0: the Valheim creatures standing in for Minecraft mobs make no Valheim sound (idle, footsteps, attack,
    /// hit, death, alert...). Visual effects stay. Minecraft plays its own mob sounds.
    ///  - On creation: AudioSources/ZSFX on the proxy and its children are stopped and disabled, and the audio-only
    ///    entries are removed from every EffectList on the proxy's components (Instantiate copies those per object).
    ///  - EffectList.Create is hooked: anything spawned under a proxy, or while a proxy is handling damage/death/
    ///    alert/attack, gets its audio stopped and disabled.
    internal static class ProxyAudio
    {
        static readonly HashSet<GameObject> _proxies = new HashSet<GameObject>();
        static int _ctx; // > 0 while a proxy's own code is running (hit/death/alert/attack)
        static Type _elT, _zsfxT;
        internal static int Muted;
        /// 0.15.1: per mob family (launcher settings, [MobAudio] Swap_<family>): true = silence the Valheim stand-in.
        internal static readonly Dictionary<string, bool> Swap = new Dictionary<string, bool>();
        internal static readonly string[] Families = { "zombie", "skeleton", "creeper", "spider", "enderman", "slime", "illager", "animal", "villager", "other" };
        internal static bool SwapFor(string mob) => !Swap.TryGetValue(Family(mob), out var v) || v;
        internal static string Family(string mob)
        {
            var m = (mob ?? "").ToLowerInvariant().Replace(' ', '_');
            if (m.Contains("zombie") || m == "husk" || m == "drowned" || m.Contains("piglin") || m == "zoglin") return "zombie";
            if (m.Contains("skeleton") || m == "stray" || m == "bogged") return "skeleton";
            if (m.Contains("creeper")) return "creeper";
            if (m.Contains("spider")) return "spider";
            if (m.Contains("enderman") || m.Contains("endermite")) return "enderman";
            if (m.Contains("slime") || m.Contains("magma_cube")) return "slime";
            if (m == "pillager" || m == "vindicator" || m == "evoker" || m == "ravager" || m == "witch" || m == "illusioner" || m == "vex") return "illager";
            if (m.Contains("villager") || m.Contains("wandering_trader")) return "villager";
            if (System.Text.RegularExpressions.Regex.IsMatch(m, "^(cow|pig|sheep|chicken|horse|donkey|mule|rabbit|wolf|cat|ocelot|fox|goat|llama|trader_llama|parrot|bee|turtle|panda|polar_bear|mooshroom|frog|axolotl|camel|sniffer|armadillo|strider|hoglin)$")) return "animal";
            return "other";
        }

        internal static void Apply(Harmony h)
        {
            _elT = Refl.T("EffectList"); _zsfxT = Refl.T("ZSFX");
            int n = 0;
            if (_elT != null)
                foreach (var m in _elT.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (m.Name == "Create" && m.ReturnType == typeof(GameObject[]))
                        try { h.Patch(m, postfix: new HarmonyMethod(typeof(ProxyAudio), nameof(CreatePost))); n++; } catch (Exception e) { ValCraftPlugin.Log.LogWarning("[mobaudio] " + e.Message); }
            int c = 0;
            foreach (var (tn, mn) in new[] { ("Character", "RPC_Damage"), ("Character", "OnDamaged"), ("Character", "OnDeath"), ("Character", "ApplyDamage"),
                                             ("Humanoid", "StartAttack"), ("Humanoid", "OnDamaged"), ("Humanoid", "OnDeath"), ("BaseAI", "SetAlerted"), ("BaseAI", "UpdateAI"), ("BaseAI", "Alert"),
                                             ("MonsterAI", "UpdateAI"), ("FootStep", "OnFoot"), ("FootStep", "FindBestStepEffect"), ("Attack", "Start") })
            {
                var t = Refl.T(tn); if (t == null) continue;
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (m.Name == mn && !m.IsAbstract && !m.IsGenericMethod)
                        try { h.Patch(m, prefix: new HarmonyMethod(typeof(ProxyAudio), nameof(CtxPre)), finalizer: new HarmonyMethod(typeof(ProxyAudio), nameof(CtxFin))); c++; } catch { }
            }
            ValCraftPlugin.Log.LogInfo($"[mobaudio] EffectList.Create hooks {n}, proxy context hooks {c}");
        }

        internal static void Silence(GameObject go)
        {
            if (go == null) return;
            _proxies.Add(go);
            MuteTree(go);
            int stripped = 0;
            foreach (var comp in go.GetComponentsInChildren<Component>(true))
                if (comp != null) stripped += StripFields(comp, 0);
            Diag.Info("mobaudio", $"[mobaudio] proxy silenced ({stripped} audio effects removed)");
        }

        internal static void Forget(GameObject go) { if (go != null) _proxies.Remove(go); _proxies.RemoveWhere(g => g == null); }

        static void MuteTree(GameObject go)
        {
            foreach (var a in go.GetComponentsInChildren<AudioSource>(true)) { try { a.Stop(); } catch { } a.mute = true; a.volume = 0; a.enabled = false; Muted++; }
            if (_zsfxT != null) foreach (var z in go.GetComponentsInChildren(_zsfxT, true)) if (z is Behaviour b) b.enabled = false;
        }

        static bool AudioOnly(GameObject p)
        {
            if (p == null) return false;
            bool audio = p.GetComponentInChildren<AudioSource>(true) != null || (_zsfxT != null && p.GetComponentInChildren(_zsfxT, true) != null);
            if (!audio) return false;
            return p.GetComponentInChildren<ParticleSystem>(true) == null && p.GetComponentInChildren<Renderer>(true) == null && p.GetComponentInChildren<Light>(true) == null;
        }

        // Removes audio-only EffectData from EffectList fields (also inside nested serializable classes, depth 2)
        static int StripFields(object o, int depth)
        {
            int n = 0;
            for (var t = o.GetType(); t != null && t != typeof(MonoBehaviour) && t != typeof(object); t = t.BaseType)
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    object v; try { v = f.GetValue(o); } catch { continue; }
                    if (v == null || v is UnityEngine.Object) continue;
                    if (_elT != null && f.FieldType == _elT) n += StripList(v);
                    else if (depth < 2 && f.FieldType.IsClass && f.FieldType.IsSerializable && f.FieldType != typeof(string) && !(v is IEnumerable)) n += StripFields(v, depth + 1);
                }
            return n;
        }

        static int StripList(object el)
        {
            var fArr = AccessTools.Field(_elT, "m_effectPrefabs"); if (fArr == null) return 0;
            if (!(fArr.GetValue(el) is Array arr) || arr.Length == 0) return 0;
            var et = arr.GetType().GetElementType(); var fPre = AccessTools.Field(et, "m_prefab");
            var keep = new List<object>();
            foreach (var d in arr) if (!(fPre?.GetValue(d) is GameObject p) || !AudioOnly(p)) keep.Add(d);
            if (keep.Count == arr.Length) return 0;
            var na = Array.CreateInstance(et, keep.Count); for (int i = 0; i < keep.Count; i++) na.SetValue(keep[i], i);
            fArr.SetValue(el, na);
            return arr.Length - keep.Count;
        }

        static bool IsProxy(object inst)
        {
            if (_proxies.Count == 0 || !(inst is Component c) || c == null) return false;
            for (var tr = c.transform; tr != null; tr = tr.parent) if (_proxies.Contains(tr.gameObject)) return true;
            return false;
        }

        static void CtxPre(object __instance, out bool __state)
        {
            __state = false;
            if (__instance is Component) __state = IsProxy(__instance);
            else if (__instance != null && _proxies.Count > 0) // Attack: owned by a Humanoid in m_character
            { try { __state = IsProxy(AccessTools.Field(__instance.GetType(), "m_character")?.GetValue(__instance)); } catch { } }
            if (__state) _ctx++;
        }
        static Exception CtxFin(bool __state, Exception __exception) { if (__state) _ctx--; return __exception; }

        static void CreatePost(GameObject[] __result, object[] __args)
        {
            if (__result == null || __result.Length == 0 || _proxies.Count == 0) return;
            bool hit = _ctx > 0;
            if (!hit) foreach (var a in __args) if (a is Transform tr && tr != null && IsProxy(tr)) { hit = true; break; }
            if (!hit) foreach (var g in __result) if (g != null && IsProxy(g.transform)) { hit = true; break; }
            if (!hit) return;
            foreach (var g in __result) if (g != null) MuteTree(g);
        }
    }
}
